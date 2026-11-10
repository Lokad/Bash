using System;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Bash.Memory;

/// <summary>Prepays capacity for one serialized owner's estimates and reusable scratch space.</summary>
/// <remarks>
/// Logical usage can shrink without returning prepaid capacity to the governor. That capacity
/// remains charged until trimmed, disposed or published, so repeated records spend local credit rather than
/// admitting each item separately. Publication leaves logical usage charged to the enclosing scope.
/// Refused admission fails the operation, including requests for spare capacity.
/// Owners must stop all users before disposing this allowance or its enclosing memory scope.
/// </remarks>
public sealed class MemoryCapacity : IDisposable
{
    private const long CapacityIncrementBytes = 64 * 1024;
    private const long MinimumGrowthBytes = 256;
    private readonly IMemoryBudget _budget;
    private IMemoryReservation? _reservation;
    private bool _disposed;

    public MemoryCapacity(IMemoryBudget budget, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _budget = budget;
        if (bytes != 0)
            _reservation = budget.Reserve(bytes);
        Bytes = bytes;
    }

    /// <summary>Reuses an admitted reservation when adapting a non-throwing host contract.</summary>
    private MemoryCapacity(IMemoryBudget budget, IMemoryReservation reservation)
    {
        _budget = budget;
        _reservation = reservation;
        Bytes = reservation.Bytes;
    }

    /// <summary>Admits an initial estimate exactly, then amortizes resizing through reusable capacity.</summary>
    public static bool TryReserve(IMemoryBudget budget, long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
    {
        if (!budget.TryReserve(bytes, out var admitted))
        {
            reservation = null;
            return false;
        }
        reservation = new CapacityReservation(admitted, new MemoryCapacity(budget, admitted));
        return true;
    }

    // External runtimes serialize each reservation. Keep logical usage separate
    // from prepaid capacity, refunding large releases with hysteresis.
    private sealed class CapacityReservation(IMemoryReservation reservation, MemoryCapacity capacity) : IMemoryReservation
    {
        public long Bytes => reservation.Bytes == 0 ? 0 : capacity.Bytes;

        public bool TryResize(long bytes)
        {
            // Zero can mean an empty charge or a disposed ancestor. Validate that
            // uncommon case through the underlying reservation, even for a zero resize.
            if (reservation.Bytes == 0)
                reservation.TryResize(0);
            var previous = capacity.Bytes;
            if (!capacity.TryResize(bytes))
                return false;
            if (bytes < previous)
                capacity.TrimExcess();
            return true;
        }

        public void Dispose() => capacity.Dispose();
    }

    /// <summary>Current logical usage, excluding unused capacity still charged to the governor.</summary>
    public long Bytes { get; private set; }

    public bool TryResize(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var previous = _reservation?.Bytes ?? 0;
        if (bytes > previous)
        {
            var capacity = previous == 0 ? bytes : bytes <= CapacityIncrementBytes
                ? Math.Max(bytes, Math.Min(CapacityIncrementBytes, Math.Max(MinimumGrowthBytes, previous * 2)))
                : bytes > long.MaxValue - (CapacityIncrementBytes - 1)
                    ? bytes : (bytes + CapacityIncrementBytes - 1) / CapacityIncrementBytes * CapacityIncrementBytes;
            var admitted = _reservation is { } reservation
                ? reservation.TryResize(capacity)
                : _budget.TryReserve(capacity, out _reservation);
            if (!admitted)
                return false;
        }
        Bytes = bytes;
        return true;
    }

    public void Resize(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes != Bytes && !TryResize(bytes))
            throw new MemoryBudgetExceededException();
    }

    public void Grow(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes != 0)
            Resize(checked(Bytes + bytes));
    }

    /// <summary>Returns large amounts of unused credit after a batch has relinquished its storage.</summary>
    public void TrimExcess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Keep one capacity increment of slack after trimming, and another before the next trim.
        // Otherwise a descending record loop would resize the reservation on every release.
        if (_reservation is { } reservation && reservation.Bytes - Bytes > 2 * CapacityIncrementBytes)
            reservation.Resize(checked(Bytes + CapacityIncrementBytes));
    }

    /// <summary>Returns spare credit so a refused operation does not starve later work.</summary>
    public void ReleaseUnusedCapacity() => _reservation?.Resize(Bytes);

    /// <summary>Leaves logical usage charged to the enclosing scope and returns unused credit.</summary>
    /// <remarks>This ends the capacity owner's lifetime; retained data must stay within that scope.</remarks>
    public void DetachReservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _reservation?.Resize(Bytes);
        _reservation = null;
        Bytes = 0;
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Bytes = 0;
        _reservation?.Dispose();
    }
}

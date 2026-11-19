using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Lokad.Bash.Memory;

/// <summary>Prepays capacity for one serialized owner's estimates and reusable scratch space.</summary>
/// <remarks>
/// Logical usage can shrink without returning prepaid capacity to the governor. That capacity
/// remains charged until trimmed, disposed or published, so repeated records spend local credit rather than
/// admitting each item separately. Publication leaves logical usage charged to the enclosing scope.
/// Refused admission fails the operation, including requests for spare capacity.
/// Owners must stop all users before disposing this allowance or its enclosing memory scope.
/// Capacity adjustments require a live owner, including adjustments that leave usage unchanged.
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
            var previous = capacity.Bytes;
            if (!capacity.TryResize(bytes))
                return false;
            if (bytes < previous)
                capacity.TrimExcess();
            return true;
        }

        public void Dispose() => capacity.Dispose();
    }

    /// <summary>The last logical usage recorded by this helper, excluding unused prepaid capacity.</summary>
    /// <remarks>Disposing or detaching this helper clears the estimate. The enclosing scope controls the live charge.</remarks>
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
        else if (previous == 0)
        {
            CheckEmptyCapacityLifetime();
        }
        Bytes = bytes;
        return true;
    }

    public void Resize(long bytes)
    {
        if (!TryResize(bytes))
            throw new MemoryBudgetExceededException();
    }

    public void Grow(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Resize(checked(Bytes + bytes));
    }

    internal static long ArrayBackingBytes<T>(int capacity) =>
        capacity == 0 ? 0 : 32L + (long)Unsafe.SizeOf<T>() * capacity;

    /// <summary>Admits a list's replacement array and releases the previous array after growth.</summary>
    internal void PrepareListCapacity<T>(List<T> list, int additional)
    {
        var previous = list.Capacity;
        var required = checked(list.Count + additional);
        if (required <= previous)
            return;

        var capacity = (int)Math.Max(required, Math.Min(Array.MaxLength, Math.Max(4L, 2L * previous)));
        Grow(ArrayBackingBytes<T>(capacity));
        list.Capacity = capacity;
        Resize(Bytes - ArrayBackingBytes<T>(previous));
    }

    /// <summary>Returns large amounts of unused credit after a batch has relinquished its storage.</summary>
    public void TrimExcess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var prepaid = _reservation?.Bytes ?? 0;
        if (prepaid == 0)
        {
            CheckEmptyCapacityLifetime();
            return;
        }
        // Keep one capacity increment of slack after trimming, and another before the next trim.
        // Otherwise a descending record loop would resize the reservation on every release.
        if (prepaid - Bytes > 2 * CapacityIncrementBytes && _reservation is { } reservation)
            reservation.Resize(checked(Bytes + CapacityIncrementBytes));
    }

    /// <summary>Returns spare credit so a refused operation does not starve later work.</summary>
    public void ReleaseUnusedCapacity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reservation is { } reservation)
            reservation.Resize(Bytes);
        else
            CheckEmptyCapacityLifetime();
    }

    /// <summary>Leaves logical usage charged to the enclosing scope and returns unused credit.</summary>
    /// <remarks>This ends the capacity owner's lifetime; retained data must stay within that scope.</remarks>
    public void DetachReservation()
    {
        ReleaseUnusedCapacity();
        _reservation = null;
        Bytes = 0;
        _disposed = true;
    }

    private void CheckEmptyCapacityLifetime()
    {
        // A zero reading can also mean that the reservation's enclosing scope ended.
        if (_reservation is { } reservation)
        {
            _ = reservation.TryResize(0);
            return;
        }

        // Unused scratch owns no reservation. A host budget can check its lifetime
        // through the empty-scope operation, which requests no admission.
        if (!MemoryGovernor.TryCheckLifetime(_budget))
            _budget.CreateScope().Dispose();
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

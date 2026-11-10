using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Lokad.Bash.Memory;

/// <summary>One shared, fixed allowance for cooperating memory consumers within an isolation boundary.</summary>
public sealed class MemoryGovernor : IMemoryBudget
{
    private readonly Scope _root;
    private long _reservedBytes;
    private long _peakBytes;

    /// <summary>Creates a governor with the caller's nonnegative allowance in estimated bytes.</summary>
    public MemoryGovernor(long limitBytes)
    {
        if (limitBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limitBytes));
        LimitBytes = limitBytes;
        _root = new Scope(this, null);
    }

    /// <summary>The configured allowance, excluding the caller's separately provisioned headroom.</summary>
    public long LimitBytes { get; }

    /// <summary>The sum of outstanding reservations.</summary>
    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    /// <summary>The largest admitted sum since this governor was created.</summary>
    public long PeakBytes => Interlocked.Read(ref _peakBytes);

    /// <inheritdoc />
    public IMemoryScope CreateScope() => _root.CreateScope();

    /// <inheritdoc />
    public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
        _root.TryReserve(bytes, out reservation);

    private bool TryAcquire(long bytes)
    {
        if (bytes == 0)
            return true;
        while (true)
        {
            var previous = Interlocked.Read(ref _reservedBytes);
            if (bytes > LimitBytes - previous)
                return false;
            var next = previous + bytes;
            if (Interlocked.CompareExchange(ref _reservedBytes, next, previous) != previous)
                continue;
            while (true)
            {
                var peak = Interlocked.Read(ref _peakBytes);
                if (next <= peak || Interlocked.CompareExchange(ref _peakBytes, next, peak) == peak)
                    break;
            }
            return true;
        }
    }

    // Only upward links are needed: each scope counts its subtree, and descendants check
    // their ancestors for disposal. Neither reservations nor managed objects are registered.
    private sealed class Scope(MemoryGovernor governor, Scope? parent) : IMemoryScope
    {
        private long _bytes;
        private bool _disposed;

        public bool IsDisposed
        {
            get
            {
                for (Scope? scope = this; scope != null; scope = scope.Parent)
                    if (scope._disposed)
                        return true;
                return false;
            }
        }

        private Scope? Parent => parent;

        public IMemoryScope CreateScope()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return new Scope(governor, this);
        }

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            var created = new Reservation(this);
            if (!TryAcquire(bytes))
            {
                reservation = null;
                return false;
            }
            created.SetInitialBytes(bytes);
            reservation = created;
            return true;
        }

        public bool TryAcquire(long bytes)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (bytes == 0)
                return true;
            if (!governor.TryAcquire(bytes))
                return false;
            for (Scope? scope = this; scope != null; scope = scope.Parent)
                Interlocked.Add(ref scope._bytes, bytes);
            return true;
        }

        public void Release(long bytes)
        {
            if (bytes == 0)
                return;
            for (Scope? scope = this; scope != null; scope = scope.Parent)
                Interlocked.Add(ref scope._bytes, -bytes);
            Interlocked.Add(ref governor._reservedBytes, -bytes);
        }

        public void Dispose()
        {
            // The owner must first stop all subtree users. Individual admissions can run
            // concurrently, but disposal is a lifetime boundary, not cancellation.
            if (IsDisposed)
                return;
            Release(_bytes);
            _disposed = true;
        }
    }

    private sealed class Reservation(Scope scope) : IMemoryReservation
    {
        private bool _disposed;
        private long _bytes;
        public long Bytes => _disposed || scope.IsDisposed ? 0 : _bytes;

        public void SetInitialBytes(long bytes) => _bytes = bytes;

        public bool TryResize(long bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed || scope.IsDisposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            var difference = bytes - _bytes;
            if (difference > 0 && !scope.TryAcquire(difference))
                return false;
            if (difference < 0)
                scope.Release(-difference);
            _bytes = bytes;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (!scope.IsDisposed)
                scope.Release(_bytes);
            _disposed = true;
            _bytes = 0;
        }
    }
}

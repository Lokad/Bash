using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

/// <summary>Counts admission operations across all scopes to detect accounting in record loops.</summary>
internal sealed class CountingMemoryBudget : IMemoryBudget
{
    private readonly IMemoryBudget _budget;
    private readonly Counter _counter;

    public CountingMemoryBudget(IMemoryBudget budget) : this(budget, new Counter()) { }

    private CountingMemoryBudget(IMemoryBudget budget, Counter counter)
    {
        _budget = budget;
        _counter = counter;
    }

    public long AdmissionCalls => Interlocked.Read(ref _counter.Calls);

    /// <summary>Observes successful admission after storage has been charged.</summary>
    public event Action AdmissionSucceeded
    {
        add => _counter.AdmissionSucceeded += value;
        remove => _counter.AdmissionSucceeded -= value;
    }

    public IMemoryScope CreateScope() => new Scope(_budget.CreateScope(), _counter);

    public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
    {
        Interlocked.Increment(ref _counter.Calls);
        if (!_budget.TryReserve(bytes, out var admitted))
        {
            reservation = null;
            return false;
        }
        reservation = new Reservation(admitted, _counter);
        _counter.AdmissionSucceeded?.Invoke();
        return true;
    }

    private sealed class Counter
    {
        public long Calls;
        public Action? AdmissionSucceeded;
    }

    private sealed class Scope(IMemoryScope scope, Counter counter) : IMemoryScope
    {
        private readonly CountingMemoryBudget _budget = new(scope, counter);
        public IMemoryScope CreateScope() => _budget.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
            _budget.TryReserve(bytes, out reservation);
        public void Dispose() => scope.Dispose();
    }

    private sealed class Reservation(IMemoryReservation reservation, Counter counter) : IMemoryReservation
    {
        public long Bytes => reservation.Bytes;
        public bool TryResize(long bytes)
        {
            Interlocked.Increment(ref counter.Calls);
            if (!reservation.TryResize(bytes)) return false;
            counter.AdmissionSucceeded?.Invoke();
            return true;
        }
        public void Dispose() => reservation.Dispose();
    }
}

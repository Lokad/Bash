using System;

namespace Lokad.Bash.Memory;

/// <summary>Throwing adapters for consumers that unwind on reservation refusal.</summary>
public static class MemoryBudgetExtensions
{
    /// <summary>Reserves before allocation, throwing MemoryBudgetExceededException to fail the command on refusal.</summary>
    public static IMemoryReservation Reserve(this IMemoryBudget budget, long bytes) =>
        budget.TryReserve(bytes, out var reservation) ? reservation : throw new MemoryBudgetExceededException();

    /// <summary>Resizes before growth, preserving the old allowance on refusal.</summary>
    public static void Resize(this IMemoryReservation reservation, long bytes)
    {
        if (!reservation.TryResize(bytes))
            throw new MemoryBudgetExceededException();
    }

    /// <summary>Adds an estimate to an existing owner's reservation before allocation.</summary>
    public static void Grow(this IMemoryReservation reservation, long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        reservation.Resize(checked(reservation.Bytes + bytes));
    }
}

/// <summary>A cooperative memory refusal, distinct from actual allocation failure.</summary>
public sealed class MemoryBudgetExceededException : Exception
{
    /// <summary>Creates the common diagnostic used by cooperating consumers.</summary>
    public MemoryBudgetExceededException() : base("Shared memory budget exhausted.") { }
}

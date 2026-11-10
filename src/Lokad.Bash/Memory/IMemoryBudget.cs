using System;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Bash.Memory;

/// <summary>Admits estimated owned memory before allocation, shared across concurrent consumers.</summary>
/// <remarks>
/// Implementations must be thread-safe and must not wait for memory to become available.
/// Refusal changes no accounting and fails the command; consumers must not retry or
/// switch storage strategies to recover. Cleanup allows subsequent commands to proceed.
/// Estimates include capacity, overhead and temporary copies.
/// Keep reservations charged while components retain the storage, including caches and pools.
/// Release follows relinquishing ownership: the allowance becomes reusable immediately,
/// without waiting for or triggering garbage collection.
/// </remarks>
public interface IMemoryBudget
{
    /// <summary>Creates an empty ownership scope sharing this budget's allowance.</summary>
    /// <remarks>Throws ObjectDisposedException if this budget is a disposed scope.</remarks>
    IMemoryScope CreateScope();

    /// <summary>Reserves nonnegative bytes, or returns false with a null reservation.</summary>
    bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation);
}

/// <summary>Owns reservations and child scopes against the same root allowance.</summary>
/// <remarks>
/// Dispose only after all users, including asynchronous work and cleanup, have stopped
/// and all accounted storage has been relinquished. Physical collection may happen later.
/// Descendants cannot outlive their parent. Disposal releases the entire subtree and invalidates
/// its budgets and reservations; later disposal of a descendant is harmless.
/// Allocate results that survive execution under a longer-lived owner.
/// Admission is thread-safe. The owner must serialize disposal with all subtree operations.
/// Scopes have no independent cap and reserve nothing on creation. Disposal is idempotent and non-throwing.
/// </remarks>
public interface IMemoryScope : IMemoryBudget, IDisposable
{
}

/// <summary>An allowance owned by a component, collection or allocation.</summary>
/// <remarks>
/// Owners serialize resize and disposal, including disposal of ancestor scopes.
/// Ownership may be passed with the data within the enclosing scope's lifetime;
/// sharing data does not require another reservation, but copying it does.
/// Reusing retained storage reuses its reservation. Disposal after relinquishing storage
/// restores the allowance immediately, is idempotent and must not throw.
/// </remarks>
public interface IMemoryReservation : IDisposable
{
    /// <summary>The current reserved estimate; zero after disposal of this reservation or an ancestor.</summary>
    long Bytes { get; }

    /// <summary>
    /// Replaces the estimate with a nonnegative value. Refusal leaves it unchanged.
    /// Growth must precede allocation; shrinking follows relinquishing ownership.
    /// Buffer replacement must cover both old and new storage until copying completes.
    /// Resizing a disposed reservation throws ObjectDisposedException.
    /// </summary>
    bool TryResize(long bytes);
}

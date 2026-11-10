using System;
using System.Threading;

namespace Lokad.Bash.Commands;

/// <summary>Shares cumulative expansion work allowances across related commands.</summary>
public sealed class CommandAllocationBudget
{
    private AllocationAllowance? _generation;

    /// <summary>Shared allowance for constructing values and copying command arguments.</summary>
    public AllocationAllowance Values { get; } = new("shell: word expansion exceeds its word or character limit\n");

    /// <summary>Shares a separate generation allowance, creating it only when an interpreter needs it.</summary>
    /// <remarks>Repeated requests share the same allowance and its original diagnostic; they never reset it.</remarks>
    public AllocationAllowance GetGenerationAllowance(string limitMessage)
    {
        var generation = Volatile.Read(ref _generation);
        if (generation is not null)
            return generation;
        generation = new AllocationAllowance(limitMessage);
        return Interlocked.CompareExchange(ref _generation, generation, null) ?? generation;
    }
}

/// <summary>Atomically charges expansion work, optionally admitting an evaluator's scratch before allocation.</summary>
public sealed class AllocationAllowance(string limitMessage)
{
    private AllocationAllowance? _shared;
    private Action<long, long>? _admit;

    public AllocationAllowance WithMemory(Action<long, long> admit) =>
        new(LimitMessage) { _shared = this, _admit = admit };
    private readonly Lock _lock = new();
    private long _remainingWords = 65536;
    private long _remainingCharacters = 16 * 1024 * 1024;

    /// <summary>The diagnostic associated with exhausting this allowance.</summary>
    public string LimitMessage => limitMessage;

    /// <summary>Charges nonnegative counts atomically; rejection consumes neither count.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Either count is negative.</exception>
    public bool TryCharge(long words, long characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(words);
        ArgumentOutOfRangeException.ThrowIfNegative(characters);
        if (_shared != null)
        {
            if (!_shared.TryCharge(words, characters))
                return false;
            _admit?.Invoke(words, characters);
            return true;
        }
        lock (_lock)
        {
            if (words > _remainingWords || characters > _remainingCharacters)
                return false;

            _remainingWords -= words;
            _remainingCharacters -= characters;
            return true;
        }
    }
}

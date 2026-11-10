using System;
using Lokad.Bash.Commands;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Interprets shared allocation accounting as separate brace-generation and word-construction allowances.
/// Neither is refunded; this bounds expansion work, not retained variable or host memory.
/// </summary>
internal sealed class ExpansionBudget
{
    internal ExpansionBudget() : this(new CommandAllocationBudget())
    {
    }

    internal ExpansionBudget(CommandAllocationBudget allocationBudget)
    {
        AllocationBudget = allocationBudget;
    }

    // Standalone evaluators can be stepped without a host. Engine installs the owner
    // before evaluation starts. Hosted shells share AllocationBudget with their caller
    // but use a separate context so their values can be released after execution.
    private IMemoryBudget _memoryBudget = new MemoryGovernor(long.MaxValue);
    internal IMemoryBudget MemoryBudget
    {
        get => _memoryBudget;
        set
        {
            // Engine installs the host owner before execution, including for prepared evaluators.
            _memory?.Finish(retainVariables: false);
            _memory = null;
            _words = null;
            _braces = null;
            _memoryBudget = value;
        }
    }

    private EvaluationMemory? _memory;
    private AllocationAllowance? _words;
    private AllocationAllowance? _braces;
    internal EvaluationMemory Memory => _memory ??= new(MemoryBudget);

    internal ExpansionBudget Fork() => new(AllocationBudget) { MemoryBudget = MemoryBudget };
    internal void FinishMemory(bool retainVariables) => _memory?.Finish(retainVariables);

    internal CommandAllocationBudget AllocationBudget { get; }
    public AllocationAllowance Braces => _braces ??= AllocationBudget
        .GetGenerationAllowance("shell: brace expansion exceeds its word or character limit\n")
        .WithMemory(Memory.ChargeExpansion);
    public AllocationAllowance Words => _words ??= AllocationBudget.Values.WithMemory(Memory.ChargeExpansion);

    internal sealed class LimitExceededException(string message) : Exception(message)
    {
        // Nested expansion boundaries report at the innermost active stderr mapping.
        internal bool IsReported { get; set; }
    }
}

/// <summary>Reports shared-accounting rejection at Bash expansion boundaries.</summary>
internal static class ExpansionBudgetExtensions
{
    internal static void Charge(this AllocationAllowance allowance, long words, long characters)
    {
        if (!allowance.TryCharge(words, characters))
            throw new ExpansionBudget.LimitExceededException(allowance.LimitMessage);
    }
}

using System;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>One evaluator's prepaid scratch space and retained variable/handler estimate.</summary>
/// <remarks>
/// Pending commands keep their scratch while nested commands run. Completed siblings reuse
/// capacity; only current bindings survive the outer command. Saved bindings are charged by their caller.
/// This follows lexical execution without tracking object identities, aliases or GC reachability.
/// Array estimates are cached.
/// </remarks>
internal sealed class EvaluationMemory
{
    private MemoryCapacity _memory;
    private IMemoryBudget _budget;
    private Command? _command;
    private long _retainedBytes;
    private bool _refused;

    public EvaluationMemory(IMemoryBudget budget)
    {
        _budget = budget;
        _memory = new MemoryCapacity(budget, 0);
    }

    /// <summary>The shared allowance owning this evaluator's live storage.</summary>
    public IMemoryBudget Budget
    {
        get => _budget;
        set
        {
            // Engine installs the owner before execution, including prepared evaluations.
            _memory.Dispose();
            _budget = value;
            _memory = new MemoryCapacity(value, 0);
            _retainedBytes = 0;
        }
    }

    public IDisposable BeginCommand(ScopedEvaluation scope)
    {
        if (_command == null)
        {
            _retainedBytes = RetainedBytes(scope);
            _memory.Resize(_retainedBytes);
        }
        var command = new Command(this, scope, _command,
            _memory.Bytes - _retainedBytes);
        _command = command;
        return command;
    }

    public void Charge(long bytes)
    {
        try { _memory.Grow(bytes); }
        catch (MemoryBudgetExceededException) { _refused = true; throw; }
    }

    public Variables CloneVariables(Variables variables)
    {
        // Cover collection copies and saved values through the surrounding command.
        Charge(variables.EstimatedMemoryBytes);
        return variables.Clone();
    }

    public void ReleaseUnusedCapacity() => _memory.ReleaseUnusedCapacity();

    public void Finish(bool retainVariables)
    {
        if (retainVariables)
            _memory.DetachReservation();
        else
            _memory.Dispose();
    }

    private static long RetainedBytes(ScopedEvaluation scope)
    {
        var bytes = checked(scope.Variables.EstimatedMemoryBytes + scope.Effects.RetainedTrapMemoryBytes);
        for (var function = scope.Context.FunctionContext; function != null; function = function.Enclosing)
            bytes = checked(bytes + function.Locals.SavedMemoryBytes);
        return bytes;
    }

    private sealed class Command(EvaluationMemory owner, ScopedEvaluation scope, Command? parent, long baseline) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                var retained = RetainedBytes(scope);
                owner._memory.Resize(checked(baseline + retained));
                if (owner._refused)
                {
                    owner._memory.ReleaseUnusedCapacity();
                    owner._refused = false;
                }
                else
                    owner._memory.TrimExcess();
                owner._retainedBytes = retained;
            }
            finally { owner._command = parent; }
        }
    }
}

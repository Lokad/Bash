using System;
using System.Numerics;
using System.Threading;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

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
    private long? _initialRetainedBytes;
    private bool _refused;

    internal bool HasCommand => _command != null;
    internal MemoryCapacity Workspace => _memory;
    internal CancellationToken CancellationToken => _command?.CancellationToken ?? CancellationToken.None;
    internal Command? ActiveCommand => _command;

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
            _initialRetainedBytes = null;
        }
    }

    internal Command BeginCommand(ScopedEvaluation scope)
    {
        if (_command == null)
        {
            var retained = RetainedBytes(scope);
            _memory.ResizeWithRetainedBaseline(retained, GrowthBaseline(retained));
            _initialRetainedBytes ??= retained;
            _retainedBytes = retained;
        }
        var command = new Command(this, scope, _command,
            _memory.Bytes - _retainedBytes);
        _command = command;
        return command;
    }

    public void Charge(long bytes) => Charge(_memory, bytes);

    internal void Charge(MemoryCapacity owner, long bytes)
    {
        try { owner.Grow(bytes); }
        catch (MemoryBudgetExceededException) { _refused = true; throw; }
    }

    internal void PrepareListCapacity<T>(List<T> list, int additional) => PrepareListCapacity(_memory, list, additional);

    internal void PrepareListCapacity<T>(MemoryCapacity owner, List<T> list, int additional)
    {
        try { owner.PrepareListCapacity(list, additional); }
        catch (MemoryBudgetExceededException) { _refused = true; throw; }
    }

    public void ReleaseUnusedCapacity() => _memory.ReleaseUnusedCapacity();

    /// <summary>Captures the live scratch to keep when an operation abandons its staged result.</summary>
    internal long CaptureScratchBytes(ScopedEvaluation scope) =>
        Math.Max(0, _memory.Bytes - RetainedBytes(scope));

    /// <summary>Refunds abandoned scratch after the operation has restored its live bindings.</summary>
    internal void ReleaseAbandonedWorkspace(ScopedEvaluation scope, long scratchBytes)
    {
        var retained = RetainedBytes(scope);
        var required = checked(retained + scratchBytes);
        if (required <= _memory.Bytes)
            ResizeWorkspace(retained, required);
        ReleaseUnusedCapacity();
    }

    /// <summary>Admits retained storage using scratch capacity released at command completion.</summary>
    internal void PrepareRetainedStorage(long bytes)
    {
        if (_command is { } command)
            command.PrepareRetainedStorage(bytes);
        else
            Charge(bytes);
    }

    public void Finish(bool retainVariables)
    {
        if (retainVariables)
            _memory.DetachReservation();
        else
            _memory.Dispose();
    }

    // Preserve prepaid growth as new bindings settle. Rebase only at increasing
    // size classes beyond the retained storage observed at evaluator entry.
    private long GrowthBaseline(long retained)
    {
        var initial = _initialRetainedBytes ?? retained;
        return retained <= initial
            ? retained
            : initial + (1L << BitOperations.Log2((ulong)(retained - initial)));
    }

    private void ResizeWorkspace(long retained, long bytes)
    {
        _memory.ResizeWithRetainedBaseline(bytes, GrowthBaseline(retained));
        _retainedBytes = retained;
    }

    private static long RetainedBytes(ScopedEvaluation scope)
    {
        var savedBytes = scope.Context.FunctionContext?.Locals.SavedMemoryTracker?.Bytes ?? 0;
        return checked(scope.Variables.EstimatedMemoryBytes + scope.Effects.RetainedTrapMemoryBytes
            + scope.Effects.RetainedFunctionMemoryBytes + scope.Effects.RetainedDescriptorMemoryBytes
            + scope.Effects.RetainedShellStateBytes + savedBytes);
    }

    // Active scopes read this total at command boundaries. Frames enter and leave in stack order,
    // while detached child contexts build their own total.
    internal sealed class SavedLocalMemory
    {
        public long Bytes { get; set; }
    }

    internal sealed class Command(EvaluationMemory owner, ScopedEvaluation scope, Command? parent, long baseline) : IDisposable
    {
        private CaptureDocumentReads? _documentReads;
        internal CancellationToken CancellationToken => scope.Effects.CancellationToken;
        internal ScopedEvaluation Scope => scope;
        internal EvaluationState.GlobIgnorePrefix? PreparedGlobIgnorePrefix { get; set; }
        internal bool ExpandingGlobIgnorePrefix { get; set; }
        internal CaptureDocumentReads DocumentReads => _documentReads ??= new CaptureDocumentReads(owner.Budget);

        internal void PrepareRetainedStorage(long bytes)
        {
            var required = checked(baseline + RetainedBytes(scope) + bytes);
            if (required > owner._memory.Bytes)
                owner.Charge(required - owner._memory.Bytes);
        }

        /// <summary>Reuses the active frame after its temporary results have been consumed.</summary>
        /// <remarks>Nested frames must have finished before resetting their caller.</remarks>
        internal void ResetWorkspace() => ResetWorkspace(0);

        /// <summary>Retires scratch while preserving the charge for a result still owned by this frame.</summary>
        internal void ResetWorkspace(long liveWorkspaceBytes)
        {
            _documentReads?.Dispose();
            _documentReads = null;
            var retained = RetainedBytes(scope);
            owner.ResizeWorkspace(retained, checked(baseline + retained + liveWorkspaceBytes));
            if (owner._refused)
            {
                owner._memory.ReleaseUnusedCapacity();
                owner._refused = false;
            }
            else
                owner._memory.TrimExcess();
        }

        public void Dispose()
        {
            try { ResetWorkspace(); }
            finally { owner._command = parent; }
        }
    }
}

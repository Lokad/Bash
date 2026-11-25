using System;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Tracks Bash redirection scopes, persistent bindings and handles opened by redirections.</summary>
/// <remarks>
/// Keep one mutable value per evaluation; use forks instead of copying this value.
/// Forks start with independent mappings and an empty restoration stack. A terminating pipeline child can
/// also restore saved logical bindings from enclosing shells while those shells retain the handles.
/// Bookkeeping never closes handles; the evaluator's lexical scopes retain cleanup responsibility.
/// </remarks>
internal struct RedirectionState
{
    private readonly Stack<RedirectionFrame> _frames;
    private readonly InheritedExitBindings? _inheritedExitBindings;
    // A lexical restoration boundary belongs to a logical number even when its handle is unchanged.
    private IReadOnlySet<LogicalDescriptor> _changedDescriptors;
    private long _savedAdditionalBytes;
    private DescriptorHandleOwner _openedDescriptors;

    internal readonly long OwnedMemoryBytes => checked(_savedAdditionalBytes + (CurrentTable?.OwnedMemoryBytes ?? 0)
        + _openedDescriptors.OwnedMemoryBytes + 128L * _frames.Count
        + (_frames.EnsureCapacity(0) == 0 ? 0 : 32L + 8L * _frames.EnsureCapacity(0)));

    public DescriptorTable? CurrentTable { get; private set; }
    public readonly IReadOnlyCollection<FileDescriptor> OpenedDescriptors => _openedDescriptors.Handles;
    public readonly int Depth => _frames.Count;

    public RedirectionState(DescriptorTable? inheritedTable)
        : this(inheritedTable, new DescriptorHandleOwner(), inheritedExitBindings: null)
    {
    }

    private RedirectionState(DescriptorTable? table, DescriptorHandleOwner openedDescriptors,
        InheritedExitBindings? inheritedExitBindings)
    {
        _frames = new();
        _changedDescriptors = new HashSet<LogicalDescriptor>();
        _savedAdditionalBytes = 0;
        CurrentTable = table;
        _openedDescriptors = openedDescriptors;
        _inheritedExitBindings = inheritedExitBindings;
    }

    /// <summary>Forks the active mappings without carrying enclosing restoration frames into a subshell.</summary>
    public readonly RedirectionState ForkForSubshell(InheritedExitBindings? inheritedExitBindings) =>
        new(CurrentTable?.Clone(), _openedDescriptors.Capture(), inheritedExitBindings);

    /// <summary>Forks supplied bindings for a substitution or pipeline stage, retaining inherited handle lists.</summary>
    public readonly RedirectionState ForkForNestedEvaluation(DescriptorTable.Snapshot bindings,
        InheritedExitBindings? inheritedExitBindings) =>
        new(bindings.Fork(), _openedDescriptors.Capture(), inheritedExitBindings);

    /// <summary>Captures the reader's descriptor mappings and the standard streams its child must restore.</summary>
    /// <remarks>The parent retains the referenced handles through all stage completions.</remarks>
    internal readonly PipelineExitRedirections? CaptureForPipelineExit(int depth, bool useInherited, EvaluationMemory memory)
    {
        var local = CaptureRestoration(depth, Depth, keepChangedFrames: false, memory);
        var inherited = useInherited ? _inheritedExitBindings : null;
        return local == null && inherited == null ? null
            : new PipelineExitRedirections(local?.Bindings, local?.RestoresInput == true, local?.RestoresOutput == true, inherited);
    }

    /// <summary>Captures the enclosing shell's temporarily rebound logical descriptors.</summary>
    /// <remarks>The parent retains the committed change sets and handles until the child settles.</remarks>
    internal readonly InheritedExitBindings? CaptureInheritedExitBindings(int depth, EvaluationMemory memory)
    {
        var local = CaptureRestoration(depth, Depth, keepChangedFrames: true, memory);
        return local is { } restore
            ? new InheritedExitBindings(restore.Bindings, restore.ChangedFrames, _inheritedExitBindings)
            : _inheritedExitBindings;
    }

    /// <summary>Projects unwound reader mappings onto a temporary copy of the reached table.</summary>
    internal readonly DescriptorTable? CaptureLoadedExitTable(LoadedExitUnwind unwind, EvaluationMemory memory)
    {
        if (CurrentTable is not { } reached || !TryTakeRange(out var outerDepth, out var innerDepth))
            return null;
        var depth = Depth;
        if ((uint)outerDepth > (uint)innerDepth || (uint)innerDepth > (uint)depth)
            throw new InvalidOperationException("The reader's redirection boundaries must be active.");
        DescriptorTable? table = null;
        var changed = _changedDescriptors;
        foreach (var frame in _frames)
        {
            if (depth <= innerDepth && changed.Count != 0 && frame.Table is { } previous)
            {
                table ??= reached.Clone();
                foreach (var descriptor in changed)
                    table.Set(descriptor, previous.ResolveBinding(descriptor), memory);
            }
            if (--depth == outerDepth && !TryTakeRange(out outerDepth, out innerDepth))
                break;
            changed = frame.Changed;
        }
        return table;

        bool TryTakeRange(out int start, out int end)
        {
            while (unwind.TryTake(out var outer, out var inner))
            {
                start = outer.RedirectionDepth;
                end = inner.RedirectionDepth;
                if (start != end)
                    return true;
            }
            start = end = 0;
            return false;
        }
    }

    private readonly CapturedRedirections? CaptureRestoration(int depth, int endDepth, bool keepChangedFrames, EvaluationMemory memory)
    {
        if ((uint)depth > (uint)endDepth || (uint)endDepth > (uint)Depth)
            throw new InvalidOperationException("The reader's redirection boundaries must be active.");
        var remaining = endDepth - depth;
        if (remaining == 0)
            return null;

        var newer = _frames.Count - endDepth;
        var changed = _changedDescriptors;
        var restoresMappings = false;
        var restoresInput = false;
        var restoresOutput = false;
        IReadOnlySet<LogicalDescriptor>[]? changedFrames = null;
        var count = 0;
        foreach (var frame in _frames)
        {
            if (newer > 0)
            {
                newer--;
                changed = frame.Changed;
                continue;
            }
            restoresMappings |= changed.Count != 0;
            restoresInput |= changed.Contains(LogicalDescriptor.StdIn);
            restoresOutput |= changed.Contains(LogicalDescriptor.StdOut);
            if (keepChangedFrames && changed.Count != 0)
            {
                if (changedFrames == null)
                {
                    memory.Charge(MemoryCapacity.ArrayBackingBytes<IReadOnlySet<LogicalDescriptor>>(remaining));
                    changedFrames = new IReadOnlySet<LogicalDescriptor>[remaining];
                }
                changedFrames[count++] = changed;
            }
            if (--remaining == 0)
                return restoresMappings && frame.Table is { } table
                    ? new CapturedRedirections(table.Capture(), restoresInput, restoresOutput,
                        changedFrames == null ? default : new ArraySegment<IReadOnlySet<LogicalDescriptor>>(changedFrames, 0, count))
                    : null;
            changed = frame.Changed;
        }
        throw new InvalidOperationException("The reader's redirection boundary must be active.");
    }

    /// <summary>Restores borrowed bindings at child completion while retaining the child's handle ownership.</summary>
    internal void RestoreForPipelineExit(PipelineExitRedirections redirections, EvaluationMemory memory)
    {
        if (_frames.Count != 0 || CurrentTable == null)
            throw new InvalidOperationException("Pipeline EXIT requires the child shell's base mappings.");
        CurrentTable = redirections.Restore(CurrentTable, memory);
    }

    private readonly record struct CapturedRedirections(DescriptorTable.Snapshot Bindings, bool RestoresInput, bool RestoresOutput,
        ArraySegment<IReadOnlySet<LogicalDescriptor>> ChangedFrames);

    internal sealed class PipelineExitRedirections(DescriptorTable.Snapshot? local, bool restoresInput, bool restoresOutput,
        InheritedExitBindings? inherited)
    {
        internal DescriptorTable Restore(DescriptorTable active, EvaluationMemory memory)
        {
            var restored = local is { } bindings ? bindings.WithStandardStreams(
                restoresInput ? bindings.ResolveBinding(LogicalDescriptor.StdIn) : active.ResolveBinding(LogicalDescriptor.StdIn),
                restoresOutput ? bindings.ResolveBinding(LogicalDescriptor.StdOut) : active.ResolveBinding(LogicalDescriptor.StdOut),
                bindings.ResolveBinding(LogicalDescriptor.StdErr)).Fork() : active.Clone();
            for (var frame = inherited; frame != null; frame = frame.Enclosing)
                frame.Restore(restored, memory);
            return restored;
        }
    }

    internal sealed class InheritedExitBindings(DescriptorTable.Snapshot bindings,
        ArraySegment<IReadOnlySet<LogicalDescriptor>> changedFrames, InheritedExitBindings? enclosing)
    {
        internal InheritedExitBindings? Enclosing { get; } = enclosing;

        internal void Restore(DescriptorTable active, EvaluationMemory memory) =>
            RestoreChangedDescriptors(active, bindings, changedFrames, memory);
    }

    private static void RestoreChangedDescriptors(DescriptorTable active, DescriptorTable.Snapshot bindings,
        ArraySegment<IReadOnlySet<LogicalDescriptor>> changedFrames, EvaluationMemory memory)
    {
        foreach (var changed in changedFrames)
            foreach (var descriptor in changed)
                active.Set(descriptor, bindings.ResolveBinding(descriptor), memory);
    }

    /// <summary>
    /// Borrows active redirection handles in scope order, including repeated aliases.
    /// </summary>
    public readonly IEnumerable<FileDescriptor> EnumerateHandles()
    {
        foreach (var descriptor in OpenedDescriptors)
            yield return descriptor;
        foreach (var entry in _frames)
            foreach (var descriptor in entry.Opened.Handles)
                yield return descriptor;
    }

    public void Push(
        DescriptorTable table,
        DescriptorHandleOwner openedDescriptors,
        IReadOnlySet<LogicalDescriptor> changedDescriptors,
        EvaluationMemory? memory)
    {
        var capacity = _frames.EnsureCapacity(0);
        var target = _frames.Count == capacity ? (int)Math.Min(int.MaxValue, Math.Max(4L, 2L * capacity)) : capacity;
        memory?.Charge(checked(128L + (target == capacity ? 0 : 32L + 8L * target)));
        if (target != capacity)
            _frames.EnsureCapacity(target);
        _savedAdditionalBytes = checked(_savedAdditionalBytes + (CurrentTable?.OwnedMemoryBytes ?? 0)
            + _openedDescriptors.OwnedMemoryBytes);
        _frames.Push(new RedirectionFrame(
            CurrentTable,
            _openedDescriptors,
            _changedDescriptors));
        CurrentTable = table;
        _openedDescriptors = openedDescriptors;
        _changedDescriptors = changedDescriptors;
    }

    public IReadOnlyCollection<FileDescriptor> Pop()
    {
        var openedDescriptors = OpenedDescriptors;
        if (_frames.Count == 0)
        {
            CurrentTable = null;
            _openedDescriptors = new DescriptorHandleOwner();
            _changedDescriptors = new HashSet<LogicalDescriptor>();
            return openedDescriptors;
        }

        var previous = _frames.Pop();
        _savedAdditionalBytes -= (previous.Table?.OwnedMemoryBytes ?? 0) + previous.Opened.OwnedMemoryBytes;
        CurrentTable = previous.Table;
        _openedDescriptors = previous.Opened;
        _changedDescriptors = previous.Changed;
        return openedDescriptors;
    }

    /// <summary>
    /// Commits the current scope through enclosing scopes until each logical descriptor reaches
    /// the scope that temporarily owns it, then removes the committed scope.
    /// </summary>
    public IReadOnlyCollection<FileDescriptor> Preserve(
        EvaluationMemory? memory, OrderedDescriptorSet? pending, Stack<HashSet<FileDescriptor>>? borrowed)
    {
        var committedTable = CurrentTable
            ?? throw new InvalidOperationException("A redirection table is required for preservation.");
        var changedDescriptors = _changedDescriptors;
        foreach (var logicalDescriptor in changedDescriptors)
        {
            var binding = committedTable.ResolveBinding(logicalDescriptor);
            var handle = binding.Handle;
            var acquired = pending?.Contains(handle) ?? OwnsHandle(handle);
            if (acquired && borrowed != null)
                foreach (var scope in borrowed)
                    if (scope.Contains(handle)) { acquired = false; break; }
            SetPersistentDescriptor(logicalDescriptor, binding, memory, includeCurrent: false,
                acquired);
        }

        // Admission can refuse while committing a later binding. Keep this scope
        // active until then, so its ordinary close/transfer path handles earlier commits.
        return Pop();
    }

    /// <summary>Persists a binding through enclosing scopes up to its temporary restoration boundary.</summary>
    public void SetPersistentDescriptor(LogicalDescriptor descriptor, DescriptorBinding binding, EvaluationMemory? memory, bool acquired) =>
        SetPersistentDescriptor(descriptor, binding, memory, includeCurrent: true, acquired);

    private void SetPersistentDescriptor(
        LogicalDescriptor descriptor, DescriptorBinding binding, EvaluationMemory? memory, bool includeCurrent, bool acquired)
    {
        var handle = binding.Handle;
        var current = CurrentTable;
        var frames = _frames;
        var currentChanged = _changedDescriptors;
        var currentOpened = _openedDescriptors;
        DescriptorHandleOwner? longestOwner = null;
        foreach (var target in Targets())
        {
            var table = target.Table;
            var before = table.OwnedMemoryBytes;
            try { table.PrepareSet(descriptor, binding, memory?.Workspace); }
            finally
            {
                if (!ReferenceEquals(table, current))
                    _savedAdditionalBytes = checked(_savedAdditionalBytes + table.OwnedMemoryBytes - before);
            }
            var old = table.Resolve(descriptor);
            if (old != handle && target.Opened.Contains(old))
                PrepareHandles(target.Opened, 0, memory, publication: false);
            longestOwner = target.Opened;
        }
        var publishOwnership = acquired && !handle.IsClosed && longestOwner != null
            && !longestOwner.Contains(handle) && !HasLongerOwner(longestOwner, handle);
        if (publishOwnership && longestOwner != null)
            PrepareHandles(longestOwner, 1, memory, publication: true);
        // Prepare every receiving table before publishing the binding anywhere.
        // Refusal after acquiring a handle must not leave a partially published alias.
        foreach (var target in Targets())
        {
            var before = target.Table.OwnedMemoryBytes;
            target.Table.Set(descriptor, binding);
            if (!ReferenceEquals(target.Table, current))
                _savedAdditionalBytes = checked(_savedAdditionalBytes + target.Table.OwnedMemoryBytes - before);
        }
        if (publishOwnership && longestOwner != null)
            longestOwner.PublishPrepared(handle);

        IEnumerable<(DescriptorTable Table, DescriptorHandleOwner Opened)> Targets()
        {
            if (includeCurrent)
            {
                if (current != null)
                    yield return (current, currentOpened);
                if (currentChanged.Contains(descriptor))
                    yield break;
            }
            foreach (var frame in frames)
            {
                if (frame.Table != null)
                    yield return (frame.Table, frame.Opened);
                if (frame.Changed.Contains(descriptor))
                    yield break;
            }
        }
    }

    /// <summary>
    /// Transfers handles to their already prepared longest-lived owner.
    /// Returns the borrowed, preadmitted closing batch in reverse open order.
    /// The caller consumes it before another batch or ownership mutation.
    /// </summary>
    public IReadOnlyCollection<FileDescriptor> TransferHandles(
        IReadOnlyCollection<FileDescriptor> descriptors, EvaluationMemory? memory, OrderedDescriptorSet closing)
    {
        closing.Clear();
        if (descriptors is IReadOnlyList<FileDescriptor> indexed)
        {
            for (var index = 0; index < indexed.Count; index++)
                TransferHandle(indexed[index], memory, closing);
        }
        else
            foreach (var descriptor in descriptors)
                TransferHandle(descriptor, memory, closing);
        closing.Reverse();
        return closing;
    }

    private void TransferHandle(FileDescriptor descriptor, EvaluationMemory? memory, OrderedDescriptorSet closing)
    {
        DescriptorHandleOwner? owner = null;
        // Stack enumeration runs from newest to oldest: the last match lives longest.
        foreach (var frame in _frames)
            if (frame.Table?.References(descriptor) == true)
                owner = frame.Opened;
        if (owner == null && CurrentTable?.References(descriptor) == true)
            owner = _openedDescriptors;
        if (owner != null)
        {
            if (owner.Contains(descriptor))
            {
                SettleHandle(owner, descriptor, discard: false);
                return;
            }
            if (memory == null)
                PrepareHandles(owner, 1, null, publication: false);
            owner.AddPrepared(descriptor);
        }
        else
        {
            SettleHandle(_openedDescriptors, descriptor, discard: true);
            foreach (var frame in _frames)
                SettleHandle(frame.Opened, descriptor, discard: true);
            closing.AddPrepared(descriptor);
        }
    }

    /// <summary>
    /// Returns whether any active descriptor mapping still references the resolved descriptor.
    /// </summary>
    public readonly bool References(FileDescriptor descriptor)
    {
        if (CurrentTable?.References(descriptor) == true)
            return true;

        return _frames.Any(frame => frame.Table?.References(descriptor) == true);
    }

    /// <summary>
    /// Removes an owned descriptor from active redirection lifetime tracking.
    /// </summary>
    public bool TryTakeHandle(FileDescriptor descriptor, EvaluationMemory? memory)
    {
        var taken = TakeHandle(_openedDescriptors, descriptor, memory);
        foreach (var frame in _frames)
            taken |= TakeHandle(frame.Opened, descriptor, memory);
        return taken;
    }

    private bool TakeHandle(DescriptorHandleOwner owner, FileDescriptor descriptor, EvaluationMemory? memory)
    {
        if (!owner.Contains(descriptor))
            return false;
        if (memory == null)
            PrepareHandles(owner, 0, null, publication: false);
        return owner.RemovePrepared(descriptor);
    }

    private readonly bool OwnsHandle(FileDescriptor handle) => _openedDescriptors.Contains(handle)
        || _frames.Any(frame => frame.Opened.Contains(handle));

    /// <summary>Retires handle indexes after final cleanup, preserving retained logical mappings.</summary>
    public void RetireHandles()
    {
        long saved = 0;
        foreach (var frame in _frames)
            saved = checked(saved + frame.Opened.OwnedMemoryBytes);
        _savedAdditionalBytes -= saved;
        foreach (var frame in _frames)
            frame.Opened.Retire();
        _openedDescriptors.Retire();
    }

    private readonly bool HasLongerOwner(DescriptorHandleOwner receiving, FileDescriptor descriptor)
    {
        var reached = ReferenceEquals(receiving, _openedDescriptors);
        foreach (var frame in _frames)
        {
            if (ReferenceEquals(frame.Opened, receiving)) reached = true;
            if (reached && frame.Opened.Contains(descriptor)) return true;
        }
        return false;
    }

    private void SettleHandle(DescriptorHandleOwner owner, FileDescriptor descriptor, bool discard)
    {
        var before = owner.OwnedMemoryBytes;
        if (discard) owner.DiscardUnsettled(descriptor);
        else owner.SettlePrepared(descriptor);
        if (!ReferenceEquals(owner, _openedDescriptors))
            _savedAdditionalBytes = checked(_savedAdditionalBytes + owner.OwnedMemoryBytes - before);
    }

    private void PrepareHandles(DescriptorHandleOwner owner, int additional, EvaluationMemory? memory, bool publication)
    {
        var before = owner.OwnedMemoryBytes;
        try
        {
            if (publication) owner.PreparePublication(memory?.Workspace);
            else owner.PrepareAdditional(additional, memory?.Workspace);
        }
        finally
        {
            if (!ReferenceEquals(owner, _openedDescriptors))
                _savedAdditionalBytes = checked(_savedAdditionalBytes + owner.OwnedMemoryBytes - before);
        }
    }

    private sealed class RedirectionFrame(
        DescriptorTable? table,
        DescriptorHandleOwner opened,
        IReadOnlySet<LogicalDescriptor> changed)
    {
        public DescriptorTable? Table { get; } = table;

        public DescriptorHandleOwner Opened { get; } = opened;

        public IReadOnlySet<LogicalDescriptor> Changed { get; } = changed;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>Tracks Bash redirection scopes, persistent bindings and handles opened by redirections.</summary>
/// <remarks>
/// Keep one mutable value per evaluation; use forks instead of copying this value.
/// Forks start with independent mappings and an empty restoration stack, retaining inherited handle lists.
/// Bookkeeping never closes handles; the evaluator's lexical scopes retain cleanup responsibility.
/// </remarks>
internal struct RedirectionState
{
    private readonly Stack<RedirectionFrame> _frames;
    private IReadOnlySet<LogicalDescriptor> _changedDescriptors;
    private long _savedAdditionalBytes;
    private DescriptorHandleOwner _openedDescriptors;

    internal readonly long OwnedMemoryBytes => checked(_savedAdditionalBytes + (CurrentTable?.OwnedMemoryBytes ?? 0)
        + _openedDescriptors.OwnedMemoryBytes + 128L * _frames.Count
        + (_frames.EnsureCapacity(0) == 0 ? 0 : 32L + 8L * _frames.EnsureCapacity(0)));

    public DescriptorTable? CurrentTable { get; private set; }
    public readonly IReadOnlyCollection<FileDescriptor> OpenedDescriptors => _openedDescriptors.Handles;

    public RedirectionState(DescriptorTable? inheritedTable)
        : this(inheritedTable, new DescriptorHandleOwner())
    {
    }

    private RedirectionState(DescriptorTable? table, DescriptorHandleOwner openedDescriptors)
    {
        _frames = new();
        _changedDescriptors = new HashSet<LogicalDescriptor>();
        _savedAdditionalBytes = 0;
        CurrentTable = table;
        _openedDescriptors = openedDescriptors;
    }

    /// <summary>Forks the active mappings without carrying enclosing restoration frames into a subshell.</summary>
    public readonly RedirectionState ForkForSubshell() => new(CurrentTable?.Clone(), _openedDescriptors.Capture());

    /// <summary>Forks supplied bindings for a substitution or pipeline stage, retaining inherited handle lists.</summary>
    public readonly RedirectionState ForkForNestedEvaluation(DescriptorTable.Snapshot bindings) =>
        new(bindings.Fork(), _openedDescriptors.Capture());

    /// <summary>
    /// Appends active redirection handles, excluding handles supplied by the caller.
    /// </summary>
    public readonly void AddHandlesTo(
        ref DescriptorHandleCollector handles,
        IReadOnlyCollection<FileDescriptor> excludedHandles)
    {
        var excluded = excludedHandles.Count == 0 ? null : new HashSet<FileDescriptor>(excludedHandles);

        AddHandles(OpenedDescriptors, excluded, ref handles);
        foreach (var entry in _frames)
            AddHandles(entry.Opened.Handles, excluded, ref handles);

        static void AddHandles(
            IReadOnlyCollection<FileDescriptor> source,
            HashSet<FileDescriptor>? excludedIndex,
            ref DescriptorHandleCollector target)
        {
            foreach (var descriptor in source)
            {
                if (excludedIndex?.Contains(descriptor) == true)
                    continue;

                target.Add(descriptor);
            }
        }
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

    /// <summary>Completes setup without reinstalling the already active lexical lifetime.</summary>
    public void CompleteSetup(IReadOnlySet<LogicalDescriptor> changedDescriptors) =>
        _changedDescriptors = changedDescriptors;

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
            var handle = committedTable.Resolve(logicalDescriptor);
            var acquired = pending?.Contains(handle) ?? OwnsHandle(handle);
            if (acquired && borrowed != null)
                foreach (var scope in borrowed)
                    if (scope.Contains(handle)) { acquired = false; break; }
            SetPersistentDescriptor(logicalDescriptor, handle, memory, includeCurrent: false,
                acquired);
        }

        // Admission can refuse while committing a later binding. Keep this scope
        // active until then, so its ordinary close/transfer path handles earlier commits.
        return Pop();
    }

    /// <summary>Persists a binding through enclosing scopes up to its temporary restoration boundary.</summary>
    public void SetPersistentDescriptor(LogicalDescriptor descriptor, FileDescriptor handle, EvaluationMemory? memory, bool acquired) =>
        SetPersistentDescriptor(descriptor, handle, memory, includeCurrent: true, acquired);

    private void SetPersistentDescriptor(
        LogicalDescriptor descriptor, FileDescriptor handle, EvaluationMemory? memory, bool includeCurrent, bool acquired)
    {
        var current = CurrentTable;
        var frames = _frames;
        var currentChanged = _changedDescriptors;
        var currentOpened = _openedDescriptors;
        DescriptorHandleOwner? longestOwner = null;
        foreach (var target in Targets())
        {
            var table = target.Table;
            var before = table.OwnedMemoryBytes;
            try { table.PrepareSet(descriptor, handle, memory?.Workspace); }
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
            target.Table.Set(descriptor, handle);
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
    /// Returns unreferenced handles in reverse open order.
    /// </summary>
    public IReadOnlyList<FileDescriptor> TransferHandles(IReadOnlyCollection<FileDescriptor> descriptors, EvaluationMemory? memory)
    {
        List<FileDescriptor>? closing = null;
        if (descriptors is IReadOnlyList<FileDescriptor> indexed)
        {
            for (var index = 0; index < indexed.Count; index++)
                TransferHandle(indexed[index], memory, ref closing);
        }
        else
            foreach (var descriptor in descriptors)
                TransferHandle(descriptor, memory, ref closing);
        closing?.Reverse();
        return closing ?? (IReadOnlyList<FileDescriptor>)[];
    }

    private void TransferHandle(FileDescriptor descriptor, EvaluationMemory? memory, ref List<FileDescriptor>? closing)
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
            (closing ??= new List<FileDescriptor>()).Add(descriptor);
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

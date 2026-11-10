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

    public DescriptorTable? CurrentTable { get; private set; }
    public IReadOnlyList<FileDescriptor> OpenedDescriptors { get; private set; }

    public RedirectionState(DescriptorTable? inheritedTable)
        : this(inheritedTable, [])
    {
    }

    private RedirectionState(DescriptorTable? table, IReadOnlyList<FileDescriptor> openedDescriptors)
    {
        _frames = new();
        _changedDescriptors = new HashSet<LogicalDescriptor>();
        CurrentTable = table;
        OpenedDescriptors = openedDescriptors;
    }

    /// <summary>Forks the active mappings without carrying enclosing restoration frames into a subshell.</summary>
    public readonly RedirectionState ForkForSubshell() => new(CurrentTable?.Clone(), OpenedDescriptors);

    /// <summary>Forks supplied bindings for a substitution or pipeline stage, retaining inherited handle lists.</summary>
    public readonly RedirectionState ForkForNestedEvaluation(DescriptorTable.Snapshot bindings) =>
        new(bindings.Fork(), OpenedDescriptors);

    /// <summary>
    /// Appends active redirection handles, excluding handles supplied by the caller.
    /// </summary>
    public readonly void AddHandlesTo(
        ref DescriptorHandleCollector handles,
        IReadOnlyList<FileDescriptor> excludedHandles)
    {
        var excluded = excludedHandles.Count == 0 ? null : new HashSet<FileDescriptor>(excludedHandles);

        AddHandles(OpenedDescriptors, excluded, ref handles);
        foreach (var entry in _frames)
            AddHandles(entry.Opened, excluded, ref handles);

        static void AddHandles(
            IReadOnlyList<FileDescriptor> source,
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
        IReadOnlyList<FileDescriptor> openedDescriptors,
        IReadOnlySet<LogicalDescriptor> changedDescriptors)
    {
        _frames.Push(new RedirectionFrame(
            CurrentTable,
            OpenedDescriptors,
            _changedDescriptors));
        CurrentTable = table;
        OpenedDescriptors = openedDescriptors;
        _changedDescriptors = changedDescriptors;
    }

    public IReadOnlyList<FileDescriptor> Pop()
    {
        var openedDescriptors = OpenedDescriptors;
        if (_frames.Count == 0)
        {
            CurrentTable = null;
            OpenedDescriptors = [];
            _changedDescriptors = new HashSet<LogicalDescriptor>();
            return openedDescriptors;
        }

        var previous = _frames.Pop();
        CurrentTable = previous.Table;
        OpenedDescriptors = previous.Opened;
        _changedDescriptors = previous.Changed;
        return openedDescriptors;
    }

    /// <summary>
    /// Commits the current scope through enclosing scopes until each logical descriptor reaches
    /// the scope that temporarily owns it, then removes the committed scope.
    /// </summary>
    public IReadOnlyList<FileDescriptor> Preserve()
    {
        var committedTable = CurrentTable
            ?? throw new InvalidOperationException("A redirection table is required for preservation.");
        var changedDescriptors = _changedDescriptors;
        var openedDescriptors = Pop();

        foreach (var logicalDescriptor in changedDescriptors)
            SetPersistentDescriptor(logicalDescriptor, committedTable.Resolve(logicalDescriptor));

        return openedDescriptors;
    }

    /// <summary>Persists a binding through enclosing scopes up to its temporary restoration boundary.</summary>
    public void SetPersistentDescriptor(LogicalDescriptor descriptor, FileDescriptor handle)
    {
        CurrentTable?.Set(descriptor, handle);
        if (_changedDescriptors.Contains(descriptor))
            return;

        foreach (var frame in _frames)
        {
            frame.Table?.Set(descriptor, handle);
            if (frame.Changed.Contains(descriptor))
                break;
        }
    }

    /// <summary>
    /// Transfers handles to their longest-lived referencing frame, copying each owner's list once.
    /// Returns unreferenced handles in reverse open order.
    /// </summary>
    public IReadOnlyList<FileDescriptor> TransferHandles(IReadOnlyList<FileDescriptor> descriptors)
    {
        Dictionary<RedirectionFrame, DescriptorHandleCollector>? owners = null;
        DescriptorHandleCollector? current = null;
        List<FileDescriptor>? closing = null;
        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            RedirectionFrame? owner = null;
            // Stack enumeration runs from newest to oldest: the last match lives longest.
            foreach (var frame in _frames)
            {
                if (frame.Table?.References(descriptor) == true)
                    owner = frame;
            }

            if (owner != null)
            {
                if (owner.Opened.Contains(descriptor))
                    continue;
                owners ??= new Dictionary<RedirectionFrame, DescriptorHandleCollector>();
                if (!owners.TryGetValue(owner, out var collector))
                    collector = CopyOwned(owner.Opened);
                collector.Add(descriptor);
                owners[owner] = collector;
            }
            else if (CurrentTable?.References(descriptor) == true)
            {
                if (OpenedDescriptors.Contains(descriptor))
                    continue;
                var collector = current ?? CopyOwned(OpenedDescriptors);
                collector.Add(descriptor);
                current = collector;
            }
            else
            {
                (closing ??= new List<FileDescriptor>()).Add(descriptor);
            }
        }

        if (owners != null)
        {
            foreach (var pair in owners)
                pair.Key.Opened = pair.Value.Descriptors;
        }
        if (current is { } currentOwner)
            OpenedDescriptors = currentOwner.Descriptors;
        closing?.Reverse();
        return closing ?? (IReadOnlyList<FileDescriptor>)[];

        DescriptorHandleCollector CopyOwned(IReadOnlyList<FileDescriptor> owned)
        {
            var collector = new DescriptorHandleCollector(new List<FileDescriptor>(owned.Count + Math.Min(descriptors.Count, 8)));
            collector.AddRange(owned);
            return collector;
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
    public bool TryTakeHandle(FileDescriptor descriptor)
    {
        var taken = false;
        OpenedDescriptors = RemoveDescriptor(OpenedDescriptors, descriptor, ref taken);
        foreach (var frame in _frames)
            frame.Opened = RemoveDescriptor(frame.Opened, descriptor, ref taken);

        return taken;

        static IReadOnlyList<FileDescriptor> RemoveDescriptor(
            IReadOnlyList<FileDescriptor> descriptors, FileDescriptor removed, ref bool taken)
        {
            var retainedCount = 0;
            for (var index = 0; index < descriptors.Count; index++)
            {
                if (descriptors[index] != removed)
                    retainedCount++;
            }
            if (retainedCount == descriptors.Count)
                return descriptors;

            taken = true;
            if (retainedCount == 0)
                return [];

            var retained = new FileDescriptor[retainedCount];
            var next = 0;
            for (var index = 0; index < descriptors.Count; index++)
            {
                if (descriptors[index] != removed)
                    retained[next++] = descriptors[index];
            }
            return retained;
        }
    }

    private sealed class RedirectionFrame(
        DescriptorTable? table,
        IReadOnlyList<FileDescriptor> opened,
        IReadOnlySet<LogicalDescriptor> changed)
    {
        public DescriptorTable? Table { get; } = table;

        public IReadOnlyList<FileDescriptor> Opened { get; set; } = opened;

        public IReadOnlySet<LogicalDescriptor> Changed { get; } = changed;
    }
}

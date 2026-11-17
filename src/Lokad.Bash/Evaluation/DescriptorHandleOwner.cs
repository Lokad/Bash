using System;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>One redirection lifetime's handle index, with admitted detachment after a borrow.</summary>
/// <remarks>One evaluator mutates this owner. Forks capture storage and never mutate their source.</remarks>
internal sealed class DescriptorHandleOwner
{
    private IReadOnlyCollection<FileDescriptor> _handles;
    private OrderedDescriptorSet? _owned;
    private HashSet<FileDescriptor>? _unsettled;
    private bool _shared;

    public DescriptorHandleOwner()
    {
        _owned = new OrderedDescriptorSet();
        _handles = _owned;
    }

    public DescriptorHandleOwner(IReadOnlyCollection<FileDescriptor> borrowed) => _handles = borrowed;

    public IReadOnlyCollection<FileDescriptor> Handles => _handles;
    public long OwnedMemoryBytes => checked((_owned?.EstimatedMemoryBytes ?? 0)
        + (_unsettled is { } pending && pending.EnsureCapacity(0) > 0 ? 128L + 16L * pending.EnsureCapacity(0) : 0));

    public bool Contains(FileDescriptor descriptor) => _handles is OrderedDescriptorSet indexed
        ? indexed.Contains(descriptor) : _handles.Contains(descriptor);

    public DescriptorHandleOwner Capture()
    {
        if (_unsettled?.Count > 0)
            throw new InvalidOperationException("Unsettled descriptor ownership cannot be borrowed.");
        _shared = true;
        return new DescriptorHandleOwner(_handles);
    }

    /// <summary>Prepares publication/removal before changing a persistent mapping.</summary>
    public void PrepareAdditional(int additional, MemoryCapacity? memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additional);
        if (_owned != null && !_shared)
        {
            _owned.PrepareAdditional(additional, memory);
            return;
        }
        if (additional > int.MaxValue - _handles.Count)
            throw new ByteMemoryBuilder.LimitExceededException();
        var count = _handles.Count + additional;
        if (count == 0)
            return;
        var replacement = new OrderedDescriptorSet();
        replacement.PrepareAdditional(count, memory);
        foreach (var descriptor in _handles)
            replacement.AddPrepared(descriptor);
        _owned = replacement;
        _handles = replacement;
        _shared = false;
    }

    public void AddPrepared(FileDescriptor descriptor)
    {
        if (Contains(descriptor))
            return;
        if (_owned == null || _shared)
            throw new InvalidOperationException("Descriptor ownership was not prepared.");
        _owned.AddPrepared(descriptor);
    }

    /// <summary>Admits membership and ordering metadata before a persistent binding is published.</summary>
    public void PreparePublication(MemoryCapacity? memory)
    {
        PrepareAdditional(1, memory);
        var pending = _unsettled ??= new HashSet<FileDescriptor>();
        var capacity = pending.EnsureCapacity(0);
        if (pending.Count < capacity)
            return;
        var target = (int)Math.Min(int.MaxValue, Math.Max(4L, 2L * capacity));
        memory?.Grow(checked(128L + 32L * (2L * target + 1)));
        pending.EnsureCapacity(target);
    }

    public void PublishPrepared(FileDescriptor descriptor)
    {
        if (Contains(descriptor))
            return;
        if (_unsettled == null || _unsettled.Count >= _unsettled.EnsureCapacity(0))
            throw new InvalidOperationException("Descriptor publication was not prepared.");
        AddPrepared(descriptor);
        _unsettled.Add(descriptor);
    }

    /// <summary>Settles encounter order in the transferring scope's order, leaving existing ownership in place.</summary>
    public void SettlePrepared(FileDescriptor descriptor)
    {
        if (_unsettled?.Contains(descriptor) != true)
            return;
        if (_owned == null || _shared)
            throw new InvalidOperationException("Descriptor order was not prepared.");
        _unsettled.Remove(descriptor);
        _owned.Remove(descriptor);
        _owned.AddPrepared(descriptor);
        RetirePublicationBatch();
    }

    public void DiscardUnsettled(FileDescriptor descriptor)
    {
        if (_unsettled?.Contains(descriptor) != true)
            return;
        if (_owned == null || _shared)
            throw new InvalidOperationException("Descriptor discard was not prepared.");
        _unsettled.Remove(descriptor);
        _owned.Remove(descriptor);
        RetirePublicationBatch();
    }

    private void RetirePublicationBatch()
    {
        // Reuse a small marker index; a completed wide batch no longer owns its large scratch.
        if (_unsettled is { Count: 0 } pending && pending.EnsureCapacity(0) > 64)
            _unsettled = null;
    }

    public bool RemovePrepared(FileDescriptor descriptor)
    {
        if (!Contains(descriptor))
            return false;
        if (_owned == null || _shared)
            throw new InvalidOperationException("Descriptor ownership removal was not prepared.");
        return _owned.Remove(descriptor);
    }

    /// <summary>Drops this owner's storage after all handle users and close attempts have settled.</summary>
    public void Retire()
    {
        _handles = Array.Empty<FileDescriptor>();
        _owned = null;
        _unsettled = null;
        _shared = false;
    }
}

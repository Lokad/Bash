using System.Collections.Generic;
using System.Collections.Immutable;

namespace Lokad.Bash;

/// <summary>Immutable indexed slots preserve descriptor encounter order across shared snapshots.</summary>
internal sealed class DescriptorMap
{
    internal static readonly DescriptorMap Empty = new(
        ImmutableDictionary<LogicalDescriptor, int>.Empty, ImmutableList<Binding>.Empty,
        ImmutableDictionary<FileDescriptor, int>.Empty, -1);

    private readonly ImmutableDictionary<LogicalDescriptor, int> _positions;
    private readonly ImmutableList<Binding> _slots;
    private readonly ImmutableDictionary<FileDescriptor, int> _references;
    private readonly int _firstFree;

    private DescriptorMap(ImmutableDictionary<LogicalDescriptor, int> positions,
        ImmutableList<Binding> slots, ImmutableDictionary<FileDescriptor, int> references, int firstFree)
    {
        _positions = positions;
        _slots = slots;
        _references = references;
        _firstFree = firstFree;
    }

    internal int SlotCount => _slots.Count;

    // Include all three trees, bounded reference deltas/live keys, metadata
    // and replacement generations. Capture consumes already admitted headroom.
    internal static long EstimateMemoryBytes(int slots) => checked(1024L + 512L * slots);

    internal int RequiredSlots(bool present) =>
        RequiredSlots(SlotCount, _positions.Count, _firstFree, present, captured: true);

    internal bool TryGetValue(LogicalDescriptor descriptor, out DescriptorBinding binding) =>
        TryGetValue(_positions, _slots, descriptor, out binding);

    internal bool ContainsKey(LogicalDescriptor descriptor) => _positions.ContainsKey(descriptor);

    internal bool ContainsValue(FileDescriptor handle) => _references.ContainsKey(handle);

    private static int RequiredSlots(int slotCount, int liveCount, int firstFree, bool present, bool captured)
    {
        var slots = captured && slotCount - liveCount > liveCount ? liveCount : slotCount;
        return present || (!captured && firstFree >= 0) ? slots : checked(slots + 1);
    }

    private static bool TryGetValue(IReadOnlyDictionary<LogicalDescriptor, int> positions,
        IReadOnlyList<Binding> slots, LogicalDescriptor descriptor, out DescriptorBinding binding)
    {
        if (positions.TryGetValue(descriptor, out var position))
        {
            binding = slots[position].Value;
            return true;
        }
        binding = DescriptorBinding.Closed;
        return false;
    }

    internal void AddReferencedHandlesTo(ref Helpers.DescriptorHandleCollector handles)
    {
        foreach (var binding in _slots)
            if (binding.IsPresent)
                handles.Add(binding.Handle);
    }

    /// <summary>One writer reuses unfrozen nodes; capture freezes only paths changed since the preceding capture.</summary>
    internal sealed class Builder
    {
        private ImmutableDictionary<LogicalDescriptor, int>.Builder _positions;
        private ImmutableList<Binding>.Builder _slots;
        private readonly ImmutableDictionary<FileDescriptor, int>.Builder _references;
        private Dictionary<FileDescriptor, (int Count, int Position)> _referenceChanges = new(3);
        private List<FileDescriptor> _changedHandles = new(3);
        private int _firstFree;
        private bool _captured = true;
        private DescriptorMap? _snapshot;

        internal Builder(DescriptorMap source)
        {
            _positions = source._positions.ToBuilder();
            _slots = source._slots.ToBuilder();
            _references = source._references.ToBuilder();
            _firstFree = source._firstFree;
            _snapshot = source;
        }

        internal int SlotCount => _slots.Count;

        internal int RequiredSlots(bool present) =>
            DescriptorMap.RequiredSlots(SlotCount, _positions.Count, _firstFree, present, _captured);

        internal bool TryGetValue(LogicalDescriptor descriptor, out DescriptorBinding binding) =>
            DescriptorMap.TryGetValue(_positions, _slots, descriptor, out binding);

        internal bool ContainsKey(LogicalDescriptor descriptor) => _positions.ContainsKey(descriptor);

        internal bool ContainsValue(FileDescriptor handle) => ReferenceCount(handle) != 0;

        private int ReferenceCount(FileDescriptor handle) =>
            _referenceChanges.TryGetValue(handle, out var changed) ? changed.Count
                : _references.TryGetValue(handle, out var count) ? count : 0;

        private void ChangeReferences(FileDescriptor handle, int difference)
        {
            var count = checked(ReferenceCount(handle) + difference);
            _references.TryGetValue(handle, out var baseline);
            if (count == baseline)
                RemoveReferenceChange(handle);
            else if (_referenceChanges.TryGetValue(handle, out var previous))
                _referenceChanges[handle] = (count, previous.Position);
            else
            {
                _referenceChanges.Add(handle, (count, _changedHandles.Count));
                _changedHandles.Add(handle);
            }
        }

        private void RemoveReferenceChange(FileDescriptor handle)
        {
            if (!_referenceChanges.Remove(handle, out var removed)) return;
            var last = _changedHandles[^1];
            _changedHandles.RemoveAt(_changedHandles.Count - 1);
            if (removed.Position == _changedHandles.Count) return;
            _changedHandles[removed.Position] = last;
            _referenceChanges[last] = (_referenceChanges[last].Count, removed.Position);
        }

        internal void Set(LogicalDescriptor descriptor, DescriptorBinding value)
        {
            PrepareWrite();
            var handle = value.Handle;
            var binding = new Binding(descriptor, value, -1);
            if (_positions.TryGetValue(descriptor, out var position))
            {
                var oldHandle = _slots[position].Handle;
                if (oldHandle != handle)
                {
                    ChangeReferences(oldHandle, -1);
                    ChangeReferences(handle, 1);
                }
                _slots[position] = binding;
                return;
            }
            // Build reference nodes alongside new logical bindings. Replacing
            // existing bindings instead accumulates reusable deltas, avoiding
            // a new immutable key node for every uncaptured value change.
            MergeReferenceChanges();
            _references.TryGetValue(handle, out var references);
            _references[handle] = checked(references + 1);
            if (_firstFree >= 0)
            {
                position = _firstFree;
                _firstFree = _slots[position].NextFree;
                _slots[position] = binding;
            }
            else
            {
                position = _slots.Count;
                _slots.Add(binding);
            }
            _positions.Add(descriptor, position);
        }

        internal void Remove(LogicalDescriptor descriptor)
        {
            PrepareWrite();
            var position = _positions[descriptor];
            ChangeReferences(_slots[position].Handle, -1);
            _positions.Remove(descriptor);
            _slots[position] = new Binding(default, DescriptorBinding.Closed, _firstFree);
            _firstFree = position;
        }

        private void PrepareWrite()
        {
            _snapshot = null;
            if (!_captured) return;
            _captured = false;
            // The former detached Dictionary compacted vacant slots. Discard
            // its free-list reuse at that boundary, preserving relative order.
            _firstFree = -1;
            if (_slots.Count - _positions.Count <= _positions.Count) return;
            var positions = ImmutableDictionary.CreateBuilder<LogicalDescriptor, int>();
            var slots = ImmutableList.CreateBuilder<Binding>();
            foreach (var binding in _slots)
            {
                if (!binding.IsPresent) continue;
                positions.Add(binding.Descriptor, slots.Count);
                slots.Add(binding);
            }
            _positions = positions;
            _slots = slots;
            // Capture has already merged every change. A compacted slot tree
            // must not keep the preceding wide dictionary's vacant arrays.
            if (_referenceChanges.EnsureCapacity(0) > 3)
            {
                _referenceChanges = new Dictionary<FileDescriptor, (int Count, int Position)>(3);
                _changedHandles = new List<FileDescriptor>(3);
            }
        }

        private void MergeReferenceChanges()
        {
            // Clear must follow live keys. Dictionary.Clear and dictionary
            // enumeration would revisit retained buckets/vacant entries after
            // an earlier wide batch, making later bounded captures quadratic.
            while (_changedHandles.Count != 0)
            {
                var handle = _changedHandles[^1];
                var change = _referenceChanges[handle];
                if (change.Count == 0) _references.Remove(handle);
                else _references[handle] = change.Count;
                RemoveReferenceChange(handle);
            }
        }

        internal DescriptorMap Capture()
        {
            _captured = true;
            if (_snapshot != null) return _snapshot;
            MergeReferenceChanges();
            return _snapshot = new DescriptorMap(_positions.ToImmutable(), _slots.ToImmutable(),
                _references.ToImmutable(), _firstFree);
        }
    }

    private readonly record struct Binding(LogicalDescriptor Descriptor, DescriptorBinding Value, int NextFree)
    {
        internal FileDescriptor Handle => Value.Handle;
        internal bool IsPresent => Descriptor.Number > 2;
    }
}

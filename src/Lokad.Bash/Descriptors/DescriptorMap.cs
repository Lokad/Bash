using System.Collections.Generic;
using System.Collections.Immutable;

namespace Lokad.Bash;

/// <summary>Immutable indexed slots preserve descriptor encounter order across shared snapshots.</summary>
internal sealed class DescriptorMap
{
    internal static readonly DescriptorMap Empty = new(
        ImmutableDictionary<LogicalDescriptor, int>.Empty, ImmutableList<Binding>.Empty, -1);

    private readonly ImmutableDictionary<LogicalDescriptor, int> _positions;
    private readonly ImmutableList<Binding> _slots;
    private readonly int _firstFree;

    private DescriptorMap(ImmutableDictionary<LogicalDescriptor, int> positions,
        ImmutableList<Binding> slots, int firstFree)
    {
        _positions = positions;
        _slots = slots;
        _firstFree = firstFree;
    }

    internal int SlotCount => _slots.Count;

    // Include both trees, builder/header metadata and a complete replacement
    // generation. One coarse owner covers path copying without node reservations.
    internal static long EstimateMemoryBytes(int slots) => checked(512L + 256L * slots);

    internal int RequiredSlots(LogicalDescriptor descriptor) =>
        RequiredSlots(_positions, SlotCount, _firstFree, descriptor, captured: true);

    internal bool TryGetValue(LogicalDescriptor descriptor, out FileDescriptor handle) =>
        TryGetValue(_positions, _slots, descriptor, out handle);

    internal bool ContainsKey(LogicalDescriptor descriptor) => _positions.ContainsKey(descriptor);

    internal bool ContainsValue(FileDescriptor handle) => ContainsValue(_slots.GetEnumerator(), handle);

    private static int RequiredSlots(IReadOnlyDictionary<LogicalDescriptor, int> positions, int slotCount,
        int firstFree, LogicalDescriptor descriptor, bool captured)
    {
        var slots = captured && slotCount - positions.Count > positions.Count ? positions.Count : slotCount;
        return positions.ContainsKey(descriptor) || (!captured && firstFree >= 0) ? slots : checked(slots + 1);
    }

    private static bool TryGetValue(IReadOnlyDictionary<LogicalDescriptor, int> positions,
        IReadOnlyList<Binding> slots, LogicalDescriptor descriptor, out FileDescriptor handle)
    {
        if (positions.TryGetValue(descriptor, out var position))
        {
            handle = slots[position].Handle;
            return true;
        }
        handle = FileDescriptor.Closed;
        return false;
    }

    private static bool ContainsValue(ImmutableList<Binding>.Enumerator iterator, FileDescriptor handle)
    {
        using (iterator)
            while (iterator.MoveNext())
                if (iterator.Current is { IsPresent: true } binding && binding.Handle == handle)
                    return true;
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
        private int _firstFree;
        private bool _captured = true;
        private DescriptorMap? _snapshot;

        internal Builder(DescriptorMap source)
        {
            _positions = source._positions.ToBuilder();
            _slots = source._slots.ToBuilder();
            _firstFree = source._firstFree;
            _snapshot = source;
        }

        internal int SlotCount => _slots.Count;

        internal int RequiredSlots(LogicalDescriptor descriptor) =>
            DescriptorMap.RequiredSlots(_positions, SlotCount, _firstFree, descriptor, _captured);

        internal bool TryGetValue(LogicalDescriptor descriptor, out FileDescriptor handle) =>
            DescriptorMap.TryGetValue(_positions, _slots, descriptor, out handle);

        internal bool ContainsKey(LogicalDescriptor descriptor) => _positions.ContainsKey(descriptor);

        internal bool ContainsValue(FileDescriptor handle) => DescriptorMap.ContainsValue(_slots.GetEnumerator(), handle);

        internal void Set(LogicalDescriptor descriptor, FileDescriptor handle)
        {
            PrepareWrite();
            var binding = new Binding(descriptor, handle, -1);
            if (_positions.TryGetValue(descriptor, out var position))
            {
                _slots[position] = binding;
                return;
            }
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
            _positions.Remove(descriptor);
            _slots[position] = new Binding(default, FileDescriptor.Closed, _firstFree);
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
        }

        internal DescriptorMap Capture()
        {
            _captured = true;
            return _snapshot ??= new DescriptorMap(_positions.ToImmutable(), _slots.ToImmutable(), _firstFree);
        }
    }

    private readonly record struct Binding(LogicalDescriptor Descriptor, FileDescriptor Handle, int NextFree)
    {
        internal bool IsPresent => Descriptor.Number > 2;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Indexes live handles in encounter order, reusing removed slots without moving their neighbors.</summary>
/// <remarks>One serialized owner mutates the set. Enumeration borrows it and must finish before mutation.</remarks>
internal sealed class OrderedDescriptorSet : IReadOnlyCollection<FileDescriptor>
{
    private readonly Dictionary<FileDescriptor, int> _positions = new();
    private readonly List<Entry> _entries = new();
    private int _first = -1;
    private int _last = -1;
    private int _free = -1;

    public int Count => _positions.Count;
    public long EstimatedMemoryBytes => _entries.Capacity == 0 ? 0
        : checked(256L + 16L * _entries.Capacity + 32L * _positions.EnsureCapacity(0));

    public bool Contains(FileDescriptor descriptor) => _positions.ContainsKey(descriptor);

    /// <summary>Admits replacement overlap before growing either backing container.</summary>
    public void PrepareAdditional(int additional, MemoryCapacity? memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additional);
        if (additional > int.MaxValue - Count)
            throw new ByteMemoryBuilder.LimitExceededException();
        var required = Count + additional;
        var indexCapacity = _positions.EnsureCapacity(0);
        var entryCapacity = _entries.Capacity;
        if (required <= indexCapacity && required <= entryCapacity)
            return;
        var indexTarget = required <= indexCapacity ? indexCapacity : Grow(indexCapacity, required);
        var entryTarget = required <= entryCapacity ? entryCapacity : Grow(entryCapacity, required);
        var estimate = checked(256L + (indexTarget == indexCapacity ? 0 : 32L * (2L * indexTarget + 1))
            + (entryTarget == entryCapacity ? 0 : 16L * entryTarget));
        memory?.Grow(estimate);
        if (indexTarget != indexCapacity)
            _positions.EnsureCapacity(indexTarget);
        if (entryTarget != entryCapacity)
            _entries.Capacity = entryTarget;

        static int Grow(int capacity, int required) =>
            (int)Math.Min(int.MaxValue, Math.Max(required, Math.Max(4L, 2L * capacity)));
    }

    /// <summary>Publishes a handle using previously prepared capacity; existing handles keep their order.</summary>
    public bool AddPrepared(FileDescriptor descriptor)
    {
        if (_positions.ContainsKey(descriptor))
            return false;
        if (Count >= _positions.EnsureCapacity(0) || Count >= _entries.Capacity)
            throw new InvalidOperationException("Descriptor tracking capacity was not prepared.");
        int index;
        if (_free >= 0)
        {
            index = _free;
            _free = _entries[index].Next;
            _entries[index] = new Entry(descriptor, _last, -1);
        }
        else
        {
            index = _entries.Count;
            _entries.Add(new Entry(descriptor, _last, -1));
        }
        if (_last >= 0)
        {
            var previous = _entries[_last];
            previous.Next = index;
            _entries[_last] = previous;
        }
        else
            _first = index;
        _last = index;
        _positions.Add(descriptor, index);
        return true;
    }

    public bool Remove(FileDescriptor descriptor)
    {
        if (!_positions.Remove(descriptor, out var index))
            return false;
        var entry = _entries[index];
        if (entry.Previous >= 0)
        {
            var previous = _entries[entry.Previous];
            previous.Next = entry.Next;
            _entries[entry.Previous] = previous;
        }
        else
            _first = entry.Next;
        if (entry.Next >= 0)
        {
            var next = _entries[entry.Next];
            next.Previous = entry.Previous;
            _entries[entry.Next] = next;
        }
        else
            _last = entry.Previous;
        _entries[index] = new Entry(default, -1, _free);
        _free = index;
        return true;
    }

    public bool TryPeekFirst(out FileDescriptor descriptor)
    {
        descriptor = _first >= 0 ? _entries[_first].Descriptor : default;
        return _first >= 0;
    }

    /// <summary>Reverses encounter order in place, retaining reusable slots and membership.</summary>
    public void Reverse()
    {
        for (var index = _first; index >= 0;)
        {
            var entry = _entries[index];
            var next = entry.Next;
            (entry.Previous, entry.Next) = (entry.Next, entry.Previous);
            _entries[index] = entry;
            index = next;
        }
        (_first, _last) = (_last, _first);
    }

    /// <summary>Ends a consumed batch while retaining its admitted backing storage.</summary>
    public void Clear()
    {
        // Dictionary.Clear touches every bucket at the retained high-water width.
        // Bounded batches must visit only their live entries after an earlier wide batch.
        while (TryPeekFirst(out var descriptor))
            Remove(descriptor);
    }

    /// <summary>Discards empty backing storage only after its final consumer has settled.</summary>
    public void ReleaseStorage()
    {
        if (Count != 0)
            throw new InvalidOperationException("Live descriptors still require cleanup.");
        _positions.Clear();
        _positions.TrimExcess();
        _entries.Clear();
        _entries.Capacity = 0;
        _first = _last = _free = -1;
    }

    public IEnumerator<FileDescriptor> GetEnumerator()
    {
        for (var index = _first; index >= 0; index = _entries[index].Next)
            yield return _entries[index].Descriptor;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private struct Entry(FileDescriptor descriptor, int previous, int next)
    {
        public FileDescriptor Descriptor = descriptor;
        public int Previous = previous;
        public int Next = next;
    }
}

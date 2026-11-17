using System;
using System.Collections.Generic;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Appends distinct host handles to caller-owned storage in encounter order.</summary>
/// <remarks>The caller must not otherwise mutate the storage while this collector is in use.</remarks>
public struct DescriptorHandleCollector
{
    // Linear scans avoid a hash index for standard streams and small alias sets.
    private const int HashIndexThreshold = 128;
    private readonly List<FileDescriptor> _descriptors;
    private HashSet<FileDescriptor>? _seen;
    private readonly MemoryCapacity? _memory;
    private long _listMemoryBytes;
    private long _indexMemoryBytes;

    public DescriptorHandleCollector(List<FileDescriptor> descriptors)
        : this(descriptors, null)
    {
    }

    /// <summary>Runtime collectors borrow a command owner through their final consumer.</summary>
    internal DescriptorHandleCollector(List<FileDescriptor> descriptors, MemoryCapacity? memory)
    {
        _descriptors = descriptors;
        _seen = null;
        _memory = memory;
        _listMemoryBytes = 0;
        _indexMemoryBytes = 0;
    }

    public IReadOnlyList<FileDescriptor> Descriptors => _descriptors;

    public void Add(FileDescriptor descriptor)
    {
        if (_seen is null)
        {
            if (_descriptors.Contains(descriptor))
                return;
            if (_descriptors.Count < HashIndexThreshold)
            {
                PrepareList();
                _descriptors.Add(descriptor);
                return;
            }
        }

        var seen = GetIndex();
        if (seen.Contains(descriptor))
            return;
        PrepareIndex(seen, checked(seen.Count + 1));
        PrepareList();
        seen.Add(descriptor);
        _descriptors.Add(descriptor);
    }

    /// <summary>Publishes the completed collector's existing membership index without copying it.</summary>
    internal HashSet<FileDescriptor> GetIndex()
    {
        if (_seen != null)
            return _seen;
        var seen = new HashSet<FileDescriptor>();
        PrepareIndex(seen, _descriptors.Count);
        foreach (var descriptor in _descriptors)
            seen.Add(descriptor);
        _seen = seen;
        return seen;
    }

    private void PrepareIndex(HashSet<FileDescriptor> seen, int required)
    {
        var capacity = seen.EnsureCapacity(0);
        if (required <= capacity)
            return;
        var target = (int)Math.Min(int.MaxValue, Math.Max(required, Math.Max(4L, 2L * capacity)));
        var peak = checked(128L + 32L * (2L * target + 1));
        _memory?.Grow(peak);
        seen.EnsureCapacity(target);
        var retained = checked(128L + 16L * seen.EnsureCapacity(0));
        _memory?.Resize(checked(_memory.Bytes - peak + retained - _indexMemoryBytes));
        _indexMemoryBytes = retained;
    }

    private void PrepareList()
    {
        if (_descriptors.Count < _descriptors.Capacity)
            return;
        var target = (int)Math.Min(int.MaxValue, Math.Max(4L, 2L * _descriptors.Capacity));
        var replacement = checked(32L + 4L * target);
        _memory?.Grow(replacement);
        _descriptors.Capacity = target;
        _memory?.Resize(checked(_memory.Bytes - _listMemoryBytes));
        _listMemoryBytes = replacement;
    }

    public void AddRange(IReadOnlyList<FileDescriptor> descriptors)
    {
        for (var index = 0; index < descriptors.Count; index++)
            Add(descriptors[index]);
    }
}

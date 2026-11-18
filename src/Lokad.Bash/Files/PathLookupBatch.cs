using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Files;

/// <summary>Collects distinct paths and maps operands to their ordered lookup results.</summary>
public sealed class PathLookupBatch<TPath> where TPath : notnull
{
    private readonly List<TPath> _paths = [];
    private readonly Dictionary<TPath, int> _indices = [];

    public IReadOnlyList<TPath> Paths => _paths;

    public int this[TPath path] => _indices[path];

    public int Add(TPath path) => Add(path, null);

    internal int Add(TPath path, MemoryCapacity? memory)
    {
        if (_indices.TryGetValue(path, out var index))
            return index;
        index = _paths.Count;
        if (memory is not null)
        {
            var previous = _indices.EnsureCapacity(0);
            if (index == previous)
            {
                var target = (int)Math.Min(int.MaxValue, Math.Max(3L, 2L * previous));
                // Account for prime rounding and the old index until EnsureCapacity returns.
                var replacement = IndexBackingBytes(2L * target + 1);
                memory.Grow(replacement);
                var capacity = _indices.EnsureCapacity(target);
                memory.Resize(memory.Bytes - replacement - IndexBackingBytes(previous) + IndexBackingBytes(capacity));
            }
            memory.PrepareListCapacity(_paths, 1);
        }
        _indices.Add(path, index);
        _paths.Add(path);
        return index;
    }

    private static long IndexBackingBytes(long capacity) => capacity == 0 ? 0
        : 64L + (Unsafe.SizeOf<TPath>() + 24L) * capacity;
}

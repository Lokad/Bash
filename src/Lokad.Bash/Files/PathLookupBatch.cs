using System.Collections.Generic;

namespace Lokad.Bash.Files;

/// <summary>Collects distinct paths and maps operands to their ordered lookup results.</summary>
public sealed class PathLookupBatch<TPath> where TPath : notnull
{
    private readonly List<TPath> _paths = [];
    private readonly Dictionary<TPath, int> _indices = [];

    public IReadOnlyList<TPath> Paths => _paths;

    public int this[TPath path] => _indices[path];

    public int Add(TPath path)
    {
        if (_indices.TryGetValue(path, out var index))
            return index;
        index = _paths.Count;
        _indices.Add(path, index);
        _paths.Add(path);
        return index;
    }
}

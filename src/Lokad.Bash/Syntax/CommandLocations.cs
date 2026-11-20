using System.Collections.Generic;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>LF coordinates in the source from which a command runs.</summary>
internal readonly record struct CommandLocation(int StartLine, int FirstElementEndLine, int EndLine);

/// <summary>Parser-owned locations that use node identity and leave public AST equality intact.</summary>
internal sealed class CommandLocations
{
    private ICommand? _first;
    private CommandLocation _firstLocation;
    private Dictionary<ICommand, CommandLocation>? _entries;

    internal bool TryGet(ICommand command, out CommandLocation location)
    {
        if (ReferenceEquals(command, _first))
        {
            location = _firstLocation;
            return true;
        }
        location = default;
        return _entries != null && _entries.TryGetValue(command, out location);
    }

    internal void Add(ICommand command, CommandLocation location, MemoryCapacity? memory)
    {
        if (_first == null)
        {
            memory?.Grow(64);
            _first = command;
            _firstLocation = location;
            return;
        }
        // Covers the entry, the bucket array and overlapping generations during growth.
        memory?.Grow(128);
        (_entries ??= new Dictionary<ICommand, CommandLocation>(ReferenceEqualityComparer.Instance)).Add(command, location);
    }

    internal void Replace(ICommand original, ICommand replacement)
    {
        if (ReferenceEquals(original, _first))
            _first = replacement;
        else if (_entries != null && _entries.Remove(original, out var location))
            _entries.Add(replacement, location);
    }

    internal void Update(ICommand command, CommandLocation location)
    {
        if (ReferenceEquals(command, _first))
            _firstLocation = location;
        else
            _entries![command] = location;
    }
}

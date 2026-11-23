using System.Collections.Generic;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>LF coordinates in the source from which a command runs.</summary>
internal readonly record struct CommandLocation(int StartLine, int FirstElementEndLine, int EndLine)
{
    /// <summary>End of the next token after an initial word, when it changes the callback anchor.</summary>
    internal int WrapperOperandEndLine { get; init; }

    /// <summary>Line selected from a redirection operand immediately after an initial word.</summary>
    internal int RedirectionOperandLine { get; init; }
}

/// <summary>Cumulative line change after a spelling edit at a physical source position.</summary>
internal readonly record struct SourceLineAdjustment(int End, int Delta);

/// <summary>Counts LF coordinates incrementally from an offset in one immutable source.</summary>
internal struct SourceLineCursor(int start)
{
    private int _offset = start;
    private int _breaks;

    internal int GetLine(ShellSource source, int offset)
    {
        var bytes = source.AsSpan();
        _breaks += offset >= _offset ? bytes[_offset..offset].Count((byte)'\n')
            : -bytes[offset.._offset].Count((byte)'\n');
        _offset = offset;
        return _breaks + 1;
    }
}

/// <summary>Parser-owned locations that use node identity and leave public AST equality intact.</summary>
internal sealed class CommandLocations
{
    private ICommand? _first;
    private CommandLocation _firstLocation;
    private Dictionary<ICommand, CommandLocation>? _entries;
    private ReadDiagnosticEntry? _firstReadDiagnostics;
    private Dictionary<ICommand, CommandReadDiagnostics>? _otherReadDiagnostics;

    internal CommandReadDiagnostics GetReadDiagnostics(ICommand command) =>
        _firstReadDiagnostics is { } entry && ReferenceEquals(command, entry.Command) ? entry.Diagnostics
            : _otherReadDiagnostics != null && _otherReadDiagnostics.TryGetValue(command, out var later) ? later : default;

    internal void SetReadDiagnostics(ICommand command, CommandReadDiagnostics diagnostics, MemoryCapacity? memory)
    {
        if (_firstReadDiagnostics == null)
        {
            memory?.Grow(40);
            _firstReadDiagnostics = new ReadDiagnosticEntry(command, diagnostics);
        }
        else
        {
            memory?.Grow(128);
            (_otherReadDiagnostics ??= new Dictionary<ICommand, CommandReadDiagnostics>(ReferenceEqualityComparer.Instance))
                .Add(command, diagnostics);
        }
    }

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
        if (_firstReadDiagnostics is { } entry && ReferenceEquals(original, entry.Command))
            entry.Command = replacement;
        else if (_otherReadDiagnostics != null && _otherReadDiagnostics.Remove(original, out var later))
            _otherReadDiagnostics.Add(replacement, later);
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

    private sealed class ReadDiagnosticEntry(ICommand command, CommandReadDiagnostics diagnostics)
    {
        internal ICommand Command { get; set; } = command;
        internal CommandReadDiagnostics Diagnostics { get; } = diagnostics;
    }
}

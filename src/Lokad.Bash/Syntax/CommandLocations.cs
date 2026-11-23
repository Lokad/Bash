using System.Collections.Generic;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>LF coordinates in the source from which a command runs.</summary>
internal readonly record struct CommandLocation(int StartLine, int FirstElementEndLine, int EndLine)
{
    /// <summary>End of the next token after an initial word, when it changes the callback anchor.</summary>
    internal int WrapperOperandEndLine { get; init; }
}

/// <summary>Cumulative line change after a spelling edit at a physical source position.</summary>
internal readonly record struct SourceLineAdjustment(int End, int Delta);

/// <summary>Parser-owned locations that use node identity and leave public AST equality intact.</summary>
internal sealed class CommandLocations
{
    private ICommand? _first;
    private CommandLocation _firstLocation;
    private Dictionary<ICommand, CommandLocation>? _entries;
    private EndOfInputWarnings? _hereDocumentWarnings;
    private Dictionary<ICommand, IReadOnlyList<HereDocumentWarning>>? _otherWarnings;

    internal IReadOnlyList<HereDocumentWarning>? GetHereDocumentWarnings(ICommand command) =>
        _hereDocumentWarnings is { } warnings && ReferenceEquals(command, warnings.Command) ? warnings.Warnings
            : _otherWarnings != null && _otherWarnings.TryGetValue(command, out var later) ? later : null;

    internal void SetHereDocumentWarnings(ICommand command, IReadOnlyList<HereDocumentWarning> warnings, MemoryCapacity? memory)
    {
        if (_hereDocumentWarnings == null)
        {
            memory?.Grow(40);
            _hereDocumentWarnings = new EndOfInputWarnings(command, warnings);
        }
        else
        {
            memory?.Grow(128);
            (_otherWarnings ??= new Dictionary<ICommand, IReadOnlyList<HereDocumentWarning>>(ReferenceEqualityComparer.Instance))
                .Add(command, warnings);
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
        if (_hereDocumentWarnings is { } warnings && ReferenceEquals(original, warnings.Command))
            warnings.Command = replacement;
        else if (_otherWarnings != null && _otherWarnings.Remove(original, out var later))
            _otherWarnings.Add(replacement, later);
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

    private sealed class EndOfInputWarnings(ICommand command, IReadOnlyList<HereDocumentWarning> warnings)
    {
        internal ICommand Command { get; set; } = command;
        internal IReadOnlyList<HereDocumentWarning> Warnings { get; } = warnings;
    }
}

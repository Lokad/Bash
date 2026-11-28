using System;
using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Holds the AST for a parsed script.
/// </summary>
/// <remarks>
/// Trees created by the parser retain the original reader text for verbose input echoing.
/// Trees constructed from commands alone have no authored reader.
/// Callers must not mutate retained collections anywhere in the AST.
/// </remarks>
public sealed class ParsedScript
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ParsedScript"/> class.
    /// </summary>
    /// <param name="commands">The top-level commands in the script.</param>
    public ParsedScript(IReadOnlyList<ICommand> commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    /// <summary>
    /// Gets the top-level commands in the script.
    /// </summary>
    public IReadOnlyList<ICommand> Commands { get; }

    internal CommandLocations? Locations { get; init; }
    internal AuthoredReader? Reader { get; set; }

    internal sealed record AuthoredReader(ShellSource Source, Range Content, IReadOnlyList<int> UnitEnds,
        int EmptyFirstUnitEnd, ReaderSpelling Spelling)
    {
        internal Range Unit(int index) => (index == 0 ? Content.Start.Value : UnitEnds[index - 1])..UnitEnds[index];

        internal Range TrailingInput => (UnitEnds.Count == 0 ? Content.Start.Value : UnitEnds[^1])..Content.End.Value;
    }
    internal int? RenderedLineBreaks { get; set; }
    internal int RenderedFunctionBraceLine { get; set; }
    internal char TruncatedAdjacentDelimiter { get; set; }
    internal bool HasDecodedNulWord { get; set; }
    internal bool HasReadAheadHereDocuments { get; set; }
    /// <summary>End of the final command before its delimiter and trailing trivia in a whole-source parse.</summary>
    internal int LastCommandEnd { get; set; }
    /// <summary>The final command unit consumes the input through its own terminator.</summary>
    internal bool FinalUnitReachesInputEnd { get; set; }

    /// <summary>Preserves the printer's input when an omitted separator changed the executable tree.</summary>
    internal IReadOnlyList<ICommand>? AuthoredCaptureCommands { get; set; }
}

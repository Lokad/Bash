using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>A complete script command unit or its terminal syntax failure.</summary>
internal abstract record CommandUnitParseResult
{
    private CommandUnitParseResult() { }

    internal CommandLocations? Locations { get; init; }
    internal IReadOnlyList<HereDocumentWarning>? HereDocumentWarnings { get; init; }

    /// <summary>A complete unit whose syntax and here-document bodies have been parsed.</summary>
    internal sealed record Parsed(ICommand Command) : CommandUnitParseResult;

    /// <summary>A terminal syntax diagnostic with any complete prefix recognized at end of input.</summary>
    internal sealed record Failed(ParserFailure Failure) : CommandUnitParseResult
    {
        internal SyntaxError Error => Failure.Error;
        internal ICommand? CompletedPrefix { get; init; }
    }
}

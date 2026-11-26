using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>Reader information produced before executing any command in the current unit.</summary>
internal readonly record struct CommandReadDiagnostics(IReadOnlyList<HereDocumentWarning>? HereDocumentWarnings,
    IReadOnlyList<CompoundItemReadDiagnostic>? CompoundItems)
{
    internal int FunctionBraceLine { get; init; }
    internal bool IsEmpty => HereDocumentWarnings == null && CompoundItems == null && FunctionBraceLine == 0;
}

/// <summary>A complete script command unit or its syntax failure.</summary>
internal abstract record CommandUnitParseResult
{
    private CommandUnitParseResult() { }

    internal CommandLocations? Locations { get; init; }
    internal CommandReadDiagnostics ReadDiagnostics { get; init; }
    internal int UnitReadLine { get; init; }

    /// <summary>A complete unit whose syntax and here-document bodies have been parsed.</summary>
    internal sealed record Parsed(ICommand Command) : CommandUnitParseResult;

    /// <summary>A syntax diagnostic with any complete prefix recognized at end of input.</summary>
    internal sealed record Failed(ParserFailure Failure) : CommandUnitParseResult
    {
        internal SyntaxError Error => Failure.Error;
        internal ICommand? CompletedPrefix { get; init; }
        internal int DiagnosticLine { get; init; }
        internal bool ResumesAfterArrayToken { get; init; }
        internal (UnclosedDelimiter Delimiter, int Line)? QuotedRecovery { get; init; }
    }
}

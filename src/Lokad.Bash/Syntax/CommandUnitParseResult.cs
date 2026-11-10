namespace Lokad.Bash.Syntax;

/// <summary>A complete sourced-script command unit or its terminal syntax failure.</summary>
internal abstract record CommandUnitParseResult
{
    private CommandUnitParseResult() { }

    /// <summary>A complete unit whose syntax and here-document bodies have been parsed.</summary>
    internal sealed record Parsed(ICommand Command) : CommandUnitParseResult;

    /// <summary>A syntax error in the next unit; earlier parsed units remain valid.</summary>
    internal sealed record Failed(SyntaxError Error) : CommandUnitParseResult;
}

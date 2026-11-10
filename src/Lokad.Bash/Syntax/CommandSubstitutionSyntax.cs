namespace Lokad.Bash.Syntax;

/// <summary>Retains executable bytes and any tree already built to locate their closing delimiter.</summary>
internal abstract record CommandSubstitutionSyntax(ShellSource Source)
{
    /// <summary>A parenthesized body parsed by the delimiter grammar, including completed here-documents.</summary>
    internal sealed record Parsed(ShellSource SourceText, ParsedScript Script)
        : CommandSubstitutionSyntax(SourceText);

    /// <summary>Bytes still requiring execution-time parsing, including legacy backticks and unfinished here-documents.</summary>
    internal sealed record Deferred(ShellSource SourceText) : CommandSubstitutionSyntax(SourceText);
}

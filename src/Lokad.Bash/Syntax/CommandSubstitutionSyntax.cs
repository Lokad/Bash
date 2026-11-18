namespace Lokad.Bash.Syntax;

/// <summary>Retains executable bytes and any tree already built to locate their closing delimiter.</summary>
internal abstract record CommandSubstitutionSyntax(ShellSource Source)
{
    /// <summary>A parenthesized body parsed by the delimiter grammar, including completed here-documents.</summary>
    internal sealed record Parsed(ShellSource SourceText, ParsedScript Script)
        : CommandSubstitutionSyntax(SourceText);

    /// <summary>Legacy executable bytes and their original delimiters/escapes, validated only when reached.</summary>
    internal sealed record Legacy(ShellSource SourceText, ShellSource Spelling, LegacySubstitutionOrigin Origin)
        : CommandSubstitutionSyntax(SourceText);

    /// <summary>Bytes still requiring execution-time parsing, including unfinished here-documents.</summary>
    internal sealed record Deferred(ShellSource SourceText) : CommandSubstitutionSyntax(SourceText);
}

/// <summary>Identifies which legacy bytes can be owned with the surrounding syntax.</summary>
internal enum LegacySubstitutionOrigin { Temporary, Script, HereDocument }

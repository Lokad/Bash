namespace Lokad.Bash.Syntax;

/// <summary>Retains executable bytes and any tree already built to locate their closing delimiter.</summary>
internal abstract record CommandSubstitutionSyntax(ShellSource Source)
{
    /// <summary>A parenthesized body parsed by the delimiter grammar, including completed here-documents.</summary>
    internal sealed record Parsed(ShellSource SourceText, ParsedScript Script, SubstitutionSyntaxFailure Failure,
        bool PreserveAdjacentOpening)
        : CommandSubstitutionSyntax(SourceText)
    {
        internal bool CanRenderBody => Failure == SubstitutionSyntaxFailure.None && !PreserveAdjacentOpening;
        internal bool CanDisplayBody => !PreserveAdjacentOpening && Failure is SubstitutionSyntaxFailure.None
            or SubstitutionSyntaxFailure.HereDocumentSeparator or SubstitutionSyntaxFailure.NestedHereDocumentSeparator;
        internal bool HasRejectedDisplaySpelling => (Failure is SubstitutionSyntaxFailure.HereDocumentSeparator
            or SubstitutionSyntaxFailure.NestedHereDocumentSeparator) && (CanDisplayBody || AdjacentSpelling != null);
        internal bool HasDiagnosticSpelling => HasRejectedDisplaySpelling
            || Failure is SubstitutionSyntaxFailure.MissingPipelinePrefixCommand or SubstitutionSyntaxFailure.NestedPipelinePrefixCommand;
        internal SourceSpelling? AdjacentSpelling { get; init; }
    }

    /// <summary>Legacy executable bytes and their original delimiters/escapes, validated only when reached.</summary>
    internal sealed record Legacy(ShellSource SourceText, ShellSource Spelling, LegacySubstitutionOrigin Origin)
        : CommandSubstitutionSyntax(SourceText)
    {
        internal int RemovedLineContinuations { get; init; }
    }

    /// <summary>Bytes still requiring execution-time parsing, including unfinished here-documents.</summary>
    internal sealed record Deferred(ShellSource SourceText) : CommandSubstitutionSyntax(SourceText)
    {
        /// <summary>Warnings from locating the closer during here-document expansion.</summary>
        internal IReadOnlyList<HereDocumentWarning>? ReadWarnings { get; init; }
    }

    /// <summary>Balanced adjacent subshell spelling to parse in complete units when reached.</summary>
    internal sealed record AdjacentSubshell(ShellSource SourceText) : CommandSubstitutionSyntax(SourceText);
}

/// <summary>Known syntax failures when Bash rereads the spelling of a parsed substitution.</summary>
internal enum SubstitutionSyntaxFailure
{
    None,
    MissingPipelinePrefixCommand,
    NestedPipelinePrefixCommand,
    AdjacentNestedSyntax,
    NestedSyntax,
    HereDocumentSeparator,
    NestedHereDocumentSeparator,
}

/// <summary>Identifies which legacy bytes can be owned with the surrounding syntax.</summary>
internal enum LegacySubstitutionOrigin { Temporary, Script, HereDocument }

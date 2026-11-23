namespace Lokad.Bash.Syntax;

/// <summary>A parsing failure at a UTF-16 text offset or a raw-source byte offset.</summary>
public sealed record SyntaxError(string Message, int Offset)
{
    /// <summary>Formats the Bash diagnostic text without a line terminator or text encoding.</summary>
    public string ToDiagnosticMessage()
    {
        var message = string.IsNullOrWhiteSpace(Message) ? "Syntax error." : Message;
        return $"syntax error: {message}";
    }

    internal SyntaxException ToException() => new(this);
}

/// <summary>Retains the parser's failure class independently of the public diagnostic.</summary>
internal sealed record ParserFailure(SyntaxError Error, ParserFailureKind Kind)
{
    internal CaptureEndOfInput? CaptureEndOfInput { get; init; }
    internal CaptureHereDocumentWarnings? CaptureWarnings { get; init; }
    internal UnexpectedSyntax? UnexpectedToken { get; init; }
    internal ConditionalQuoteEnd? ConditionalQuoteEnd { get; init; }
    internal bool MissingCommandAtEndOfInput { get; init; }
    internal bool IsNestedSubstitution { get; init; }

    internal bool ExitsSubstitution => Kind is ParserFailureKind.SubstitutionGrammar or ParserFailureKind.SubstitutionForHeader;
    internal bool IsDirectQuotedSubstitutionFailure =>
        Kind == ParserFailureKind.QuotedSubstitutionForHeader && !IsNestedSubstitution;

    internal ParserFailure InSubstitution() => Kind switch
    {
        ParserFailureKind.Grammar or ParserFailureKind.RedirectionOperand => this with { Kind = ParserFailureKind.SubstitutionGrammar },
        ParserFailureKind.ArithmeticForHeader => this with { Kind = ParserFailureKind.SubstitutionForHeader },
        ParserFailureKind.SubstitutionForHeader or ParserFailureKind.QuotedSubstitutionForHeader
            when !IsNestedSubstitution => this with { IsNestedSubstitution = true },
        _ => this
    };

    internal ParserFailure InDoubleQuotes(bool quoted) => quoted && Kind == ParserFailureKind.SubstitutionForHeader
        ? this with { Kind = ParserFailureKind.QuotedSubstitutionForHeader } : this;
}

/// <summary>Byte slices and reader position for a reached token diagnostic.</summary>
internal sealed record UnexpectedSyntax(ShellSource Token, ShellSource Line, int ReadPosition)
{
    internal static ShellSource NewlineToken { get; } = ShellSource.FromText("newline");

    /// <summary>Byte offset where the enclosing capture starts reading its payload.</summary>
    internal int CapturePayloadStart { get; init; }
    internal ConditionalArgumentError ConditionalError { get; init; }
    internal ShellSource Argument { get; init; }
    internal int UnclosedGroups { get; init; }
}

/// <summary>A quoted bracketed item whose capture is diagnosed as its unit is read.</summary>
internal readonly record struct CompoundItemReadDiagnostic(Word Item, int EndLine,
    ShellSource DeclarationPrefix, int PrefixEndLine, SubstitutionSyntaxFailure CaptureFailure);

internal enum ConditionalArgumentError { None, UnaryOperand, BinaryOperand, BinaryOperator }

internal readonly record struct ConditionalQuoteEnd(bool DoubleQuoted)
{
    internal ConditionalArgumentError Role { get; init; }
    internal int UnclosedGroups { get; init; }
}

/// <summary>Diagnostics from reading a capture that could not reach its closing parenthesis.</summary>
internal sealed record CaptureEndOfInput(char Delimiter, int ReadPosition)
{
    internal int LineAdvance { get; init; }
    /// <summary>Line in a here-document expansion payload when its reader reports the failure.</summary>
    internal int PayloadLine { get; init; }
    internal bool InsideConditional { get; init; }
}

/// <summary>Warnings collected inside a capture, with each document line relative to its payload.</summary>
internal readonly record struct CaptureHereDocumentWarnings(IReadOnlyList<HereDocumentWarning> Warnings, int LineOffset);

internal enum ParserFailureKind
{
    Grammar,
    RedirectionOperand,
    Delimiter,
    Limit,
    ArithmeticForHeader,
    SubstitutionGrammar,
    SubstitutionForHeader,
    QuotedSubstitutionForHeader,
    ArrayQuotedSubstitutionForHeader,
    ArraySubstitutionDelimiter,
}

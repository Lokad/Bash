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
    internal bool ExitsSubstitution => Kind is ParserFailureKind.SubstitutionGrammar or ParserFailureKind.SubstitutionForHeader;

    internal ParserFailure InSubstitution() => Kind switch
    {
        ParserFailureKind.Grammar => this with { Kind = ParserFailureKind.SubstitutionGrammar },
        ParserFailureKind.ArithmeticForHeader => this with { Kind = ParserFailureKind.SubstitutionForHeader },
        _ => this
    };

    internal ParserFailure InDoubleQuotes(bool quoted) => quoted && Kind == ParserFailureKind.SubstitutionForHeader
        ? this with { Kind = ParserFailureKind.QuotedSubstitutionForHeader } : this;
}

internal enum ParserFailureKind
{
    Grammar,
    Delimiter,
    Limit,
    ArithmeticForHeader,
    SubstitutionGrammar,
    SubstitutionForHeader,
    QuotedSubstitutionForHeader,
    ArrayQuotedSubstitutionForHeader,
}

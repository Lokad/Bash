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

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a redirection on a simple or compound command.
/// </summary>
public sealed class Redirection
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Redirection"/> class.
    /// </summary>
    /// <param name="kind">The redirection operator kind.</param>
    /// <param name="fileDescriptor">An optional file descriptor override.</param>
    /// <param name="target">The target word, or completed here-document body.</param>
    /// <param name="variableName">The optional variable name for descriptor allocation.</param>
    /// <param name="hereDocumentDelimiter">The delimiter of a completed here-document; null otherwise.</param>
    public Redirection(RedirectionKind kind, int? fileDescriptor, Word target, string variableName, Word? hereDocumentDelimiter)
    {
        Kind = kind;
        FileDescriptor = fileDescriptor;
        Target = target;
        VariableName = variableName;
        HereDocumentDelimiter = hereDocumentDelimiter;
    }

    /// <summary>
    /// Gets the redirection operator kind.
    /// </summary>
    public RedirectionKind Kind { get; }

    /// <summary>
    /// Gets the optional file descriptor that the redirection applies to.
    /// </summary>
    public int? FileDescriptor { get; }

    /// <summary>
    /// Gets the target word, or completed here-document body.
    /// </summary>
    public Word Target { get; }

    /// <summary>
    /// Gets the optional variable name used for descriptor allocation.
    /// </summary>
    public string VariableName { get; }

    /// <summary>Retains the parsed delimiter separately from the completed body in Target.</summary>
    public Word? HereDocumentDelimiter { get; }
}

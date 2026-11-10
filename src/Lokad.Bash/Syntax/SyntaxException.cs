using System;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a syntax error detected while parsing input.
/// </summary>
public sealed class SyntaxException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SyntaxException"/> class.
    /// </summary>
    /// <param name="error">The parsing failure, retaining its message and input offset.</param>
    public SyntaxException(SyntaxError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>Gets the parsing failure wrapped by this exception.</summary>
    public SyntaxError Error { get; }

    /// <summary>
    /// Gets the input offset: UTF-16 for text parsing, bytes for byte-source parsing.
    /// </summary>
    public int Offset => Error.Offset;
}

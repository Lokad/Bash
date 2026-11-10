using System;

namespace Lokad.Bash.Tree;

/// <summary>
/// Raised when a tree mutation cannot proceed because a required path is missing.
/// </summary>
public sealed class MissingPathException : InvalidOperationException
{
    public MissingPathException(string message)
        : base(message)
    {
    }
}

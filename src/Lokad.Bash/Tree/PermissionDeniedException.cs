using System;

namespace Lokad.Bash.Tree;

/// <summary>
/// Raised when a tree mutation is rejected because the target permissions do not allow it.
/// </summary>
public sealed class PermissionDeniedException : InvalidOperationException
{
    public PermissionDeniedException(string message)
        : base(message)
    {
    }
}

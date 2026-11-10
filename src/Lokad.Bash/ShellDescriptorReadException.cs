using System.IO;

namespace Lokad.Bash;

/// <summary>A descriptor failure exposed through an I/O-shaped read API.</summary>
public sealed class ShellDescriptorReadException : IOException
{
    /// <summary>Preserves the read outcome's operation error.</summary>
    public ShellDescriptorReadException(OperationError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>The diagnostic reported by the descriptor.</summary>
    public OperationError Error { get; }
}

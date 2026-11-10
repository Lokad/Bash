using System.IO;

namespace Lokad.Bash;

/// <summary>A descriptor's rejected write, distinct from an underlying storage or input exception.</summary>
public sealed class ShellDescriptorWriteException : IOException
{
    public ShellDescriptorWriteException(FileDescriptor descriptor, OperationError error)
        : base($"Write to descriptor {descriptor.Id} failed: {error.MessageWithoutTerminator}")
    {
        Error = error;
    }

    public OperationError Error { get; }
}

namespace Lokad.Bash;

/// <summary>A borrowed handle and whether the shell knows it was opened for writing only.</summary>
internal readonly record struct DescriptorBinding(FileDescriptor Handle, bool IsWriteOnly)
{
    internal DescriptorBinding(FileDescriptor handle) : this(handle, IsWriteOnly: false) { }

    internal static DescriptorBinding Closed => new(FileDescriptor.Closed);

    internal bool CannotRead => IsWriteOnly || Handle.IsClosed || Handle.IsWriteOnlyDevNull;

    internal DescriptorBinding WithHandle(FileDescriptor handle) => Handle == handle ? this : new(handle);
}

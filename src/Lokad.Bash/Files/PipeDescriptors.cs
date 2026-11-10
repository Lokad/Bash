namespace Lokad.Bash.Files;

/// <summary>
/// Represents the read and write file descriptor identifiers for a pipe.
/// </summary>
/// <param name="Read">The read descriptor identifier.</param>
/// <param name="Write">The write descriptor identifier.</param>
public readonly record struct PipeDescriptors(FileDescriptor Read, FileDescriptor Write);

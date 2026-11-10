using System;
using Lokad.Bash.Files;

namespace Lokad.Bash;

/// <summary>
/// Represents a resolved shell file descriptor handle.
/// </summary>
public readonly record struct FileDescriptor(int Id)
{
    private const int ReadOnlyDevNullId = int.MaxValue - 1;
    private const int WriteOnlyDevNullId = ReadOnlyDevNullId - 1;

    /// <summary>Last ordinary host handle; higher IDs are reserved for null-device modes, not logical numbers.</summary>
    public const int LastAllocatedId = WriteOnlyDevNullId - 1;

    /// <summary>
    /// Converts a descriptor to its numeric identifier.
    /// </summary>
    public static implicit operator int(FileDescriptor descriptor) => descriptor.Id;

    /// <summary>
    /// Wraps a numeric identifier as a descriptor.
    /// </summary>
    public static implicit operator FileDescriptor(int id) => new(id);

    /// <summary>
    /// Gets the standard input descriptor.
    /// </summary>
    public static FileDescriptor StdIn => new(0);

    /// <summary>
    /// Gets the standard output descriptor.
    /// </summary>
    public static FileDescriptor StdOut => new(1);

    /// <summary>
    /// Gets the standard error descriptor.
    /// </summary>
    public static FileDescriptor StdErr => new(2);

    /// <summary>
    /// Gets the stateless read/write null handle used for synthetic streams.
    /// Path opens use <see cref="ForDevNull"/> to preserve their access mode.
    /// </summary>
    public static FileDescriptor DevNull => new(int.MaxValue);

    /// <summary>Returns a stateless null-device handle with the requested access mode.</summary>
    public static FileDescriptor ForDevNull(FileOpenMode mode) => mode == FileOpenMode.Read
        ? new(ReadOnlyDevNullId) : ForWritableDevNull(mode.ToWritable());

    /// <summary>Returns a stateless null-device handle for a writable open.</summary>
    public static FileDescriptor ForWritableDevNull(WritableFileOpenMode mode) => mode switch
    {
        WritableFileOpenMode.WriteTruncate or WritableFileOpenMode.WriteAppend => new(WriteOnlyDevNullId),
        WritableFileOpenMode.ReadWrite => DevNull,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>
    /// Gets the reserved descriptor representing a logically closed stream.
    /// </summary>
    public static FileDescriptor Closed => new(int.MinValue);

    /// <summary>
    /// Gets the canonical null-device path recognized by evaluation and hosts.
    /// </summary>
    public static ShellPath DevNullPath { get; } = new("/dev/null");

    /// <summary>
    /// Returns true for any stateless null-device handle, which requires no owned cleanup.
    /// </summary>
    public bool IsDevNull => Id is int.MaxValue or ReadOnlyDevNullId or WriteOnlyDevNullId;

    /// <summary>Returns true for a null-device handle that rejects writes.</summary>
    public bool IsReadOnlyDevNull => Id == ReadOnlyDevNullId;

    /// <summary>Returns true for a null-device handle that rejects reads.</summary>
    public bool IsWriteOnlyDevNull => Id == WriteOnlyDevNullId;

    /// <summary>
    /// Returns true when this descriptor represents a logically closed stream.
    /// </summary>
    public bool IsClosed => this == Closed;
}

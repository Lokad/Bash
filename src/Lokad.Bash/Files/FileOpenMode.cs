using System;

namespace Lokad.Bash.Files;

/// <summary>
/// Describes a read or writable open requested by shell redirection or file-tree callers.
/// </summary>
public enum FileOpenMode
{
    /// <summary>
    /// Opens a file for reading.
    /// </summary>
    Read,

    /// <summary>
    /// Opens a file for writing and truncates it.
    /// </summary>
    WriteTruncate,

    /// <summary>
    /// Opens a file for writing at EOF on every write.
    /// </summary>
    WriteAppend,

    /// <summary>
    /// Opens without truncation, with one read/write cursor starting at zero.
    /// </summary>
    ReadWrite
}

public static class FileOpenModes
{
    /// <summary>Narrows an open request after its read-only case has been handled.</summary>
    public static WritableFileOpenMode ToWritable(this FileOpenMode mode) => mode switch
    {
        FileOpenMode.WriteTruncate => WritableFileOpenMode.WriteTruncate,
        FileOpenMode.WriteAppend => WritableFileOpenMode.WriteAppend,
        FileOpenMode.ReadWrite => WritableFileOpenMode.ReadWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

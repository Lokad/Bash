using System;

namespace Lokad.Bash.Files;

/// <summary>A directory entry described without following symbolic links, including dangling links.</summary>
/// <remarks>Construct explicitly rather than using the default value.</remarks>
public readonly record struct DirectoryEntry
{
    private readonly FileSnapshot? _snapshot;

    /// <summary>Creates one child entry, not a parent path or a canonical absolute ShellPath.</summary>
    public DirectoryEntry(string name, FilePathKind kind)
    {
        if (name.Length == 0 || name.AsSpan().IndexOfAny('/', '\0') >= 0)
            throw new ArgumentException("A directory child name must be non-empty and cannot contain '/' or NUL.", nameof(name));
        if (kind is not (FilePathKind.File or FilePathKind.Directory or FilePathKind.Other
            or FilePathKind.BlockDevice or FilePathKind.CharacterDevice or FilePathKind.Fifo or FilePathKind.Socket
            or FilePathKind.SymbolicLink))
            throw new ArgumentOutOfRangeException(nameof(kind));

        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public FilePathKind Kind { get; }
    public bool IsDirectory => Kind == FilePathKind.Directory;

    /// <summary>A regular file version captured when requested and available without loading the child.</summary>
    public FileSnapshot? Snapshot
    {
        get => _snapshot;
        init
        {
            if (value is not null && Kind != FilePathKind.File)
                throw new ArgumentException("Only regular-file entries can carry snapshots.", nameof(value));
            _snapshot = value;
        }
    }
}

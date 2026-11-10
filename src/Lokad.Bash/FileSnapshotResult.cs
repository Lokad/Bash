using System;

using Lokad.Bash.Files;

namespace Lokad.Bash;

/// <summary>The outcome of selecting a content version, independent of the inspected entry kind.</summary>
public enum FileSnapshotOutcome
{
    /// <summary>A content version was captured.</summary>
    Captured,
    /// <summary>The entry is absent or its kind does not support snapshot capture.</summary>
    NotFile,
    /// <summary>Traversal, access or another failure prevented capture.</summary>
    Failed
}

/// <summary>A captured content version or the expected failure selecting it, independent of path information.</summary>
public sealed class FileSnapshotResult
{
    private FileSnapshotResult(FileSnapshotOutcome outcome, FileSnapshot? snapshot, OperationError? error)
    {
        Outcome = outcome;
        Snapshot = snapshot;
        Error = error;
    }

    /// <summary>Distinguishes a deliberate non-file rejection from a capture failure.</summary>
    public FileSnapshotOutcome Outcome { get; }

    /// <summary>The selected version on success; no descriptor or stream is owned.</summary>
    public FileSnapshot? Snapshot { get; }

    /// <summary>The diagnostic on failure; null exactly when Snapshot is present.</summary>
    public OperationError? Error { get; }

    public static FileSnapshotResult Success(FileSnapshot snapshot) => new(FileSnapshotOutcome.Captured, snapshot, null);
    public static FileSnapshotResult Failure(OperationError error) => new(FileSnapshotOutcome.Failed, null, error);

    /// <summary>Reports capture rejection for a missing path or non-regular entry.</summary>
    /// <remarks>Use Failure for traversal or access errors, even when inspection reports a non-file kind.</remarks>
    public static FileSnapshotResult NotFile(FilePathKind kind) => new(FileSnapshotOutcome.NotFile, null, kind switch
    {
        FilePathKind.Missing => new OperationError("No such file or directory\n") { Kind = OperationErrorKind.NotFound },
        FilePathKind.Directory => new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory },
        FilePathKind.Other or FilePathKind.BlockDevice or FilePathKind.CharacterDevice
            or FilePathKind.Fifo or FilePathKind.Socket or FilePathKind.SymbolicLink => new OperationError("Not a regular file\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });
}

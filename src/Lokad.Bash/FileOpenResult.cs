using System;

namespace Lokad.Bash;

/// <summary>The outcome of attempting to open a file descriptor.</summary>
public enum FileOpenOutcome
{
    /// <summary>The open failed with a diagnostic, whose cause may be unknown.</summary>
    Failed,
    /// <summary>A descriptor was opened successfully.</summary>
    Opened
}

/// <summary>A successfully opened descriptor or an explicit failure. The default is an unknown failure.</summary>
public readonly record struct FileOpenResult
{
    private readonly OperationError? _error;

    private FileOpenResult(FileDescriptor? fileDescriptor, OperationError? error, long? readSize)
    {
        FileDescriptor = fileDescriptor;
        _error = error;
        ReadSize = readSize;
    }

    /// <summary>The outcome, with Failed as the default value.</summary>
    public FileOpenOutcome Outcome => FileDescriptor.HasValue
        ? FileOpenOutcome.Opened
        : FileOpenOutcome.Failed;

    /// <summary>The successfully opened descriptor, or null for Failed.</summary>
    public FileDescriptor? FileDescriptor { get; }

    /// <summary>The diagnostic for Failed, or null exactly when a descriptor was opened.</summary>
    public OperationError? Error => FileDescriptor.HasValue ? null : _error ?? new OperationError("Cannot open file\n");

    /// <summary>The known byte length bound to a fresh read-only descriptor.</summary>
    /// <remarks>
    /// Independent of later path changes. Null when unknown, for write opens, or without a descriptor.
    /// Hosts must leave this null if obtaining the size would require reading the content.
    /// </remarks>
    public long? ReadSize { get; }

    /// <summary>Reports a successful open with its optional, nonnegative read size.</summary>
    public static FileOpenResult Opened(FileDescriptor fileDescriptor, long? readSize)
    {
        if (readSize < 0)
            throw new ArgumentOutOfRangeException(nameof(readSize));
        return new FileOpenResult(fileDescriptor, null, readSize);
    }

    /// <summary>Reports a failed open without a descriptor or read size.</summary>
    public static FileOpenResult Failed(OperationError error) => new(null, error, null);
}

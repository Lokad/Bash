using System;
using Lokad.Bash.Files;

namespace Lokad.Bash;

/// <summary>Requested path information and, independently, an optional file snapshot result.</summary>
public readonly struct FilePathResolutionResult
{
    private readonly FilePathInfo _info;

    private FilePathResolutionResult(FilePathInfo info, OperationError? error)
    {
        _info = info;
        Error = error;
    }

    /// <summary>The lookup failure, or null when Info is available, including for a missing path.</summary>
    public OperationError? Error { get; }

    /// <summary>
    /// The requested snapshot or its capture failure. Null only when capture was not requested.
    /// Inspect this independently of Error: inspecting an entry need not permit reading its content.
    /// A successful capture does not imply a regular file; Info supplies the entry's kind when available.
    /// </summary>
    public FileSnapshotResult? File { get; init; }

    /// <summary>The successful lookup. The default result describes a missing path.</summary>
    /// <exception cref="InvalidOperationException">The lookup failed; inspect Error before accessing Info.</exception>
    public FilePathInfo Info => Error == null
        ? _info : throw new InvalidOperationException("A failed lookup has no path information.");

    public static FilePathResolutionResult Success(FilePathInfo info) => new(info, null);
    public static FilePathResolutionResult Failure(OperationError error) => new(default, error);
}

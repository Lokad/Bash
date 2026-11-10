using System;
using System.Collections.Generic;

using Lokad.Bash.Files;

namespace Lokad.Bash;

/// <summary>A complete directory listing or an expected failure, distinct from an empty directory.</summary>
public readonly struct DirectoryListingResult
{
    private readonly IReadOnlyList<DirectoryEntry>? _entries;
    private readonly IReadOnlyList<FileMetadata?>? _metadata;

    private DirectoryListingResult(
        IReadOnlyList<DirectoryEntry>? entries, IReadOnlyList<FileMetadata?>? metadata, OperationError? error)
    {
        _entries = entries;
        _metadata = metadata;
        Error = error;
    }

    /// <summary>The listing failure, or null on success. Callers add command and path context.</summary>
    public OperationError? Error { get; }

    /// <summary>The complete entries on success. The default result is a successful empty listing.</summary>
    /// <exception cref="InvalidOperationException">The listing failed; inspect Error before accessing entries.</exception>
    public IReadOnlyList<DirectoryEntry> Entries => Error is null
        ? _entries ?? Array.Empty<DirectoryEntry>()
        : throw new InvalidOperationException("A failed directory listing has no entries.");

    /// <summary>
    /// Optional metadata in entry order, with one slot per entry; a null slot means unavailable metadata.
    /// Null means no metadata was supplied. Metadata describes the entry itself, without following child links.
    /// </summary>
    /// <exception cref="InvalidOperationException">The listing failed; inspect Error before accessing metadata.</exception>
    public IReadOnlyList<FileMetadata?>? Metadata => Error is null
        ? _metadata : throw new InvalidOperationException("A failed directory listing has no metadata.");

    /// <summary>Retains a completed listing without metadata; entries must not be mutated after publication.</summary>
    public static DirectoryListingResult Success(IReadOnlyList<DirectoryEntry> entries) => new(entries, null, null);

    /// <summary>Retains entries and equally sized metadata in the same order; neither collection may be mutated.</summary>
    public static DirectoryListingResult Success(IReadOnlyList<DirectoryEntry> entries, IReadOnlyList<FileMetadata?> metadata)
    {
        if (metadata.Count != entries.Count)
            throw new ArgumentException("Metadata must contain one slot per entry.", nameof(metadata));
        return new(entries, metadata, null);
    }

    /// <summary>Reports an expected failure without publishing partial entries.</summary>
    public static DirectoryListingResult Failure(OperationError error) => new(null, null, error);
}

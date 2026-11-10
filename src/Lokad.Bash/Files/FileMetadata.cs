using System;

namespace Lokad.Bash.Files;

/// <summary>Enumerates the kinds of paths reported by a filesystem host.</summary>
public enum FilePathKind
{
    Missing,
    /// <summary>A regular file.</summary>
    File,
    Directory,
    /// <summary>An existing entry whose kind is not classified by the host.</summary>
    Other,
    BlockDevice,
    CharacterDevice,
    /// <summary>A named pipe.</summary>
    Fifo,
    Socket,
    /// <summary>A symbolic link inspected without following its final component.</summary>
    SymbolicLink
}

/// <summary>Describes a filesystem path, with metadata only when requested for an existing entry.</summary>
public readonly record struct FilePathInfo
{
    private readonly FileMetadata? _metadata;

    public FilePathInfo(FilePathKind kind, FileMetadata metadata)
    {
        Kind = kind;
        _metadata = kind == FilePathKind.Missing ? null : metadata;
    }

    private FilePathInfo(FilePathKind kind)
    {
        Kind = kind;
        _metadata = null;
    }

    public FilePathKind Kind { get; }

    /// <summary>False for kind-only lookups and missing paths, including the default value.</summary>
    public bool HasMetadata => _metadata.HasValue;

    /// <summary>The requested metadata of an existing entry.</summary>
    /// <exception cref="InvalidOperationException">This lookup has no metadata.</exception>
    public FileMetadata Metadata => _metadata ?? throw new InvalidOperationException("This path lookup has no metadata.");

    /// <summary>Reports existence and kind without supplying metadata.</summary>
    public static FilePathInfo FromKind(FilePathKind kind) => new(kind);

    public override string ToString() => HasMetadata ? $"{Kind} ({Metadata})" : Kind.ToString();
}

/// <summary>Selects the information a host path lookup must retrieve.</summary>
public enum FilePathInfoRequest
{
    /// <summary>Return existence and kind without requiring metadata or reading file contents.</summary>
    KindOnly,

    /// <summary>Return metadata without reading file contents; size is null when unknown.</summary>
    Metadata,

    /// <summary>Return metadata with an exact size, retrieving file contents when needed.</summary>
    MetadataWithExactSize
}

/// <summary>Controls whether metadata lookup may read file contents to determine their length.</summary>
public enum FileSizeRequest
{
    /// <summary>Return the size when already known, without retrieving file contents.</summary>
    KnownOnly,

    /// <summary>Resolve an unknown size, allowing the host to retrieve the selected file contents.</summary>
    Exact
}

/// <summary>
/// Represents metadata for a filesystem entry.
/// </summary>
public readonly struct FileMetadata
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileMetadata"/> struct.
    /// </summary>
    /// <param name="size">The exact size in bytes, or null when unknown.</param>
    /// <param name="modifiedAt">The last modification time.</param>
    /// <param name="effectiveAccess">The entry's effective access in the host's execution context.</param>
    /// <param name="statusChangedAt">The last status/path change time, or null when unknown.</param>
    /// <param name="accessedAt">The access time, or null when unknown.</param>
    /// <param name="specialModeBits">Stored special mode bits, or null when unknown; None means known unset.</param>
    /// <param name="ownershipMatch">Effective ownership matches, or null when unknown; None means known non-matching.</param>
    /// <param name="identity">The filesystem object identity, or null when unknown.</param>
    public FileMetadata(
        long? size,
        DateTimeOffset modifiedAt,
        FileAccessRights effectiveAccess,
        DateTimeOffset? statusChangedAt,
        DateTimeOffset? accessedAt,
        FileSpecialModeBits? specialModeBits,
        FileOwnershipMatch? ownershipMatch,
        FileIdentity? identity)
    {
        Size = size;
        ModifiedAt = modifiedAt;
        EffectiveAccess = effectiveAccess;
        StatusChangedAt = statusChangedAt;
        AccessedAt = accessedAt;
        SpecialModeBits = specialModeBits;
        OwnershipMatch = ownershipMatch;
        Identity = identity;
    }

    /// <summary>
    /// Gets the exact size in bytes, or null when unknown without retrieving content.
    /// </summary>
    public long? Size { get; }

    /// <summary>Returns an already resolved size; never retrieves content.</summary>
    /// <exception cref="InvalidOperationException">The lookup did not resolve the file size.</exception>
    public long GetRequiredSize() => Size ?? throw new InvalidOperationException("The file size has not been resolved.");

    /// <summary>
    /// Gets the last modification time.
    /// </summary>
    public DateTimeOffset ModifiedAt { get; }

    /// <summary>
    /// Gets the entry's effective access, without any inherited permission policy.
    /// </summary>
    public FileAccessRights EffectiveAccess { get; }

    /// <summary>Stored special mode bits, or null when unknown. These do not describe effective access.</summary>
    public FileSpecialModeBits? SpecialModeBits { get; }

    /// <summary>Matches with the host's effective user/group, or null when unknown. These do not describe access rights.</summary>
    public FileOwnershipMatch? OwnershipMatch { get; }

    /// <summary>The filesystem object identity, or null when unknown; independent of content equality.</summary>
    public FileIdentity? Identity { get; }

    /// <summary>
    /// Gets the last status/path change time, or null when unknown. This is not a birth time.
    /// </summary>
    public DateTimeOffset? StatusChangedAt { get; }

    /// <summary>
    /// Gets the access time, or null when unknown. Other timestamps do not establish access time.
    /// </summary>
    public DateTimeOffset? AccessedAt { get; }
}

using System;

namespace Lokad.Bash;

/// <summary>A host-owned content snapshot that can be opened later without resolving its path again.</summary>
/// <remarks>
/// Resolving a snapshot allocates no descriptor and holds no disposable resources. Only the originating
/// host may open it. Implementations retain immutable content or a version reference, never an open
/// stream. The snapshot may be opened repeatedly, with an independent cursor for each open.
/// Hosts may override equality to recognize the same captured content without I/O. Equality must
/// include host ownership; unequal snapshots need not have different bytes.
/// </remarks>
public abstract class FileSnapshot
{
    /// <summary>Initializes the scheduling information without fetching content bytes.</summary>
    protected FileSnapshot(long? size, bool isBuffered)
    {
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        IsBuffered = isBuffered;
    }

    /// <summary>The known nonnegative content length, or null when unavailable without reading.</summary>
    public long? Size { get; }

    /// <summary>Whether reading this snapshot needs no external I/O, allowing matching during other loads.</summary>
    public bool IsBuffered { get; }

    /// <summary>Copies the captured MD5 into the first 16 destination bytes when already known.</summary>
    /// <remarks>
    /// The destination must hold at least 16 bytes. Return false without modifying it when the checksum
    /// is unavailable. A successful call writes the digest bytes in their standard display order.
    /// Implementations must use the captured content version, without resolving its path again or
    /// performing I/O or hashing. Path edits and deletion must not change the returned checksum.
    /// </remarks>
    public abstract bool TryGetKnownMd5(Span<byte> destination);
}

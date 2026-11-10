using System;
using System.Collections.Generic;

namespace Lokad.Bash.Files;

/// <summary>Whether lookup follows a symbolic link in the final path component.</summary>
public enum FileLinkFollowing
{
    Follow,
    NoFollow
}

/// <summary>An absolute lookup path and final-link policy, independent of metadata detail.</summary>
/// <remarks>
/// Preserves dots, dot-dot components and trailing slashes for the host to resolve in traversal order.
/// Intermediate links are always followed. A trailing slash requires a directory and follows the final
/// link even with NoFollow. Equality compares the preserved path text and policy.
/// </remarks>
public sealed record FilePathLookup
{
    /// <summary>The preserved traversal without lookup policy, for operations on the entry itself.</summary>
    public FileEntryPath EntryPath { get; }
    public string Path => EntryPath.Path;
    public FileLinkFollowing LinkFollowing { get; }

    public FilePathLookup(ShellPath path, FileLinkFollowing linkFollowing)
        : this(new FileEntryPath(path), linkFollowing) { }

    public FilePathLookup(RawShellPath path, ShellPath currentDirectory, FileLinkFollowing linkFollowing)
        : this(new FileEntryPath(path, currentDirectory), linkFollowing) { }

    public FilePathLookup(FileEntryPath path, FileLinkFollowing linkFollowing)
    {
        if (linkFollowing is not (FileLinkFollowing.Follow or FileLinkFollowing.NoFollow))
            throw new ArgumentOutOfRangeException(nameof(linkFollowing));
        EntryPath = path;
        LinkFollowing = linkFollowing;
    }

    public bool HasFinalDotComponent => EntryPath.HasFinalDotComponent;

    /// <summary>Allows creation of a missing final directory, including one followed by slashes.</summary>
    public (ShellPath Path, IReadOnlyList<ShellPath> RequiredDirectories) ResolveDirectoryCreationWithoutSymbolicLinks() =>
        EntryPath.ResolveDirectoryCreationWithoutSymbolicLinks();

    /// <summary>Resolves lexical checkpoints for providers whose filesystem has no symbolic links.</summary>
    public (ShellPath Path, IReadOnlyList<ShellPath> RequiredDirectories) ResolveWithoutSymbolicLinks() =>
        EntryPath.ResolveWithoutSymbolicLinks();
}

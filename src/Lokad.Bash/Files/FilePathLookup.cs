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

    /// <summary>Compares an operand with a stored lookup before allocating its absolute spelling.</summary>
    internal sealed class BatchComparer(ShellPath directory) : IEqualityComparer<FilePathLookup>,
        IAlternateEqualityComparer<(RawShellPath Path, FileLinkFollowing Following), FilePathLookup>
    {
        private readonly string _directory = directory.IsRoot ? string.Empty : directory.Path;

        public bool Equals(FilePathLookup? x, FilePathLookup? y) => x == y;

        public int GetHashCode(FilePathLookup lookup)
        {
            var path = StripDirectory(lookup.Path, out var withinDirectory);
            return Hash(path, withinDirectory, lookup.LinkFollowing);
        }

        public bool Equals((RawShellPath Path, FileLinkFollowing Following) operand, FilePathLookup lookup)
        {
            if (operand.Following != lookup.LinkFollowing)
                return false;
            var left = OperandKey(operand.Path, out var leftWithinDirectory);
            var right = StripDirectory(lookup.Path, out var rightWithinDirectory);
            return leftWithinDirectory == rightWithinDirectory && left.SequenceEqual(right);
        }

        public int GetHashCode((RawShellPath Path, FileLinkFollowing Following) operand)
        {
            var path = OperandKey(operand.Path, out var withinDirectory);
            return Hash(path, withinDirectory, operand.Following);
        }

        public FilePathLookup Create((RawShellPath Path, FileLinkFollowing Following) operand) =>
            new(operand.Path, directory, operand.Following);

        private ReadOnlySpan<char> OperandKey(RawShellPath operand, out bool withinDirectory)
        {
            if (operand.Raw[0] == '/')
                return StripDirectory(operand.Raw, out withinDirectory);
            withinDirectory = true;
            return operand.Raw;
        }

        private ReadOnlySpan<char> StripDirectory(string path, out bool withinDirectory)
        {
            var prefixLength = _directory.Length;
            withinDirectory = path.Length > prefixLength && path[prefixLength] == '/'
                && path.AsSpan(0, prefixLength).SequenceEqual(_directory);
            // Keep a repeated slash in the key. A suffix starting with '/' is distinct
            // from an equal spelling outside the working directory.
            return withinDirectory ? path.AsSpan(prefixLength + 1) : path.AsSpan();
        }

        private static int Hash(ReadOnlySpan<char> path, bool withinDirectory, FileLinkFollowing following) =>
            HashCode.Combine(string.GetHashCode(path, StringComparison.Ordinal), withinDirectory, following);
    }
}

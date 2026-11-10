using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Files;

namespace Lokad.Bash.Helpers;

public readonly struct ResolvedPath(ReadOnlyMemory<byte> display, ShellPath absolute, FilePathLookup lookup)
{
    public ReadOnlyMemory<byte> Display { get; } = display;

    public ShellPath Absolute { get; } = absolute;

    /// <summary>The original traversal to use for host lookups and reads.</summary>
    public FilePathLookup Lookup { get; } = lookup;

    public ResolvedPath(ReadOnlyMemory<byte> display, ShellPath absolute)
        : this(display, absolute, new FilePathLookup(absolute, FileLinkFollowing.Follow)) { }

    /// <summary>Estimates path payload and temporary copies, including a prospective child suffix.</summary>
    public long EstimateMemoryBytes(int extraBytes)
    {
        // Display bytes, absolute/lookup strings, and temporary copies made by TryChild.
        const int PathBytesPerCharacter = 8;
        var length = Math.Max(Display.Length, Math.Max(Absolute.Path.Length, Lookup.Path.Length));
        return PathBytesPerCharacter * ((long)length + extraBytes);
    }

    /// <summary>Appends a listing's child name without normalizing the parent lookup.</summary>
    public ResolvedPath Child(string name) => Child(name, FileLinkFollowing.Follow);

    /// <summary>Appends a child with an explicit final-link policy, preserving the parent traversal.</summary>
    public ResolvedPath Child(string name, FileLinkFollowing linkFollowing) => TryChild(name, linkFollowing, out var child)
        ? child : throw new ArgumentException($"'{name}' is not a valid child path segment.", nameof(name));

    public bool TryChild(string name, out ResolvedPath child) => TryChild(name, FileLinkFollowing.Follow, out child);

    private bool TryChild(string name, FileLinkFollowing linkFollowing, out ResolvedPath child)
    {
        if (Absolute.TryChild(name) is not { } absolute)
        {
            child = default;
            return false;
        }
        var display = Utf8Text.Decode(Display);
        var childDisplay = display.Length == 0 || display[^1] == '/' ? display + name : display + "/" + name;
        child = new ResolvedPath(Utf8Text.Encode(childDisplay), absolute,
            new FilePathLookup(RawShellPath.Parse(Lookup.Path.TrimEnd('/') + "/" + name),
                ShellPath.Root, linkFollowing));
        return true;
    }

    public override string ToString() => Absolute.Path;
}

/// <summary>
/// Provides path resolution helpers for shell-style unix paths.
/// </summary>
public static class PathResolution
{
    /// <summary>Distinguishes absence from a file blocking a path whose lookup reported Missing.</summary>
    /// <remarks>The caller supplies its path semantics; a null parent marks the root.</remarks>
    public static async Task<OperationErrorKind> ClassifyMissingPathAsync<TPath>(
        TPath path, Func<TPath, TPath?> getParent, Func<TPath, CancellationToken, Task<FilePathKind>> getKind,
        CancellationToken cancellationToken) where TPath : class
    {
        // Stop at the nearest existing ancestor; higher ancestors cannot explain this failure.
        for (var ancestor = getParent(path); ancestor is not null; ancestor = getParent(ancestor))
        {
            var kind = await getKind(ancestor, cancellationToken).ConfigureAwait(false);
            if (kind is not (FilePathKind.Missing or FilePathKind.Directory))
                return OperationErrorKind.NotDirectory;
            if (kind == FilePathKind.Directory)
                break;
        }
        return OperationErrorKind.NotFound;
    }

    public const string PathLimitMessage = "path exceeds the 4096-character or 64-component limit";

    private const int MaximumPathCharacters = 4096;
    private const int MaximumPathDepth = 64;
    private static readonly OperationError UnsafeMutationPath =
        new("Cannot safely modify this path; use a path without '..'\n");

    // Check raw paths before allocating segments or directory checkpoints. Dot segments
    // count toward the limit even when normalization would discard them.
    // The prefix is an absolute directory, or empty for the filesystem root.
    public static bool FitsPathLimits(ReadOnlySpan<char> prefix, ReadOnlySpan<char> path)
    {
        if (prefix.Length > MaximumPathCharacters || path.Length > MaximumPathCharacters)
            return false;

        var root = prefix.TrimEnd('/');
        var member = path.Trim('/');
        var length = root.Length + (member.IsEmpty ? 0 : 1 + member.Length);
        if (length > MaximumPathCharacters)
            return false;

        var depth = member.IsEmpty ? 0 : 1;
        foreach (var character in root)
            if (character == '/')
                depth++;
        foreach (var character in member)
            if (character == '/')
                depth++;
        return depth <= MaximumPathDepth;
    }

    public static ResolvedPath ResolveArgument(string raw, ShellPath currentDirectory)
    {
        if (TryResolveArgument(raw, currentDirectory, out var resolved))
            return resolved;

        // Only the throwing adapter needs to distinguish syntax errors from root escape.
        throw RawShellPath.IsValid(raw)
            ? RawShellPath.CreateAboveRootException(raw)
            : RawShellPath.CreateInvalidSyntaxException(raw);
    }

    /// <summary>Preserves a read operand for physical resolution, including parent traversal at the root.</summary>
    public static ResolvedPath ResolveReadArgument(string raw, ShellPath currentDirectory) =>
        TryResolveReadArgument(raw, currentDirectory, out var resolved)
            ? resolved : throw RawShellPath.CreateInvalidSyntaxException(raw);

    public static bool TryResolveReadArgument(string raw, ShellPath currentDirectory, out ResolvedPath resolved)
    {
        if (RawShellPath.TryParse(raw, out var path))
        {
            resolved = new ResolvedPath(Utf8Text.Encode(raw), path.ToAbsoluteClamped(currentDirectory, null),
                new FilePathLookup(path, currentDirectory, FileLinkFollowing.Follow));
            return true;
        }
        resolved = default;
        return false;
    }

    /// <summary>Checks that preserved traversal and a canonical mutation path name the same parent.</summary>
    /// <remarks>
    /// Use before operations whose collision checks or cleanup rely on the canonical path.
    /// The mutation path must be the lexical normalization of the preserved lookup.
    /// When '..' could change the parent, unknown or unequal directory identities reject the operation.
    /// This is a point-in-time check; the subsequent open still validates the full traversal.
    /// </remarks>
    public static async Task<OperationError?> CheckMutationParentAsync(
        IBashHost host, FilePathLookup path, ShellPath mutationPath, CancellationToken cancellationToken)
    {
        // Only '..' can change the parent when normalization crosses a directory link.
        var lookup = path.Path.TrimEnd('/');
        var lastParentTraversal = lookup.LastIndexOf("/../", StringComparison.Ordinal);
        if (lastParentTraversal < 0 && !lookup.EndsWith("/..", StringComparison.Ordinal))
            return null;

        // A final dot component has no entry name whose parent can be compared.
        if (path.HasFinalDotComponent)
            return UnsafeMutationPath;

        if (mutationPath.Equals(FileDescriptor.DevNullPath))
        {
            // A virtual /dev/null need not have a /dev directory. Prove that traversal
            // reaches the same root before the remaining suffix, which contains no '..'.
            var prefix = RawShellPath.Parse(lookup[..(lastParentTraversal + 3)]);
            if (prefix.ToAbsoluteClamped(ShellPath.Root, null).IsRoot
                && await CheckDirectoryIdentityAsync(host,
                    new FilePathLookup(prefix, ShellPath.Root, FileLinkFollowing.Follow), ShellPath.Root, cancellationToken)
                    .ConfigureAwait(false) is null)
                return null;
        }

        var parent = lookup[..lookup.LastIndexOf('/')];
        return await CheckDirectoryIdentityAsync(host,
            new FilePathLookup(RawShellPath.Parse(parent), ShellPath.Root, FileLinkFollowing.Follow),
            mutationPath.Parent ?? ShellPath.Root, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Requires preserved and canonical paths to identify the same existing directory.</summary>
    public static async Task<OperationError?> CheckDirectoryIdentityAsync(
        IBashHost host, FilePathLookup preserved, ShellPath canonical, CancellationToken cancellationToken)
    {
        var results = await host.ResolvePathsAsync(
            [preserved, new FilePathLookup(canonical, FileLinkFollowing.Follow)],
            FilePathInfoRequest.Metadata, FileSnapshotRequest.None, cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
            if (result.Error is { } error)
                return error;

        if (results[0].Info.Kind == FilePathKind.Directory && results[1].Info.Kind == FilePathKind.Directory
            && results[0].Info.Metadata.Identity is { } identity
            && identity == results[1].Info.Metadata.Identity)
            return null;

        return UnsafeMutationPath;
    }

    /// <summary>Resolves a command operand without throwing for invalid syntax or root escape.</summary>
    public static bool TryResolveArgument(string raw, ShellPath currentDirectory, out ResolvedPath resolved)
    {
        if (RawShellPath.TryParse(raw, out var path)
            && path.TryToAbsolute(currentDirectory, jail: false, requiredDirectories: null, out var absolute))
        {
            resolved = new ResolvedPath(Utf8Text.Encode(raw), absolute,
                new FilePathLookup(path, currentDirectory, FileLinkFollowing.Follow));
            return true;
        }

        resolved = default;
        return false;
    }

    /// <summary>Removes duplicate traversal roots and optionally descendants covered by another root.</summary>
    public static async Task<IReadOnlyList<ResolvedPath>> PruneCreatePathsAsync(
        IBashHost host, IReadOnlyList<ResolvedPath> candidates, bool pruneDescendants, CancellationToken cancellationToken)
    {
        var unique = new Dictionary<string, ResolvedPath>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetTraversalKey(candidate.Lookup, candidate.Absolute);
            if (unique.TryAdd(key, candidate))
                order.Add(key);
            else if (unique[key].Lookup != candidate.Lookup)
                await ValidatePrunedPathAsync(candidate).ConfigureAwait(false);
        }

        var result = new List<ResolvedPath>(unique.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = unique[key];
            if (pruneDescendants && HasParentPath(key))
                await ValidatePrunedPathAsync(candidate).ConfigureAwait(false);
            else
                result.Add(candidate);
        }
        return result;

        bool HasParentPath(string key)
        {
            // Check ancestors without comparing every pair, but never prune across '..'.
            var path = key.AsSpan();
            var lookup = unique.GetAlternateLookup<ReadOnlySpan<char>>();
            while (path.Length > 1)
            {
                var slash = path.LastIndexOf('/');
                if (path[(slash + 1)..].SequenceEqual(".."))
                    return false;
                path = slash == 0 ? "/" : path[..slash];
                if (lookup.ContainsKey(path))
                    return true;
            }
            return false;
        }

        async Task ValidatePrunedPathAsync(ResolvedPath path)
        {
            // Dropping a root must not hide checks imposed by dots or a trailing slash.
            if (path.Lookup.Path == path.Absolute.Path)
                return;
            var info = await host.GetPathInfoAsync(path.Lookup, FilePathInfoRequest.KindOnly, cancellationToken).ConfigureAwait(false);
            if (info.Kind == FilePathKind.Missing)
                throw new ShellPathInfoException(path.Lookup,
                    new OperationError("Cannot stat\n") { Kind = OperationErrorKind.NotFound });
        }
    }

    /// <summary>Ignores dots and repeated separators without collapsing '..' across possible directory links.</summary>
    /// <remarks>Equality does not validate the lookup's directory requirements.</remarks>
    public static string GetTraversalKey(FilePathLookup path, ShellPath absolute)
    {
        var lookup = path.Path.AsSpan();
        foreach (var segment in lookup.Split('/'))
        {
            if (!lookup[segment].SequenceEqual(".."))
                continue;

            // Canonical normalization would also erase '..'; keep those traversal steps.
            var key = new StringBuilder(lookup.Length);
            foreach (var part in lookup.Split('/'))
            {
                var component = lookup[part];
                if (!component.IsEmpty && !component.SequenceEqual("."))
                    key.Append('/').Append(component);
            }
            return key.ToString();
        }
        return absolute.Path;
    }

    /// <summary>
    /// Resolves a path against a current directory and returns an absolute unix-style path.
    /// </summary>
    /// <param name="path">
    /// The path to resolve. Relative paths are combined with <paramref name="currentDirectory"/>.
    /// Absolute paths are returned as-is after separator and dot-segment normalization.
    /// </param>
    /// <param name="currentDirectory">
    /// The base directory used for relative paths. If it is null, empty, or not absolute,
    /// the root path <c>/</c> is used.
    /// </param>
    /// <returns>
    /// An absolute path with duplicate separators collapsed and <c>.</c>/<c>..</c> segments resolved.
    /// </returns>
    /// <remarks>
    /// This helper does not trim whitespace and does not translate backslashes.
    /// </remarks>
    public static string ResolveAbsolute(string path, string currentDirectory)
    {
        var baseDirectory = string.IsNullOrEmpty(currentDirectory) || currentDirectory[0] != '/'
            ? "/"
            : currentDirectory;

        if (string.IsNullOrEmpty(path))
        {
            return baseDirectory;
        }

        var segments = new List<string>();
        if (path[0] != '/')
        {
            AppendSegments(baseDirectory.AsSpan(), segments);
        }

        AppendSegments(path.AsSpan(), segments);
        return BuildAbsolutePath(segments);
    }

    private static void AppendSegments(ReadOnlySpan<char> path, List<string> segments)
    {
        var start = 0;
        for (var index = 0; index <= path.Length; index++)
        {
            if (index < path.Length && path[index] != '/')
            {
                continue;
            }

            if (index > start)
            {
                var segment = path.Slice(start, index - start);
                if (segment.Length == 1 && segment[0] == '.')
                {
                    // skip "."
                }
                else if (segment.Length == 2 && segment[0] == '.' && segment[1] == '.')
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }
                }
                else
                {
                    segments.Add(segment.ToString());
                }
            }

            start = index + 1;
        }
    }

    private static string BuildAbsolutePath(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return "/";
        }

        return "/" + string.Join("/", segments);
    }
}

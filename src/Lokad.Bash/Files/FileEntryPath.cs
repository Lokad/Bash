using System;
using System.Collections.Generic;

namespace Lokad.Bash.Files;

/// <summary>An absolute preserved path identifying a filesystem entry, without a final-link policy.</summary>
/// <remarks>
/// Preserves dots, dot-dot components and trailing slashes for resolution in traversal order.
/// Entry operations follow intermediate links and act on the final entry itself; each operation
/// defines which trailing slashes are valid. Equality compares preserved text. The default is root.
/// </remarks>
public readonly record struct FileEntryPath
{
    // Null represents root so the default and an explicitly constructed root compare equally.
    private readonly string? _path;
    public string Path => _path ?? "/";

    public FileEntryPath(ShellPath path) : this(path.Path) { }

    public FileEntryPath(RawShellPath path, ShellPath currentDirectory)
        : this(path.Raw.StartsWith('/') ? path.Raw
            : currentDirectory.IsRoot ? "/" + path.Raw : currentDirectory.Path + "/" + path.Raw) { }

    private FileEntryPath(string path) => _path = path == "/" ? null : path;

    public bool HasFinalDotComponent
    {
        get
        {
            var path = Path.AsSpan().TrimEnd('/');
            return path.EndsWith("/.", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal);
        }
    }

    /// <summary>Allows creation of a missing final directory, including one followed by slashes.</summary>
    public (ShellPath Path, IReadOnlyList<ShellPath> RequiredDirectories) ResolveDirectoryCreationWithoutSymbolicLinks()
    {
        // A final dot or dot-dot still traverses an existing directory. Only trailing slashes
        // can be omitted for creation; the resulting directory satisfies their requirement.
        var trimmed = Path.TrimEnd('/');
        return (trimmed.Length == Path.Length ? this : new FileEntryPath(trimmed.Length == 0 ? "/" : trimmed))
            .ResolveWithoutSymbolicLinks();
    }

    /// <summary>Resolves lexical checkpoints for providers whose filesystem has no symbolic links.</summary>
    public (ShellPath Path, IReadOnlyList<ShellPath> RequiredDirectories) ResolveWithoutSymbolicLinks()
    {
        if (ShellPath.IsValid(Path))
            return (new ShellPath(Path), Array.Empty<ShellPath>());
        var directories = new List<ShellPath>();
        var path = RawShellPath.Parse(Path).ToAbsoluteClamped(ShellPath.Root, directories);
        // Defer a check only to the next lookup, without skipping unrelated checkpoints.
        // An equal checkpoint still requires a directory; the final target need not be one.
        var retained = 0;
        for (var index = 0; index < directories.Count; index++)
        {
            var checkedByNextLookup = index + 1 < directories.Count
                ? directories[index].IsEqualToOrParentOf(directories[index + 1])
                : directories[index].IsParentOf(path);
            if (!checkedByNextLookup)
                directories[retained++] = directories[index];
        }
        directories.RemoveRange(retained, directories.Count - retained);
        return (path, directories);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lokad.Bash;

/// <summary>
/// A well-formed but not yet canonicalized shell path, intended to represent
/// command-line arguments to commands in Lokad.Bash. May be relative or
/// absolute, and may contain '.' and '..' segments. The original raw string
/// is preserved unchanged.
/// </summary>
/// <remarks>
/// The same character constraints as <see cref="ShellPath"/> apply (no control
/// characters), but '.' and '..' segments are allowed, and the path need not be
/// absolute.
/// Call <see cref="ToAbsolute(ShellPath)"/> to resolve the path against a working directory.
/// </remarks>
public sealed class RawShellPath : IParsable<RawShellPath>
{
    /// <summary> The original raw string. </summary>
    public string Raw { get; }

    private RawShellPath(string raw) { Raw = raw; }

    /// <summary> Returns true if <paramref name="s"/> satisfies all RawShellPath constraints. </summary>
    public static bool IsValid(string? s) =>
        !string.IsNullOrEmpty(s) && s.All(c => !ShellPath.IsForbiddenChar(c));

    public static RawShellPath Parse(string s) => Parse(s, null);

    public static RawShellPath Parse(string s, IFormatProvider? provider)
    {
        if (!TryParse(s, provider, out var result))
            throw CreateInvalidSyntaxException(s);
        return result;
    }

    public static bool TryParse(string? s, IFormatProvider? provider, [NotNullWhen(true)] out RawShellPath? result)
    {
        if (s is not null && IsValid(s))
        {
            result = new RawShellPath(s);
            return true;
        }
        result = null;
        return false;
    }

    public static bool TryParse(string? s, [NotNullWhen(true)] out RawShellPath? result) =>
        TryParse(s, null, out result);

    /// <summary>
    /// Resolves this path to a canonical absolute <see cref="ShellPath"/> by
    /// prepending <paramref name="current"/> if relative, then eliminating all
    /// '.' and '..' segments.
    /// </summary>
    /// <param name="current"> The working directory used to resolve relative paths. </param>
    /// <exception cref="ShellPathException">
    /// Thrown when a '..' segment cannot be applied because the path is already
    /// at the root.
    /// </exception>
    public ShellPath ToAbsolute(ShellPath current) =>
        ToAbsolute(current, jail: false);

    /// <summary>
    /// Resolves this path to a canonical absolute <see cref="ShellPath"/>, optionally requiring
    /// the result to remain within <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The working directory used to resolve relative paths.</param>
    /// <param name="jail">
    /// If true, throws <see cref="ShellPathException"/> when the resolved path
    /// is not <paramref name="current"/> itself or a descendant of it.
    /// </param>
    /// <exception cref="ShellPathException">
    /// Thrown when a '..' segment cannot be applied because the path is already
    /// at the root, or when <paramref name="jail"/> is true and the resolved path
    /// escapes <paramref name="current"/>.
    /// </exception>
    public ShellPath ToAbsolute(ShellPath current, bool jail) =>
        ToAbsoluteCore(current, jail, null, clampAtRoot: false);

    /// <summary>
    /// Resolves this path and records every directory that must exist while processing dot and dot-dot
    /// components. It also records the canonical parent, or the canonical target itself when the raw path
    /// ends with a slash.
    /// </summary>
    /// <remarks>
    /// This only collects lexical checkpoints; callers perform existence and access checks.
    /// The set may be partially filled on failure. Jail checks final containment, not traversal.
    /// </remarks>
    public ShellPath ToAbsolute(
        ShellPath current,
        bool jail,
        HashSet<ShellPath> requiredDirectories) =>
        ToAbsoluteCore(current, jail, requiredDirectories, clampAtRoot: false);

    /// <summary>Normalizes with '..' at the root staying at the root; does not resolve symbolic links.</summary>
    public ShellPath ToAbsoluteClamped(ShellPath current, ICollection<ShellPath>? requiredDirectories) =>
        ToAbsoluteCore(current, jail: false, requiredDirectories, clampAtRoot: true);

    private ShellPath ToAbsoluteCore(
        ShellPath current,
        bool jail,
        ICollection<ShellPath>? requiredDirectories,
        bool clampAtRoot)
    {
        if (TryToAbsoluteCore(current, jail, requiredDirectories, clampAtRoot, out var result))
            return result;

        throw result is null
            ? CreateAboveRootException(Raw)
            : new ShellPathException(
                $"Cannot resolve '{Raw}': result '{result.Path}' escapes jail '{current.Path}'.");
    }

    /// <summary>Resolves this path without throwing for above-root or jail escape.</summary>
    /// <param name="current">The working directory for relative paths.</param>
    /// <param name="jail">Require the result to equal or descend from the working directory.</param>
    /// <param name="result">The canonical absolute path on success; null on failure.</param>
    public bool TryToAbsolute(ShellPath current, bool jail, [NotNullWhen(true)] out ShellPath? result) =>
        TryToAbsolute(current, jail, null, out result);

    /// <summary>
    /// Resolves without throwing and optionally records required directories, including checkpoints
    /// removed by normalization. The caller checks their existence; the set may be partial on failure.
    /// </summary>
    public bool TryToAbsolute(
        ShellPath current,
        bool jail,
        HashSet<ShellPath>? requiredDirectories,
        [NotNullWhen(true)] out ShellPath? result)
    {
        if (TryToAbsoluteCore(current, jail, requiredDirectories, clampAtRoot: false, out result))
            return true;

        result = null;
        return false;
    }

    // Retain a rejected jail candidate so the throwing wrapper can describe it without resolving twice.
    private bool TryToAbsoluteCore(
        ShellPath current,
        bool jail,
        ICollection<ShellPath>? requiredDirectories,
        bool clampAtRoot,
        [NotNullWhen(true)] out ShellPath? result)
    {
        var combined = Raw.StartsWith('/')
            ? Raw
            : current.Path.Length == 1   // current is "/"
                ? "/" + Raw
                : current.Path + "/" + Raw;

        var span = combined.AsSpan(1);   // skip leading '/'
        List<(int start, int len)>? segments = null;
        var checkpointChanged = true;

        foreach (var range in span.Split('/'))
        {
            var start = range.Start.Value;
            var len = range.End.Value - start;

            var isDot = len == 1 && span[start] == '.';
            var isDotDot = len == 2 && span[start] == '.' && span[start + 1] == '.';

            if (!isDot && !isDotDot && len > 0)
            {
                if (segments != null)
                    segments.Add((start + 1, len));
                checkpointChanged = true;
                continue;
            }

            // First special segment: allocate and backfill good segments seen so far.
            if (segments is null)
            {
                segments = [];

                foreach (var r in span[..start].Split('/'))
                {
                    var s = r.Start.Value;
                    var l = r.End.Value - s;
                    if (l > 0)
                        segments.Add((s + 1, l));
                }
            }

            // Repeated separators and dots leave the same directory in place. Avoid
            // rebuilding and validating its full path just to discard a duplicate.
            if (requiredDirectories != null && checkpointChanged)
            {
                requiredDirectories.Add(new ShellPath(BuildNormalizedPath(combined, segments)));
                checkpointChanged = false;
            }

            if (len == 0)
                continue;

            if (isDot)
                continue;

            // ".."
            if (segments.Count == 0)
            {
                if (clampAtRoot)
                    continue;
                result = null;
                return false;
            }
            segments.RemoveAt(segments.Count - 1);
            checkpointChanged = true;
        }

        // Fast path: no special segments found, combined is already canonical.
        var resultPath = segments is null
            ? combined
            : BuildNormalizedPath(combined, segments);

        result = new ShellPath(resultPath);
        if (requiredDirectories != null)
        {
            var lastSlash = combined.EndsWith('/') ? resultPath.Length : resultPath.LastIndexOf('/');
            requiredDirectories.Add(lastSlash <= 0
                ? ShellPath.Root
                : new ShellPath(resultPath[..lastSlash]));
        }

        return !jail || current.IsEqualToOrParentOf(result);

        static string BuildNormalizedPath(string source, List<(int start, int len)> normalizedSegments)
        {
            // One '/' per segment (including leading '/') plus all segment chars.
            var totalLength = Math.Max(1, normalizedSegments.Count);
            foreach (var (_, length) in normalizedSegments)
                totalLength += length;

            return string.Create(totalLength, (source, normalizedSegments), static (destination, state) =>
            {
                var (input, segments) = state;
                destination[0] = '/';
                var position = 1;
                for (var index = 0; index < segments.Count; index++)
                {
                    if (index > 0)
                        destination[position++] = '/';
                    var (segmentStart, segmentLength) = segments[index];
                    input.AsSpan(segmentStart, segmentLength).CopyTo(destination.Slice(position));
                    position += segmentLength;
                }
            });
        }
    }

    public static FormatException CreateInvalidSyntaxException(string raw) =>
        new($"'{raw}' is not a valid shell path.");

    public static ShellPathException CreateAboveRootException(string raw) =>
        new($"Cannot resolve '{raw}': '..' goes above the root.");

    public override string ToString() => Raw;
}

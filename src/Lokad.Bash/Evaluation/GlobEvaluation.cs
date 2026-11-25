using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Expands glob patterns using directory listings.
/// </summary>
/// <remarks>
/// Fixed prefixes select the initial directory. After wildcard expansion, every
/// ordinary suffix component must exist; unmatched patterns stay literal.
/// </remarks>
internal sealed class GlobEvaluation
{
    private static readonly IComparer<string> OutputPathOrder = Comparer<string>.Create(static (left, right) =>
    {
        var leftPath = left.AsSpan();
        var rightPath = right.AsSpan();
        var prefixLength = leftPath.CommonPrefixLength(rightPath);

        // A shared high surrogate may be the first half of two different scalars.
        if (prefixLength > 0 && char.IsHighSurrogate(leftPath[prefixLength - 1])
            && (StartsWithLowSurrogate(leftPath[prefixLength..]) || StartsWithLowSurrogate(rightPath[prefixLength..])))
            prefixLength--;

        var leftSuffix = leftPath[prefixLength..];
        var rightSuffix = rightPath[prefixLength..];
        if (leftSuffix.IsEmpty || rightSuffix.IsEmpty)
            return leftSuffix.Length.CompareTo(rightSuffix.Length);

        // Scalar order agrees with UTF-8 byte order. Compare just the differing characters.
        return FirstCharacter(leftSuffix).CompareTo(FirstCharacter(rightSuffix));

        static bool StartsWithLowSurrogate(ReadOnlySpan<char> suffix) =>
            !suffix.IsEmpty && char.IsLowSurrogate(suffix[0]);

        static int FirstCharacter(ReadOnlySpan<char> suffix) =>
            Rune.DecodeFromUtf16(suffix, out var rune, out _) == OperationStatus.Done ? rune.Value : suffix[0];
    });

    private readonly GlobPattern _pattern;
    private readonly ShellPath _directory;
    private readonly MemoryCapacity _memory;
    private readonly CancellationToken _cancellationToken;

    public GlobEvaluation(GlobPattern pattern, ShellPath directory, MemoryCapacity memory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        memory.Grow(256);
        _pattern = pattern;
        _directory = directory;
        _memory = memory;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Expands matching components in order, preserving the original word for literal fallback.</summary>
    public async Task<IReadOnlyList<string>> EvaluateAsync(IFileTester tester)
    {
        var baseline = _memory.Bytes;
        var cursor = 0;
        var text = _pattern.Pattern;
        Range component;
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_pattern.TryReadPathComponent(ref cursor, out component))
                return LiteralResult();
            if (GlobPattern.RequiresExpansion(text.AsSpan(component), _pattern.GetMask(component)))
                break;
        }

        List<Candidate> candidates = [];
        List<Candidate> matches = [];
        var firstWildcardStart = component.Start.Value;
        var prefix = PreparePrefix(firstWildcardStart, out var candidatePathBytes);
        _memory.PrepareListCapacity(candidates, 1);
        candidates.Add(prefix);
        var trailingSlash = text.EndsWith('/');
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var hasNext = _pattern.TryReadPathComponent(ref cursor, out var next);
            var markDirectory = !hasNext && trailingSlash;

            // Reaching dot components already required directory selection on the previous step.
            var name = text.AsMemory(component);
            if (name.Span is "." or "..")
            {
                var literal = name.Length == 1 ? "." : "..";
                for (var index = 0; index < candidates.Count; index++)
                {
                    var previous = candidates[index];
                    var replacement = new Candidate(
                        hasNext ? AppendPath(previous.ListPath, literal, markDirectory: false) : string.Empty,
                        AppendPath(previous.OutputPath, literal, markDirectory));
                    candidates[index] = replacement;
                    _memory.Resize(_memory.Bytes - previous.PathBytes);
                    candidatePathBytes = checked(candidatePathBytes - previous.PathBytes + replacement.PathBytes);
                }
                _memory.TrimExcess();
            }
            else
            {
                var requireDirectory = hasNext || markDirectory;
                var matchDot = name.Span[0] == '.';
                var hasWildcard = component.Start.Value == firstWildcardStart
                    || GlobPattern.RequiresExpansion(name.Span, _pattern.GetMask(component));
                long matchPathBytes = 0;
                using (var matcher = new TextGlobMatcher.PathComponentMatcher(name, _pattern.GetMaskMemory(component), _memory))
                {
                    foreach (var parent in candidates)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var entries = await tester.ListDirectoryAsync(parent.ListPath).ConfigureAwait(false);
                        _cancellationToken.ThrowIfCancellationRequested();
                        foreach (var entry in entries)
                        {
                            _cancellationToken.ThrowIfCancellationRequested();
                            var entryName = entry.Name.AsSpan();
                            if (entryName.IsEmpty || !matchDot && entryName[0] == '.')
                                continue;
                            if (requireDirectory && entry.Kind is not (FilePathKind.Directory or FilePathKind.SymbolicLink))
                                continue;
                            var matchesName = hasWildcard ? matcher.Matches(entryName, _cancellationToken) : name.Span.SequenceEqual(entryName);
                            if (!matchesName)
                                continue;

                            var checkLink = requireDirectory && entry.Kind == FilePathKind.SymbolicLink;
                            var lookupPath = hasNext || checkLink
                                ? AppendPath(parent.ListPath, entry.Name, markDirectory: false) : string.Empty;
                            if (checkLink)
                            {
                                var isDirectory = await tester.IsDirectoryAsync(lookupPath).ConfigureAwait(false);
                                if (!hasNext || !isDirectory)
                                {
                                    _memory.Resize(_memory.Bytes - TextBytes(lookupPath.Length));
                                    lookupPath = string.Empty;
                                }
                                if (!isDirectory)
                                    continue;
                            }
                            var match = new Candidate(lookupPath, AppendPath(parent.OutputPath, entry.Name, markDirectory));
                            _memory.PrepareListCapacity(matches, 1);
                            matches.Add(match);
                            matchPathBytes = checked(matchPathBytes + match.PathBytes);
                        }
                    }
                }

                if (matches.Count == 0)
                {
                    ReleaseCandidateLists();
                    return LiteralResult();
                }
                (candidates, matches) = (matches, candidates);
                matches.Clear();
                _memory.Resize(_memory.Bytes - candidatePathBytes);
                candidatePathBytes = matchPathBytes;
                _memory.TrimExcess();
            }

            if (!hasNext)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var resultBytes = MemoryCapacity.ArrayBackingBytes<string>(candidates.Count);
                _memory.Grow(resultBytes);
                var result = new string[candidates.Count];
                long outputPathBytes = 0;
                for (var index = 0; index < candidates.Count; index++)
                {
                    var path = candidates[index].OutputPath;
                    result[index] = path;
                    outputPathBytes = checked(outputPathBytes + TextBytes(path.Length));
                }
                Array.Sort(result, OutputPathOrder);
                _cancellationToken.ThrowIfCancellationRequested();
                ReleaseCandidateLists();
                _memory.Resize(checked(baseline + outputPathBytes + resultBytes));
                _memory.ReleaseUnusedCapacity();
                return result;
            }
            component = next;
        }

        IReadOnlyList<string> LiteralResult()
        {
            _memory.Resize(checked(baseline + MemoryCapacity.ArrayBackingBytes<string>(1)));
            _memory.ReleaseUnusedCapacity();
            return [text];
        }

        void ReleaseCandidateLists()
        {
            candidates.Clear();
            candidates.Capacity = 0;
            matches.Capacity = 0;
        }
    }

    /// <summary>Builds the literal prefix once per spelling, before any directory lookup.</summary>
    private Candidate PreparePrefix(int end, out long pathBytes)
    {
        var text = _pattern.Pattern;
        var root = _pattern.IsAbsolute ? "/" : _directory.Path;
        var trimmed = text.AsSpan(0, end).Trim('/');
        var lookupLength = trimmed.Length;
        for (var index = 1; index < trimmed.Length; index++)
            if (trimmed[index] == '/' && trimmed[index - 1] == '/')
                lookupLength--;

        var separator = root == "/" ? string.Empty : "/";
        var hasPrefix = lookupLength != 0;
        var totalLength = hasPrefix ? checked(root.Length + separator.Length + lookupLength) : 0;
        _cancellationToken.ThrowIfCancellationRequested();
        pathBytes = checked(TextBytes(end) + TextBytes(totalLength));
        _memory.Grow(pathBytes);
        var outputPath = text[..end];
        if (!hasPrefix)
            return new Candidate(root, outputPath);

        var start = end - text.AsSpan(0, end).TrimStart('/').Length;
        var lookupPath = string.Create(totalLength, (root, separator, text.AsMemory(start, trimmed.Length)),
            static (destination, state) =>
            {
                var (root, separator, prefix) = state;
                root.CopyTo(destination);
                separator.CopyTo(destination[root.Length..]);
                var position = root.Length + separator.Length;
                var previous = '\0';
                foreach (var character in prefix.Span)
                {
                    if (character != '/' || previous != '/')
                        destination[position++] = character;
                    previous = character;
                }
            });
        return new Candidate(lookupPath, outputPath);
    }

    private string AppendPath(string prefix, string name, bool markDirectory)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var separator = prefix.Length == 0 || prefix.EndsWith('/') ? string.Empty : "/";
        var suffix = markDirectory ? "/" : string.Empty;
        var length = checked(prefix.Length + separator.Length + name.Length + suffix.Length);
        _memory.Grow(TextBytes(length));
        return string.Concat(prefix, separator, name, suffix);
    }

    private static long TextBytes(int length) => length == 0 ? 0 : 32L + 2L * length;

    private readonly record struct Candidate(string ListPath, string OutputPath)
    {
        // Terminal candidates have no list path. A prefix can borrow the current directory.
        public long PathBytes => checked(TextBytes(ListPath.Length) + TextBytes(OutputPath.Length));
    }
}

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

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
    private readonly EvaluationMemory _memory;
    private readonly CancellationToken _cancellationToken;

    public GlobEvaluation(GlobPattern pattern, ShellPath directory, EvaluationMemory memory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        memory.Charge(256);
        _pattern = pattern;
        _directory = directory;
        _memory = memory;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Expands matching components in order, preserving the original word for literal fallback.</summary>
    public async Task<IReadOnlyList<string>> EvaluateAsync(IFileTester tester)
    {
        var cursor = 0;
        var text = _pattern.Pattern;
        Range component;
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_pattern.TryReadPathComponent(ref cursor, out component))
                return [text];
            if (GlobPattern.RequiresExpansion(text.AsSpan(component), _pattern.GetMask(component)))
                break;
        }

        List<Candidate> candidates = [PreparePrefix(component.Start.Value)];
        List<Candidate> matches = [];
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
                    candidates[index] = AppendComponent(candidates[index], literal, markDirectory);
            }
            else
            {
                var requireDirectory = hasNext || markDirectory;
                foreach (var parent in candidates)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var entries = await tester.ListDirectoryAsync(parent.ListPath).ConfigureAwait(false);
                    _cancellationToken.ThrowIfCancellationRequested();
                    var matchDot = name.Span[0] == '.';
                    foreach (var entry in entries)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var entryName = entry.Name.AsSpan();
                        if (entryName.IsEmpty || !matchDot && entryName[0] == '.')
                            continue;
                        if (requireDirectory && entry.Kind is not (FilePathKind.Directory or FilePathKind.SymbolicLink))
                            continue;
                        if (!TextGlobMatcher.MatchesPathComponent(name.Span, _pattern.GetMask(component), entryName,
                                _cancellationToken))
                            continue;

                        var match = AppendComponent(parent, entry.Name, markDirectory);
                        if (requireDirectory && entry.Kind == FilePathKind.SymbolicLink
                            && !await tester.IsDirectoryAsync(match.ListPath).ConfigureAwait(false))
                            continue;
                        matches.Add(match);
                    }
                }

                if (matches.Count == 0)
                    return [text];
                (candidates, matches) = (matches, candidates);
                matches.Clear();
            }

            if (!hasNext)
                return candidates.Select(static candidate => candidate.OutputPath).Order(OutputPathOrder).ToArray();
            component = next;
        }
    }

    /// <summary>Builds the literal prefix once per spelling, before any directory lookup.</summary>
    private Candidate PreparePrefix(int end)
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
        _memory.Charge(checked(4L * (end + (long)totalLength)));
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

    private Candidate AppendComponent(Candidate parent, string name, bool markDirectory)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _memory.Charge(256);
        return new Candidate(Join(parent.ListPath, string.Empty), Join(parent.OutputPath, markDirectory ? "/" : ""));

        string Join(string prefix, string suffix)
        {
            var separator = prefix.Length == 0 || prefix.EndsWith('/') ? string.Empty : "/";
            _memory.Charge(checked(4L * (prefix.Length + (long)separator.Length + name.Length + suffix.Length)));
            return string.Concat(prefix, separator, name, suffix);
        }
    }

    private readonly record struct Candidate(string ListPath, string OutputPath);
}

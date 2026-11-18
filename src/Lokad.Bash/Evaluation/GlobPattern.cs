using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Represents a glob pattern with per-character quoting metadata.
/// </summary>
internal sealed class GlobPattern
{
    private GlobPattern(
        string pattern,
        GlobMask metaMask)
    {
        Pattern = pattern;
        MetaMask = metaMask;
    }

    /// <summary>
    /// Gets the flattened pattern content.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Gets the per-character metadata indicating whether glob metacharacters are active.
    /// </summary>
    private GlobMask MetaMask { get; }

    /// <summary>
    /// Gets whether the pattern is absolute.
    /// </summary>
    public bool IsAbsolute => Pattern.Length > 0 && Pattern[0] == '/';

    /// <summary>Tests quote-aware fields before allocating pathname metadata.</summary>
    internal static bool RequiresExpansion(ExpandedWord word)
    {
        var hasClosingBracket = false;
        for (var segmentIndex = word.Segments.Count - 1; segmentIndex >= 0; segmentIndex--)
        {
            var segment = word.Segments[segmentIndex];
            // Glob operators are ASCII; inspect bytes without decoding individual segments.
            var bytes = segment.Content.Bytes.Span;
            for (var index = bytes.Length - 1; index >= 0; index--)
                if (ScanMetacharacter(bytes[index], !segment.IsGlobQuoted, ref hasClosingBracket))
                    return true;
        }
        return false;
    }

    internal static bool RequiresExpansion(ReadOnlySpan<char> pattern, in GlobMaskSpan mask)
    {
        var hasClosingBracket = false;
        for (var index = pattern.Length - 1; index >= 0; index--)
            if (ScanMetacharacter(pattern[index], mask[index] == GlobMask.Active, ref hasClosingBracket))
                return true;
        return false;
    }

    private static bool ScanMetacharacter(int character, bool active, ref bool hasClosingBracket)
    {
        if (character == '/')
            hasClosingBracket = false;
        if (!active)
            return false;
        if (character is '*' or '?' || character == '[' && hasClosingBracket)
            return true;

        // An unmatched '[' is literal, including the command name of the test builtin.
        if (character == ']')
            hasClosingBracket = true;
        return false;
    }

    /// <summary>Matches the whole pattern as text, without pathname component boundaries.</summary>
    public bool IsMatch(ReadOnlySpan<char> text) => IsMatch(text, CancellationToken.None);

    public bool IsMatch(ReadOnlySpan<char> text, CancellationToken cancellationToken)
    {
        var mask = MetaMask.AsSpan();
        return TextGlobMatcher.MatchText(Pattern.AsSpan(), in mask, text, cancellationToken) == GlobMatchResult.Match;
    }

    /// <summary>
    /// Builds a text glob with quoting metadata, rejecting non-UTF-8 input without throwing.
    /// </summary>
    public static bool TryFromExpandedWord(
        ExpandedWord word, EvaluationMemory memory, [NotNullWhen(true)] out GlobPattern? result)
    {
        if (word.Segments.Count == 0)
        {
            result = new GlobPattern(string.Empty, new GlobMask(ReadOnlyMemory<byte>.Empty));
            return true;
        }

        var value = word.ToValue(memory);
        memory.Charge(checked(4L * value.Bytes.Length));
        if (!value.TryGetUtf8Text(out var pattern))
        {
            result = null;
            return false;
        }
        memory.Charge(checked(4L * pattern.Length));
        // Retain the same conservative reservation when active metadata needs no storage.
        var hasQuotedContent = false;
        for (var index = 0; index < word.Segments.Count; index++)
            if (word.Segments[index].IsGlobQuoted && !word.Segments[index].Content.IsEmpty)
            {
                hasQuotedContent = true;
                break;
            }
        if (!hasQuotedContent)
        {
            result = new GlobPattern(pattern, new GlobMask(ReadOnlyMemory<byte>.Empty));
            return true;
        }

        var maskBytes = new byte[pattern.Length];
        var segmentIndex = 0;
        var segmentEnd = word.Segments[0].Content.Bytes.Length;
        var byteOffset = 0;
        var textOffset = 0;
        foreach (var rune in pattern.EnumerateRunes())
        {
            while (byteOffset >= segmentEnd)
                segmentEnd += word.Segments[++segmentIndex].Content.Bytes.Length;

            // Active glob syntax is ASCII; multibyte characters cannot introduce an operator.
            var maskValue = word.Segments[segmentIndex].IsGlobQuoted ? (byte)0 : GlobMask.Active;
            maskBytes.AsSpan(textOffset, rune.Utf16SequenceLength).Fill(maskValue);
            textOffset += rune.Utf16SequenceLength;
            byteOffset += rune.Utf8SequenceLength;
        }

        result = new GlobPattern(pattern, new GlobMask(maskBytes));
        return true;
    }

    /// <summary>Finds the next nonempty component in the original pattern and mask coordinates.</summary>
    public bool TryReadPathComponent(ref int cursor, out Range component)
    {
        var span = Pattern.AsSpan();
        while (cursor < span.Length && span[cursor] == '/')
            cursor++;
        if (cursor == span.Length)
        {
            component = default;
            return false;
        }

        var separator = span[cursor..].IndexOf('/');
        var end = separator < 0 ? span.Length : cursor + separator;
        component = cursor..end;
        cursor = end;
        return true;
    }

    public GlobMaskSpan GetMask(Range component) =>
        MetaMask.Memory.IsEmpty ? default : new GlobMaskSpan(MetaMask.Memory.Span[component]);
}

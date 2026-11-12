using System;
using System.Buffers;
using System.Text;
using System.Threading;

namespace Lokad.Bash.Helpers;

/// <summary>Matches text glob patterns with optional per-character quoting metadata.</summary>
/// <remarks>Text overloads retain UTF-16 code-unit matching; byte overloads use UTF-8 or malformed-input byte matching. Callers own pathname policies.</remarks>
public static class TextGlobMatcher
{
    /// <summary>Matches a pattern with every metacharacter active, without allocating a mask.</summary>
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        var mask = default(GlobMaskSpan);
        return IsMatch(pattern, in mask, text);
    }

    /// <summary>
    /// Matches glob wildcards against text, supporting ?, *, and bracket expressions.
    /// </summary>
    public static bool IsMatch(ReadOnlySpan<char> pattern, in GlobMaskSpan mask, ReadOnlySpan<char> text)
    {
        return MatchText(pattern, in mask, text, CancellationToken.None) == GlobMatchResult.Match;
    }

    /// <summary>Matches UTF-8 characters, falling back to bytes for malformed operands; returns a prefix length in bytes.</summary>
    public static GlobMatchResult MatchUtf8(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<byte> text,
        bool requireEnd, CancellationToken cancellationToken, out int length) =>
        MatchUtf8(pattern, in mask, text, requireEnd,
            !System.Text.Unicode.Utf8.IsValid(pattern) || !System.Text.Unicode.Utf8.IsValid(text), cancellationToken, out length);

    internal static GlobMatchResult MatchUtf8(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<byte> text,
        bool requireEnd, bool byteCharacters, CancellationToken cancellationToken, out int length) =>
        Match(new GlobInput(pattern, byteCharacters), in mask, new GlobInput(text, byteCharacters), requireEnd,
            shortestPrefix: false, cancellationToken, out length);

    /// <summary>Finds a character-aligned prefix without rescanning every candidate length.</summary>
    internal static GlobMatchResult MatchUtf8Prefix(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<byte> text,
        bool shortest, bool byteCharacters, CancellationToken cancellationToken, out int length) =>
        Match(new GlobInput(pattern, byteCharacters), in mask, new GlobInput(text, byteCharacters), requireEnd: false,
            shortest, cancellationToken, out length);

    /// <summary>Matches a complete text value with cooperative cancellation.</summary>
    public static GlobMatchResult MatchText(
        ReadOnlySpan<char> pattern, in GlobMaskSpan mask, ReadOnlySpan<char> text,
        CancellationToken cancellationToken) =>
        Match(new GlobInput(pattern), in mask, new GlobInput(text), true,
            shortestPrefix: false, cancellationToken, out _);

    private static GlobMatchResult Match(
        GlobInput pattern, in GlobMaskSpan mask, GlobInput text, bool requireEnd,
        bool shortestPrefix, CancellationToken cancellationToken, out int length)
    {
        length = -1;
        var p = 0;
        var t = 0;
        var star = -1;
        var match = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (p == pattern.Length && (!requireEnd || t == text.Length))
            {
                length = t;
                if (shortestPrefix)
                    return GlobMatchResult.Match;
            }
            if (t == text.Length)
                break;

            if (p < pattern.Length
                && mask[p] == GlobMask.Active
                && pattern[p] == '?')
            {
                p++;
                t += text.Width(t);
                continue;
            }

            if (p < pattern.Length && mask[p] == GlobMask.Active && pattern[p] == '*')
            {
                star = p++;
                match = t;
                continue;
            }

            if (p < pattern.Length && mask[p] == GlobMask.Active && pattern[p] == '[')
            {
                var bracketEnd = p;
                if (TryMatchBracket(pattern, in mask, ref bracketEnd, text[t], cancellationToken))
                {
                    p = bracketEnd;
                    t += text.Width(t);
                    continue;
                }
                if (bracketEnd != p)
                {
                    if (!TryAdvanceStar(ref p, ref t, star, ref match, text))
                        break;
                    continue;
                }
            }

            if (p < pattern.Length && pattern[p] == text[t])
            {
                p += pattern.Width(p);
                t += text.Width(t);
                continue;
            }

            if (TryAdvanceStar(ref p, ref t, star, ref match, text))
                continue;

            break;
        }

        while (p < pattern.Length && mask[p] == GlobMask.Active && pattern[p] == '*')
        {
            cancellationToken.ThrowIfCancellationRequested();
            p++;
        }

        if (p == pattern.Length && (!requireEnd || t == text.Length))
            length = t;
        return length >= 0 ? GlobMatchResult.Match : GlobMatchResult.NoMatch;

        static bool TryAdvanceStar(ref int p, ref int t, int star, ref int match, GlobInput text)
        {
            if (star < 0 || match == text.Length)
                return false;

            p = star + 1;
            match += text.Width(match);
            t = match;
            return true;
        }

        // A valid but nonmatching class must not fall back to matching its opening bracket literally.
        static bool TryMatchBracket(GlobInput pattern, in GlobMaskSpan mask, ref int position, int value, CancellationToken cancellationToken)
        {
            var start = position;
            position++;
            if (position >= pattern.Length)
            {
                position = start;
                return false;
            }

            var negate = false;
            if (mask[position] == GlobMask.Active && pattern[position] is '!' or '^')
            {
                negate = true;
                position++;
            }

            var matched = false;
            // A closing bracket is a literal member when it is first, including after negation.
            var firstMember = position;
            for (; position < pattern.Length
                   && (pattern[position] != ']' || mask[position] != GlobMask.Active || position == firstMember);)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pattern[position];
                var next = position + pattern.Width(position);
                if (next + 1 < pattern.Length && pattern[next] == '-' && mask[next] == GlobMask.Active
                    && (pattern[next + 1] != ']' || mask[next + 1] != GlobMask.Active))
                {
                    var startRange = current;
                    var endRange = pattern[next + 1];
                    if (value >= startRange && value <= endRange)
                    {
                        matched = true;
                    }

                    position = next + 1 + pattern.Width(next + 1);
                    continue;
                }

                if (current == value)
                {
                    matched = true;
                }
                position = next;
            }

            if (position >= pattern.Length || pattern[position] != ']')
            {
                position = start;
                return false;
            }

            position++;
            return negate ? !matched : matched;
        }
    }

    // Share the matching algorithm without allocating decoded copies or changing existing UTF-16 callers.
    private readonly ref struct GlobInput
    {
        private readonly ReadOnlySpan<char> _text;
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly bool _utf8;
        private readonly bool _byteCharacters;

        public GlobInput(ReadOnlySpan<char> text)
        {
            _text = text;
            _bytes = default;
            _utf8 = false;
            _byteCharacters = false;
        }

        public GlobInput(ReadOnlySpan<byte> bytes, bool byteCharacters)
        {
            _bytes = bytes;
            _text = default;
            _utf8 = true;
            _byteCharacters = byteCharacters;
        }

        public int Length => _utf8 ? _bytes.Length : _text.Length;
        public int Width(int index) => _utf8 && !_byteCharacters ? Utf8Text.FirstCharacterLength(_bytes[index..]) : 1;
        public int this[int index] => !_utf8 ? _text[index]
            : _byteCharacters ? _bytes[index]
            : Rune.DecodeFromUtf8(_bytes[index..], out var rune, out _) == OperationStatus.Done ? rune.Value
            : 0x110000 + _bytes[index]; // Invalid bytes must not alias Unicode replacement characters.
    }
}

public enum GlobMatchResult { NoMatch, Match }

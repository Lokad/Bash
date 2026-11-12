using System;
using System.Buffers;
using System.Text;
using System.Threading;
using Lokad.Bash.Evaluation;

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

    /// <summary>Matches glob wildcards against text, supporting ?, *, and bracket expressions.</summary>
    public static bool IsMatch(ReadOnlySpan<char> pattern, in GlobMaskSpan mask, ReadOnlySpan<char> text) =>
        MatchText(pattern, in mask, text, CancellationToken.None) == GlobMatchResult.Match;

    /// <summary>Matches UTF-8 characters, falling back to bytes for malformed operands; returns a prefix length in bytes.</summary>
    public static GlobMatchResult MatchUtf8(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<byte> text,
        bool requireEnd, CancellationToken cancellationToken, out int length) =>
        MatchUtf8(pattern, in mask, text, requireEnd,
            !System.Text.Unicode.Utf8.IsValid(pattern) || !System.Text.Unicode.Utf8.IsValid(text), cancellationToken, out length);

    internal static GlobMatchResult MatchUtf8(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<byte> text,
        bool requireEnd, bool byteCharacters, CancellationToken cancellationToken, out int length) =>
        Match(new PatternInput(new GlobInput(pattern, byteCharacters, reverse: false), mask, default, PatternTraversal.Forward),
            new GlobInput(text, byteCharacters, reverse: false), requireEnd, shortestPrefix: false, cancellationToken, out _, out length);

    /// <summary>Finds a character-aligned prefix without rescanning every candidate length.</summary>
    internal static GlobMatchResult MatchUtf8Prefix(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<byte> text,
        bool shortest, bool byteCharacters, CancellationToken cancellationToken, out int length) =>
        Match(new PatternInput(new GlobInput(pattern, byteCharacters, reverse: false), mask, default, PatternTraversal.Forward),
            new GlobInput(text, byteCharacters, reverse: false), requireEnd: false, shortest, cancellationToken, out _, out length);

    /// <summary>Finds the leftmost, longest match in one shared search, using an implicit leading wildcard.</summary>
    internal static GlobMatchResult FindUtf8(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<byte> text,
        bool byteCharacters, CancellationToken cancellationToken, out int start, out int length) =>
        Match(new PatternInput(new GlobInput(pattern, byteCharacters, reverse: false), mask, default, PatternTraversal.Search),
            new GlobInput(text, byteCharacters, reverse: false), requireEnd: false, shortestPrefix: false, cancellationToken, out start, out length);

    /// <summary>Matches a suffix by reading pattern atoms and text characters in reverse.</summary>
    internal static GlobMatchResult MatchUtf8Suffix(
        ReadOnlySpan<byte> pattern, in GlobMaskSpan mask, ReadOnlySpan<int> atoms, ReadOnlySpan<byte> text,
        bool shortest, bool byteCharacters, CancellationToken cancellationToken, out int length) =>
        Match(new PatternInput(new GlobInput(pattern, byteCharacters, reverse: false), mask, atoms, PatternTraversal.Reverse),
            new GlobInput(text, byteCharacters, reverse: true), requireEnd: false, shortest, cancellationToken, out _, out length);

    /// <summary>Prepares original atom offsets once, retaining bracket expressions as indivisible atoms.</summary>
    internal static ReadOnlyMemory<int> PrepareUtf8Atoms(ReadOnlySpan<byte> pattern, in GlobMaskSpan mask,
        bool byteCharacters, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pattern.Length >= Array.MaxLength)
            throw new ByteMemoryBuilder.LimitExceededException();
        var input = new GlobInput(pattern, byteCharacters, reverse: false);
        var lastClosingBracket = -1;
        for (var index = 0; index < pattern.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pattern[index] == (byte)']' && mask[index] == GlobMask.Active)
                lastClosingBracket = index;
        }
        var count = 0;
        for (var position = 0; position < input.Length; count++)
            position = NextAtom(input, in mask, position, lastClosingBracket, cancellationToken);
        memory.Charge(checked(32L + 4L * count));
        cancellationToken.ThrowIfCancellationRequested();
        var offsets = new int[count];
        count = 0;
        for (var position = 0; position < input.Length;)
        {
            offsets[count++] = position;
            position = NextAtom(input, in mask, position, lastClosingBracket, cancellationToken);
        }
        return offsets;

        static int NextAtom(GlobInput input, in GlobMaskSpan mask, int position, int lastClosingBracket,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = position;
            if (position < lastClosingBracket && mask[position] == GlobMask.Active && input[position] == '[')
                _ = TryMatchBracket(input, in mask, ref end, -1, cancellationToken);
            return end == position ? position + input.Width(position) : end;
        }
    }

    /// <summary>Matches a complete text value with cooperative cancellation.</summary>
    public static GlobMatchResult MatchText(
        ReadOnlySpan<char> pattern, in GlobMaskSpan mask, ReadOnlySpan<char> text,
        CancellationToken cancellationToken) =>
        Match(new PatternInput(new GlobInput(pattern), mask, default, PatternTraversal.Forward),
            new GlobInput(text), requireEnd: true, shortestPrefix: false, cancellationToken, out _, out _);

    private static GlobMatchResult Match(PatternInput pattern, GlobInput text, bool requireEnd,
        bool shortestPrefix, CancellationToken cancellationToken, out int start, out int length)
    {
        start = -1;
        length = -1;
        var p = 0;
        var t = 0;
        var star = -1;
        var match = 0;
        var origin = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (p == pattern.Length && (!requireEnd || t == text.Length))
            {
                start = origin;
                length = t - origin;
                if (shortestPrefix)
                    return GlobMatchResult.Match;
            }
            if (t == text.Length)
                break;
            if (p < pattern.Length && pattern.IsActive(p, '?'))
            {
                p = pattern.Next(p);
                t += text.Width(t);
                continue;
            }
            if (p < pattern.Length && pattern.IsActive(p, '*'))
            {
                star = p;
                p = pattern.Next(p);
                match = t;
                continue;
            }
            if (p < pattern.Length && pattern.IsActive(p, '['))
            {
                var bracketEnd = p;
                if (pattern.MatchBracket(ref bracketEnd, text[t], cancellationToken))
                {
                    p = bracketEnd;
                    t += text.Width(t);
                    continue;
                }
                if (bracketEnd != p)
                {
                    if (!TryAdvanceStar(ref p, ref t, star, ref match, ref origin, pattern, text, length >= 0))
                        break;
                    continue;
                }
            }
            if (p < pattern.Length && pattern[p] == text[t])
            {
                p = pattern.Next(p);
                t += text.Width(t);
                continue;
            }
            if (TryAdvanceStar(ref p, ref t, star, ref match, ref origin, pattern, text, length >= 0))
                continue;
            break;
        }
        while (p < pattern.Length && pattern.IsActive(p, '*'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            p = pattern.Next(p);
        }
        if (p == pattern.Length && (!requireEnd || t == text.Length))
        {
            start = origin;
            length = t - origin;
        }
        return length >= 0 ? GlobMatchResult.Match : GlobMatchResult.NoMatch;

        static bool TryAdvanceStar(ref int p, ref int t, int star, ref int match, ref int origin,
            PatternInput pattern, GlobInput text, bool found)
        {
            // Once a search matches, keep its leftmost start while extending its own stars.
            if (star < 0 || match == text.Length || found && pattern.IsSearchStar(star))
                return false;
            p = pattern.Next(star);
            match += text.Width(match);
            t = match;
            if (pattern.IsSearchStar(star))
                origin = match;
            return true;
        }
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

    private enum PatternTraversal { Forward, Reverse, Search }

    /// <summary>Maps traversal positions to original pattern atoms and their quoting metadata.</summary>
    private readonly ref struct PatternInput
    {
        private readonly GlobInput _source;
        private readonly GlobMaskSpan _mask;
        private readonly ReadOnlySpan<int> _atoms;
        private readonly PatternTraversal _traversal;
        private bool Reverse => _traversal == PatternTraversal.Reverse;
        private bool Search => _traversal == PatternTraversal.Search;

        public PatternInput(GlobInput source, GlobMaskSpan mask, ReadOnlySpan<int> atoms, PatternTraversal traversal)
        {
            _source = source;
            _mask = mask;
            _atoms = atoms;
            _traversal = traversal;
        }
        public int Length => (Reverse ? _atoms.Length : _source.Length) + (Search ? 1 : 0);
        public bool IsSearchStar(int position) => Search && position == 0;
        private int Original(int position) => Reverse ? _atoms[_atoms.Length - 1 - position + (Search ? 1 : 0)]
            : position - (Search ? 1 : 0);
        public int this[int position] => IsSearchStar(position) ? '*' : _source[Original(position)];
        public bool IsActive(int position, int value) => this[position] == value
            && (IsSearchStar(position) || _mask[Original(position)] == GlobMask.Active);
        public int Next(int position) => Reverse || IsSearchStar(position) ? position + 1
            : position + _source.Width(Original(position));
        public bool MatchBracket(ref int position, int value, CancellationToken cancellationToken)
        {
            var original = Original(position);
            var end = original;
            var matched = TryMatchBracket(_source, in _mask, ref end, value, cancellationToken);
            if (end != original)
                position = Reverse ? position + 1 : end + (Search ? 1 : 0);
            return matched;
        }
    }

    // Text is traversed in its native byte/code-unit representation; no decoded or reversed copy is needed.
    private readonly ref struct GlobInput
    {
        private readonly ReadOnlySpan<char> _text;
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly bool _utf8;
        private readonly bool _byteCharacters;
        private readonly bool _reverse;
        public GlobInput(ReadOnlySpan<char> text)
        {
            _text = text;
            _bytes = default;
            _utf8 = false;
            _byteCharacters = false;
            _reverse = false;
        }
        public GlobInput(ReadOnlySpan<byte> bytes, bool byteCharacters, bool reverse)
        {
            _bytes = bytes;
            _text = default;
            _utf8 = true;
            _byteCharacters = byteCharacters;
            _reverse = reverse;
        }
        public int Length => _utf8 ? _bytes.Length : _text.Length;
        public int Width(int index)
        {
            if (!_utf8 || _byteCharacters) return 1;
            if (!_reverse) return Utf8Text.FirstCharacterLength(_bytes[index..]);
            return Rune.DecodeLastFromUtf8(_bytes[..(_bytes.Length - index)], out _, out var width) == OperationStatus.Done ? width : 1;
        }
        public int this[int index]
        {
            get
            {
                if (!_utf8) return _text[index];
                if (_byteCharacters) return _bytes[_reverse ? _bytes.Length - index - 1 : index];
                var status = _reverse
                    ? Rune.DecodeLastFromUtf8(_bytes[..(_bytes.Length - index)], out var rune, out _)
                    : Rune.DecodeFromUtf8(_bytes[index..], out rune, out _);
                return status == OperationStatus.Done ? rune.Value
                    : 0x110000 + _bytes[_reverse ? _bytes.Length - index - 1 : index];
            }
        }
    }
}

public enum GlobMatchResult { NoMatch, Match }

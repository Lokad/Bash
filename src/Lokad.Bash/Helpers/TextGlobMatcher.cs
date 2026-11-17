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
        var count = 0;
        var scan = default(BracketScan);
        for (var position = 0; position < input.Length; count++)
            position = NextAtom(input, in mask, position, ref scan, cancellationToken);
        memory.Charge(checked(32L + 4L * count));
        cancellationToken.ThrowIfCancellationRequested();
        var offsets = new int[count];
        count = 0;
        scan = default;
        for (var position = 0; position < input.Length;)
        {
            offsets[count++] = position;
            position = NextAtom(input, in mask, position, ref scan, cancellationToken);
        }
        return offsets;

        static int NextAtom(GlobInput input, in GlobMaskSpan mask, int position,
            ref BracketScan scan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = position;
            if (mask[position] == GlobMask.Active && input[position] == '[')
                _ = TryMatchBracket(input, in mask, ref end, -1, ref scan, cancellationToken);
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
    static bool TryMatchBracket(GlobInput pattern, in GlobMaskSpan mask, ref int position, int value,
        ref BracketScan scan, CancellationToken cancellationToken)
    {
        if (!scan.HasFollowingClose(pattern, in mask, position, cancellationToken))
            return false;
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
            if (!ReadMember(pattern, in mask, position, ref scan, cancellationToken, out var member))
            {
                position = start;
                return false;
            }
            var next = member.End;
            if (!member.IsSet && next + 1 < pattern.Length && pattern[next] == '-' && mask[next] == GlobMask.Active
                && (pattern[next + 1] != ']' || mask[next + 1] != GlobMask.Active))
            {
                var endpoint = next + 1;
                var last = new BracketMember(endpoint + pattern.Width(endpoint), pattern[endpoint], default, IsSet: false);
                // Only a collating symbol is a range endpoint. A class/equivalence prefix
                // retains '[' as the ordinary endpoint, as in the existing bracket grammar.
                if (pattern[endpoint] == '[' && mask[endpoint] == GlobMask.Active && endpoint + 1 < pattern.Length
                    && pattern[endpoint + 1] == '.' && mask[endpoint + 1] == GlobMask.Active
                    && !ReadMember(pattern, in mask, endpoint, ref scan, cancellationToken, out last))
                {
                    position = start;
                    return false;
                }
                matched |= member.Value >= 0 && last.Value >= 0 && value >= member.Value && value <= last.Value;
                position = last.End;
                continue;
            }
            matched |= member.Value >= 0 ? member.Value == value : ShellCharacterClass.Matches(member.Class, value);
            position = next;
        }

        if (position >= pattern.Length || pattern[position] != ']')
        {
            position = start;
            return false;
        }

        position++;
        return negate ? !matched : matched;

        static bool ReadMember(GlobInput pattern, in GlobMaskSpan mask, int start, ref BracketScan scan,
            CancellationToken cancellationToken, out BracketMember member)
        {
            member = new(start + pattern.Width(start), pattern[start], default, IsSet: false);
            if (pattern[start] != '[' || mask[start] != GlobMask.Active || start + 1 >= pattern.Length
                || mask[start + 1] != GlobMask.Active)
                return true;
            var marker = pattern[start + 1];
            if (marker == '=')
            {
                // Equivalence elements have one unquoted character in the fixed profile.
                // Other spellings fall back to ordinary bracket members, not a locale lookup.
                var value = start + 2;
                if (value >= pattern.Length) return true;
                var closing = value + pattern.Width(value);
                if (closing + 1 < pattern.Length && Unquoted(in mask, value, closing)
                    && pattern[closing] == '=' && mask[closing] == GlobMask.Active
                    && pattern[closing + 1] == ']' && mask[closing + 1] == GlobMask.Active)
                    member = new(closing + 2, pattern[value], default, IsSet: true);
                return true;
            }
            if (marker is not (':' or '.'))
                return true;
            var close = scan.FindTerminator(pattern, in mask, start + 2, marker, cancellationToken);
            // A malformed class prefix retains ':' and its following name as ordinary
            // members, excluding the inner '['. An unfinished collating element makes
            // the enclosing bracket incomplete, retaining its literal fallback.
            if (close < 0)
            {
                if (marker != ':') return false;
                member = new(start + 2, ':', default, IsSet: false);
                return true;
            }
            if (marker == '.')
            {
                var value = start + 2;
                var single = value < close && value + pattern.Width(value) == close
                    && Unquoted(in mask, value, close) && mask[close] == GlobMask.Active;
                member = new(close + 2, single ? pattern[value] : -1, default, IsSet: false);
                return true;
            }

            var kind = ShellCharacterClass.Kind.None;
            var length = close - start - 2;
            if (length <= 6)
            {
                Span<byte> name = stackalloc byte[6];
                var count = 0;
                for (; count < length; count++)
                {
                    var character = pattern[start + 2 + count];
                    if (character > 127) break;
                    name[count] = (byte)character;
                }
                if (count == length)
                    _ = ShellCharacterClass.TryParse(name[..length], out kind);
            }
            // Quoting ordinary name letters does not change the class. The opening
            // markers and terminating ']' still retain their own structural masks.
            member = new(close + 2, -1, kind, IsSet: true);
            return true;

            static bool Unquoted(in GlobMaskSpan mask, int start, int end)
            {
                for (var index = start; index < end; index++)
                    if (mask[index] != GlobMask.Active) return false;
                return true;
            }
        }
    }

    private readonly record struct BracketMember(int End, int Value, ShellCharacterClass.Kind Class, bool IsSet);

    /// <summary>Reuses terminal lookahead while literal fallback advances through overlapping candidates.</summary>
    private struct BracketScan
    {
        private int _lastClosingBracket;
        private int _classStart;
        private int _classEnd;
        private int _symbolStart;
        private int _symbolEnd;

        public bool HasFollowingClose(GlobInput pattern, in GlobMaskSpan mask, int start, CancellationToken cancellationToken)
        {
            // Zero is unscanned, -1 is absent, and a positive value is offset plus one.
            // Literal fallback can reach many '[' positions; scan their shared suffix once.
            if (_lastClosingBracket == 0)
            {
                _lastClosingBracket = -1;
                for (var index = 0; index < pattern.Length; index += pattern.Width(index))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pattern[index] == ']' && mask[index] == GlobMask.Active)
                        _lastClosingBracket = index + 1;
                }
            }
            return _lastClosingBracket > start + 1;
        }

        public int FindTerminator(GlobInput pattern, in GlobMaskSpan mask, int start, int marker, CancellationToken cancellationToken)
        {
            ref var from = ref (marker == ':' ? ref _classStart : ref _symbolStart);
            ref var end = ref (marker == ':' ? ref _classEnd : ref _symbolEnd);
            // Zero is unscanned, -1 is an absent suffix terminator; a positive end
            // stores its physical offset plus one. Earlier candidates still rescan.
            if (end != 0 && from <= start && (end < 0 || start < end))
                return end < 0 ? -1 : end - 1;
            from = start;
            end = -1;
            for (var index = start; index + 1 < pattern.Length; index += pattern.Width(index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pattern[index] == marker && pattern[index + 1] == ']' && mask[index + 1] == GlobMask.Active)
                {
                    end = index + 1;
                    return index;
                }
            }
            return -1;
        }
    }

    private enum PatternTraversal { Forward, Reverse, Search }

    /// <summary>Maps traversal positions to original pattern atoms and their quoting metadata.</summary>
    private ref struct PatternInput
    {
        private readonly GlobInput _source;
        private readonly GlobMaskSpan _mask;
        private readonly ReadOnlySpan<int> _atoms;
        private readonly PatternTraversal _traversal;
        private BracketScan _bracketScan;
        private bool Reverse => _traversal == PatternTraversal.Reverse;
        private bool Search => _traversal == PatternTraversal.Search;

        public PatternInput(GlobInput source, GlobMaskSpan mask, ReadOnlySpan<int> atoms, PatternTraversal traversal)
        {
            _source = source;
            _mask = mask;
            _atoms = atoms;
            _traversal = traversal;
            _bracketScan = default;
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
            if (Reverse)
            {
                var following = _atoms.Length - position;
                var endOfAtom = following == _atoms.Length ? _source.Length : _atoms[following];
                if (endOfAtom == original + 1)
                    return false; // Atom preparation already established a literal '['.
            }
            var end = original;
            var matched = TryMatchBracket(_source, in _mask, ref end, value, ref _bracketScan, cancellationToken);
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

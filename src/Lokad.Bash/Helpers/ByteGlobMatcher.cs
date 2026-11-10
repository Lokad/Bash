using System;
using System.Buffers;

namespace Lokad.Bash.Helpers;

public enum ByteGlobCaseSensitivity
{
    Sensitive,
    AsciiInsensitive
}

public enum ByteGlobSeparatorMode
{
    MatchSeparators,
    RequireGlobStar,
    RequireGlobStarPrefix
}

/// <summary>Stores a parsed shell-style wildcard pattern.</summary>
public sealed class ByteGlobPattern
{
    public ByteGlobPattern(
        ByteGlobToken[] tokens,
        ByteGlobRange[] ranges,
        ByteGlobSegment[] segments,
        bool matchesSeparators,
        bool isEmpty,
        bool startsWithDot)
    {
        Tokens = tokens;
        Ranges = ranges;
        Segments = segments;
        MatchesSeparators = matchesSeparators;
        IsEmpty = isEmpty;
        StartsWithDot = startsWithDot;
    }

    public ByteGlobToken[] Tokens { get; }

    public ByteGlobRange[] Ranges { get; }

    public ByteGlobSegment[] Segments { get; }

    public bool MatchesSeparators { get; }

    public bool IsEmpty { get; }

    public bool StartsWithDot { get; }
}

public enum ByteGlobTokenKind : byte
{
    Literal,
    AnyByte,
    Star,
    Bracket
}

public readonly record struct ByteGlobToken(
    ByteGlobTokenKind Kind,
    byte Value,
    int RangeStart,
    int RangeCount,
    bool Negated);

public readonly record struct ByteGlobRange(byte Start, byte End);

public readonly record struct ByteGlobSegment(int TokenStart, int TokenCount, bool IsGlobStar);

/// <summary>Matches precompiled shell-style wildcard patterns over UTF-8 bytes.</summary>
public static class ByteGlobMatcher
{
    private const int MaximumStackPatternBytes = 128;

    public static bool IsMatch(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text) =>
        IsMatch(pattern, text, ignoreCase: false);

    public static bool IsMatch(
        ReadOnlySpan<byte> pattern,
        ReadOnlySpan<byte> text,
        bool ignoreCase)
    {
        var caseSensitivity = ignoreCase
            ? ByteGlobCaseSensitivity.AsciiInsensitive
            : ByteGlobCaseSensitivity.Sensitive;
        if (pattern.Length <= MaximumStackPatternBytes)
        {
            Span<ByteGlobToken> tokens = stackalloc ByteGlobToken[pattern.Length];
            Span<ByteGlobRange> ranges = stackalloc ByteGlobRange[pattern.Length];
            return MatchCompiledComponent(pattern, text, caseSensitivity, tokens, ranges);
        }

        var rentedTokens = ArrayPool<ByteGlobToken>.Shared.Rent(pattern.Length);
        try
        {
            var rentedRanges = ArrayPool<ByteGlobRange>.Shared.Rent(pattern.Length);
            try
            {
                return MatchCompiledComponent(
                    pattern,
                    text,
                    caseSensitivity,
                    rentedTokens.AsSpan(0, pattern.Length),
                    rentedRanges.AsSpan(0, pattern.Length));
            }
            finally
            {
                ArrayPool<ByteGlobRange>.Shared.Return(rentedRanges);
            }
        }
        finally
        {
            ArrayPool<ByteGlobToken>.Shared.Return(rentedTokens);
        }

        static bool MatchCompiledComponent(
            ReadOnlySpan<byte> source,
            ReadOnlySpan<byte> value,
            ByteGlobCaseSensitivity sensitivity,
            scoped Span<ByteGlobToken> tokenBuffer,
            scoped Span<ByteGlobRange> rangeBuffer)
        {
            var tokenCount = 0;
            var rangeCount = 0;
            CompileTokenSequence(
                source,
                tokenBuffer,
                ref tokenCount,
                rangeBuffer,
                ref rangeCount);
            return MatchesComponent(
                tokenBuffer[..tokenCount],
                tokenStart: 0,
                tokenCount,
                rangeBuffer[..rangeCount],
                value,
                sensitivity);
        }
    }

    /// <summary>Parses a pattern once for repeated matching.</summary>
    public static ByteGlobPattern Compile(
        ReadOnlyMemory<byte> pattern,
        ByteGlobSeparatorMode separatorMode)
    {
        var source = pattern.Span;
        if (source.Length <= MaximumStackPatternBytes)
        {
            Span<ByteGlobToken> tokens = stackalloc ByteGlobToken[source.Length];
            Span<ByteGlobRange> ranges = stackalloc ByteGlobRange[source.Length];
            return CompilePattern(source, tokens, ranges, separatorMode);
        }

        var rentedTokens = ArrayPool<ByteGlobToken>.Shared.Rent(source.Length);
        try
        {
            var rentedRanges = ArrayPool<ByteGlobRange>.Shared.Rent(source.Length);
            try
            {
                return CompilePattern(
                    source,
                    rentedTokens.AsSpan(0, source.Length),
                    rentedRanges.AsSpan(0, source.Length),
                    separatorMode);
            }
            finally
            {
                ArrayPool<ByteGlobRange>.Shared.Return(rentedRanges);
            }
        }
        finally
        {
            ArrayPool<ByteGlobToken>.Shared.Return(rentedTokens);
        }

        static ByteGlobPattern CompilePattern(
            ReadOnlySpan<byte> source,
            scoped Span<ByteGlobToken> tokenBuffer,
            scoped Span<ByteGlobRange> rangeBuffer,
            ByteGlobSeparatorMode separatorMode)
        {
            var tokenCount = 0;
            var rangeCount = 0;
            if (separatorMode == ByteGlobSeparatorMode.MatchSeparators)
            {
                CompileTokenSequence(
                    source,
                    tokenBuffer,
                    ref tokenCount,
                    rangeBuffer,
                    ref rangeCount);
                return new ByteGlobPattern(
                    tokenBuffer[..tokenCount].ToArray(),
                    rangeBuffer[..rangeCount].ToArray(),
                    Array.Empty<ByteGlobSegment>(),
                    matchesSeparators: true,
                    source.IsEmpty,
                    startsWithDot: !source.IsEmpty && source[0] == (byte)'.');
            }

            var segmentCount = 1;
            foreach (var value in source)
            {
                if (value == (byte)'/')
                    segmentCount++;
            }

            var segments = new ByteGlobSegment[segmentCount];
            var segmentIndex = 0;
            var segmentStart = 0;
            while (segmentStart <= source.Length)
            {
                var separator = source[segmentStart..].IndexOf((byte)'/');
                var segmentLength = separator < 0
                    ? source.Length - segmentStart
                    : separator;
                var segment = source.Slice(segmentStart, segmentLength);
                var tokenStart = tokenCount;
                CompileTokenSequence(
                    segment,
                    tokenBuffer,
                    ref tokenCount,
                    rangeBuffer,
                    ref rangeCount);
                segments[segmentIndex++] = new ByteGlobSegment(
                    tokenStart,
                    tokenCount - tokenStart,
                    segment.SequenceEqual("**"u8));
                if (separator < 0)
                    break;

                segmentStart += separator + 1;
            }

            return new ByteGlobPattern(
                tokenBuffer[..tokenCount].ToArray(),
                rangeBuffer[..rangeCount].ToArray(),
                segments,
                matchesSeparators: false,
                source.IsEmpty,
                startsWithDot: !source.IsEmpty && source[0] == (byte)'.');
        }

    }

    private static void CompileTokenSequence(
        ReadOnlySpan<byte> sequence,
        scoped Span<ByteGlobToken> tokens,
        ref int tokenCount,
        scoped Span<ByteGlobRange> ranges,
        ref int rangeCount)
    {
        for (var position = 0; position < sequence.Length; position++)
        {
            var value = sequence[position];
            if (value == (byte)'*')
            {
                while (position + 1 < sequence.Length
                       && sequence[position + 1] == (byte)'*')
                {
                    position++;
                }
                tokens[tokenCount++] = new ByteGlobToken(
                    ByteGlobTokenKind.Star,
                    Value: 0,
                    RangeStart: 0,
                    RangeCount: 0,
                    Negated: false);
                continue;
            }
            if (value == (byte)'?')
            {
                tokens[tokenCount++] = new ByteGlobToken(
                    ByteGlobTokenKind.AnyByte,
                    Value: 0,
                    RangeStart: 0,
                    RangeCount: 0,
                    Negated: false);
                continue;
            }
            if (value == (byte)'[')
            {
                var bracketPosition = position;
                var bracketRangeStart = rangeCount;
                if (TryCompileBracket(
                        sequence,
                        ref bracketPosition,
                        ranges,
                        ref rangeCount,
                        out var negated))
                {
                    tokens[tokenCount++] = new ByteGlobToken(
                        ByteGlobTokenKind.Bracket,
                        Value: 0,
                        bracketRangeStart,
                        rangeCount - bracketRangeStart,
                        negated);
                    position = bracketPosition - 1;
                    continue;
                }

                rangeCount = bracketRangeStart;
            }

            tokens[tokenCount++] = new ByteGlobToken(
                ByteGlobTokenKind.Literal,
                value,
                RangeStart: 0,
                RangeCount: 0,
                Negated: false);
        }
    }

    private static bool TryCompileBracket(
        ReadOnlySpan<byte> sequence,
        ref int position,
        scoped Span<ByteGlobRange> ranges,
        ref int rangeCount,
        out bool negated)
    {
        position++;
        if (position >= sequence.Length)
        {
            negated = false;
            return false;
        }

        negated = sequence[position] is (byte)'!' or (byte)'^';
        if (negated)
            position++;

        for (; position < sequence.Length && sequence[position] != (byte)']'; position++)
        {
            var start = sequence[position];
            if (position + 2 < sequence.Length
                && sequence[position + 1] == (byte)'-'
                && sequence[position + 2] != (byte)']')
            {
                ranges[rangeCount++] = new ByteGlobRange(start, sequence[position + 2]);
                position += 2;
            }
            else
            {
                ranges[rangeCount++] = new ByteGlobRange(start, start);
            }
        }

        if (position >= sequence.Length)
            return false;

        position++;
        return true;
    }

    /// <summary>Matches a parsed pattern with optional ASCII case folding and separator handling.</summary>
    public static bool IsMatch(
        ByteGlobPattern pattern,
        ReadOnlySpan<byte> text,
        ByteGlobCaseSensitivity caseSensitivity,
        ByteGlobSeparatorMode separatorMode)
    {
        var matchesSeparators = separatorMode == ByteGlobSeparatorMode.MatchSeparators;
        if (pattern.MatchesSeparators != matchesSeparators)
        {
            throw new ArgumentException(
                "The glob was compiled for a different separator mode.",
                nameof(separatorMode));
        }

        if (matchesSeparators)
        {
            return MatchesComponent(
                pattern.Tokens,
                tokenStart: 0,
                pattern.Tokens.Length,
                pattern.Ranges,
                text,
                caseSensitivity);
        }

        var segments = pattern.Segments;
        var patternSegmentIndex = 0;
        var textPosition = 0;
        var globStarPatternSegmentIndex = -1;
        var globStarTextPosition = -1;
        while (textPosition <= text.Length)
        {
            if (patternSegmentIndex < segments.Length)
            {
                var segment = segments[patternSegmentIndex];
                if (segment.IsGlobStar)
                {
                    globStarPatternSegmentIndex = patternSegmentIndex + 1;
                    globStarTextPosition = textPosition;
                    patternSegmentIndex++;
                    continue;
                }

                ReadTextSegment(text, textPosition, out var textSegment, out var nextTextPosition);
                if (MatchesComponent(
                        pattern.Tokens,
                        segment.TokenStart,
                        segment.TokenCount,
                        pattern.Ranges,
                        textSegment,
                        caseSensitivity))
                {
                    patternSegmentIndex++;
                    textPosition = nextTextPosition;
                    continue;
                }
            }

            if (globStarPatternSegmentIndex < 0 || globStarTextPosition > text.Length)
                return false;

            ReadTextSegment(text, globStarTextPosition, out _, out globStarTextPosition);
            patternSegmentIndex = globStarPatternSegmentIndex;
            textPosition = globStarTextPosition;
        }

        if (separatorMode == ByteGlobSeparatorMode.RequireGlobStarPrefix)
            return true;

        while (patternSegmentIndex < segments.Length)
        {
            if (!segments[patternSegmentIndex++].IsGlobStar)
                return false;
        }

        return true;

        static void ReadTextSegment(
            ReadOnlySpan<byte> value,
            int position,
            out ReadOnlySpan<byte> segment,
            out int nextPosition)
        {
            var separator = value[position..].IndexOf((byte)'/');
            if (separator < 0)
            {
                segment = value[position..];
                nextPosition = value.Length + 1;
                return;
            }

            segment = value.Slice(position, separator);
            nextPosition = position + separator + 1;
        }
    }

    private static bool MatchesComponent(
        ReadOnlySpan<ByteGlobToken> tokens,
        int tokenStart,
        int tokenCount,
        ReadOnlySpan<ByteGlobRange> ranges,
        ReadOnlySpan<byte> text,
        ByteGlobCaseSensitivity caseSensitivity)
    {
        var tokenEnd = tokenStart + tokenCount;
        var tokenIndex = tokenStart;
        var textIndex = 0;
        var starTokenIndex = -1;
        var starTextIndex = 0;
        while (textIndex < text.Length)
        {
            if (tokenIndex < tokenEnd)
            {
                var token = tokens[tokenIndex];
                switch (token.Kind)
                {
                    case ByteGlobTokenKind.Star:
                        starTokenIndex = ++tokenIndex;
                        starTextIndex = textIndex;
                        continue;
                    case ByteGlobTokenKind.AnyByte:
                        tokenIndex++;
                        textIndex++;
                        continue;
                    case ByteGlobTokenKind.Bracket:
                        if (MatchesBracket(token, ranges, text[textIndex], caseSensitivity))
                        {
                            tokenIndex++;
                            textIndex++;
                            continue;
                        }
                        break;
                    case ByteGlobTokenKind.Literal:
                        if (EqualsByte(token.Value, text[textIndex], caseSensitivity))
                        {
                            tokenIndex++;
                            textIndex++;
                            continue;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            if (starTokenIndex < 0 || starTextIndex >= text.Length)
                return false;

            tokenIndex = starTokenIndex;
            textIndex = ++starTextIndex;
        }

        while (tokenIndex < tokenEnd && tokens[tokenIndex].Kind == ByteGlobTokenKind.Star)
            tokenIndex++;

        return tokenIndex == tokenEnd;

        static bool MatchesBracket(
            ByteGlobToken token,
            ReadOnlySpan<ByteGlobRange> ranges,
            byte value,
            ByteGlobCaseSensitivity caseSensitivity)
        {
            var rangeEnd = token.RangeStart + token.RangeCount;
            for (var rangeIndex = token.RangeStart; rangeIndex < rangeEnd; rangeIndex++)
            {
                var range = ranges[rangeIndex];
                if (IsInRange(value, range.Start, range.End, caseSensitivity))
                    return !token.Negated;
            }

            return token.Negated;

            static bool IsInRange(
                byte value,
                byte start,
                byte end,
                ByteGlobCaseSensitivity caseSensitivity)
            {
                if (caseSensitivity == ByteGlobCaseSensitivity.Sensitive)
                    return value >= start && value <= end;

                var lowerValue = AsciiByte.ToLowerAscii(value);
                var lowerStart = AsciiByte.ToLowerAscii(start);
                var lowerEnd = AsciiByte.ToLowerAscii(end);
                return lowerValue >= lowerStart && lowerValue <= lowerEnd;
            }
        }

        static bool EqualsByte(
            byte left,
            byte right,
            ByteGlobCaseSensitivity caseSensitivity) =>
            caseSensitivity == ByteGlobCaseSensitivity.Sensitive
                ? left == right
                : AsciiByte.ToLowerAscii(left) == AsciiByte.ToLowerAscii(right);
    }
}

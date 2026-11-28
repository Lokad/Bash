using System;
using System.Buffers;
using System.Collections.Generic;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a shell word with preserved quoting context.
/// </summary>
/// <remarks>
/// Word parts retain quoting and escapes for expansion.
/// </remarks>
public sealed class Word
{
    private static readonly SearchValues<byte> UnquotedExpansionMarkers = SearchValues.Create("$\\`{}~"u8);

    public Word(IReadOnlyList<WordPart> parts)
    {
        Parts = parts;
        LiteralFields = PrepareLiteral(parts);
        if (LiteralFields != null)
            return;
        var hasHash = false;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            hasHash = hasHash || part.GetUnexpandedValue().Bytes.Span.Contains((byte)'#');
            if (part is CommandSubstitutionWordPart { Substitution: CommandSubstitutionSyntax.Parsed direct })
            {
                HasDecodedNulCapture |= direct.HasDecodedNulWord;
                HasRejectedCaptureSpelling |= direct.HasRejectedDisplaySpelling;
            }
            else if (part is SourceWordPart source)
            {
                HasIncompleteQuotedParameterSubscript |= source.ParameterSubscriptFailure != ParameterSubscriptFailure.None;
                if (source.SpellingCaptures is not { } captures)
                    continue;
                foreach (var capture in captures)
                {
                    var offset = capture.Offset - source.SourceOffset;
                    if (offset < 0 || capture.Length > source.Source.Length - offset
                        || capture.Syntax is not CommandSubstitutionSyntax.Parsed parsed)
                        continue;
                    HasRejectedCaptureSpelling |= parsed.HasRejectedDisplaySpelling;
                    HasDecodedNulCapture |= parsed.HasDecodedNulWord;
                    if (HasRejectedCaptureSpelling && HasDecodedNulCapture)
                        break;
                }
            }
        }
        HasBraceDelimiterCandidate = hasHash && HasArithmeticAndOpeningBrace(parts);

        static bool HasArithmeticAndOpeningBrace(IReadOnlyList<WordPart> parts)
        {
            var hasBrace = false;
            var hasArithmetic = false;
            for (var index = 0; index < parts.Count; index++)
            {
                var bytes = parts[index].GetUnexpandedValue().Bytes.Span;
                hasBrace = hasBrace || bytes.Contains((byte)'{');
                hasArithmetic = hasArithmetic || bytes.IndexOf("$(("u8) >= 0
                    || bytes.IndexOf("\\\n"u8) >= 0 && bytes.Contains((byte)'$') && bytes.Contains((byte)'(');
                if (hasBrace && hasArithmetic)
                    return true;
            }
            return false;
        }

        static IReadOnlyList<ExpandedWord>? PrepareLiteral(IReadOnlyList<WordPart> parts)
        {
            ShellValue value;
            bool quoted;
            if (parts is [LiteralWordPart literal])
            {
                value = literal.Value;
                quoted = true;
            }
            else if (parts is [SourceWordPart source])
            {
                var bytes = source.Source.AsSpan();
                if (source.Kind == WordPartKind.DoubleQuoted && bytes.IndexOfAny("$\\`"u8) < 0)
                    quoted = true;
                else if (source.Kind == WordPartKind.Unquoted && IsUnquotedLiteral(bytes))
                    quoted = false;
                else
                    return null;
                value = source.Source.ToValue();
            }
            else
                return null;
            return [new ExpandedWord([new ExpandedSegment(value, quoted)])];
        }

        static bool IsUnquotedLiteral(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return false;

            var marker = bytes.IndexOfAny(UnquotedExpansionMarkers);
            if (marker < 0)
                return true;
            if (marker == 0 || bytes[marker] != (byte)'~' || bytes[marker - 1] is (byte)':' or (byte)'=')
                return false;

            // An interior tilde can be literal. Everything before it has already been checked.
            var remaining = bytes[(marker + 1)..];
            return remaining.IndexOfAny("$\\`{}"u8) < 0
                && remaining.IndexOf(":~"u8) < 0 && remaining.IndexOf("=~"u8) < 0;
        }
    }

    /// <summary>Frozen literal expansion scaffolding borrowing immutable syntax bytes.</summary>
    internal IReadOnlyList<ExpandedWord>? LiteralFields { get; }
    internal bool HasIncompleteQuotedParameterSubscript { get; }
    internal bool HasRejectedCaptureSpelling { get; }
    internal bool HasBraceDelimiterCandidate { get; }
    internal bool HasDecodedNulCapture { get; }

    /// <summary>Removes an assignment prefix while retaining the already tokenized suffix.</summary>
    internal Word AfterSourcePrefix(SourceWordPart leading, int prefixLength, MemoryCapacity? memory)
    {
        var leadingLength = leading.Source.Length - prefixLength;
        var skippedParts = leadingLength > 0 ? 0 : 1;
        var partCount = Parts.Count;
        var count = partCount - skippedParts;
        if (count == 0)
            return new Word([]);

        memory?.Grow(MemoryCapacity.ArrayBackingBytes<WordPart>(count));
        var suffix = new WordPart[count];
        if (skippedParts == 0)
            suffix[0] = leading.Slice(prefixLength, leadingLength, memory);
        for (var index = 1; index < partCount; index++)
            suffix[index - skippedParts] = Parts[index];
        return new Word(suffix);
    }

    /// <summary>
    /// Gets the parts that compose this word.
    /// </summary>
    public IReadOnlyList<WordPart> Parts { get; }
}

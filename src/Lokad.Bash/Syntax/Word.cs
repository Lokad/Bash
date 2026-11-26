using System;
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
    public Word(IReadOnlyList<WordPart> parts)
    {
        Parts = parts;
        LiteralFields = PrepareLiteral(parts);
        if (LiteralFields != null)
            return;
        HasBraceDelimiterCandidate = BraceExpansion.HasCommentedDelimiterCandidate(parts);
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part is SourceWordPart { ParameterSubscriptFailure: not ParameterSubscriptFailure.None })
                HasIncompleteQuotedParameterSubscript = true;
            if (HasRejectedCaptureSpelling)
                continue;
            if (part is CommandSubstitutionWordPart { Substitution: CommandSubstitutionSyntax.Parsed { HasRejectedDisplaySpelling: true } })
                HasRejectedCaptureSpelling = true;
            else if (part is SourceWordPart { SpellingCaptures: { } captures } source)
            {
                foreach (var capture in captures)
                {
                    var offset = capture.Offset - source.SourceOffset;
                    if (offset >= 0 && capture.Length <= source.Source.Length - offset
                        && capture.Syntax is CommandSubstitutionSyntax.Parsed { HasRejectedDisplaySpelling: true })
                    {
                        HasRejectedCaptureSpelling = true;
                        break;
                    }
                }
            }
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
                else if (source.Kind == WordPartKind.Unquoted && bytes.Length > 0 && bytes[0] != (byte)'~'
                    && bytes.IndexOfAny("$\\`{}"u8) < 0
                    && bytes.IndexOf(":~"u8) < 0 && bytes.IndexOf("=~"u8) < 0)
                    quoted = false;
                else
                    return null;
                value = source.Source.ToValue();
            }
            else
                return null;
            return [new ExpandedWord([new ExpandedSegment(value, quoted)])];
        }
    }

    /// <summary>Frozen literal expansion scaffolding borrowing immutable syntax bytes.</summary>
    internal IReadOnlyList<ExpandedWord>? LiteralFields { get; }
    internal bool HasIncompleteQuotedParameterSubscript { get; }
    internal bool HasRejectedCaptureSpelling { get; }
    internal bool HasBraceDelimiterCandidate { get; }

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

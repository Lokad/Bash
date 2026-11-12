using System;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Helpers;
using Lokad.Bash.Commands;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Represents expanded byte-valued segments with preserved glob-quoting context.
/// </summary>
/// <remarks>Callers must not mutate retained segments.</remarks>
internal sealed class ExpandedWord(IReadOnlyList<ExpandedSegment> segments)
{
    /// <summary>
    /// Gets the expanded segments.
    /// </summary>
    public IReadOnlyList<ExpandedSegment> Segments { get; } = segments;

    /// <summary>Joins scalar or pattern operand fields without losing quote metadata.</summary>
    public static ExpandedWord JoinWithSpaces(IReadOnlyList<ExpandedWord> words, EvaluationMemory memory)
    {
        if (words.Count == 0)
            return new ExpandedWord([]);
        if (words.Count == 1)
            return words[0];

        var segmentCount = words.Sum(static word => (long)word.Segments.Count) + Math.Max(0, words.Count - 1);
        if (segmentCount >= Array.MaxLength)
            throw new ByteMemoryBuilder.LimitExceededException();
        memory.Charge(checked(256L * (segmentCount)));
        var segments = new List<ExpandedSegment>((int)segmentCount);
        var separator = ShellValue.FromText(" ");
        for (var index = 0; index < words.Count; index++)
        {
            if (index > 0)
                segments.Add(new ExpandedSegment(separator, isGlobQuoted: false));

            segments.AddRange(words[index].Segments);
        }

        return new ExpandedWord(segments);
    }

    /// <summary>
    /// Concatenates the expanded bytes without requiring individually valid UTF-8 segments.
    /// </summary>
    public ShellValue ToValue(EvaluationMemory memory)
    {
        if (Segments.Count == 0)
            return ShellValue.Empty;

        if (Segments.Count == 1)
            return Segments[0].Content;

        var length = Segments.Sum(static segment => (long)segment.Content.Bytes.Length);
        if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();
        memory.Charge(checked(4L * length));
        var bytes = new byte[(int)length];
        var destination = bytes.AsSpan();
        foreach (var segment in Segments)
        {
            segment.Content.Bytes.Span.CopyTo(destination);
            destination = destination[segment.Content.Bytes.Length..];
        }
        return ShellValue.FromBytes(bytes);
    }

    /// <summary>Decodes the joined value only at a text-only consumer boundary.</summary>
    public string ToText(EvaluationMemory memory)
    {
        var value = ToValue(memory);
        // UTF-16 output cannot exceed the UTF-8 byte count; reserve before decoding.
        memory.Charge(checked(4L * value.Bytes.Length));
        return value.ToUtf8String();
    }
}

/// <summary>
/// Represents a single expanded segment with glob-quoting context.
/// </summary>
/// <param name="content">The segment content.</param>
/// <param name="isGlobQuoted">True when glob metacharacters should be treated literally.</param>
internal readonly struct ExpandedSegment(ShellValue content, bool isGlobQuoted)
{
    /// <summary>
    /// Gets the segment content.
    /// </summary>
    public ShellValue Content { get; } = content;

    /// <summary>
    /// Gets whether glob metacharacters should be treated literally for this segment.
    /// </summary>
    public bool IsGlobQuoted { get; } = isGlobQuoted;
}

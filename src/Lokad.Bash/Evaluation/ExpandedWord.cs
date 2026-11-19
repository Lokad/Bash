using System;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Represents expanded byte-valued segments with preserved glob-quoting context.
/// </summary>
/// <remarks>Callers must not mutate retained segments.</remarks>
internal sealed class ExpandedWord(IReadOnlyList<ExpandedSegment> segments, bool isEmptyAggregateField)
{
    public ExpandedWord(IReadOnlyList<ExpandedSegment> segments) : this(segments, false) { }

    /// <summary>
    /// Gets the expanded segments.
    /// </summary>
    public IReadOnlyList<ExpandedSegment> Segments { get; } = segments;

    /// <summary>An empty element that was present in a quoted aggregate before operand splicing.</summary>
    internal bool IsEmptyAggregateField { get; } = isEmptyAggregateField;

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

        var bytes = AllocateValue(GetValueLength(), memory);
        CopyValueTo(bytes);
        return ShellValue.FromBytes(bytes);
    }

    /// <summary>Joins expanded fields directly for consumers that use only their bytes.</summary>
    internal static ShellValue JoinValuesWithSpaces(IReadOnlyList<ExpandedWord> words, EvaluationMemory memory)
    {
        if (words.Count == 0)
            return ShellValue.Empty;
        if (words.Count == 1)
            return words[0].ToValue(memory);

        var bytes = AllocateValue(GetJoinedValueLength(words), memory);
        CopyJoinedValueTo(words, bytes);
        return ShellValue.FromBytes(bytes);
    }

    internal static long GetJoinedValueLength(IReadOnlyList<ExpandedWord> words)
    {
        long length = Math.Max(0, words.Count - 1);
        for (var index = 0; index < words.Count; index++)
            length += words[index].GetValueLength();
        return length;
    }

    internal static void CopyJoinedValueTo(IReadOnlyList<ExpandedWord> words, Span<byte> destination)
    {
        for (var index = 0; index < words.Count; index++)
        {
            if (index > 0)
            {
                destination[0] = (byte)' ';
                destination = destination[1..];
            }
            destination = words[index].CopyValueTo(destination);
        }
    }

    private long GetValueLength()
    {
        var length = 0L;
        for (var index = 0; index < Segments.Count; index++)
            length += Segments[index].Content.Bytes.Length;
        return length;
    }

    private Span<byte> CopyValueTo(Span<byte> destination)
    {
        for (var index = 0; index < Segments.Count; index++)
        {
            var content = Segments[index].Content.Bytes.Span;
            content.CopyTo(destination);
            destination = destination[content.Length..];
        }
        return destination;
    }

    private static byte[] AllocateValue(long length, EvaluationMemory memory)
    {
        if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();
        memory.Charge(checked(4L * length));
        return new byte[(int)length];
    }

    /// <summary>Retains per-byte quoting for shell-word patterns and replacement operands.</summary>
    public GlobMask GetGlobMask(EvaluationMemory memory)
    {
        if (!Segments.Any(segment => segment.IsGlobQuoted && !segment.Content.IsEmpty))
            return new GlobMask(ReadOnlyMemory<byte>.Empty);
        var length = GetValueLength();
        if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();
        memory.Charge(checked(2L * length));
        var mask = new byte[(int)length];
        var position = 0;
        foreach (var segment in Segments)
        {
            var count = segment.Content.Bytes.Length;
            if (!segment.IsGlobQuoted)
                mask.AsSpan(position, count).Fill(GlobMask.Active);
            position += count;
        }
        return new GlobMask(mask);
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

using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Represents expanded byte-valued segments with preserved glob-quoting context.
/// </summary>
/// <remarks>Callers must not mutate retained segments.</remarks>
internal sealed class ExpandedWord(IReadOnlyList<ExpandedSegment> segments, bool isEmptyAggregateField)
{
    private static readonly ShellValue Space = ShellValue.FromText(" ");

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

        var segmentCount = (long)words.Count - 1;
        for (var index = 0; index < words.Count; index++)
            segmentCount += words[index].Segments.Count;
        if (segmentCount >= Array.MaxLength)
            throw new ByteMemoryBuilder.LimitExceededException();
        var count = (int)segmentCount;
        memory.Charge(MemoryCapacity.ArrayBackingBytes<ExpandedSegment>(count));
        var segments = new ExpandedSegment[count];
        var separator = new ExpandedSegment(Space, isGlobQuoted: false);
        var destination = 0;
        for (var index = 0; index < words.Count; index++)
        {
            if (index > 0)
                segments[destination++] = separator;

            var field = words[index].Segments;
            for (var part = 0; part < field.Count; part++)
            {
                var segment = field[part];
                segments[destination++] = segment;
            }
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

    internal static Span<byte> CopyJoinedValueTo(IReadOnlyList<ExpandedWord> words, Span<byte> destination)
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
        return destination;
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

    /// <summary>Projects joined bytes and quote runs directly from the expanded fields.</summary>
    internal static (ShellValue Value, GlobMask Mask) JoinPatternWithSpaces(
        IReadOnlyList<ExpandedWord> words, EvaluationMemory memory)
    {
        var value = JoinValuesWithSpaces(words, memory);
        var length = value.Bytes.Length;
        byte[]? mask = null;
        var position = 0;
        for (var index = 0; index < words.Count; index++)
        {
            if (index > 0)
            {
                if (mask != null)
                    mask[position] = GlobMask.Active;
                position++;
            }
            position = words[index].AppendGlobMask(length, position, memory, ref mask);
        }
        return (value, new GlobMask(mask));
    }

    private int AppendGlobMask(int length, int position, EvaluationMemory memory, ref byte[]? mask)
    {
        for (var index = 0; index < Segments.Count; index++)
        {
            var segment = Segments[index];
            var count = segment.Content.Bytes.Length;
            if (segment.IsGlobQuoted)
            {
                if (count != 0 && mask == null)
                {
                    // Every position before the first quoted byte is active.
                    if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                        throw new ByteMemoryBuilder.LimitExceededException();
                    memory.Charge(checked(2L * length));
                    mask = new byte[length];
                    mask.AsSpan(0, position).Fill(GlobMask.Active);
                }
            }
            else if (mask != null)
                mask.AsSpan(position, count).Fill(GlobMask.Active);
            position += count;
        }
        return position;
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

    /// <summary>Protects this segment's bytes from IFS when splicing a case pattern operand.</summary>
    internal bool IsCasePatternIfsProtected { get; init; }

    /// <summary>Identifies a synthesized DEL carried by a selected case-pattern operand.</summary>
    internal bool IsCaseQuotedNull { get; init; }

    /// <summary>Carries the treatment of spaces chosen while expanding a selected operand.</summary>
    internal OperandSpaces Spaces { get; init; }
}

internal enum OperandSpaces : byte { Default, Exposed, Protected }

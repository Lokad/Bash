using System;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>
/// Common helpers for byte-oriented line content used by shell tools.
/// </summary>
public static class ByteLines
{
    /// <summary>
    /// Finds up to <paramref name="limit"/> newline-terminated lines and returns their total byte length.
    /// The caller may refund the offsets' reservation if the completed batch cannot be published.
    /// </summary>
    public static int FindCompleteLines(
        ReadOnlySpan<byte> content,
        int limit,
        IMemoryBudget memoryBudget,
        out int[] lineStartOffsets,
        out IMemoryReservation? reservation) =>
        FindCompleteRecords(content, (byte)'\n', limit, examined: 0, reachedEof: false, maximumBatchBytes: int.MaxValue,
            memoryBudget, out lineStartOffsets, out reservation);

    /// <summary>Skips an examined delimiter-free prefix and returns offsets relative to the full input.</summary>
    /// <remarks>Returns the framed length even when the batch exceeds the byte limit and has no retained offsets.</remarks>
    internal static int FindCompleteRecords(
        ReadOnlySpan<byte> content,
        byte delimiter,
        int limit,
        int examined,
        bool reachedEof,
        int maximumBatchBytes,
        IMemoryBudget memoryBudget,
        out int[] recordStartOffsets,
        out IMemoryReservation? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        reservation = null;

        // Ordinary 128-record batches fit here. Large requests still allocate one exact array.
        Span<int> cachedStarts = stackalloc int[Math.Min(limit - 1, 127)];
        var recordCount = 0;
        var consumed = examined;
        while (recordCount < limit)
        {
            var delimiterOffset = content[consumed..].IndexOf(delimiter);
            if (delimiterOffset < 0)
                break;
            consumed += delimiterOffset + 1;
            if (recordCount < cachedStarts.Length)
                cachedStarts[recordCount] = consumed;
            recordCount++;
        }

        if (reachedEof && consumed < content.Length && recordCount < limit)
        {
            // The last delimiter has already supplied the start of the final record.
            consumed = content.Length;
            recordCount++;
        }
        if (recordCount <= 1 || consumed > maximumBatchBytes)
        {
            recordStartOffsets = Array.Empty<int>();
            return recordCount == 0 ? 0 : consumed;
        }

        // The returned offsets can outlive this read just like its retained byte snapshot.
        const int ArrayOverheadBytes = 32;
        reservation = memoryBudget.Reserve(ArrayOverheadBytes + sizeof(int) * (long)(recordCount - 1));
        try
        {
            recordStartOffsets = new int[recordCount - 1];
            var cachedCount = Math.Min(cachedStarts.Length, recordStartOffsets.Length);
            cachedStarts[..cachedCount].CopyTo(recordStartOffsets);
            var position = recordStartOffsets[cachedCount - 1];
            for (var index = cachedCount; index < recordStartOffsets.Length; index++)
            {
                position += content[position..].IndexOf(delimiter) + 1;
                recordStartOffsets[index] = position;
            }
            // The enclosing scope owns the returned offsets.
            return consumed;
        }
        catch { reservation.Dispose(); throw; }
    }

    /// <summary>
    /// Trims trailing CR and LF bytes from a span.
    /// </summary>
    public static ReadOnlySpan<byte> TrimLineEnding(ReadOnlySpan<byte> content)
    {
        var end = content.Length;
        while (end > 0)
        {
            var value = content[end - 1];
            if (value != (byte)'\n' && value != (byte)'\r')
                break;

            end--;
        }

        return content[..end];
    }

    /// <summary>
    /// Trims trailing CR and LF bytes from a memory block.
    /// </summary>
    public static ReadOnlyMemory<byte> TrimLineEnding(ReadOnlyMemory<byte> content)
    {
        var end = TrimLineEnding(content.Span).Length;
        return content[..end];
    }

    /// <summary>
    /// Ensures the content ends with a newline terminator.
    /// </summary>
    public static ReadOnlyMemory<byte> EnsureNewline(ReadOnlyMemory<byte> content) =>
        EnsureTerminator(content, (byte)'\n');

    /// <summary>
    /// Ensures the content ends with the specified terminator byte.
    /// </summary>
    public static ReadOnlyMemory<byte> EnsureTerminator(ReadOnlyMemory<byte> content, byte terminator)
    {
        if (!content.IsEmpty && content.Span[^1] == terminator)
            return content;

        return AppendTerminator(content, terminator);
    }

    /// <summary>
    /// Appends an LF record terminator, even when the content itself ends with LF.
    /// </summary>
    public static ReadOnlyMemory<byte> AppendNewline(ReadOnlyMemory<byte> content) =>
        AppendTerminator(content, (byte)'\n');

    /// <summary>
    /// Appends a record terminator, even when the content itself ends with that byte.
    /// </summary>
    public static ReadOnlyMemory<byte> AppendTerminator(ReadOnlyMemory<byte> content, byte terminator)
    {
        var buffer = new byte[content.Length + 1];
        content.CopyTo(buffer.AsMemory());
        buffer[^1] = terminator;
        return buffer;
    }
}

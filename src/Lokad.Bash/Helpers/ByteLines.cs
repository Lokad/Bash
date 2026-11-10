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
        out IMemoryReservation? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        reservation = null;

        var lineCount = 0;
        var consumed = 0;
        while (lineCount < limit)
        {
            var newlineOffset = content[consumed..].IndexOf((byte)'\n');
            if (newlineOffset < 0)
                break;
            lineCount++;
            consumed += newlineOffset + 1;
        }
        if (lineCount <= 1)
        {
            lineStartOffsets = Array.Empty<int>();
            return consumed;
        }

        // Count first so admission covers one exact array, with no growing scratch collection.
        // The returned offsets can outlive this read just like its retained byte snapshot.
        const int ArrayOverheadBytes = 32;
        reservation = memoryBudget.Reserve(ArrayOverheadBytes + sizeof(int) * (long)(lineCount - 1));
        try
        {
            lineStartOffsets = new int[lineCount - 1];
            var position = 0;
            for (var index = 0; index < lineStartOffsets.Length; index++)
            {
                position += content[position..].IndexOf((byte)'\n') + 1;
                lineStartOffsets[index] = position;
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

using System;

namespace Lokad.Bash.Helpers;

/// <summary>
/// Formats numeric values as UTF-8 byte sequences for tool output.
/// </summary>
public static class NumberFormatting
{
    private static readonly ReadOnlyMemory<byte> Zero = Utf8Text.Encode("0");

    /// <summary>
    /// Gets the number of fixed-size blocks required to contain a non-negative value.
    /// </summary>
    public static long GetBlockCount(long value, int blockSize)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));

        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize));

        return value / blockSize + (value % blockSize == 0 ? 0 : 1);
    }

    /// <summary>
    /// Formats a signed integer value using ASCII digits.
    /// </summary>
    public static ReadOnlyMemory<byte> FormatNumber(long value)
    {
        if (value == 0) return Zero;

        var negative = value < 0;
        // The positive magnitude of long.MinValue is not representable as a long.
        var remaining = negative ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        Span<byte> buffer = stackalloc byte[24];
        var length = 0;
        while (remaining > 0)
        {
            buffer[length++] = (byte)('0' + (remaining % 10));
            remaining /= 10;
        }

        if (negative)
        {
            buffer[length++] = (byte)'-';
        }

        var result = new byte[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = buffer[length - index - 1];
        }

        return result;
    }

    /// <summary>
    /// Formats byte counts into human-friendly units (K, M, G).
    /// </summary>
    public static ReadOnlyMemory<byte> FormatHumanSize(long bytes)
    {
        var suffix = ReadOnlyMemory<byte>.Empty;
        var value = bytes;

        switch (bytes)
        {
            case >= 1024L * 1024L * 1024L:
                value = bytes / (1024L * 1024L * 1024L);
                suffix = Utf8Text.Encode("G");
                break;
            case >= 1024L * 1024L:
                value = bytes / (1024L * 1024L);
                suffix = Utf8Text.Encode("M");
                break;
            case >= 1024L:
                value = bytes / 1024L;
                suffix = Utf8Text.Encode("K");
                break;
        }

        var number = FormatNumber(value);
        if (suffix.IsEmpty)
            return number;

        var buffer = new byte[number.Length + suffix.Length];
        number.CopyTo(buffer.AsMemory());
        suffix.CopyTo(buffer.AsMemory(number.Length));
        return buffer;
    }
}

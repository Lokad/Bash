using System;
using System.Buffers.Text;

namespace Lokad.Bash.Helpers;

/// <summary>
/// Formats integers and sizes as ASCII byte sequences.
/// </summary>
public static class NumberFormatting
{
    internal const int MaximumNumberLength = 20;
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

        Span<byte> buffer = stackalloc byte[MaximumNumberLength];
        return FormatNumber(value, buffer).ToArray();
    }

    internal static ReadOnlySpan<byte> FormatNumber(long value, Span<byte> destination)
    {
        if (!Utf8Formatter.TryFormat(value, destination, out var length))
            throw new ArgumentException("The number does not fit in the destination buffer.", nameof(destination));
        return destination[..length];
    }

    /// <summary>
    /// Formats byte counts into human-friendly units (K, M, G).
    /// </summary>
    public static ReadOnlyMemory<byte> FormatHumanSize(long bytes)
    {
        if (bytes < 1024)
            return FormatNumber(bytes);

        var (divisor, suffix) = bytes switch
        {
            >= 1024L * 1024L * 1024L => (1024L * 1024L * 1024L, (byte)'G'),
            >= 1024L * 1024L => (1024L * 1024L, (byte)'M'),
            _ => (1024L, (byte)'K'),
        };

        Span<byte> buffer = stackalloc byte[MaximumNumberLength + 1];
        var number = FormatNumber(bytes / divisor, buffer[..^1]);
        buffer[number.Length] = suffix;
        return buffer[..(number.Length + 1)].ToArray();
    }
}

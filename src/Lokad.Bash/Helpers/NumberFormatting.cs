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

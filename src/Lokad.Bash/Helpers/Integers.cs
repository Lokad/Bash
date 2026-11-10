using System;
using System.Buffers.Text;
using System.Globalization;
using System.Numerics;

namespace Lokad.Bash.Helpers;

/// <summary>Parses ASCII integer operands and formats invariant decimal values.</summary>
public static class Integers
{
    /// <summary>Parses a nonnegative Int32 from ASCII chars or bytes, without a leading plus.</summary>
    public static bool TryParseExitStatus<T>(ReadOnlySpan<T> input, out int value) where T : IBinaryInteger<T>
    {
        value = 0;
        if (!TryParseNumberToken(input, out var parsed, out _, out _))
        {
            return false;
        }

        if (parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryParseArrayIndex(ReadOnlySpan<char> span, out int index) =>
        TryParseExitStatus(span, out index);

    public static bool TryParseDescriptor(ReadOnlySpan<char> span, out int descriptor) =>
        TryParseExitStatus(span, out descriptor);

    public static bool TryParseInt(ReadOnlySpan<char> span, out int value)
    {
        value = 0;
        if (span.IsEmpty)
        {
            return false;
        }

        if (span[0] == '+')
        {
            span = span[1..];
        }

        return TryParseNumberToken(span, out value, out _, out _);
    }

    /// <summary>Parses a complete signed 64-bit decimal without whitespace or base prefixes.</summary>
    public static bool TryParseNumber(ReadOnlySpan<char> span, out long value)
    {
        value = 0;
        // Int64.TryParse otherwise accepts terminating NUL characters.
        return !span.Contains('\0')
            && long.TryParse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseNumberToken<T>(
        ReadOnlySpan<T> span,
        out int value,
        out int digits,
        out bool padded) where T : IBinaryInteger<T>
    {
        value = 0;
        digits = 0;
        padded = false;
        if (span.IsEmpty)
        {
            return false;
        }

        var index = 0;
        var negative = false;
        if (span[index] == T.CreateChecked('-'))
        {
            negative = true;
            index++;
        }

        if (index >= span.Length)
        {
            return false;
        }

        digits = span.Length - index;
        padded = digits > 1 && span[index] == T.CreateChecked('0');

        var parsed = 0L;
        var limit = negative ? -(long)int.MinValue : int.MaxValue;
        for (; index < span.Length; index++)
        {
            var valueUnit = span[index];
            if (valueUnit < T.CreateChecked('0') || valueUnit > T.CreateChecked('9'))
            {
                return false;
            }

            var digit = int.CreateChecked(valueUnit) - '0';
            if (parsed > (limit - digit) / 10)
            {
                return false;
            }

            parsed = (parsed * 10) + digit;
        }

        value = negative ? (int)-parsed : (int)parsed;
        return true;
    }

    /// <summary>
    /// Parses a complete signed 64-bit ASCII decimal without whitespace or base prefixes.
    /// </summary>
    public static bool TryParseNumber(ReadOnlySpan<byte> span, out long value)
    {
        value = 0;
        if (!Utf8Parser.TryParse(span, out long parsed, out var consumed, 'D') || consumed != span.Length)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Parses a signed decimal with surrounding ASCII whitespace and an optional
    /// K/M/G binary suffix. Rejects overflow after scaling.
    /// </summary>
    public static bool TryParseHumanNumber(ReadOnlySpan<byte> span, out long value)
    {
        value = 0;
        span = TrimWhitespace(span);
        if (span.IsEmpty)
        {
            return false;
        }

        long multiplier = 1;
        var suffix = span[^1];
        if (suffix is (byte)'K' or (byte)'k')
        {
            multiplier = 1024;
            span = span[..^1];
        }
        else if (suffix is (byte)'M' or (byte)'m')
        {
            multiplier = 1024L * 1024L;
            span = span[..^1];
        }
        else if (suffix is (byte)'G' or (byte)'g')
        {
            multiplier = 1024L * 1024L * 1024L;
            span = span[..^1];
        }

        if (!TryParseNumber(span, out var parsed)
            || parsed < long.MinValue / multiplier
            || parsed > long.MaxValue / multiplier)
        {
            return false;
        }

        value = parsed * multiplier;
        return true;
    }

    public static bool IsDigit(byte value)
    {
        return value is >= (byte)'0' and <= (byte)'9';
    }

    private static ReadOnlySpan<byte> TrimWhitespace(ReadOnlySpan<byte> span)
    {
        var start = 0;
        var end = span.Length - 1;

        while (start < span.Length && AsciiByte.IsWhitespace(span[start]))
        {
            start++;
        }

        while (end >= start && AsciiByte.IsWhitespace(span[end]))
        {
            end--;
        }

        return start <= end ? span[start..(end + 1)] : ReadOnlySpan<byte>.Empty;
    }

    /// <summary>
    /// Formats a signed 64-bit value as invariant decimal.
    /// </summary>
    public static string FormatNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}

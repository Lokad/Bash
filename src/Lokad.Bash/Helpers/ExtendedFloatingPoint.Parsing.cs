using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Lokad.Bash.Helpers;

internal readonly record struct FloatingPointPrefixResult(
    ExtendedFloatingPoint Value, int ConsumedBytes, bool HasNumber, bool RangeError);

public readonly partial record struct ExtendedFloatingPoint
{
    /// <summary>Reads a C-locale numeric prefix, retaining NaN payloads and classifying missing numbers.</summary>
    /// <param name="reserveWorkspace">Reserves peak conversion workspace before allocating temporary buffers.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    /// <param name="content">The input byte content.</param>
    public static ExtendedFloatingPoint ParseGeneral(
        ReadOnlySpan<byte> content,
        Action<long> reserveWorkspace,
        CancellationToken cancellationToken) =>
        ParseGeneralPrefix(content, reserveWorkspace, cancellationToken, retainNanPayload: true).Value;

    /// <summary>Retains prefix validity, sign and range causes for formatting NaNs and ordinary numbers.</summary>
    internal static FloatingPointPrefixResult ParseGeneralPrefix(
        ReadOnlySpan<byte> content, Action<long> reserveWorkspace, CancellationToken cancellationToken) =>
        ParseGeneralPrefix(content, reserveWorkspace, cancellationToken, retainNanPayload: false);

    private static FloatingPointPrefixResult ParseGeneralPrefix(
        ReadOnlySpan<byte> content, Action<long> reserveWorkspace, CancellationToken cancellationToken, bool retainNanPayload)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var totalBytes = content.Length;
        var prefix = DecimalPrefix.Parse(content, DecimalPrefixSyntax.GeneralNumeric, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (StartsWithIgnoreCase(prefix.Unsigned, "inf"u8))
        {
            var length = StartsWithIgnoreCase(prefix.Unsigned, "infinity"u8) ? 8 : 3;
            return new FloatingPointPrefixResult(
                new ExtendedFloatingPoint(prefix.Negative, FloatingPointClass.Infinity, 0, 0),
                totalBytes - prefix.Unsigned.Length + length, true, false);
        }
        if (StartsWithIgnoreCase(prefix.Unsigned, "nan"u8))
        {
            var length = 3;
            if (prefix.Unsigned.Length > 3 && prefix.Unsigned[3] == (byte)'(')
            {
                var cursor = 4;
                while (cursor < prefix.Unsigned.Length && (prefix.Unsigned[cursor] is >= (byte)'a' and <= (byte)'z'
                    or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
                {
                    CheckCancellation(cursor);
                    cursor++;
                }
                if (cursor < prefix.Unsigned.Length && prefix.Unsigned[cursor] == (byte)')')
                    length = cursor + 1;
            }
            var rangeError = false;
            var payload = length > 3 ? ReadNanPayload(prefix.Unsigned[4..(length - 1)], out rangeError) : 0;
            return new FloatingPointPrefixResult(
                new ExtendedFloatingPoint(prefix.Negative, FloatingPointClass.Nan, payload, 0),
                totalBytes - prefix.Unsigned.Length + length, true, rangeError);
        }
        if (prefix.Unsigned.StartsWith("0x"u8) || prefix.Unsigned.StartsWith("0X"u8))
            return ParseHexadecimal(prefix.Unsigned[2..], prefix.Negative);
        if (!prefix.HasDigits)
            return new FloatingPointPrefixResult(new ExtendedFloatingPoint(false, FloatingPointClass.NonNumber, 0, 0),
                0, false, false);
        return ParseDecimal(prefix);

        FloatingPointPrefixResult ParseDecimal(DecimalPrefix decimalPrefix)
        {
            var whole = decimalPrefix.Whole;
            var fraction = decimalPrefix.Fraction;
            var exponent = ReadExponent(decimalPrefix.Remainder, (byte)'e', out var exponentBytes) - fraction.Length;
            var consumed = totalBytes - decimalPrefix.Remainder.Length + exponentBytes;
            if (fraction.IsEmpty)
            {
                var trimmed = whole.TrimEnd((byte)'0');
                exponent += whole.Length - trimmed.Length;
                whole = trimmed;
            }
            if (whole.IsEmpty)
                fraction = fraction.TrimStart((byte)'0');
            var digits = whole.Length + fraction.Length;
            if (digits == 0 || digits + exponent < -5_000)
                return new FloatingPointPrefixResult(new ExtendedFloatingPoint(decimalPrefix.Negative, FloatingPointClass.Finite, 0, 0),
                    consumed, true, digits != 0);
            if (digits + exponent > 5_000)
                return new FloatingPointPrefixResult(new ExtendedFloatingPoint(decimalPrefix.Negative, FloatingPointClass.Infinity, 0, 0),
                    consumed, true, true);

            // Every rounding midpoint is a <=65-bit integer times a power of two, down to 2^-16446.
            // Its terminating decimal coefficient has fewer than 16446 + 20 significant digits.
            var truncated = LimitSignificand(ref whole, ref fraction, ref exponent, maximumDigits: 16_512, exponentStep: 1);
            digits = whole.Length + fraction.Length + (truncated ? 1 : 0);
            // Cover the digit buffer, BigInteger parsing/arithmetic and fixed rounding buffers.
            reserveWorkspace(32L * digits + 256 * 1024);
            cancellationToken.ThrowIfCancellationRequested();
            var numerator = ParseSignificand(whole, fraction, truncated, hexadecimal: false);
            var denominator = BigInteger.One;
            if (exponent >= 0)
                numerator *= BigInteger.Pow(10, (int)exponent);
            else
                denominator = BigInteger.Pow(10, (int)-exponent);
            return ConvertRational(numerator, denominator, decimalPrefix.Negative, consumed);
        }

        FloatingPointPrefixResult ParseHexadecimal(ReadOnlySpan<byte> hexadecimal, bool negative)
        {
            var index = 0;
            while (index < hexadecimal.Length && ShellEscapeDecoder.TryDigitValue((char)hexadecimal[index], 16, out _))
            {
                CheckCancellation(index);
                index++;
            }
            var whole = hexadecimal[..index].TrimStart((byte)'0');
            ReadOnlySpan<byte> fraction = default;
            if (index < hexadecimal.Length && hexadecimal[index] == (byte)'.')
            {
                var start = ++index;
                while (index < hexadecimal.Length && ShellEscapeDecoder.TryDigitValue((char)hexadecimal[index], 16, out _))
                {
                    CheckCancellation(index);
                    index++;
                }
                fraction = hexadecimal[start..index].TrimEnd((byte)'0');
            }

            var hasDigits = index != 0 && (index != 1 || hexadecimal[0] != (byte)'.');
            if (!hasDigits)
                return new FloatingPointPrefixResult(new ExtendedFloatingPoint(negative, FloatingPointClass.Finite, 0, 0),
                    totalBytes - hexadecimal.Length - 1, true, false);
            var exponent = ReadExponent(hexadecimal[index..], (byte)'p', out var exponentBytes) - 4L * fraction.Length;
            var consumed = totalBytes - hexadecimal.Length + index + exponentBytes;
            if (fraction.IsEmpty)
            {
                var trimmed = whole.TrimEnd((byte)'0');
                exponent += 4L * (whole.Length - trimmed.Length);
                whole = trimmed;
            }
            if (whole.IsEmpty)
                fraction = fraction.TrimStart((byte)'0');
            var digits = whole.Length + fraction.Length;
            if (digits == 0 || 4L * digits + exponent < -16_500)
                return new FloatingPointPrefixResult(new ExtendedFloatingPoint(negative, FloatingPointClass.Finite, 0, 0),
                    consumed, true, digits != 0);
            if (4L * digits + exponent > 16_500)
                return new FloatingPointPrefixResult(new ExtendedFloatingPoint(negative, FloatingPointClass.Infinity, 0, 0),
                    consumed, true, true);

            // 32 hexadecimal digits exceed the 65 significant bits of every rounding midpoint.
            var truncated = LimitSignificand(ref whole, ref fraction, ref exponent, maximumDigits: 32, exponentStep: 4);
            digits = whole.Length + fraction.Length + (truncated ? 1 : 0);
            reserveWorkspace(32L * digits + 256 * 1024);
            cancellationToken.ThrowIfCancellationRequested();
            var numerator = ParseSignificand(whole, fraction, truncated, hexadecimal: true);
            var denominator = BigInteger.One;
            if (exponent >= 0)
                numerator <<= (int)exponent;
            else
                denominator <<= (int)-exponent;
            return ConvertRational(numerator, denominator, negative, consumed);
        }

        static BigInteger ParseSignificand(ReadOnlySpan<byte> whole, ReadOnlySpan<byte> fraction,
            bool truncated, bool hexadecimal)
        {
            // A leading zero keeps the hexadecimal significand unsigned in BigInteger.
            var prefixLength = hexadecimal ? 1 : 0;
            var length = prefixLength + whole.Length + fraction.Length + (truncated ? 1 : 0);
            Span<char> digits = length <= 256 ? stackalloc char[length] : new char[length];
            if (hexadecimal)
                digits[0] = '0';
            Encoding.ASCII.GetChars(whole, digits[prefixLength..]);
            Encoding.ASCII.GetChars(fraction, digits[(prefixLength + whole.Length)..]);
            if (truncated)
                digits[^1] = '1';
            var style = hexadecimal ? NumberStyles.HexNumber : NumberStyles.Integer;
            return BigInteger.Parse(digits, style, CultureInfo.InvariantCulture);
        }

        FloatingPointPrefixResult ConvertRational(
            BigInteger numerator, BigInteger denominator, bool negative, int consumed)
        {
            var value = FromRational(numerator, denominator, negative, out var rangeError);
            // strtold warns for an inexact subnormal, not an exactly represented one.
            if (rangeError && value.Class == FloatingPointClass.Finite && value.Significand != 0)
                rangeError = (numerator << (63 - value.Exponent)) != denominator * value.Significand;
            return new FloatingPointPrefixResult(value, consumed, true, rangeError);
        }

        // Normalization leaves a nonzero final digit, so any discarded suffix is nonzero.
        // Replace it with one final digit and adjust the exponent. Since the retained prefix
        // extends past every rounding midpoint, this preserves which side of the midpoint we occupy.
        static bool LimitSignificand(
            ref ReadOnlySpan<byte> whole,
            ref ReadOnlySpan<byte> fraction,
            ref long exponent,
            int maximumDigits,
            int exponentStep)
        {
            var discarded = whole.Length + fraction.Length - maximumDigits;
            if (discarded <= 0)
                return false;

            exponent += exponentStep * (discarded - 1L);
            whole = whole[..Math.Min(whole.Length, maximumDigits)];
            fraction = fraction[..(maximumDigits - whole.Length)];
            return true;
        }

        long ReadExponent(ReadOnlySpan<byte> suffix, byte marker, out int consumed)
        {
            consumed = 0;
            if (suffix.IsEmpty || AsciiByte.ToLowerAscii(suffix[0]) != marker)
                return 0;
            var index = 1;
            var negative = index < suffix.Length && suffix[index] == (byte)'-';
            if (index < suffix.Length && suffix[index] is (byte)'+' or (byte)'-')
                index++;
            var digitsStart = index;
            long exponent = 0;
            while (index < suffix.Length && Integers.IsDigit(suffix[index]))
            {
                CheckCancellation(index);
                // Larger exponents cannot be offset by a memory-bounded input mantissa.
                exponent = Math.Min(int.MaxValue, exponent * 10 + suffix[index] - '0');
                index++;
            }
            if (index != digitsStart)
                consumed = index;
            return negative ? -exponent : exponent;
        }

        static bool StartsWithIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
        {
            if (value.Length < prefix.Length)
                return false;
            for (var index = 0; index < prefix.Length; index++)
            {
                if (AsciiByte.ToLowerAscii(value[index]) != prefix[index])
                    return false;
            }
            return true;
        }

        ulong ReadNanPayload(ReadOnlySpan<byte> digits, out bool rangeError)
        {
            rangeError = false;
            uint numberBase = 10;
            var minimumOverflowDigits = 20;
            if (digits.StartsWith("0x"u8) || digits.StartsWith("0X"u8))
            {
                numberBase = 16;
                minimumOverflowDigits = 17;
                digits = digits[2..];
            }
            else if (!digits.IsEmpty && digits[0] == (byte)'0')
            {
                numberBase = 8;
                minimumOverflowDigits = 22;
            }

            // The formatter only needs to know whether the payload is out of range.
            if (!retainNanPayload && digits.Length < minimumOverflowDigits)
                return 0;

            var cutoff = ulong.MaxValue / numberBase;
            var cutoffDigit = ulong.MaxValue % numberBase;
            ulong payload = 0;
            for (var index = 0; index < digits.Length; index++)
            {
                CheckCancellation(index);
                if (!ShellEscapeDecoder.TryDigitValue((char)digits[index], (int)numberBase, out var digit))
                    return 0;
                if (rangeError)
                    continue;
                if (payload > cutoff || payload == cutoff && (uint)digit > cutoffDigit)
                {
                    rangeError = true;
                    if (!retainNanPayload)
                        return 0;
                    payload = ulong.MaxValue;
                }
                else
                    payload = payload * numberBase + (uint)digit;
            }
            // The integer and quiet-NaN bits leave 62 bits for the payload.
            return retainNanPayload ? payload & 0x3fff_ffff_ffff_ffff : 0;
        }

        void CheckCancellation(int index)
        {
            if ((index & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Lokad.Bash.Helpers;

public readonly partial record struct ExtendedFloatingPoint
{
    /// <summary>Reads a C-locale numeric prefix, retaining NaN payloads and classifying missing numbers.</summary>
    /// <param name="reserveWorkspace">Reserves peak conversion workspace before allocating temporary buffers.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    /// <param name="content">The input byte content.</param>
    public static ExtendedFloatingPoint ParseGeneral(
        ReadOnlySpan<byte> content,
        Action<long> reserveWorkspace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = DecimalPrefix.Parse(content, DecimalPrefixSyntax.GeneralNumeric);
        cancellationToken.ThrowIfCancellationRequested();
        if (StartsWithIgnoreCase(prefix.Unsigned, "inf"u8))
            return new ExtendedFloatingPoint(prefix.Negative, FloatingPointClass.Infinity, 0, 0);
        if (StartsWithIgnoreCase(prefix.Unsigned, "nan"u8))
            return new ExtendedFloatingPoint(prefix.Negative, FloatingPointClass.Nan, NanPayload(prefix.Unsigned[3..]), 0);
        if (prefix.Unsigned.StartsWith("0x"u8) || prefix.Unsigned.StartsWith("0X"u8))
            return ParseHexadecimal(prefix.Unsigned[2..], prefix.Negative);
        if (!prefix.HasDigits)
            return new ExtendedFloatingPoint(false, FloatingPointClass.NonNumber, 0, 0);
        return ParseDecimal(prefix);

        ExtendedFloatingPoint ParseDecimal(DecimalPrefix decimalPrefix)
        {
            var whole = decimalPrefix.Whole;
            var fraction = decimalPrefix.Fraction;
            var exponent = ReadExponent(decimalPrefix.Remainder, (byte)'e') - fraction.Length;
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
                return new ExtendedFloatingPoint(decimalPrefix.Negative, FloatingPointClass.Finite, 0, 0);
            if (digits + exponent > 5_000)
                return new ExtendedFloatingPoint(decimalPrefix.Negative, FloatingPointClass.Infinity, 0, 0);

            // Every rounding midpoint is a <=65-bit integer times a power of two, down to 2^-16446.
            // Its terminating decimal coefficient has fewer than 16446 + 20 significant digits.
            var truncated = LimitSignificand(ref whole, ref fraction, ref exponent, maximumDigits: 16_512, exponentStep: 1);
            digits = whole.Length + fraction.Length + (truncated ? 1 : 0);
            // Cover text copies, BigInteger parsing/arithmetic and fixed extended-precision rounding buffers.
            reserveWorkspace(32L * digits + 256 * 1024);
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(Encoding.ASCII.GetString(whole), Encoding.ASCII.GetString(fraction), truncated ? "1" : string.Empty);
            var numerator = BigInteger.Parse(text, CultureInfo.InvariantCulture);
            var denominator = BigInteger.One;
            if (exponent >= 0)
                numerator *= BigInteger.Pow(10, (int)exponent);
            else
                denominator = BigInteger.Pow(10, (int)-exponent);
            return FromRational(numerator, denominator, decimalPrefix.Negative, out _);
        }

        ExtendedFloatingPoint ParseHexadecimal(ReadOnlySpan<byte> hexadecimal, bool negative)
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

            var exponent = ReadExponent(hexadecimal[index..], (byte)'p') - 4L * fraction.Length;
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
                return new ExtendedFloatingPoint(negative, FloatingPointClass.Finite, 0, 0);
            if (4L * digits + exponent > 16_500)
                return new ExtendedFloatingPoint(negative, FloatingPointClass.Infinity, 0, 0);

            // 32 hexadecimal digits exceed the 65 significant bits of every rounding midpoint.
            var truncated = LimitSignificand(ref whole, ref fraction, ref exponent, maximumDigits: 32, exponentStep: 4);
            digits = whole.Length + fraction.Length + (truncated ? 1 : 0);
            reserveWorkspace(32L * digits + 256 * 1024);
            cancellationToken.ThrowIfCancellationRequested();
            // A leading zero makes BigInteger's hexadecimal parser treat the significand as unsigned.
            var text = string.Concat("0", Encoding.ASCII.GetString(whole), Encoding.ASCII.GetString(fraction), truncated ? "1" : string.Empty);
            var numerator = BigInteger.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var denominator = BigInteger.One;
            if (exponent >= 0)
                numerator <<= (int)exponent;
            else
                denominator <<= (int)-exponent;
            return FromRational(numerator, denominator, negative, out _);
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

        long ReadExponent(ReadOnlySpan<byte> suffix, byte marker)
        {
            if (suffix.IsEmpty || AsciiByte.ToLowerAscii(suffix[0]) != marker)
                return 0;
            var index = 1;
            var negative = index < suffix.Length && suffix[index] == (byte)'-';
            if (index < suffix.Length && suffix[index] is (byte)'+' or (byte)'-')
                index++;
            long exponent = 0;
            while (index < suffix.Length && Integers.IsDigit(suffix[index]))
            {
                CheckCancellation(index);
                // Larger exponents cannot be offset by a memory-bounded input mantissa.
                exponent = Math.Min(int.MaxValue, exponent * 10 + suffix[index] - '0');
                index++;
            }
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

        ulong NanPayload(ReadOnlySpan<byte> suffix)
        {
            if (suffix.IsEmpty || suffix[0] != (byte)'(')
                return 0;
            var closing = suffix.IndexOf((byte)')');
            if (closing < 0)
                return 0;
            var digits = suffix[1..closing];
            var numberBase = 10;
            if (digits.StartsWith("0x"u8) || digits.StartsWith("0X"u8))
            {
                numberBase = 16;
                digits = digits[2..];
            }
            else if (!digits.IsEmpty && digits[0] == (byte)'0')
                numberBase = 8;

            ulong payload = 0;
            for (var index = 0; index < digits.Length; index++)
            {
                CheckCancellation(index);
                var value = digits[index];
                if (!ShellEscapeDecoder.TryDigitValue((char)value, numberBase, out var digit))
                    return 0;
                payload = payload > (ulong.MaxValue - (uint)digit) / (uint)numberBase
                    ? ulong.MaxValue
                    : payload * (uint)numberBase + (uint)digit;
            }
            // The integer and quiet-NaN bits leave 62 bits for the payload.
            return payload & 0x3fff_ffff_ffff_ffff;
        }

        void CheckCancellation(int index)
        {
            if ((index & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

using System;
using System.Numerics;
using System.Threading;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private readonly record struct FloatingPointParseResult(
        ExtendedFloatingPoint Value,
        bool Invalid,
        bool RangeError);

    private sealed partial class Formatter
    {
        private readonly record struct FloatingPointFormatResult
        {
            public FormattedNumber Number { get; }

            public bool Invalid { get; }

            public bool RangeError { get; }

            public FloatingPointFormatResult(
                ShellValue argument,
                char conversion,
                int? precision,
                bool alternate, Action<long> reserveWorkspace, CancellationToken cancellationToken)
            {
                var parsed = Parse(argument);
                var value = parsed.Value;
                var uppercase = char.IsUpper(conversion);
                var hexadecimalConversion = value.Class == FloatingPointClass.Finite
                    && char.ToLowerInvariant(conversion) == 'a';
                var text = value.Class switch
                {
                    FloatingPointClass.Nan => uppercase ? "NAN" : "nan",
                    FloatingPointClass.Infinity => uppercase ? "INF" : "inf",
                    _ => char.ToLowerInvariant(conversion) switch
                    {
                        'f' => FormatFixed(value, precision ?? 6, alternate),
                        'e' => FormatScientific(value, precision ?? 6, uppercase, alternate),
                        'g' => FormatGeneral(value, precision ?? 6, uppercase, alternate),
                        'a' => FormatHexadecimal(value, precision, uppercase, alternate),
                        _ => throw new InvalidOperationException("Unsupported floating-point conversion.")
                    }
                };

                Number = new FormattedNumber(
                    text,
                    hexadecimalConversion ? uppercase ? "0X" : "0x" : string.Empty,
                    value.Negative ? FormattedNumberSign.Negative : FormattedNumberSign.Positive,
                    value.Class == FloatingPointClass.Finite
                        ? FormattedNumberPadding.ZeroAllowed
                        : FormattedNumberPadding.SpaceOnly);
                Invalid = parsed.Invalid;
                RangeError = parsed.RangeError;

                FloatingPointParseResult Parse(ShellValue input)
                {
                    var zero = new ExtendedFloatingPoint(false, FloatingPointClass.Finite, 0, 0);
                    if (input.IsEmpty)
                        return new FloatingPointParseResult(zero, false, false);
                    if (TryParseCharacterConstant(input, out var character))
                        return new FloatingPointParseResult(
                            ExtendedFloatingPoint.FromRational(character, BigInteger.One, negative: false, out _),
                            false, false);

                    var prefix = ExtendedFloatingPoint.ParseGeneralPrefix(input.Bytes.Span, reserveWorkspace, cancellationToken);
                    var invalid = !prefix.HasNumber || prefix.ConsumedBytes != input.Bytes.Length;
                    return new FloatingPointParseResult(prefix.HasNumber ? prefix.Value : zero,
                        invalid, prefix.RangeError);
                }

                string FormatFixed(ExtendedFloatingPoint finite, int precision, bool alternate)
                {
                    var rounded = RoundAtDecimalScale(finite, precision);
                    var digits = rounded.ToString(Culture).PadLeft(precision + 1, '0');
                    if (precision == 0)
                        return alternate ? digits + "." : digits;
                    return digits[..^precision] + "." + digits[^precision..];
                }

                string FormatScientific(
                    ExtendedFloatingPoint finite,
                    int precision,
                    bool uppercase,
                    bool alternate)
                {
                    var significantDigits = precision + 1;
                    var exponent = GetDecimalExponent(finite);
                    var rounded = RoundAtDecimalScale(finite, significantDigits - 1 - exponent);
                    var threshold = BigInteger.Pow(10, significantDigits);
                    if (rounded >= threshold)
                    {
                        rounded /= 10;
                        exponent++;
                    }

                    var digits = rounded.ToString(Culture).PadLeft(significantDigits, '0');
                    var fraction = precision == 0 ? string.Empty : digits[1..];
                    var point = precision > 0 || alternate ? "." : string.Empty;
                    return digits[0] + point + fraction + FormatExponent(exponent, uppercase ? 'E' : 'e');
                }

                string FormatGeneral(
                    ExtendedFloatingPoint finite,
                    int precision,
                    bool uppercase,
                    bool alternate)
                {
                    if (precision == 0)
                        precision = 1;

                    var exponent = GetDecimalExponent(finite);
                    var rounded = RoundAtDecimalScale(finite, precision - 1 - exponent);
                    var threshold = BigInteger.Pow(10, precision);
                    if (rounded >= threshold)
                    {
                        rounded /= 10;
                        exponent++;
                    }

                    if (exponent < -4 || exponent >= precision)
                    {
                        var digits = rounded.ToString(Culture).PadLeft(precision, '0');
                        var fraction = digits.Length > 1 ? digits[1..] : string.Empty;
                        if (!alternate)
                            fraction = fraction.TrimEnd('0');
                        var point = fraction.Length > 0 || alternate ? "." : string.Empty;
                        var text = digits[0] + point + fraction
                            + FormatExponent(exponent, uppercase ? 'E' : 'e');
                        return uppercase ? text.ToUpperInvariant() : text;
                    }

                    var fractionalDigits = Math.Max(0, precision - exponent - 1);
                    var fixedText = FormatFixed(finite, fractionalDigits, alternate);
                    if (!alternate && fixedText.Contains('.'))
                        fixedText = fixedText.TrimEnd('0').TrimEnd('.');
                    return uppercase ? fixedText.ToUpperInvariant() : fixedText;
                }

                string FormatHexadecimal(
                    ExtendedFloatingPoint finite,
                    int? requestedPrecision,
                    bool uppercase,
                    bool alternate)
                {
                    var marker = uppercase ? 'P' : 'p';
                    if (finite.Significand == 0)
                    {
                        var zeroFraction = requestedPrecision is { } zeroPrecision
                            ? zeroPrecision > 0 || alternate
                                ? "." + new string('0', zeroPrecision)
                                : string.Empty
                            : string.Empty;
                        return $"0{zeroFraction}{marker}+0";
                    }

                    var exponent = finite.Exponent - 3;
                    string hexadecimal;
                    if (requestedPrecision is { } precision && precision < 15)
                    {
                        var shift = 4 * (15 - precision);
                        var rounded = ExtendedFloatingPoint.RoundQuotient(finite.Significand, BigInteger.One << shift);
                        var expectedDigits = precision + 1;
                        hexadecimal = rounded.ToString(uppercase ? "X" : "x", Culture);
                        if (hexadecimal.Length > expectedDigits && hexadecimal[0] == '0')
                            hexadecimal = hexadecimal[1..];
                        if (hexadecimal.Length > expectedDigits)
                        {
                            rounded >>= 4;
                            exponent += 4;
                            hexadecimal = rounded.ToString(uppercase ? "X" : "x", Culture);
                            if (hexadecimal.Length > expectedDigits && hexadecimal[0] == '0')
                                hexadecimal = hexadecimal[1..];
                        }
                        hexadecimal = hexadecimal.PadLeft(expectedDigits, '0');
                    }
                    else
                    {
                        hexadecimal = finite.Significand.ToString(uppercase ? "X16" : "x16", Culture);
                        if (requestedPrecision is { } paddedPrecision)
                            hexadecimal += new string('0', paddedPrecision - 15);
                    }

                    var leading = hexadecimal[0];
                    var fraction = hexadecimal[1..];
                    if (requestedPrecision == null)
                        fraction = fraction.TrimEnd('0');
                    var point = fraction.Length > 0 || alternate ? "." : string.Empty;
                    var exponentSign = exponent >= 0 ? "+" : string.Empty;
                    return $"{leading}{point}{fraction}{marker}{exponentSign}{exponent.ToString(Culture)}";
                }

                BigInteger RoundAtDecimalScale(ExtendedFloatingPoint finite, int decimalScale)
                {
                    GetRational(finite, out var numerator, out var denominator);
                    if (decimalScale >= 0)
                        numerator *= BigInteger.Pow(10, decimalScale);
                    else
                        denominator *= BigInteger.Pow(10, -decimalScale);
                    return ExtendedFloatingPoint.RoundQuotient(numerator, denominator);
                }

                int GetDecimalExponent(ExtendedFloatingPoint finite)
                {
                    if (finite.Significand == 0)
                        return 0;

                    GetRational(finite, out var numerator, out var denominator);
                    var binaryExponent = numerator.GetBitLength() - denominator.GetBitLength();
                    var exponent = (int)Math.Floor(binaryExponent * 0.3010299956639812);
                    while (CompareToPowerOfTen(numerator, denominator, exponent) < 0)
                        exponent--;
                    while (CompareToPowerOfTen(numerator, denominator, exponent + 1) >= 0)
                        exponent++;
                    return exponent;
                }

                static void GetRational(
                    ExtendedFloatingPoint finite,
                    out BigInteger numerator,
                    out BigInteger denominator)
                {
                    numerator = finite.Significand;
                    denominator = BigInteger.One;
                    var shift = finite.Exponent - 63;
                    if (shift >= 0)
                        numerator <<= shift;
                    else
                        denominator <<= -shift;
                }

                static string FormatExponent(int exponent, char marker)
                {
                    var sign = exponent < 0 ? '-' : '+';
                    var digits = Math.Abs(exponent).ToString(Culture).PadLeft(2, '0');
                    return $"{marker}{sign}{digits}";
                }

                static int CompareToPowerOfTen(BigInteger numerator, BigInteger denominator, int exponent) =>
                    exponent >= 0
                        ? numerator.CompareTo(denominator * BigInteger.Pow(10, exponent))
                        : (numerator * BigInteger.Pow(10, -exponent)).CompareTo(denominator);
            }
        }
    }
}

using System;
using System.Numerics;
using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private sealed partial class Formatter
    {
        private long ParseSigned(string argument)
        {
            if (argument.Length == 0)
                return 0;

            if (TryParseCharacterConstant(argument, out var character))
                return character;

            var parsed = ParseInteger(argument, out var negative, out var valid, out var overflow);
            var signedLimit = negative ? (BigInteger)long.MaxValue + 1 : long.MaxValue;
            if (!valid)
                AddConversionDiagnostic($"{argument}: invalid number");
            else if (overflow || parsed > signedLimit)
                AddWarning($"warning: {argument}: Numerical result out of range");

            if (negative)
            {
                var limit = (BigInteger)long.MaxValue + 1;
                if (parsed >= limit)
                    return parsed == limit ? long.MinValue : long.MinValue;
                return -(long)parsed;
            }

            return parsed > long.MaxValue ? long.MaxValue : (long)parsed;
        }

        private BigInteger ParseInteger(
            string argument,
            out bool negative,
            out bool valid,
            out bool overflow)
        {
            var span = argument.AsSpan();
            var index = 0;
            while (index < span.Length && char.IsWhiteSpace(span[index]))
                index++;

            negative = false;
            if (index < span.Length && span[index] is '+' or '-')
            {
                negative = span[index] == '-';
                index++;
            }

            var numberStart = index;
            var numberBase = 10;
            if (index + 2 <= span.Length && span[index] == '0')
            {
                if (index + 2 < span.Length
                    && span[index + 1] is 'x' or 'X'
                    && TryHexValue(span[index + 2], out _))
                {
                    numberBase = 16;
                    index += 2;
                }
                else
                {
                    numberBase = 8;
                }
            }

            var value = BigInteger.Zero;
            var digits = 0;
            while (index < span.Length && ShellEscapeDecoder.TryDigitValue(span[index], numberBase, out var digit))
            {
                value = value * numberBase + digit;
                index++;
                digits++;
            }

            valid = digits > 0 && index == span.Length;
            if (digits == 0 && numberStart < span.Length && span[numberStart] == '0')
            {
                digits = 1;
                value = BigInteger.Zero;
            }

            overflow = value > (BigInteger)ulong.MaxValue;
            return value;
        }

        private static bool TryParseCharacterConstant(string argument, out long value)
        {
            value = 0;
            if (argument.Length == 0 || argument[0] is not ('\'' or '"'))
                return false;
            if (argument.Length == 1)
                return true;

            Rune.DecodeFromUtf16(argument.AsSpan(1), out var rune, out _);
            value = rune.Value;
            return true;
        }

        private static bool TryHexValue(char value, out int digit) => ShellEscapeDecoder.TryDigitValue(value, 16, out digit);
    }
}

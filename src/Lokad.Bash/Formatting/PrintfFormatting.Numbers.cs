using System;
using System.Buffers;
using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private sealed partial class Formatter
    {
        private long ParseSigned(ShellValue argument)
        {
            if (argument.IsEmpty)
                return 0;

            if (TryParseCharacterConstant(argument, out var character))
                return character;

            var parsed = ParseInteger(argument, out var negative, out var valid, out var overflow);
            var signedLimit = negative ? (ulong)long.MaxValue + 1 : (ulong)long.MaxValue;
            if (!valid)
                AddNumericDiagnostic(argument, NumericErrorSuffix(argument), warning: false);
            else if (overflow || parsed > signedLimit)
                AddNumericDiagnostic(argument, ": Numerical result out of range", warning: true);

            if (negative)
            {
                var limit = (ulong)long.MaxValue + 1;
                if (parsed >= limit)
                    return long.MinValue;
                return -(long)parsed;
            }

            return parsed > (ulong)long.MaxValue ? long.MaxValue : (long)parsed;
        }

        private ulong ParseInteger(
            ShellValue argument,
            out bool negative,
            out bool valid,
            out bool overflow)
        {
            var span = argument.Bytes.Span;
            var index = 0;
            while (index < span.Length && span[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f')
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                index++;
            }

            negative = false;
            if (index < span.Length && span[index] is (byte)'+' or (byte)'-')
            {
                negative = span[index] == (byte)'-';
                index++;
            }

            var numberBase = 10;
            if (index + 2 <= span.Length && span[index] == (byte)'0')
            {
                if (index + 2 < span.Length
                    && span[index + 1] is (byte)'x' or (byte)'X'
                    && ShellEscapeDecoder.TryDigitValue((char)span[index + 2], 16, out _))
                {
                    numberBase = 16;
                    index += 2;
                }
                else
                {
                    numberBase = 8;
                }
            }

            ulong value = 0;
            overflow = false;
            var digits = 0;
            while (index < span.Length && ShellEscapeDecoder.TryDigitValue((char)span[index], numberBase, out var digit))
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (value > (ulong.MaxValue - (uint)digit) / (uint)numberBase)
                {
                    value = ulong.MaxValue;
                    overflow = true;
                }
                else
                    value = value * (uint)numberBase + (uint)digit;
                index++;
                digits++;
            }

            valid = digits > 0 && index == span.Length;
            return value;
        }

        private static bool TryParseCharacterConstant(ShellValue argument, out long value)
        {
            value = 0;
            var bytes = argument.Bytes.Span;
            if (bytes.IsEmpty || bytes[0] is not ((byte)'\'' or (byte)'"'))
                return false;
            if (bytes.Length == 1)
                return true;

            value = Rune.DecodeFromUtf8(bytes[1..], out var rune, out _) == OperationStatus.Done
                ? rune.Value : bytes[1];
            return true;
        }

        private static string NumericErrorSuffix(ShellValue argument) => argument.Bytes.Span switch
        {
            [(byte)'0', (byte)'x' or (byte)'X', ..] => ": invalid hex number",
            [(byte)'0', >= (byte)'0' and <= (byte)'9', ..] => ": invalid octal number",
            _ => ": invalid number"
        };

        private void AddNumericDiagnostic(ShellValue argument, string suffix, bool warning)
        {
            AddDiagnostic(new Diagnostic(warning ? "warning: " : string.Empty, argument, suffix));
            if (!warning) _conversionError = true;
        }
    }
}

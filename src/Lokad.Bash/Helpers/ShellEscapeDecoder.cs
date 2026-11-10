using System;

namespace Lokad.Bash.Helpers;

/// <summary>Escape grammars share byte decoding, but not stop, warning or NUL policy.</summary>
public enum ShellEscapeMode { PrintfFormat, PrintfArgument, AnsiCQuoted }

public enum ShellEscapeStatus { Literal, Decoded, StopOutput, MissingDigits }

/// <summary>Counts source bytes after the backslash and bytes written into the caller's scratch span.</summary>
public readonly record struct ShellEscapeResult(int Consumed, int Written, ShellEscapeStatus Status);

/// <summary>Shares byte decoding for shell literals, parameter values and printf escapes.</summary>
public static class ShellEscapeDecoder
{
    public const int MaximumEscapeBytes = 6;

    /// <summary>Decodes an ANSI-C value to bytes, preserving unknown escapes and truncating at decoded NUL.</summary>
    public static ShellValue DecodeAnsiCValue(ReadOnlySpan<byte> source)
    {
        var output = ByteMemoryBuilder.CreateUnaccounted(Math.Min(source.Length, 256));
        Span<byte> encoded = stackalloc byte[MaximumEscapeBytes];
        for (var index = 0; index < source.Length;)
        {
            if (source[index] != (byte)'\\' || index + 1 == source.Length)
            {
                var textStart = index++;
                while (index < source.Length && source[index] != (byte)'\\')
                    index++;
                output.Append(source[textStart..index]);
                continue;
            }

            var result = Decode(source[(index + 1)..], ShellEscapeMode.AnsiCQuoted, encoded);
            index += 1 + result.Consumed;
            if (result.Status == ShellEscapeStatus.Literal)
            {
                output.Append((byte)'\\');
                continue;
            }

            var bytes = encoded[..result.Written];
            var nul = bytes.IndexOf((byte)0);
            output.Append(nul < 0 ? bytes : bytes[..nul]);
            // NUL ends this value; callers retain their lexical span, adjacent parts and other fields.
            if (nul >= 0)
                break;
        }
        return ShellValue.FromBytes(output.ToMemory());
    }

    /// <summary>Requires nonempty source after a backslash and space for MaximumEscapeBytes.</summary>
    public static ShellEscapeResult Decode(ReadOnlySpan<byte> source, ShellEscapeMode mode, Span<byte> output)
    {
        var escaped = source[0];
        var value = escaped switch
        {
            (byte)'a' => 0x07,
            (byte)'b' => 0x08,
            (byte)'e' or (byte)'E' => 0x1b,
            (byte)'f' => 0x0c,
            (byte)'n' => 0x0a,
            (byte)'r' => 0x0d,
            (byte)'t' => 0x09,
            (byte)'v' => 0x0b,
            (byte)'\\' => (byte)'\\',
            (byte)'\'' or (byte)'"' or (byte)'?' when mode != ShellEscapeMode.PrintfArgument => escaped,
            _ => -1
        };
        if (value >= 0)
        {
            output[0] = (byte)value;
            return new(1, 1, ShellEscapeStatus.Decoded);
        }

        if (escaped == (byte)'c')
        {
            if (mode == ShellEscapeMode.PrintfArgument)
                return new(1, 0, ShellEscapeStatus.StopOutput);
            if (mode != ShellEscapeMode.AnsiCQuoted || source.Length == 1)
                return default;

            var control = source[1];
            if (control > 0x7f)
            {
                var length = Utf8Text.FirstCharacterLength(source[1..]);
                source.Slice(1, length).CopyTo(output);
                output[0] &= 0x1f;
                return new(1 + length, length, ShellEscapeStatus.Decoded);
            }
            output[0] = (byte)(control == (byte)'?' ? 0x7f : control & 0x1f);
            var consumed = control == (byte)'\\' && source.Length > 2 && source[2] == (byte)'\\' ? 3 : 2;
            return new(consumed, 1, ShellEscapeStatus.Decoded);
        }

        if (escaped is >= (byte)'0' and <= (byte)'7')
        {
            var limit = mode == ShellEscapeMode.PrintfArgument && escaped == (byte)'0' ? 4 : 3;
            var length = 0;
            value = 0;
            while (length < source.Length && length < limit && source[length] is >= (byte)'0' and <= (byte)'7')
                value = value * 8 + source[length++] - (byte)'0';
            output[0] = (byte)(value & 0xff);
            return new(length, 1, ShellEscapeStatus.Decoded);
        }

        if (escaped is not ((byte)'x' or (byte)'u' or (byte)'U'))
            return default;

        var digitLimit = escaped == (byte)'x' ? 2 : escaped == (byte)'u' ? 4 : 8;
        uint scalar = 0;
        var cursor = 1;
        while (cursor < source.Length && cursor <= digitLimit && TryDigitValue((char)source[cursor], 16, out var digit))
        {
            scalar = scalar * 16 + (uint)digit;
            cursor++;
        }
        if (cursor == 1)
            return mode == ShellEscapeMode.AnsiCQuoted ? default : new(0, 0, ShellEscapeStatus.MissingDigits);

        if (escaped == (byte)'x')
        {
            output[0] = (byte)scalar;
            return new(cursor, 1, ShellEscapeStatus.Decoded);
        }
        return new(cursor, EncodeExtendedUtf8(scalar, output), ShellEscapeStatus.Decoded);

        // Bash's numeric escapes retain its extended 1–6 byte encoding, including non-Unicode values.
        static int EncodeExtendedUtf8(uint scalar, Span<byte> encoded)
        {
            if (scalar <= 0x7f)
            {
                encoded[0] = (byte)scalar;
                return 1;
            }
            if (scalar <= 0x7ff)
            {
                encoded[0] = (byte)(0xc0 | scalar >> 6);
                encoded[1] = (byte)(0x80 | scalar & 0x3f);
                return 2;
            }
            if (scalar <= 0xffff)
            {
                encoded[0] = (byte)(0xe0 | scalar >> 12);
                encoded[1] = (byte)(0x80 | scalar >> 6 & 0x3f);
                encoded[2] = (byte)(0x80 | scalar & 0x3f);
                return 3;
            }
            if (scalar <= 0x1f_ffff)
            {
                encoded[0] = (byte)(0xf0 | scalar >> 18);
                encoded[1] = (byte)(0x80 | scalar >> 12 & 0x3f);
                encoded[2] = (byte)(0x80 | scalar >> 6 & 0x3f);
                encoded[3] = (byte)(0x80 | scalar & 0x3f);
                return 4;
            }
            if (scalar <= 0x3ff_ffff)
            {
                encoded[0] = (byte)(0xf8 | scalar >> 24);
                encoded[1] = (byte)(0x80 | scalar >> 18 & 0x3f);
                encoded[2] = (byte)(0x80 | scalar >> 12 & 0x3f);
                encoded[3] = (byte)(0x80 | scalar >> 6 & 0x3f);
                encoded[4] = (byte)(0x80 | scalar & 0x3f);
                return 5;
            }
            if (scalar <= 0x7fff_ffff)
            {
                encoded[0] = (byte)(0xfc | scalar >> 30);
                encoded[1] = (byte)(0x80 | scalar >> 24 & 0x3f);
                encoded[2] = (byte)(0x80 | scalar >> 18 & 0x3f);
                encoded[3] = (byte)(0x80 | scalar >> 12 & 0x3f);
                encoded[4] = (byte)(0x80 | scalar >> 6 & 0x3f);
                encoded[5] = (byte)(0x80 | scalar & 0x3f);
                return 6;
            }
            return 0;
        }
    }

    /// <summary>Shares printf's numeric digit recognition with hexadecimal escape readers.</summary>
    public static bool TryDigitValue(char value, int numberBase, out int digit)
    {
        if (value is >= '0' and <= '9')
            digit = value - '0';
        else if (value is >= 'a' and <= 'f')
            digit = value - 'a' + 10;
        else if (value is >= 'A' and <= 'F')
            digit = value - 'A' + 10;
        else
        {
            digit = 0;
            return false;
        }
        return digit < numberBase;
    }
}

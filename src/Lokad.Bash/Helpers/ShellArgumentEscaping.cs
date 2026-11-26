using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Lokad.Bash.Helpers;

/// <summary>Selects the printable form; nonprinting or malformed values use ANSI-C quoting.</summary>
public enum ShellQuoteStyle { Backslash, SingleQuoted, DoubleQuoted, AnsiC, AssociativeKey, AssignmentValue }

/// <summary>Provides helpers for rendering shell command arguments safely.</summary>
public static class ShellArgumentEscaping
{
    // Printable ASCII except the two bytes escaped inside ANSI-C quotes.
    private static readonly SearchValues<byte> AnsiCLiteralAscii = SearchValues.Create(
        " !\"#$%&()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{|}~"u8);

    /// <summary>Delimits a diagnostic operand without decoding or escaping its bytes.</summary>
    public static ShellValue QuoteDiagnosticOperand(ShellValue operand) =>
        ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append((byte)'`').Append(operand.Bytes).Append((byte)'\'').ToMemory());

    /// <summary>Formats a command line by escaping and joining argument tokens.</summary>
    /// <param name="arguments">The command arguments to render.</param>
    /// <returns>A shell-safe command line assembled from <paramref name="arguments"/>.</returns>
    public static string JoinArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(EscapeArgument));

    /// <summary>Escapes a single shell argument when it contains special characters.</summary>
    /// <param name="argument">The argument to encode for the shell.</param>
    /// <returns>A quoted or unchanged shell argument.</returns>
    public static string EscapeArgument(string argument)
    {
        if (argument.Length == 0)
            return "''";

        if (argument.All(static c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':'))
            return argument;

        return "'" + argument.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <summary>Appends shell syntax under the caller's byte budget; false leaves an incomplete suffix.</summary>
    public static bool TryAppendQuoted(IByteMemoryWriter output, ReadOnlySpan<byte> input, ShellQuoteStyle style) =>
        TryAppendQuoted(output, input, style, cLocaleDiagnostic: false);

    /// <summary>Quotes nonprinting arithmetic source using Bash's C-locale reader profile.</summary>
    internal static bool TryAppendArithmeticDiagnosticToken(IByteMemoryWriter output, ReadOnlySpan<byte> input) =>
        TryAppendQuoted(output, input, ShellQuoteStyle.AnsiC, cLocaleDiagnostic: true);

    private static bool TryAppendQuoted(IByteMemoryWriter output, ReadOnlySpan<byte> input, ShellQuoteStyle style,
        bool cLocaleDiagnostic)
    {
        if (input.IsEmpty)
            return style == ShellQuoteStyle.AssignmentValue
                || output.TryAppend(style == ShellQuoteStyle.DoubleQuoted ? "\"\""u8 : "''"u8);

        var printablePrefix = style == ShellQuoteStyle.AnsiC ? 0 : GetPrintablePrefixLength(input);

        if (style == ShellQuoteStyle.AnsiC || printablePrefix < input.Length)
        {
            if (!output.TryAppend("$'"u8) || !AppendPrintablePrefix(output, input[..printablePrefix]))
                return false;
            Span<byte> octalEscape = stackalloc byte[4];
            for (var index = printablePrefix; index < input.Length; index++)
            {
                var value = input[index];
                // Bash protects its internal escape and null markers when reading the operand.
                if (cLocaleDiagnostic && value is 0x01 or 0x7f && !output.TryAppend("\\001"u8))
                    return false;
                ReadOnlySpan<byte> escaped = value switch
                {
                    0x07 => "\\a"u8,
                    0x08 => "\\b"u8,
                    0x1b => "\\E"u8,
                    0x0c => "\\f"u8,
                    (byte)'\n' => "\\n"u8,
                    (byte)'\r' => "\\r"u8,
                    (byte)'\t' => "\\t"u8,
                    0x0b => "\\v"u8,
                    (byte)'\\' => "\\\\"u8,
                    (byte)'\'' => "\\'"u8,
                    _ => ReadOnlySpan<byte>.Empty
                };
                if (!escaped.IsEmpty)
                {
                    if (!output.TryAppend(escaped))
                        return false;
                    continue;
                }
                if (value is >= 0x20 and <= 0x7e)
                {
                    var rest = input[(index + 1)..];
                    var end = rest.IndexOfAnyExcept(AnsiCLiteralAscii);
                    var runLength = end < 0 ? rest.Length + 1 : end + 1;
                    // Each ASCII byte is a complete character in the writer's partial output.
                    if (!AppendLiteralRun(output, input.Slice(index, runLength)))
                        return false;
                    index += runLength - 1;
                    continue;
                }
                var length = cLocaleDiagnostic ? 0 : PrintableCharacterLength(input[index..]);
                if (length == 0)
                {
                    octalEscape[0] = (byte)'\\';
                    octalEscape[1] = (byte)('0' + (value >> 6));
                    octalEscape[2] = (byte)('0' + ((value >> 3) & 7));
                    octalEscape[3] = (byte)('0' + (value & 7));
                    if (!output.TryAppend(octalEscape))
                        return false;
                }
                else
                {
                    if (!output.TryAppend(input.Slice(index, length)))
                        return false;
                    index += length - 1;
                }
            }
            return output.TryAppend((byte)'\'');
        }

        if (style is ShellQuoteStyle.AssociativeKey or ShellQuoteStyle.AssignmentValue)
        {
            // '@' is special as an entire array key, not as a scalar assignment value.
            var needsQuotes = style == ShellQuoteStyle.AssociativeKey && input.Length == 1 && input[0] == (byte)'@';
            for (var index = 0; !needsQuotes && index < input.Length; index++)
                needsQuotes = !IsShellSafe(input, index);
            if (!needsQuotes)
                return output.TryAppend(input);
            style = style == ShellQuoteStyle.AssociativeKey ? ShellQuoteStyle.DoubleQuoted : ShellQuoteStyle.SingleQuoted;
        }
        if (style == ShellQuoteStyle.SingleQuoted)
            return TryAppendLiteralSingleQuoted(output, input);
        var quote = style == ShellQuoteStyle.DoubleQuoted ? (byte)'"' : (byte)'\'';
        var delimited = style != ShellQuoteStyle.Backslash;
        if (delimited && !output.TryAppend(quote))
            return false;
        var runStart = 0;
        for (var index = 0; index < input.Length; index++)
        {
            var value = input[index];
            var escape = style == ShellQuoteStyle.Backslash ? !IsShellSafe(input, index)
                : style == ShellQuoteStyle.DoubleQuoted && value is (byte)'"' or (byte)'$' or (byte)'`' or (byte)'\\';
            if (!escape)
                continue;

            if (!AppendLiteralRun(output, input[runStart..index])
                || !output.TryAppend((byte)'\\') || !output.TryAppend(value))
                return false;
            runStart = index + 1;
        }
        return AppendLiteralRun(output, input[runStart..]) && (!delimited || output.TryAppend(quote));

        static int GetPrintablePrefixLength(ReadOnlySpan<byte> bytes)
        {
            var position = 0;
            while (position < bytes.Length)
            {
                var current = bytes[position];
                if (current is >= 0x20 and <= 0x7e)
                {
                    var runLength = bytes[position..].IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e);
                    if (runLength < 0)
                        return bytes.Length;
                    position += runLength;
                    current = bytes[position];
                }

                if (current < 0x80)
                    break;
                var length = PrintableCharacterLength(bytes[position..]);
                if (length == 0)
                    break;
                position += length;
            }
            return position;
        }

        static bool AppendPrintablePrefix(IByteMemoryWriter output, ReadOnlySpan<byte> bytes)
        {
            // The first scan validated these characters. Only the ANSI-C quote and escape bytes need rewriting.
            while (!bytes.IsEmpty)
            {
                var escape = bytes.IndexOfAny((byte)'\\', (byte)'\'');
                var literal = escape < 0 ? bytes : bytes[..escape];
                if (!literal.IsEmpty && !output.TryAppend(literal))
                {
                    // Keep the same complete characters when a bounded writer rejects the span.
                    while (!literal.IsEmpty)
                    {
                        var length = PrintableCharacterLength(literal);
                        if (!output.TryAppend(literal[..length]))
                            return false;
                        literal = literal[length..];
                    }
                }
                if (escape < 0)
                    return true;
                if (!output.TryAppend(bytes[escape] == (byte)'\\' ? "\\\\"u8 : "\\'"u8))
                    return false;
                bytes = bytes[(escape + 1)..];
            }
            return true;
        }

        // Resume character recognition after each escaped byte, preserving printable suffixes.
        static int PrintableCharacterLength(ReadOnlySpan<byte> bytes)
        {
            if (Rune.DecodeFromUtf8(bytes, out var character, out var length) != OperationStatus.Done)
                return 0;
            return Rune.GetUnicodeCategory(character) is UnicodeCategory.Control
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.OtherNotAssigned ? 0 : length;
        }

        static bool IsShellSafe(ReadOnlySpan<byte> bytes, int index)
        {
            var value = bytes[index];
            // Tildes can also expand after '=' and ':'.
            return value is >= (byte)'a' and <= (byte)'z'
                or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'_' or (byte)'@' or (byte)'%' or (byte)'+'
                or (byte)'=' or (byte)':' or (byte)',' or (byte)'.'
                or (byte)'/' or (byte)'-'
                || value >= 0x80
                || value == (byte)'#' && index > 0
                || value == (byte)'~' && index > 0 && bytes[index - 1] is not (byte)'=' and not (byte)':';
        }
    }

    internal static long GetLiteralSingleQuotedLength(ReadOnlySpan<byte> literal) =>
        2L + literal.Length + 3L * literal.Count((byte)'\'');

    /// <summary>Quotes literal bytes without introducing ANSI-C syntax; false leaves an incomplete suffix.</summary>
    public static bool TryAppendLiteralSingleQuoted(IByteMemoryWriter target, ReadOnlySpan<byte> literal) =>
        target.TryAppend((byte)'\'') && TryAppendSingleQuotedContent(target, literal) && target.TryAppend((byte)'\'');

    /// <summary>Appends a literal fragment inside an open single-quoted word.</summary>
    internal static bool TryAppendSingleQuotedContent(IByteMemoryWriter target, ReadOnlySpan<byte> literal)
    {
        while (!literal.IsEmpty)
        {
            var quote = literal.IndexOf((byte)'\'');
            if (quote < 0)
                return AppendLiteralRun(target, literal);
            if (!AppendLiteralRun(target, literal[..quote]) || !target.TryAppend("'\\''"u8))
                return false;
            literal = literal[(quote + 1)..];
        }

        return true;
    }

    private static bool AppendLiteralRun(IByteMemoryWriter target, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 1 && target.TryAppend(bytes))
            return true;

        // A rejected span leaves the writer unchanged. Fill the same partial prefix as single-byte appends.
        foreach (var value in bytes)
            if (!target.TryAppend(value))
                return false;
        return true;
    }
}

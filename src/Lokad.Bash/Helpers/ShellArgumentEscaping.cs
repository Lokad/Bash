using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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
    // Comma is last so that assignment values and array keys can omit it from their search.
    private static ReadOnlySpan<byte> UnquotedSpecialAscii => " !\"#$&'()*;<>?[\\]^`{|}~,"u8;
    private static readonly SearchValues<byte> UnquotedSpecialBytes = SearchValues.Create(UnquotedSpecialAscii[..^1]);
    private static readonly SearchValues<byte> BackslashQuoteBytes = SearchValues.Create(UnquotedSpecialAscii);
    private const byte UnquotedSpecial = 1;
    private const byte BackslashSpecial = 2;
    private static readonly byte[] UnquotedSpecialLookup = CreateUnquotedSpecialLookup();
    private static readonly SearchValues<byte> DoubleQuoteEscapes = SearchValues.Create("\"$`\\"u8);

    private static byte[] CreateUnquotedSpecialLookup()
    {
        var lookup = new byte[256];
        foreach (var value in UnquotedSpecialAscii[..^1])
            lookup[value] = UnquotedSpecial;
        lookup[UnquotedSpecialAscii[^1]] = BackslashSpecial;
        return lookup;
    }

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
            var needsQuotes = style == ShellQuoteStyle.AssociativeKey && input is [(byte)'@']
                || FindUnquotedSpecialByte(input, 0, escapeComma: false) >= 0;
            if (!needsQuotes)
                return output.TryAppend(input);
            style = style == ShellQuoteStyle.AssociativeKey ? ShellQuoteStyle.DoubleQuoted : ShellQuoteStyle.SingleQuoted;
        }
        if (style == ShellQuoteStyle.SingleQuoted)
            return TryAppendBashSingleQuoted(output, input);
        if (style == ShellQuoteStyle.DoubleQuoted)
            return output.TryAppend((byte)'"') && AppendDoubleQuotedContent(output, input) && output.TryAppend((byte)'"');
        if (style != ShellQuoteStyle.Backslash)
            return output.TryAppend((byte)'\'') && AppendLiteralRun(output, input) && output.TryAppend((byte)'\'');

        var runStart = 0;
        while (runStart < input.Length)
        {
            // A reusable word can be inserted into a brace list.
            var index = FindUnquotedSpecialByte(input, runStart, escapeComma: true);
            if (index < 0)
                break;

            if ((index > runStart && !AppendLiteralRun(output, input[runStart..index]))
                || !AppendBackslashEscape(output, input[index]))
                return false;
            runStart = index + 1;
        }
        return AppendLiteralRun(output, input[runStart..]);

        static bool AppendDoubleQuotedContent(IByteMemoryWriter output, ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var escape = DoubleQuoteEscapes.Contains(bytes[0]) ? 0 : bytes.IndexOfAny(DoubleQuoteEscapes);
                if (escape < 0)
                    return AppendLiteralRun(output, bytes);
                if (!AppendLiteralRun(output, bytes[..escape]) || !AppendBackslashEscape(output, bytes[escape]))
                    return false;
                bytes = bytes[(escape + 1)..];
            }
            return true;
        }

        static bool AppendBackslashEscape(IByteMemoryWriter output, byte value) =>
            output.TryAppend((byte)'\\') && output.TryAppend(value);

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int FindUnquotedSpecialByte(ReadOnlySpan<byte> bytes, int start, bool escapeComma)
        {
            var mask = escapeComma ? UnquotedSpecial | BackslashSpecial : UnquotedSpecial;
            // The caller already validated the printable UTF-8. Only ASCII can require quoting here.
            while (start < bytes.Length)
            {
                var remaining = bytes[start..];
                var offset = 0;
                // Keep adjacent and alternating escapes in the scalar path.
                if ((UnquotedSpecialLookup[remaining[0]] & mask) == 0)
                {
                    offset = remaining.Length > 1 && (UnquotedSpecialLookup[remaining[1]] & mask) != 0
                        ? 1 : remaining.IndexOfAny(escapeComma ? BackslashQuoteBytes : UnquotedSpecialBytes);
                }
                if (offset < 0)
                    return -1;

                var index = start + offset;
                var value = bytes[index];
                if (value == (byte)'#' && index > 0
                    || value == (byte)'~' && index > 0 && bytes[index - 1] is not (byte)'=' and not (byte)':')
                {
                    start = index + 1;
                    continue;
                }

                return index;
            }

            return -1;
        }
    }

    internal static long GetLiteralSingleQuotedLength(ReadOnlySpan<byte> literal) =>
        2L + literal.Length + 3L * literal.Count((byte)'\'');

    internal static long GetBashSingleQuotedLength(ReadOnlySpan<byte> literal) =>
        literal is [(byte)'\''] ? 2 : GetLiteralSingleQuotedLength(literal);

    internal static bool TryAppendBashSingleQuoted(IByteMemoryWriter target, ReadOnlySpan<byte> literal) =>
        literal is [(byte)'\''] ? target.TryAppend("\\'"u8) : TryAppendLiteralSingleQuoted(target, literal);

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

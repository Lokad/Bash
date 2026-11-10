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
    public static bool TryAppendQuoted(IByteMemoryWriter output, ReadOnlySpan<byte> input, ShellQuoteStyle style)
    {
        if (input.IsEmpty)
            return style == ShellQuoteStyle.AssignmentValue
                || output.TryAppend(style == ShellQuoteStyle.DoubleQuoted ? "\"\""u8 : "''"u8);

        var needsAnsi = style == ShellQuoteStyle.AnsiC;
        var needsQuotes = false;
        for (var index = 0; index < input.Length; index++)
            needsQuotes |= !IsShellSafe(input, index);
        for (var index = 0; index < input.Length;)
        {
            var length = PrintableCharacterLength(input[index..]);
            if (length == 0)
            {
                needsAnsi = true;
                break;
            }
            index += length;
        }

        if (needsAnsi)
        {
            if (!output.TryAppend("$'"u8))
                return false;
            Span<byte> octalEscape = stackalloc byte[4];
            for (var index = 0; index < input.Length; index++)
            {
                var value = input[index];
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
                var length = PrintableCharacterLength(input[index..]);
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
            needsQuotes |= style == ShellQuoteStyle.AssociativeKey && input.Length == 1 && input[0] == (byte)'@';
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
        for (var index = 0; index < input.Length; index++)
        {
            var value = input[index];
            var escape = style == ShellQuoteStyle.Backslash ? !IsShellSafe(input, index)
                : style == ShellQuoteStyle.DoubleQuoted && value is (byte)'"' or (byte)'$' or (byte)'`' or (byte)'\\';
            if (escape && !output.TryAppend((byte)'\\') || !output.TryAppend(value))
                return false;
        }
        return !delimited || output.TryAppend(quote);

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

    /// <summary>Quotes literal bytes without introducing ANSI-C syntax; false leaves an incomplete suffix.</summary>
    public static bool TryAppendLiteralSingleQuoted(IByteMemoryWriter target, ReadOnlySpan<byte> literal)
    {
        if (!target.TryAppend((byte)'\''))
            return false;
        foreach (var value in literal)
            if (value == (byte)'\'' ? !target.TryAppend("'\\''"u8) : !target.TryAppend(value))
                return false;
        return target.TryAppend((byte)'\'');
    }
}

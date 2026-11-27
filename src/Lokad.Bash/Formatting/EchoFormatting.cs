using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Formatting;

/// <summary>Shared byte formatting for echo builtins and hosted commands.</summary>
public static class EchoFormatting
{
    /// <summary>Resolves option handling and calculates the bytes to reserve before formatting.</summary>
    public static OutputFormat GetOutputFormat(IReadOnlyList<ShellValue> arguments) =>
        GetOutputFormat(arguments, out _);

    private static OutputFormat GetOutputFormat(IReadOnlyList<ShellValue> arguments, out bool needsEscaping)
    {
        var firstArgument = ReadOptions(arguments, arguments.Count, detectNewlineSuppression: true,
            out var suppressNewline, out var expandEscapes);
        if (expandEscapes)
        {
            var length = WalkEscapedArguments(arguments, firstArgument, output: null, out var status);
            needsEscaping = status != ShellEscapeStatus.Literal;
            suppressNewline |= status == ShellEscapeStatus.StopOutput;
            return new OutputFormat(firstArgument, suppressNewline, length + (suppressNewline ? 0 : 1));
        }

        needsEscaping = false;
        long outputLength = Math.Max(0, arguments.Count - firstArgument - 1) + (suppressNewline ? 0 : 1);
        for (var index = firstArgument; index < arguments.Count; index++)
            outputLength += arguments[index].Bytes.Length;
        return new OutputFormat(firstArgument, suppressNewline, outputLength);
    }

    private static int ReadOptions(
        IReadOnlyList<ShellValue> arguments, int limit, bool detectNewlineSuppression,
        out bool suppressNewline, out bool expandEscapes)
    {
        suppressNewline = false;
        expandEscapes = false;
        var firstArgument = 0;
        while (firstArgument < limit)
        {
            var option = arguments[firstArgument].Bytes.Span;
            if (option.Length < 2 || option[0] != (byte)'-' || option[1..].IndexOfAnyExcept("neE"u8) >= 0)
                break;

            var flags = option[1..];
            if (detectNewlineSuppression && !suppressNewline && flags.Contains((byte)'n'))
                suppressNewline = true;
            var lastEscapeFlag = flags.LastIndexOfAny((byte)'e', (byte)'E');
            if (lastEscapeFlag >= 0)
                expandEscapes = flags[lastEscapeFlag] == (byte)'e';
            firstArgument++;
        }
        return firstArgument;
    }

    /// <summary>Reserves and formats output owned until the caller's memory scope ends.</summary>
    public static ReadOnlyMemory<byte> Format(
        IReadOnlyList<ShellValue> arguments, OutputFormat format, IMemoryScope memory)
    {
        ReadOptions(arguments, Math.Min(format.FirstArgument, arguments.Count), detectNewlineSuppression: false,
            out _, out var expandEscapes);
        return Format(arguments, format, expandEscapes, memory);
    }

    /// <summary>Formats output, borrowing an unchanged operand when no bytes need to be added.</summary>
    /// <remarks>The caller retains the expanded operands and their owners through the write.</remarks>
    internal static ReadOnlyMemory<byte> Format(IReadOnlyList<ShellValue> arguments, IMemoryScope memory)
    {
        var format = GetOutputFormat(arguments, out var needsEscaping);
        if (!needsEscaping && format.SuppressNewline && format.FirstArgument == arguments.Count - 1)
            return arguments[format.FirstArgument].Bytes;

        return Format(arguments, format, needsEscaping, memory);
    }

    private static ReadOnlyMemory<byte> Format(
        IReadOnlyList<ShellValue> arguments, OutputFormat format, bool expandEscapes, IMemoryScope memory)
    {
        using var output = new ByteMemoryBuilder(checked((int)format.Length), memory);
        var status = ShellEscapeStatus.Literal;
        if (expandEscapes)
            WalkEscapedArguments(arguments, format.FirstArgument, output, out status);
        else
        {
            for (var index = format.FirstArgument; index < arguments.Count; index++)
            {
                if (index > format.FirstArgument)
                    output.Append((byte)' ');
                output.Append(arguments[index].Bytes);
            }
        }
        if (!format.SuppressNewline && status != ShellEscapeStatus.StopOutput)
            output.Append((byte)'\n');

        return output.DetachSnapshot();
    }

    // Use the same traversal to measure exact capacity and to write admitted bytes.
    private static long WalkEscapedArguments(
        IReadOnlyList<ShellValue> arguments, int first, ByteMemoryBuilder? output, out ShellEscapeStatus status)
    {
        Span<byte> decoded = stackalloc byte[ShellEscapeDecoder.MaximumEscapeBytes];
        status = ShellEscapeStatus.Literal;
        long length = 0;
        for (var index = first; index < arguments.Count; index++)
        {
            if (index > first)
            {
                output?.Append((byte)' ');
                length++;
            }

            var remaining = arguments[index].Bytes.Span;
            while (!remaining.IsEmpty)
            {
                var slash = remaining.IndexOf((byte)'\\');
                if (slash < 0 || slash == remaining.Length - 1)
                {
                    output?.Append(remaining);
                    length += remaining.Length;
                    break;
                }

                output?.Append(remaining[..slash]);
                length += slash;
                remaining = remaining[(slash + 1)..];
                var escape = ShellEscapeDecoder.DecodeEcho(remaining, decoded);
                if (escape.Status == ShellEscapeStatus.StopOutput)
                {
                    status = ShellEscapeStatus.StopOutput;
                    return length;
                }
                if (escape.Status == ShellEscapeStatus.Literal)
                {
                    output?.Append((byte)'\\');
                    length++;
                    continue;
                }

                status = ShellEscapeStatus.Decoded;
                output?.Append(decoded[..escape.Written]);
                length += escape.Written;
                remaining = remaining[escape.Consumed..];
            }
        }
        return length;
    }

    public readonly record struct OutputFormat(int FirstArgument, bool SuppressNewline, long Length);
}

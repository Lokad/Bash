using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Formatting;

/// <summary>Shared byte formatting for echo builtins and hosted commands.</summary>
public static class EchoFormatting
{
    /// <summary>Resolves option handling and calculates the bytes to reserve before formatting.</summary>
    public static OutputFormat GetOutputFormat(IReadOnlyList<ShellValue> arguments)
    {
        var suppressNewline = arguments.Count > 0 && arguments[0].Bytes.Span.SequenceEqual("-n"u8);
        var firstArgument = suppressNewline ? 1 : 0;
        long outputLength = Math.Max(0, arguments.Count - firstArgument - 1) + (suppressNewline ? 0 : 1);
        for (var index = firstArgument; index < arguments.Count; index++)
            outputLength += arguments[index].Bytes.Length;
        return new OutputFormat(firstArgument, suppressNewline, outputLength);
    }

    /// <summary>Reserves and formats output owned until the caller's memory scope ends.</summary>
    public static ReadOnlyMemory<byte> Format(
        IReadOnlyList<ShellValue> arguments, OutputFormat format, IMemoryScope memory)
    {
        using var output = new ByteMemoryBuilder(checked((int)format.Length), memory);
        for (var index = format.FirstArgument; index < arguments.Count; index++)
        {
            if (index > format.FirstArgument)
                output.Append((byte)' ');

            output.Append(arguments[index].Bytes);
        }

        if (!format.SuppressNewline)
            output.Append((byte)'\n');

        return output.DetachSnapshot();
    }

    public readonly record struct OutputFormat(int FirstArgument, bool SuppressNewline, long Length);
}

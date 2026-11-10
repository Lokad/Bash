using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private sealed partial class Formatter
    {
        private int AppendEscape(
            BoundedByteMemoryBuilder output,
            ReadOnlyMemory<byte> source,
            int index,
            bool percentB,
            out bool stop)
        {
            Span<byte> encoded = stackalloc byte[ShellEscapeDecoder.MaximumEscapeBytes];
            var mode = percentB ? ShellEscapeMode.PrintfArgument : ShellEscapeMode.PrintfFormat;
            var result = ShellEscapeDecoder.Decode(source.Span[index..], mode, encoded);
            stop = result.Status == ShellEscapeStatus.StopOutput;
            if (result.Status == ShellEscapeStatus.MissingDigits)
                AddWarning((char)source.Span[index] == 'x' ? "missing hex digit for \\x" : $"missing unicode digit for \\{(char)source.Span[index]}");
            if (result.Written != 0)
                TryAppendBytes(output, encoded[..result.Written]);
            return result.Consumed;
        }
    }
}

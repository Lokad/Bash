using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private sealed partial class Formatter
    {
        private const int MaximumLiteralChunkLength = 64 * 1024;

        // Keep cancellation checks between spans and preserve the prefix at the output limit.
        private int AppendLiteralRun(OutputBuffer output, ReadOnlySpan<byte> source, bool formatText)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = source[..Math.Min(source.Length, MaximumLiteralChunkLength)];
            var boundary = formatText ? chunk.IndexOfAny((byte)'\\', (byte)'%') : chunk.IndexOf((byte)'\\');
            if (boundary >= 0)
                chunk = chunk[..boundary];

            var length = Math.Min(chunk.Length, output.RemainingLength);
            if (length > 0 && !TryAppendBytes(output, chunk[..length]))
                return 0;
            if (length < chunk.Length)
                FailOutputLimit();
            return length;
        }

        private int AppendEscape(
            OutputBuffer output,
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
                AddWarning((char)source.Span[index] switch
                {
                    'x' => "missing hex digit for \\x",
                    'u' => "missing unicode digit for \\u",
                    _ => "missing unicode digit for \\U"
                });
            if (result.Written != 0)
                TryAppendBytes(output, encoded[..result.Written]);
            return result.Consumed;
        }
    }
}

using System;
using System.Buffers;
using System.Buffers.Text;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Byte-preserving TIMEFORMAT expansion, following the Bash 5.2 timing format.</summary>
public static class ShellTimeFormatting
{
    public static readonly ShellValue DefaultFormat = ShellValue.FromText("\nreal\t%3lR\nuser\t%3lU\nsys\t%3lS");
    public static readonly ShellValue PortableFormat = ShellValue.FromText("real %2R\nuser %2U\nsys %2S");

    /// <summary>Formats elapsed and CPU times or returns a diagnostic for an invalid format.</summary>
    /// <remarks>
    /// Supply a caller-owned <see cref="IMemoryScope"/> as <paramref name="memoryBudget"/>.
    /// Successful output stays charged until the scope ends. Dispose the scope after every output consumer finishes.
    /// </remarks>
    public static bool TryFormat(ShellValue format, ShellTimingSnapshot duration,
        out ReadOnlyMemory<byte> output, out ReadOnlyMemory<byte> error, IMemoryBudget memoryBudget)
    {
        output = ReadOnlyMemory<byte>.Empty;
        error = ReadOnlyMemory<byte>.Empty;
        if (format.IsEmpty)
            return true;

        using var builder = new ByteMemoryBuilder(0, memoryBudget);
        Span<byte> number = stackalloc byte[40];
        try
        {
            var bytes = format.Bytes.Span;
            // Short formats typically interleave brief literals with conversions.
            var scanLiteralRuns = bytes.Length >= 64;
            for (var index = 0; index < bytes.Length; index++)
            {
                var current = bytes[index];
                if (current != '%' || index + 1 == bytes.Length)
                {
                    if (!scanLiteralRuns)
                    {
                        builder.Append(current);
                        continue;
                    }
                    var literal = bytes[index..];
                    var next = literal[1..].IndexOf((byte)'%');
                    var count = next < 0 ? literal.Length : next + 1;
                    builder.Append(literal[..count]);
                    index += count - 1;
                    continue;
                }

                current = bytes[++index];
                if (current == '%')
                {
                    builder.Append((byte)'%');
                    continue;
                }
                if (current == 'P')
                {
                    var percent = duration.Real.Ticks == 0 ? 0
                        : 100m * (duration.User.Ticks + duration.System.Ticks) / duration.Real.Ticks;
                    var truncated = decimal.Truncate(percent * 100) / 100;
                    if (!Utf8Formatter.TryFormat(truncated, number, out var written, new StandardFormat('F', 2)))
                        throw new InvalidOperationException("A CPU percentage must fit its format buffer.");
                    builder.Append(number[..written]);
                    continue;
                }

                var precision = 3;
                if (current is >= (byte)'0' and <= (byte)'9')
                {
                    precision = Math.Min(3, current - '0');
                    current = ++index < bytes.Length ? bytes[index] : (byte)0;
                }
                var longFormat = current == 'l';
                if (longFormat)
                    current = ++index < bytes.Length ? bytes[index] : (byte)0;

                var ticks = current switch
                {
                    (byte)'R' or (byte)'E' => duration.Real.Ticks,
                    (byte)'U' => duration.User.Ticks,
                    (byte)'S' => duration.System.Ticks,
                    _ => -1
                };
                if (ticks < 0)
                {
                    error = ByteMemoryBuilder.CreateUnaccounted(0).Append("TIMEFORMAT: `"u8)
                        .Append(current).Append("': invalid format character\n"u8).ToMemory();
                    return false;
                }

                // Bash 5.2 first rounds the measurement to milliseconds, then truncates
                // that value to the requested precision (including carrying into the seconds).
                var scale = precision switch { 0 => 1L, 1 => 10L, 2 => 100L, _ => 1000L };
                var milliseconds = ticks / TimeSpan.TicksPerMillisecond
                    + (ticks % TimeSpan.TicksPerMillisecond >= TimeSpan.TicksPerMillisecond / 2 ? 1 : 0);
                var units = milliseconds / (1000 / scale);
                if (longFormat)
                {
                    AppendInteger(units / (60 * scale), number, default);
                    builder.Append((byte)'m');
                    units %= 60 * scale;
                }
                AppendInteger(units / scale, number, default);
                if (precision != 0)
                {
                    builder.Append((byte)'.');
                    AppendInteger(units % scale, number, new StandardFormat('D', (byte)precision));
                }
                if (longFormat)
                    builder.Append((byte)'s');
            }
            builder.Append((byte)'\n');
            output = builder.DetachSnapshot();
            return true;
        }
        catch (ByteMemoryBuilder.LimitExceededException)
        {
            error = "TIMEFORMAT: output exceeds the CLR array length limit\n"u8.ToArray();
            return false;
        }

        void AppendInteger(long value, Span<byte> buffer, StandardFormat format)
        {
            if (!Utf8Formatter.TryFormat(value, buffer, out var written, format))
                throw new InvalidOperationException("A timing number must fit its format buffer.");
            builder.Append(buffer[..written]);
        }
    }
}

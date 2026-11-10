using System;
using System.Globalization;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Byte-preserving TIMEFORMAT expansion, following the Bash 5.2 timing format.</summary>
public static class ShellTimeFormatting
{
    public static readonly ShellValue DefaultFormat = ShellValue.FromText("\nreal\t%3lR\nuser\t%3lU\nsys\t%3lS");
    public static readonly ShellValue PortableFormat = ShellValue.FromText("real %2R\nuser %2U\nsys %2S");

    public static bool TryFormat(ShellValue format, ShellTimingSnapshot duration,
        out ReadOnlyMemory<byte> output, out ReadOnlyMemory<byte> error, IMemoryBudget memoryBudget)
    {
        output = ReadOnlyMemory<byte>.Empty;
        error = ReadOnlyMemory<byte>.Empty;
        if (format.IsEmpty)
            return true;

        using var builder = new ByteMemoryBuilder(0, memoryBudget);
        try
        {
            var bytes = format.Bytes.Span;
            for (var index = 0; index < bytes.Length; index++)
            {
                var current = bytes[index];
                if (current != '%' || index + 1 == bytes.Length)
                {
                    builder.Append(current);
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
                    builder.Append(Utf8Text.Encode((decimal.Truncate(percent * 100) / 100).ToString("F2", CultureInfo.InvariantCulture)));
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
                    builder.Append(Utf8Text.Encode((units / (60 * scale)).ToString(CultureInfo.InvariantCulture)))
                        .Append((byte)'m');
                    units %= 60 * scale;
                }
                builder.Append(Utf8Text.Encode((units / scale).ToString(CultureInfo.InvariantCulture)));
                if (precision != 0)
                    builder.Append((byte)'.')
                        .Append(Utf8Text.Encode((units % scale).ToString("D" + precision, CultureInfo.InvariantCulture)));
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
    }
}

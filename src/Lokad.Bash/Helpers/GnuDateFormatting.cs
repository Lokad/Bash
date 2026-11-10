using System;
using System.Globalization;
using System.Text;

namespace Lokad.Bash.Helpers;

/// <summary>Identifies the percent-directive dialect used to format a timestamp.</summary>
public enum GnuDateFormatDialect
{
    /// <summary>GNU <c>date</c> directives.</summary>
    Date,

    /// <summary>Bash <c>printf</c> directives backed by <c>strftime</c>.</summary>
    Strftime
}

/// <summary>Formats timestamps using GNU date-style percent directives.</summary>
public static class GnuDateFormatting
{
    // Covers padded directives, UTF-16 builder growth, and the completed text/UTF-8 output.
    public const long EstimatedBytesPerCharacter = 8;
    private const int MaximumInitialCapacity = 4096;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly record struct FormattedDirective(
        string Text,
        bool Supported,
        char DefaultPadding);

    public static string Format(DateTimeOffset value, string format) =>
        Format(value, format, GnuDateFormatDialect.Date);

    public static string Format(
        DateTimeOffset value,
        string format,
        GnuDateFormatDialect dialect)
    {
        if (!TryFormat(value, format, dialect, int.MaxValue, out var result, static _ => { }))
            throw new InvalidOperationException("Unbounded date formatting unexpectedly exceeded its limit.");

        return result;
    }

    /// <summary>Formats a timestamp without constructing text beyond an explicit maximum length.</summary>
    public static bool TryFormat(
        DateTimeOffset value,
        string format,
        GnuDateFormatDialect dialect,
        int maximumLength,
        out string result,
        Action<int> reserveCharacters)
    {
        if (maximumLength < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        var initialCapacity = Math.Min(Math.Min(format.Length, maximumLength), MaximumInitialCapacity);
        var admittedCharacters = initialCapacity;
        if (initialCapacity != 0)
            reserveCharacters(initialCapacity);
        var builder = new StringBuilder(initialCapacity);

        for (var index = 0; index < format.Length; index++)
        {
            var ch = format[index];
            if (ch != '%')
            {
                if (!TryAppend(ch))
                {
                    result = string.Empty;
                    return false;
                }
                continue;
            }

            if (index + 1 >= format.Length)
            {
                if (!TryAppend('%'))
                {
                    result = string.Empty;
                    return false;
                }
                continue;
            }

            index++;
            var directiveStart = index;
            var noPadding = false;
            char? padding = null;
            var uppercase = false;
            var swapCase = false;

            while (index < format.Length)
            {
                switch (format[index])
                {
                    case '-':
                        noPadding = true;
                        index++;
                        continue;
                    case '_':
                        noPadding = false;
                        padding = ' ';
                        index++;
                        continue;
                    case '0':
                        noPadding = false;
                        padding = '0';
                        index++;
                        continue;
                    case '^':
                        uppercase = true;
                        index++;
                        continue;
                    case '#':
                        swapCase = true;
                        index++;
                        continue;
                    default:
                        break;
                }

                break;
            }

            var colonCount = 0;
            while (index < format.Length && format[index] == ':')
            {
                colonCount++;
                index++;
            }

            var width = 0;
            var widthOverflow = false;
            while (index < format.Length && char.IsDigit(format[index]))
            {
                var digit = format[index] - '0';
                if (digit is < 0 or > 9 || width > (int.MaxValue - digit) / 10)
                {
                    widthOverflow = true;
                }
                else if (!widthOverflow)
                {
                    width = width * 10 + digit;
                }
                index++;
            }

            if (widthOverflow)
            {
                result = string.Empty;
                return false;
            }

            char? modifier = null;
            if (index < format.Length && format[index] is 'E' or 'O')
            {
                modifier = format[index];
                index++;
            }

            if (index >= format.Length)
            {
                if (!TryAppend('%') || !TryAppendText(format.AsSpan(directiveStart)))
                {
                    result = string.Empty;
                    return false;
                }
                break;
            }

            var specifier = format[index];
            var directive = format[directiveStart..(index + 1)];
            var formatted = FormatDirective(value, specifier, colonCount, modifier, directive, dialect);
            var text = formatted.Text;
            if (formatted.Supported && uppercase)
            {
                text = text.ToUpperInvariant();
            }
            else if (formatted.Supported && swapCase)
            {
                text = SwapCase(text);
            }

            var directiveLength = formatted.Supported && !noPadding ? Math.Max(width, text.Length) : text.Length;
            if (directiveLength > maximumLength - builder.Length)
            {
                result = string.Empty;
                return false;
            }
            EnsureCharacters(builder.Length + directiveLength);
            if (formatted.Supported
                && !TryApplyPadding(
                    ref text,
                    noPadding,
                    padding,
                    formatted.DefaultPadding,
                    width,
                    maximumLength - builder.Length,
                    fractionalSeconds: specifier == 'N'))
            {
                result = string.Empty;
                return false;
            }

            if (!TryAppendText(text.AsSpan()))
            {
                result = string.Empty;
                return false;
            }
        }

        result = builder.ToString();
        return true;

        bool TryAppend(char appended)
        {
            if (builder.Length >= maximumLength)
                return false;

            EnsureCharacters(builder.Length + 1);
            builder.Append(appended);
            return true;
        }

        bool TryAppendText(ReadOnlySpan<char> appended)
        {
            if (appended.Length > maximumLength - builder.Length)
                return false;

            EnsureCharacters(builder.Length + appended.Length);
            builder.Append(appended);
            return true;
        }

        void EnsureCharacters(int required)
        {
            if (required <= admittedCharacters)
                return;
            var capacity = (int)Math.Min(maximumLength, Math.Max(required, Math.Max(16, 2L * admittedCharacters)));
            reserveCharacters(capacity - admittedCharacters);
            admittedCharacters = capacity;
            builder.EnsureCapacity(capacity);
        }

        static FormattedDirective FormatDirective(
            DateTimeOffset directiveValue,
            char specifier,
            int colonCount,
            char? modifier,
            string directive,
            GnuDateFormatDialect directiveDialect)
        {
            if (directiveDialect == GnuDateFormatDialect.Strftime
                && (colonCount > 0 || !IsStrftimeSpecifier(specifier, modifier)))
            {
                return new FormattedDirective("%" + directive, false, ' ');
            }

            if (colonCount > 0 && specifier != 'z')
                throw new GnuDateFormatException(directive);

            var text = specifier switch
            {
                '%' => "%",
                'n' => "\n",
                't' => "\t",
                'H' => directiveValue.ToString("HH", Culture),
                'I' => directiveValue.ToString("hh", Culture),
                'k' => directiveValue.Hour.ToString(Culture).PadLeft(2, ' '),
                'l' => (directiveValue.Hour % 12 == 0 ? 12 : directiveValue.Hour % 12).ToString(Culture).PadLeft(2, ' '),
                'M' => directiveValue.ToString("mm", Culture),
                'N' => FormatNanoseconds(directiveValue),
                'p' => directiveValue.ToString("tt", Culture),
                'P' => directiveValue.ToString("tt", Culture).ToLowerInvariant(),
                'r' => Format(directiveValue, "%I:%M:%S %p", directiveDialect),
                'R' => Format(directiveValue, "%H:%M", directiveDialect),
                's' => directiveValue.ToUnixTimeSeconds().ToString(Culture),
                'S' => directiveValue.ToString("ss", Culture),
                'T' => Format(directiveValue, "%H:%M:%S", directiveDialect),
                'X' => Format(directiveValue, "%H:%M:%S", directiveDialect),
                '+' => Format(directiveValue, "%a %b %e %H:%M:%S %Z %Y", directiveDialect),
                'z' => FormatOffset(directiveValue.Offset, colonCount),
                'Z' => directiveValue.Offset == TimeSpan.Zero ? "UTC" : FormatOffset(directiveValue.Offset, 1),
                'a' => directiveValue.ToString("ddd", Culture),
                'A' => directiveValue.ToString("dddd", Culture),
                'b' => directiveValue.ToString("MMM", Culture),
                'B' => directiveValue.ToString("MMMM", Culture),
                'c' => Format(directiveValue, "%a %b %e %H:%M:%S %Y", directiveDialect),
                'C' => (directiveValue.Year / 100).ToString(Culture).PadLeft(2, '0'),
                'd' => directiveValue.ToString("dd", Culture),
                'D' => Format(directiveValue, "%m/%d/%y", directiveDialect),
                'e' => directiveValue.Day.ToString(Culture).PadLeft(2, ' '),
                'F' => Format(directiveValue, "%Y-%m-%d", directiveDialect),
                'g' => (ISOWeek.GetYear(directiveValue.DateTime) % 100).ToString(Culture).PadLeft(2, '0'),
                'G' => ISOWeek.GetYear(directiveValue.DateTime).ToString(Culture).PadLeft(4, '0'),
                'h' => directiveValue.ToString("MMM", Culture),
                'j' => directiveValue.DayOfYear.ToString(Culture).PadLeft(3, '0'),
                'm' => directiveValue.ToString("MM", Culture),
                'q' => (((directiveValue.Month - 1) / 3) + 1).ToString(Culture),
                'u' => (directiveValue.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)directiveValue.DayOfWeek).ToString(Culture),
                'U' => GetWeekNumber(directiveValue, mondayFirst: false).ToString(Culture).PadLeft(2, '0'),
                'V' => ISOWeek.GetWeekOfYear(directiveValue.DateTime).ToString(Culture).PadLeft(2, '0'),
                'w' => ((int)directiveValue.DayOfWeek).ToString(Culture),
                'W' => GetWeekNumber(directiveValue, mondayFirst: true).ToString(Culture).PadLeft(2, '0'),
                'x' => Format(directiveValue, "%m/%d/%y", directiveDialect),
                'y' => directiveValue.ToString("yy", Culture),
                'Y' => directiveValue.ToString("yyyy", Culture),
                _ => throw new GnuDateFormatException(directive)
            };
            return new FormattedDirective(text, true, GetDefaultPadding(specifier));

            static char GetDefaultPadding(char value) => value is
                'C' or 'd' or 'g' or 'G' or 'H' or 'I' or 'j' or 'm' or 'M'
                    or 'S' or 'u' or 'U' or 'V' or 'w' or 'W' or 'y' or 'Y'
                ? '0'
                : ' ';

            static bool IsStrftimeSpecifier(char value, char? alternativeModifier) => alternativeModifier switch
            {
                'E' => value is 'c' or 'C' or 'x' or 'X' or 'y' or 'Y',
                'O' => value is 'd' or 'e' or 'H' or 'I' or 'm' or 'M' or 'S'
                    or 'u' or 'U' or 'V' or 'w' or 'W' or 'y',
                null => value is
                    '%' or 'n' or 't'
                    or 'H' or 'I' or 'k' or 'l' or 'M' or 'p' or 'P' or 'r' or 'R' or 's' or 'S' or 'T' or 'X'
                    or 'z' or 'Z'
                    or 'a' or 'A' or 'b' or 'B' or 'c' or 'C' or 'd' or 'D' or 'e' or 'F'
                    or 'g' or 'G' or 'h' or 'j' or 'm' or 'u' or 'U' or 'V' or 'w' or 'W' or 'x' or 'y' or 'Y',
                _ => false
            };
        }

        static string FormatNanoseconds(DateTimeOffset directiveValue)
        {
            var ticksWithinSecond = directiveValue.Ticks % TimeSpan.TicksPerSecond;
            var nanoseconds = ticksWithinSecond * 100;
            return nanoseconds.ToString(Culture).PadLeft(9, '0');
        }

        static string FormatOffset(TimeSpan offset, int colonCount)
        {
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var absoluteOffset = offset.Duration();
            return colonCount switch
            {
                0 => string.Create(Culture, $"{sign}{absoluteOffset.Hours:00}{absoluteOffset.Minutes:00}"),
                1 => string.Create(Culture, $"{sign}{absoluteOffset.Hours:00}:{absoluteOffset.Minutes:00}"),
                2 => string.Create(Culture, $"{sign}{absoluteOffset.Hours:00}:{absoluteOffset.Minutes:00}:{absoluteOffset.Seconds:00}"),
                _ when absoluteOffset.Seconds != 0 => string.Create(Culture, $"{sign}{absoluteOffset.Hours:00}:{absoluteOffset.Minutes:00}:{absoluteOffset.Seconds:00}"),
                _ when absoluteOffset.Minutes != 0 => string.Create(Culture, $"{sign}{absoluteOffset.Hours:00}:{absoluteOffset.Minutes:00}"),
                _ => string.Create(Culture, $"{sign}{absoluteOffset.Hours:00}")
            };
        }

        static bool TryApplyPadding(
            ref string input,
            bool noPadding,
            char? padding,
            char defaultPadding,
            int width,
            int maximumLength,
            bool fractionalSeconds)
        {
            if (fractionalSeconds)
            {
                // %N widths select fractional precision; padding and trimming apply on the right.
                width = width == 0 ? input.Length : width;
                if (width < input.Length)
                    input = input[..width];
                if (noPadding || padding == ' ')
                {
                    input = input.TrimEnd('0');
                    if (input.Length == 0)
                        input = "0";
                }
                if (!noPadding)
                {
                    if (width > maximumLength)
                        return false;
                    input = input.PadRight(width, padding ?? '0');
                }
            }
            else if (noPadding)
            {
                var unpadded = input.TrimStart('0', ' ');
                input = unpadded.Length == 0 && input.Length > 0
                    ? "0"
                    : unpadded;
            }
            else
            {
                if (padding == ' ')
                    input = ReplaceLeading(input, '0', ' ');
                else if (padding == '0')
                    input = ReplaceLeading(input, ' ', '0');

                if (width > input.Length)
                {
                    if (width > maximumLength)
                        return false;

                    var pad = padding ?? defaultPadding;
                    input = input.PadLeft(width, pad);
                }
            }

            return input.Length <= maximumLength;
        }

        static string ReplaceLeading(string input, char from, char to)
        {
            var chars = input.ToCharArray();
            for (var index = 0; index < chars.Length && chars[index] == from; index++)
            {
                chars[index] = to;
            }

            return new string(chars);
        }

        static string SwapCase(string input)
        {
            var chars = input.ToCharArray();
            for (var index = 0; index < chars.Length; index++)
            {
                var ch = chars[index];
                chars[index] = char.IsUpper(ch)
                    ? char.ToLowerInvariant(ch)
                    : char.IsLower(ch)
                        ? char.ToUpperInvariant(ch)
                        : ch;
            }

            return new string(chars);
        }
    }

    public static void Validate(string format)
    {
        Format(DateTimeOffset.UnixEpoch, format);
    }

    public static bool TryFormatFindTimeDirective(DateTimeOffset value, char specifier, out string result)
    {
        var text = specifier switch
        {
            '@' => value.ToUnixTimeSeconds().ToString(Culture),
            '+' => value.ToString("yyyy-MM-dd+HH:mm:ss.FFFFFFFK", Culture),
            'H' => value.ToString("HH", Culture),
            'I' => value.ToString("hh", Culture),
            'k' => value.Hour.ToString(Culture).PadLeft(2, ' '),
            'l' => (value.Hour % 12 == 0 ? 12 : value.Hour % 12).ToString(Culture).PadLeft(2, ' '),
            'M' => value.ToString("mm", Culture),
            'p' => value.ToString("tt", Culture),
            'r' => value.ToString("hh:mm:ss tt", Culture),
            'S' => value.ToString("ss.FFFFFFF", Culture),
            'T' => value.ToString("HH:mm:ss.FFFFFFF", Culture),
            'X' => value.ToString("T", Culture),
            'Z' => value.ToString("zzz", Culture),
            'a' => value.ToString("ddd", Culture),
            'A' => value.ToString("dddd", Culture),
            'b' => value.ToString("MMM", Culture),
            'B' => value.ToString("MMMM", Culture),
            'c' => value.ToString("ddd MMM dd HH:mm:ss yyyy", Culture),
            'd' => value.ToString("dd", Culture),
            'D' => value.ToString("MM/dd/yy", Culture),
            'F' => value.ToString("yyyy-MM-dd", Culture),
            'h' => value.ToString("MMM", Culture),
            'j' => value.DayOfYear.ToString(Culture).PadLeft(3, '0'),
            'm' => value.ToString("MM", Culture),
            'U' => GetWeekNumber(value, mondayFirst: false).ToString(Culture).PadLeft(2, '0'),
            'w' => ((int)value.DayOfWeek).ToString(Culture),
            'W' => GetWeekNumber(value, mondayFirst: true).ToString(Culture).PadLeft(2, '0'),
            'x' => value.ToString("MM/dd/yy", Culture),
            'y' => value.ToString("yy", Culture),
            'Y' => value.ToString("yyyy", Culture),
            _ => null
        };
        result = text ?? string.Empty;
        return text != null;
    }

    private static int GetWeekNumber(DateTimeOffset value, bool mondayFirst)
    {
        var firstWeekDay = mondayFirst
            ? DayOfWeek.Monday
            : DayOfWeek.Sunday;
        var firstDayOfYear = new DateTime(value.Year, 1, 1).DayOfWeek;
        var firstWeekStart = ((int)firstWeekDay - (int)firstDayOfYear + 7) % 7;
        var dayOfYear = value.DayOfYear - 1;
        if (dayOfYear < firstWeekStart)
            return 0;

        return ((dayOfYear - firstWeekStart) / 7) + 1;
    }
}

public sealed class GnuDateFormatException(string directive)
    : Exception($"'%{directive}' is not supported on this system.");

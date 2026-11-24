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

    private enum DirectiveCase { Unchanged, Upper, Lower, Swap }

    // Weekdays and leap days repeat in a 400-year Gregorian cycle.
    private readonly record struct CalendarTime(DateTimeOffset Local, long YearOffset, long UnixSeconds)
    {
        internal int StrftimeYear => unchecked((int)(Local.Year + YearOffset));

        internal (int Year, int Week) GetStrftimeIsoWeek()
        {
            var year = StrftimeYear;
            var dayFromMonday = ((int)Local.DayOfWeek + 6) % 7;
            var thursday = Local.DayOfYear - 1 - dayFromMonday + 3;
            if (thursday < 0)
            {
                year = unchecked(year - 1);
                thursday += DaysInYear(year);
            }
            else
            {
                // Linux strftime counts leap days using its signed display year.
                var daysInYear = DaysInYear(year);
                if (thursday >= daysInYear)
                {
                    year = unchecked(year + 1);
                    thursday -= daysInYear;
                }
            }
            return (year, thursday / 7 + 1);

            static int DaysInYear(int year) =>
                year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 366 : 365;
        }

        internal static CalendarTime FromUnixSeconds(long unixSeconds, TimeSpan offset)
        {
            const long SecondsPerCycle = 146097L * 86400;
            var (cycles, remainder) = Int128.DivRem((Int128)unixSeconds + offset.Ticks / TimeSpan.TicksPerSecond, SecondsPerCycle);
            if (remainder < 0)
            {
                cycles--;
                remainder += SecondsPerCycle;
            }

            var yearOffset = (long)cycles * 400;
            var local = DateTime.UnixEpoch.AddTicks((long)remainder * TimeSpan.TicksPerSecond);
            var tmYear = local.Year + yearOffset - 1900;
            return tmYear is >= int.MinValue and <= int.MaxValue
                ? new CalendarTime(new DateTimeOffset(local.Ticks, offset), yearOffset, unixSeconds)
                : new CalendarTime(DateTimeOffset.UnixEpoch.ToOffset(offset), 0, 0);
        }
    }

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
        Action<int> reserveCharacters) =>
        TryFormat(new CalendarTime(value, 0, value.ToUnixTimeSeconds()), format, dialect,
            maximumLength, out result, reserveCharacters);

    internal static bool TryFormatUnixTime(
        long seconds, TimeSpan offset, string format, int maximumLength, out string result, Action<int> reserveCharacters) =>
        TryFormat(CalendarTime.FromUnixSeconds(seconds, offset), format, GnuDateFormatDialect.Strftime,
            maximumLength, out result, reserveCharacters);

    private static bool TryFormat(
        CalendarTime value,
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
        var strftime = dialect == GnuDateFormatDialect.Strftime;
        // Generated fields fit even with a signed Unix timestamp, a long year, or a numeric zone.
        Span<char> field = stackalloc char[48];

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
            var changeCase = false;

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
                        changeCase = true;
                        index++;
                        continue;
                    default:
                        break;
                }

                break;
            }

            var colonCount = 0;
            while (!strftime && index < format.Length && format[index] == ':')
            {
                colonCount++;
                index++;
            }

            var width = 0;
            var widthOverflow = false;
            while (index < format.Length && format[index] is >= '0' and <= '9')
            {
                var digit = format[index] - '0';
                if (width > (int.MaxValue - digit) / 10)
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

            var complete = index < format.Length;
            var specifier = complete ? format[index] : '\0';
            if (strftime && (!complete || !IsStrftimeSpecifier(specifier, modifier)))
            {
                var end = complete ? index + 1 : index;
                var literal = format.AsSpan(directiveStart - 1, end - directiveStart + 1);
                // The abbreviated month directives select uppercase before rejecting their E modifier.
                var upperLiteral = uppercase || (changeCase && specifier is 'b' or 'h');
                if (!TryAppendStrftimeLiteral(literal, noPadding ? ' ' : padding ?? ' ', width, upperLiteral))
                {
                    result = string.Empty;
                    return false;
                }
                continue;
            }

            if (!complete)
            {
                if (!TryAppend('%') || !TryAppendText(format.AsSpan(directiveStart)))
                {
                    result = string.Empty;
                    return false;
                }
                break;
            }

            var directive = format.AsSpan(directiveStart, index - directiveStart + 1);
            if (colonCount > 0 && specifier != 'z')
                throw new GnuDateFormatException(directive.ToString());

            var composite = specifier switch
            {
                'r' => "%I:%M:%S %p",
                'R' => "%H:%M",
                'T' or 'X' => "%H:%M:%S",
                '+' => "%a %b %e %H:%M:%S %Z %Y",
                'c' => "%a %b %e %H:%M:%S %Y",
                'D' or 'x' => "%m/%d/%y",
                'F' => "%Y-%m-%d",
                _ => null
            };
            var length = composite == null
                ? WriteScalarDirective(value, specifier, colonCount, directive, strftime, field)
                : WriteComposite(value, composite, strftime, field);
            ReadOnlySpan<char> text = field[..length];
            var defaultPadding = specifier is
                'C' or 'd' or 'g' or 'G' or 'H' or 'I' or 'j' or 'm' or 'M'
                    or 'S' or 'u' or 'U' or 'V' or 'w' or 'W' or 'y' or 'Y'
                ? '0' : ' ';

            if (strftime)
            {
                if (!TryAppendStrftime(text, specifier, noPadding, padding, defaultPadding, width,
                        GetStrftimeCase(specifier, uppercase, changeCase)))
                {
                    result = string.Empty;
                    return false;
                }
                continue;
            }

            var dateCase = uppercase ? DirectiveCase.Upper : changeCase ? DirectiveCase.Swap : DirectiveCase.Unchanged;
            if (!TryAppendDate(text, specifier, noPadding, padding, defaultPadding, width, dateCase))
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

        bool TryAppendStrftime(
            ReadOnlySpan<char> text, char specifier, bool noPadding, char? padding, char defaultPadding, int width,
            DirectiveCase casing)
        {
            if (specifier == 'z')
                return TryAppendStrftimeOffset(text, noPadding, padding, width);

            var pad = noPadding ? ' ' : padding ?? defaultPadding;
            if ((noPadding || pad != defaultPadding) && specifier is
                'd' or 'e' or 'g' or 'H' or 'I' or 'j' or 'k' or 'l' or 'm' or 'M' or 'S' or 'u' or 'U' or 'V' or 'w' or 'W' or 'y')
            {
                if (!noPadding)
                    width = Math.Max(width, text.Length);

                // Ordinary numeric directives include their default padding in the rendered text.
                while (text.Length > 1 && text[0] is '0' or ' ')
                    text = text[1..];
            }

            if (!TryBeginField(text.Length, width, out var padCount))
                return false;

            if (padCount > 0 && pad == '0' && specifier is 'C' or 'G' or 'Y' && text[0] == '-')
            {
                builder.Append('-');
                text = text[1..];
            }

            builder.Append(pad, padCount);
            AppendDirectiveText(text, casing);
            return true;
        }

        bool TryAppendDate(
            ReadOnlySpan<char> text, char specifier, bool noPadding, char? padding, char defaultPadding, int width,
            DirectiveCase casing)
        {
            // Admit the generated field width before trimming or taking a fractional prefix.
            if (!TryBeginField(text.Length, noPadding ? 0 : width, out var padCount))
                return false;

            if (specifier == 'N')
            {
                // Date widths select fractional precision. Padding and trimming apply on the right.
                var precision = width == 0 ? text.Length : width;
                text = text[..Math.Min(precision, text.Length)];
                if (noPadding || padding == ' ')
                {
                    text = text.TrimEnd('0');
                    if (text.IsEmpty)
                        text = "0";
                }
                builder.Append(text);
                if (!noPadding)
                    builder.Append(padding ?? '0', precision - text.Length);
                return true;
            }

            if (noPadding)
            {
                var digits = text.TrimStart("0 ");
                text = digits.IsEmpty && !text.IsEmpty ? "0" : digits;
            }
            else
            {
                var pad = padding ?? defaultPadding;
                if (padding.HasValue)
                {
                    var unpadded = text.TrimStart(pad == ' ' ? '0' : ' ');
                    padCount += text.Length - unpadded.Length;
                    text = unpadded;
                }
                builder.Append(pad, padCount);
            }

            AppendDirectiveText(text, casing);
            return true;
        }

        void AppendDirectiveText(ReadOnlySpan<char> text, DirectiveCase casing)
        {
            if (casing == DirectiveCase.Unchanged)
            {
                builder.Append(text);
                return;
            }

            // Generated directive text uses the invariant, ASCII calendar profile.
            foreach (var ch in text)
            {
                builder.Append(casing switch
                {
                    DirectiveCase.Upper => char.ToUpperInvariant(ch),
                    DirectiveCase.Lower => char.ToLowerInvariant(ch),
                    _ when char.IsUpper(ch) => char.ToLowerInvariant(ch),
                    _ when char.IsLower(ch) => char.ToUpperInvariant(ch),
                    _ => ch
                });
            }
        }

        bool TryAppendStrftimeLiteral(ReadOnlySpan<char> literal, char padding, int width, bool uppercase)
        {
            if (!TryBeginField(literal.Length, width, out var padCount))
                return false;

            builder.Append(padding, padCount);
            if (uppercase && literal[^1] is >= 'a' and <= 'z')
            {
                builder.Append(literal[..^1]);
                builder.Append((char)(literal[^1] - 'a' + 'A'));
            }
            else
            {
                builder.Append(literal);
            }
            return true;
        }

        bool TryBeginField(int textLength, int width, out int paddingCount)
        {
            var length = Math.Max(width, textLength);
            paddingCount = length - textLength;
            if (length > maximumLength - builder.Length)
                return false;

            EnsureCharacters(builder.Length + length);
            return true;
        }

        bool TryAppendStrftimeOffset(ReadOnlySpan<char> text, bool noPadding, char? padding, int width)
        {
            var sign = text[0];
            var digits = text[1..].TrimStart('0');
            if (digits.IsEmpty)
                digits = "0";

            // The width applies separately to the sign field and to the offset digits.
            var signWidth = Math.Max(1, width);
            var minimumDigitWidth = noPadding ? digits.Length : text.Length - 1;
            var digitWidth = Math.Max(width, minimumDigitWidth);
            var available = maximumLength - builder.Length;
            if (signWidth > available || digitWidth > available - signWidth)
                return false;

            EnsureCharacters(builder.Length + signWidth + digitWidth);
            builder.Append(noPadding ? ' ' : padding ?? ' ', signWidth - 1);
            builder.Append(sign);
            builder.Append(noPadding ? ' ' : padding ?? '0', digitWidth - digits.Length);
            builder.Append(digits);
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

        static DirectiveCase GetStrftimeCase(char specifier, bool uppercase, bool changeCase)
        {
            if (specifier == 'P')
                return DirectiveCase.Unchanged;
            if (changeCase && specifier is 'p' or 'Z')
                return DirectiveCase.Lower;
            if (uppercase || (changeCase && specifier is 'a' or 'A' or 'b' or 'B' or 'h'))
                return DirectiveCase.Upper;
            return DirectiveCase.Unchanged;
        }

        static int WriteComposite(CalendarTime calendar, string recipe, bool strftime, Span<char> destination)
        {
            // Recipes contain literal separators and unmodified scalar directives.
            var length = 0;
            for (var index = 0; index < recipe.Length; index++)
            {
                var ch = recipe[index];
                if (ch != '%')
                {
                    destination[length++] = ch;
                    continue;
                }

                index++;
                length += WriteScalarDirective(calendar, recipe[index], 0, recipe.AsSpan(index, 1), strftime,
                    destination[length..]);
            }
            return length;
        }

        static int WriteScalarDirective(
            CalendarTime calendar,
            char specifier,
            int colonCount,
            ReadOnlySpan<char> directive,
            bool strftime,
            Span<char> destination)
        {
            var directiveValue = calendar.Local;
            return specifier switch
            {
                '%' => WriteLiteral("%", destination),
                'n' => WriteLiteral("\n", destination),
                't' => WriteLiteral("\t", destination),
                'H' => WriteValue(directiveValue, "HH", destination),
                'I' => WriteValue(directiveValue, "hh", destination),
                'k' => WriteSpacePaddedNumber(directiveValue.Hour, destination),
                'l' => WriteSpacePaddedNumber(1 + (directiveValue.Hour + 11) % 12, destination),
                'M' => WriteValue(directiveValue, "mm", destination),
                'N' => WriteValue(directiveValue.Ticks % TimeSpan.TicksPerSecond * 100, "D9", destination),
                'p' => WriteValue(directiveValue, "tt", destination),
                'P' => WriteLiteral(directiveValue.Hour < 12 ? "am" : "pm", destination),
                's' => WriteValue(calendar.UnixSeconds, default, destination),
                'S' => WriteValue(directiveValue, "ss", destination),
                'z' => WriteOffset(directiveValue.Offset, colonCount, destination),
                'Z' => directiveValue.Offset == TimeSpan.Zero ? WriteLiteral("UTC", destination)
                    : WriteOffset(directiveValue.Offset, 1, destination),
                'a' => WriteValue(directiveValue, "ddd", destination),
                'A' => WriteValue(directiveValue, "dddd", destination),
                'b' or 'h' => WriteValue(directiveValue, "MMM", destination),
                'B' => WriteValue(directiveValue, "MMMM", destination),
                'C' when strftime => WriteValue((calendar.StrftimeYear - (long)LastTwoDigits(calendar.StrftimeYear)) / 100,
                    default, destination),
                'C' => WriteValue(directiveValue.Year / 100, "D2", destination),
                'd' => WriteValue(directiveValue, "dd", destination),
                'e' => WriteSpacePaddedNumber(directiveValue.Day, destination),
                'g' when strftime => WriteValue(LastTwoDigits(calendar.GetStrftimeIsoWeek().Year), "D2", destination),
                'g' => WriteValue(ISOWeek.GetYear(directiveValue.DateTime) % 100, "D2", destination),
                'G' when strftime => WriteValue(calendar.GetStrftimeIsoWeek().Year, default, destination),
                'G' => WriteValue(ISOWeek.GetYear(directiveValue.DateTime), "D4", destination),
                'j' => WriteValue(directiveValue.DayOfYear, "D3", destination),
                'm' => WriteValue(directiveValue, "MM", destination),
                'q' => WriteValue((directiveValue.Month - 1) / 3 + 1, default, destination),
                'u' => WriteValue(directiveValue.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)directiveValue.DayOfWeek,
                    default, destination),
                'U' => WriteValue(GetWeekNumber(directiveValue, mondayFirst: false), "D2", destination),
                'V' when strftime => WriteValue(calendar.GetStrftimeIsoWeek().Week, "D2", destination),
                'V' => WriteValue(ISOWeek.GetWeekOfYear(directiveValue.DateTime), "D2", destination),
                'w' => WriteValue((int)directiveValue.DayOfWeek, default, destination),
                'W' => WriteValue(GetWeekNumber(directiveValue, mondayFirst: true), "D2", destination),
                'y' when strftime => WriteValue(LastTwoDigits(directiveValue.Year + calendar.YearOffset), "D2", destination),
                'y' => WriteValue(directiveValue, "yy", destination),
                'Y' when strftime => WriteValue(calendar.StrftimeYear, default, destination),
                'Y' => WriteValue(directiveValue, "yyyy", destination),
                _ => throw new GnuDateFormatException(directive.ToString())
            };

            static int LastTwoDigits(long year)
            {
                var remainder = (int)(year % 100);
                return remainder < 0 ? remainder + 100 : remainder;
            }
        }

        static bool IsStrftimeSpecifier(char value, char? alternativeModifier)
        {
            // The Linux C locale accepts either modifier on these directives.
            if (value is '%' or 'n' or 't' or 'p' or 'P' or 'r' or 'R' or 's' or 'T' or 'z' or 'Z')
                return true;

            return alternativeModifier switch
            {
                'E' => value is 'c' or 'C' or 'u' or 'x' or 'X' or 'y' or 'Y',
                'O' => value is 'H' or 'I' or 'k' or 'l' or 'M' or 'S'
                    or 'b' or 'B' or 'C' or 'd' or 'e' or 'g' or 'G' or 'h' or 'j' or 'm'
                    or 'u' or 'U' or 'V' or 'w' or 'W' or 'y',
                null => value is 'H' or 'I' or 'k' or 'l' or 'M' or 'S' or 'X'
                    or 'a' or 'A' or 'b' or 'B' or 'c' or 'C' or 'd' or 'D' or 'e' or 'F'
                    or 'g' or 'G' or 'h' or 'j' or 'm' or 'u' or 'U' or 'V' or 'w' or 'W' or 'x' or 'y' or 'Y',
                _ => false
            };
        }

        static int WriteValue<T>(T value, ReadOnlySpan<char> format, Span<char> destination) where T : ISpanFormattable
        {
            if (!value.TryFormat(destination, out var length, format, Culture))
                throw new InvalidOperationException("A generated date field exceeded its buffer.");
            return length;
        }

        static int WriteLiteral(ReadOnlySpan<char> text, Span<char> destination)
        {
            text.CopyTo(destination);
            return text.Length;
        }

        static int WriteSpacePaddedNumber(int value, Span<char> destination)
        {
            if (value >= 10)
                return WriteValue(value, default, destination);
            destination[0] = ' ';
            destination[1] = (char)('0' + value);
            return 2;
        }

        static int WriteOffset(TimeSpan offset, int colonCount, Span<char> destination)
        {
            destination[0] = offset < TimeSpan.Zero ? '-' : '+';
            var absoluteOffset = offset.Duration();
            var length = 1 + WriteValue(absoluteOffset.Hours, "D2", destination[1..]);
            if (colonCount >= 3 && absoluteOffset.Minutes == 0 && absoluteOffset.Seconds == 0)
                return length;

            if (colonCount != 0)
                destination[length++] = ':';
            length += WriteValue(absoluteOffset.Minutes, "D2", destination[length..]);
            if (colonCount == 2 || colonCount >= 3 && absoluteOffset.Seconds != 0)
            {
                destination[length++] = ':';
                length += WriteValue(absoluteOffset.Seconds, "D2", destination[length..]);
            }
            return length;
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

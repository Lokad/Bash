using System;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private sealed partial class Formatter
    {
        public async ValueTask<PrintfResult> RunAsync(
            Func<PrintfVariableAssignment, ValueTask<bool>> assignCount,
            Func<ShellValue, ValueTask<bool>> reportDiagnostic)
        {
            var reportedDiagnostics = 0;
            var diagnosticOutputClosed = false;
            using var timeMemory = new MemoryCapacity(memoryBudget, 0);
            try
            {
                if (format.Length == 0)
                    return BuildResult();

                do
                {
                    var argumentIndexAtCycleStart = _argumentIndex;
                    _cycleOutputStart = _output.Length;
                    await FormatCycleAsync().ConfigureAwait(false);

                    if (_stop || _failed || _argumentIndex == argumentIndexAtCycleStart)
                        break;
                }
                while (_argumentIndex < arguments.Count);

                await ReportPendingDiagnosticsAsync().ConfigureAwait(false);
                return BuildResult();
            }
            catch (MemoryBudgetExceededException)
            {
                AddFatalDiagnostic("shared memory budget exhausted");
                await ReportPendingDiagnosticsAsync().ConfigureAwait(false);
                return new PrintfResult(ReadOnlyMemory<byte>.Empty, [], _diagnostics, 1);
            }
            finally { _temporaryOutput?.Dispose(); _output.Dispose(); }

            async ValueTask FormatCycleAsync()
            {
                for (var index = 0; index < format.Length && !_stop && !_failed;)
                {
                    var current = (char)format.Span[index];
                    if (current == '\\')
                    {
                        index++;
                        if (index >= format.Length)
                        {
                            AppendByte((byte)'\\');
                            break;
                        }

                        var consumed = AppendEscape(_output, format, index, percentB: false, out _);
                        if (consumed == 0)
                        {
                            AppendByte((byte)'\\');
                        }
                        else
                        {
                            index += consumed;
                        }

                        continue;
                    }

                    if (current != '%')
                    {
                        AppendByte(format.Span[index++]);
                        continue;
                    }

                    var specificationStart = index;
                    index++;
                    if (index < format.Length && (char)format.Span[index] == '%')
                    {
                        AppendByte((byte)'%');
                        index++;
                        continue;
                    }

                    if (!TryParseSpecification(specificationStart, ref index, out var specification))
                        return;

                    await ApplySpecificationAsync(specification).ConfigureAwait(false);
                    await ReportPendingDiagnosticsAsync().ConfigureAwait(false);
                }
            }

            bool TryParseSpecification(
                int specificationStart,
                ref int index,
                out FormatSpecification specification)
            {
                var flags = FormatFlags.None;
                while (index < format.Length)
                {
                    var flag = (char)format.Span[index] switch
                    {
                        '#' => FormatFlags.Alternate,
                        '0' => FormatFlags.ZeroPad,
                        '-' => FormatFlags.LeftJustify,
                        ' ' => FormatFlags.SpaceSign,
                        '+' => FormatFlags.PlusSign,
                        '\'' => FormatFlags.Grouping,
                        _ => FormatFlags.None
                    };

                    if (flag == FormatFlags.None)
                        break;

                    flags |= flag;
                    index++;
                }

                var width = 0;
                if (index < format.Length && (char)format.Span[index] == '*')
                {
                    index++;
                    width = ReadDynamicInteger("field width", 0);
                    if (width < 0)
                    {
                        flags |= FormatFlags.LeftJustify;
                        width = width == int.MinValue ? int.MaxValue : -width;
                    }
                }
                else if (!TryReadDecimal(format, ref index, out width))
                {
                    AddConversionDiagnostic($"{Utf8Text.Decode(format[specificationStart..index])}: numerical result out of range");
                    width = 0;
                }

                int? precision = null;
                if (index < format.Length && (char)format.Span[index] == '.')
                {
                    index++;
                    if (index < format.Length && (char)format.Span[index] == '*')
                    {
                        index++;
                        var dynamicPrecision = ReadDynamicInteger("precision", -1);
                        if (dynamicPrecision >= 0)
                            precision = dynamicPrecision;
                    }
                    else
                    {
                        var negative = index < format.Length && (char)format.Span[index] == '-';
                        if (negative)
                            index++;

                        if (!TryReadDecimal(format, ref index, out var parsedPrecision))
                        {
                            AddConversionDiagnostic($"{Utf8Text.Decode(format[specificationStart..index])}: numerical result out of range");
                        }
                        else if (!negative)
                        {
                            precision = parsedPrecision;
                        }
                    }
                }

                while (index < format.Length && (char)format.Span[index] is 'h' or 'j' or 'l' or 'L' or 't' or 'z')
                    index++;

                if (index >= format.Length)
                {
                    AddFatalDiagnostic($"{Utf8Text.Decode(format[specificationStart..])}: missing format character");
                    specification = default;
                    return false;
                }

                var conversion = (char)format.Span[index++];
                string? timeFormat = null;
                if (conversion == '(')
                {
                    var timeStart = index;
                    var depth = 1;
                    while (index < format.Length && depth > 0)
                    {
                        if ((char)format.Span[index] == '(')
                            depth++;
                        else if ((char)format.Span[index] == ')')
                            depth--;

                        if (depth > 0)
                            index++;
                    }

                    if (depth != 0 || index + 1 >= format.Length || (char)format.Span[index + 1] != 'T')
                    {
                        AddFatalDiagnostic("invalid time format specification");
                        specification = default;
                        return false;
                    }

                    timeFormat = Utf8Text.Decode(format[timeStart..index]);
                    index += 2;
                    conversion = 'T';
                }

                if (width > MaximumFieldSize)
                {
                    AddFatalDiagnostic($"field width exceeds {MaximumFieldSize}");
                    specification = default;
                    return false;
                }
                if (precision > MaximumFieldSize)
                {
                    AddFatalDiagnostic($"precision exceeds {MaximumFieldSize}");
                    specification = default;
                    return false;
                }

                specification = new FormatSpecification(
                    flags,
                    width,
                    precision,
                    conversion,
                    timeFormat);
                return true;

                int ReadDynamicInteger(string role, int overflowValue)
                {
                    if (!TryGetArgument(out var argument))
                        return 0;

                    var text = argument.ToUtf8String();
                    var value = ParseSigned(text);
                    if (value is < int.MinValue or > int.MaxValue)
                    {
                        AddConversionDiagnostic($"{text}: {role} out of range");
                        return overflowValue;
                    }

                    return (int)value;
                }

                static bool TryReadDecimal(ReadOnlyMemory<byte> source, ref int cursor, out int value)
                {
                    value = 0;
                    while (cursor < source.Length && (char)source.Span[cursor] is >= '0' and <= '9')
                    {
                        var digit = (char)source.Span[cursor] - '0';
                        if (value > (int.MaxValue - digit) / 10)
                        {
                            while (cursor < source.Length && (char)source.Span[cursor] is >= '0' and <= '9')
                                cursor++;
                            return false;
                        }

                        value = value * 10 + digit;
                        cursor++;
                    }
                    return true;
                }
            }

            async ValueTask ApplySpecificationAsync(FormatSpecification specification)
            {
                switch (specification.Conversion)
                {
                    case 'c':
                        {
                            var argument = GetValueArgument().Bytes;
                            ApplyText(specification,
                                argument.IsEmpty ? new byte[] { 0 } : argument[..1],
                                ignorePrecision: true);
                            return;
                        }
                    case 's':
                        ApplyText(specification, GetValueArgument().Bytes, ignorePrecision: false);
                        return;
                    case 'b':
                        AppendEscapedString(specification);
                        return;
                    case 'q':
                    case 'Q':
                        AppendQuotedString(specification);
                        return;
                    case 'n' when allowAssignments:
                        {
                            var variable = GetValueArgument();
                            if (variable.IsEmpty)
                                return;

                            if (!VariableNames.IsValidVariableName(variable.Bytes.Span))
                            {
                                AddFatalDiagnostic(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                                    .Append(ShellArgumentEscaping.QuoteDiagnosticOperand(variable).Bytes)
                                    .Append(": not a valid identifier"u8).ToMemory()));
                                return;
                            }

                            await ReportPendingDiagnosticsAsync().ConfigureAwait(false);
                            if (!_stop && !await assignCount(new PrintfVariableAssignment(
                                    variable.ToUtf8String(), _output.Length - _cycleOutputStart)).ConfigureAwait(false))
                                _stop = true;
                            return;
                        }
                    case 'T' when specification.TimeFormat != null:
                        {
                            var configuredFormat = specification.TimeFormat;
                            long seconds;
                            if (TryGetArgument(out var argument))
                                seconds = ParseSigned(argument.ToUtf8String());
                            else
                                seconds = -1;

                            var value = seconds switch
                            {
                                -1 => timestamp,
                                -2 => timestamp,
                                _ => FromUnixTime(seconds, timestamp.Offset)
                            };

                            try
                            {
                                timeMemory.Resize(0);
                                var timeFormat = configuredFormat.Length == 0 ? "%X" : configuredFormat;
                                if (!GnuDateFormatting.TryFormat(
                                        value,
                                        timeFormat,
                                        GnuDateFormatDialect.Strftime,
                                        _output.RemainingLength,
                                        out var text, count => timeMemory.Grow(GnuDateFormatting.EstimatedBytesPerCharacter * count)))
                                {
                                    FailOutputLimit();
                                    return;
                                }

                                ApplyUtf8Text(
                                    specification,
                                    text.AsSpan(),
                                    ignorePrecision: false,
                                    maximumBytes: int.MaxValue);
                            }
                            catch (GnuDateFormatException error)
                            {
                                AddFatalDiagnostic(error.Message);
                            }

                            return;

                            static DateTimeOffset FromUnixTime(long unixSeconds, TimeSpan offset)
                            {
                                try
                                {
                                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(offset);
                                }
                                catch (ArgumentOutOfRangeException)
                                {
                                    return DateTimeOffset.UnixEpoch;
                                }
                            }
                        }
                    case 'd':
                    case 'i':
                        {
                            var value = ParseSigned(GetValueArgument().ToUtf8String());
                            var negative = value < 0;
                            var magnitude = negative ? unchecked((ulong)(-(value + 1)) + 1) : (ulong)value;
                            var digits = magnitude.ToString(Culture);
                            digits = ApplyIntegerPrecision(digits, magnitude == 0, specification.Precision);
                            AppendNumber(
                                specification,
                                new FormattedNumber(
                                    digits,
                                    string.Empty,
                                    negative ? FormattedNumberSign.Negative : FormattedNumberSign.Positive,
                                    specification.Precision == null
                                        ? FormattedNumberPadding.ZeroAllowed
                                        : FormattedNumberPadding.SpaceOnly));
                            return;
                        }
                    case 'o':
                    case 'u':
                    case 'x':
                    case 'X':
                        {
                            var value = ParseUnsigned(GetValueArgument().ToUtf8String());
                            var digits = specification.Conversion switch
                            {
                                'o' => ConvertUnsigned(value, 8, uppercase: false),
                                'x' => ConvertUnsigned(value, 16, uppercase: false),
                                'X' => ConvertUnsigned(value, 16, uppercase: true),
                                _ => value.ToString(Culture)
                            };
                            digits = ApplyIntegerPrecision(digits, value == 0, specification.Precision);

                            var prefix = string.Empty;
                            if (specification.Flags.HasFlag(FormatFlags.Alternate))
                            {
                                if (specification.Conversion == 'o' && !digits.StartsWith('0'))
                                    prefix = "0";
                                else if (value != 0 && specification.Conversion == 'x')
                                    prefix = "0x";
                                else if (value != 0 && specification.Conversion == 'X')
                                    prefix = "0X";
                            }

                            AppendNumber(
                                specification,
                                new FormattedNumber(
                                    digits,
                                    prefix,
                                    FormattedNumberSign.None,
                                    specification.Precision == null
                                        ? FormattedNumberPadding.ZeroAllowed
                                        : FormattedNumberPadding.SpaceOnly));
                            return;

                            static string ConvertUnsigned(ulong unsigned, int numberBase, bool uppercase)
                            {
                                if (unsigned == 0)
                                    return "0";

                                Span<char> buffer = stackalloc char[64];
                                var index = buffer.Length;
                                var alphabet = uppercase ? "0123456789ABCDEF" : "0123456789abcdef";
                                var radix = (ulong)numberBase;
                                while (unsigned > 0)
                                {
                                    buffer[--index] = alphabet[(int)(unsigned % radix)];
                                    unsigned /= radix;
                                }

                                return new string(buffer[index..]);
                            }

                            ulong ParseUnsigned(string unsignedArgument)
                            {
                                if (unsignedArgument.Length == 0)
                                    return 0;

                                if (TryParseCharacterConstant(unsignedArgument, out var character))
                                    return (ulong)character;

                                var parsed = ParseInteger(
                                    unsignedArgument,
                                    out var negative,
                                    out var valid,
                                    out var overflow);
                                if (!valid)
                                    AddConversionDiagnostic($"{unsignedArgument}: invalid number");
                                else if (overflow)
                                    AddWarning($"warning: {unsignedArgument}: Numerical result out of range");

                                if (parsed > ulong.MaxValue)
                                    return ulong.MaxValue;

                                var unsigned = (ulong)parsed;
                                return negative ? unchecked(0UL - unsigned) : unsigned;
                            }
                        }
                    case 'a':
                    case 'A':
                    case 'e':
                    case 'E':
                    case 'f':
                    case 'F':
                    case 'g':
                    case 'G':
                        {
                            var argument = GetValueArgument().ToUtf8String();
                            var result = new FloatingPointFormatResult(
                                argument,
                                specification.Conversion,
                                specification.Precision,
                                specification.Flags.HasFlag(FormatFlags.Alternate));
                            if (result.Invalid)
                            {
                                AddConversionDiagnostic(result.InvalidHexadecimal
                                    ? $"{argument}: invalid hex number"
                                    : $"{argument}: invalid number");
                            }
                            if (result.RangeError)
                                AddWarning($"warning: {argument}: Numerical result out of range");

                            AppendNumber(specification, result.Number);
                            return;
                        }
                    default:
                        var diagnostic = ByteMemoryBuilder.CreateUnaccounted(0);
                        diagnostic.Append((byte)'`').Append((byte)specification.Conversion)
                            .Append("': invalid format character"u8);
                        AddFatalDiagnostic(ShellValue.FromBytes(diagnostic.ToMemory()));
                        return;
                }

                void AppendEscapedString(FormatSpecification escapedSpecification)
                {
                    var argument = GetValueArgument().Bytes;
                    var bytes = GetTemporaryOutput(argument.Length, _output.RemainingLength);
                    for (var index = 0; index < argument.Length;)
                    {
                        if (argument.Span[index] != (byte)'\\')
                        {
                            if (!TryAppendByte(bytes, argument.Span[index++]))
                                return;
                            continue;
                        }

                        index++;
                        if (index >= argument.Length)
                        {
                            TryAppendByte(bytes, (byte)'\\');
                            break;
                        }

                        var consumed = AppendEscape(bytes, argument, index, percentB: true, out var stop);
                        if (_failed)
                            return;

                        if (consumed == 0)
                        {
                            if (!TryAppendByte(bytes, (byte)'\\'))
                                return;
                        }
                        else
                        {
                            index += consumed;
                        }

                        if (stop)
                        {
                            ApplyText(escapedSpecification, bytes.ToMemory(), ignorePrecision: false);
                            _stop = true;
                            return;
                        }
                    }

                    ApplyText(escapedSpecification, bytes.ToMemory(), ignorePrecision: false);
                }

                void AppendQuotedString(FormatSpecification quotedSpecification)
                {
                    var argument = GetValueArgument().Bytes;
                    var rawByteLimit = quotedSpecification.Conversion == 'Q'
                        && quotedSpecification.Precision is { } rawPrecision
                        ? rawPrecision
                        : int.MaxValue;
                    var bytes = argument[..Math.Min(argument.Length, rawByteLimit)];
                    // Empty %q syntax can be truncated by precision before consuming the final output budget.
                    var quoted = bytes.IsEmpty
                        ? GetTemporaryOutput(2, 2) : GetTemporaryOutput(bytes.Length, _output.RemainingLength);
                    if (!ShellArgumentEscaping.TryAppendQuoted(quoted, bytes.Span,
                            quotedSpecification.Flags.HasFlag(FormatFlags.Alternate)
                                ? ShellQuoteStyle.SingleQuoted : ShellQuoteStyle.Backslash))
                    {
                        FailOutputLimit();
                        return;
                    }
                    var adjusted = quotedSpecification.Conversion == 'Q'
                        ? quotedSpecification with { Precision = null }
                        : quotedSpecification;
                    ApplyText(adjusted, quoted.ToMemory(), ignorePrecision: false);
                }

                ShellValue GetValueArgument() =>
                    TryGetArgument(out var argument) ? argument : ShellValue.Empty;

                static string ApplyIntegerPrecision(string digits, bool isZero, int? precision)
                {
                    if (precision == 0 && isZero)
                        return string.Empty;
                    return precision is { } minimum ? digits.PadLeft(minimum, '0') : digits;
                }

                void ApplyUtf8Text(
                    FormatSpecification textSpecification,
                    ReadOnlySpan<char> text,
                    bool ignorePrecision,
                    int maximumBytes)
                {
                    var byteCount = Math.Min(Utf8.GetByteCount(text), maximumBytes);
                    if (!ignorePrecision
                        && textSpecification.Precision is { } precision
                        && precision < byteCount)
                    {
                        byteCount = precision;
                    }

                    AppendLeadingTextPadding(textSpecification, byteCount);
                    TryAppendUtf8Prefix(_output, text, byteCount);
                    AppendTrailingTextPadding(textSpecification, byteCount);
                }

                void ApplyText(
                    FormatSpecification textSpecification,
                    ReadOnlyMemory<byte> bytes,
                    bool ignorePrecision)
                {
                    if (!ignorePrecision
                        && textSpecification.Precision is { } precision
                        && precision < bytes.Length)
                    {
                        bytes = bytes[..precision];
                    }

                    AppendLeadingTextPadding(textSpecification, bytes.Length);
                    TryAppendBytes(_output, bytes.Span);
                    AppendTrailingTextPadding(textSpecification, bytes.Length);
                }

                void AppendLeadingTextPadding(
                    FormatSpecification textSpecification,
                    int textWidth)
                {
                    var padding = Math.Max(0, textSpecification.Width - textWidth);
                    if (!textSpecification.Flags.HasFlag(FormatFlags.LeftJustify))
                        AppendPadding(padding, (byte)' ');
                }

                void AppendTrailingTextPadding(
                    FormatSpecification textSpecification,
                    int textWidth)
                {
                    var padding = Math.Max(0, textSpecification.Width - textWidth);
                    if (textSpecification.Flags.HasFlag(FormatFlags.LeftJustify))
                        AppendPadding(padding, (byte)' ');
                }

                void AppendNumber(
                    FormatSpecification numberSpecification,
                    FormattedNumber number)
                {
                    var sign = number.Sign switch
                    {
                        FormattedNumberSign.Negative => "-",
                        FormattedNumberSign.Positive
                            when numberSpecification.Flags.HasFlag(FormatFlags.PlusSign) => "+",
                        FormattedNumberSign.Positive
                            when numberSpecification.Flags.HasFlag(FormatFlags.SpaceSign) => " ",
                        _ => string.Empty
                    };
                    var length = sign.Length + number.Prefix.Length + number.Text.Length;
                    var padding = Math.Max(0, numberSpecification.Width - length);
                    var left = numberSpecification.Flags.HasFlag(FormatFlags.LeftJustify);
                    var zero = number.Padding == FormattedNumberPadding.ZeroAllowed
                        && numberSpecification.Flags.HasFlag(FormatFlags.ZeroPad)
                        && !left;

                    if (!left && !zero)
                        AppendPadding(padding, (byte)' ');

                    AppendText(sign);
                    AppendText(number.Prefix);
                    if (zero)
                        AppendPadding(padding, (byte)'0');
                    AppendText(number.Text);

                    if (left)
                        AppendPadding(padding, (byte)' ');
                }

            }

            async ValueTask ReportPendingDiagnosticsAsync()
            {
                while (!diagnosticOutputClosed && reportedDiagnostics < _diagnostics.Count)
                    if (!await reportDiagnostic(_diagnostics[reportedDiagnostics++]).ConfigureAwait(false))
                    {
                        diagnosticOutputClosed = true;
                        _stop = true;
                    }
            }

            PrintfResult BuildResult() => new(
                _output.DetachSnapshot(),
                [],
                _diagnostics,
                _failed || _conversionError ? 1 : 0);
        }
    }

    [Flags]
    private enum FormatFlags
    {
        None = 0,
        Alternate = 1 << 0,
        ZeroPad = 1 << 1,
        LeftJustify = 1 << 2,
        SpaceSign = 1 << 3,
        PlusSign = 1 << 4,
        Grouping = 1 << 5
    }

    private readonly record struct FormatSpecification(
        FormatFlags Flags,
        int Width,
        int? Precision,
        char Conversion,
        string? TimeFormat);
}

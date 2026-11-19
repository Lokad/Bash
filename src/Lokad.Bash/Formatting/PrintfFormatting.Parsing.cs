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
        public async ValueTask<(ReadOnlyMemory<byte> Output, int ExitCode)> RunAsync(
            Func<PrintfVariableAssignment, ValueTask<bool>> assignCount)
        {
            var diagnosticOutputClosed = false;
            using var timeMemory = new MemoryCapacity(memoryBudget, 0);
            using var numericMemory = new MemoryCapacity(memoryBudget, 0);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                try { await ReportPendingDiagnosticsAsync().ConfigureAwait(false); }
                catch (MemoryBudgetExceededException) { } // Report the refusal without retrying a queued record.
                if (reportDiagnostic != null && !diagnosticOutputClosed)
                    await reportDiagnostic(Diagnostic.MemoryRefusal).ConfigureAwait(false);
                return (ReadOnlyMemory<byte>.Empty, 1);
            }
            finally
            {
                _pendingDiagnostics?.Clear();
                _diagnosticMemory?.Dispose();
                _temporaryOutput?.Dispose();
                _output.Dispose();
            }

            async ValueTask FormatCycleAsync()
            {
                for (var index = 0; index < format.Length && !_stop && !_failed;)
                {
                    if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
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

                    await ApplySpecificationAsync(specification, index - 1).ConfigureAwait(false);
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
                    width = ReadDynamicInteger(": field width out of range", 0);
                    if (width < 0)
                    {
                        flags |= FormatFlags.LeftJustify;
                        width = width == int.MinValue ? int.MaxValue : -width;
                    }
                }
                else if (!TryReadDecimal(format, ref index, out width))
                {
                    AddConversionDiagnostic(new Diagnostic(string.Empty, ShellValue.FromBytes(format[specificationStart..index]),
                        ": numerical result out of range"));
                    width = 0;
                }

                int? precision = null;
                if (index < format.Length && (char)format.Span[index] == '.')
                {
                    index++;
                    if (index < format.Length && (char)format.Span[index] == '*')
                    {
                        index++;
                        var dynamicPrecision = ReadDynamicInteger(": precision out of range", -1);
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
                            AddConversionDiagnostic(new Diagnostic(string.Empty, ShellValue.FromBytes(format[specificationStart..index]),
                                ": numerical result out of range"));
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
                    AddFatalDiagnostic(new Diagnostic(string.Empty, ShellValue.FromBytes(format[specificationStart..]),
                        ": missing format character"));
                    specification = default;
                    return false;
                }

                var conversion = (char)format.Span[index++];
                ShellValue? timeFormat = null;
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

                    timeFormat = ShellValue.FromBytes(format[timeStart..index]);
                    index += 2;
                    conversion = 'T';
                }

                specification = new FormatSpecification(
                    flags,
                    width,
                    precision,
                    conversion,
                    timeFormat);
                return true;

                int ReadDynamicInteger(string overflowSuffix, int overflowValue)
                {
                    if (!TryGetArgument(out var argument))
                        return 0;

                    var value = ParseSigned(argument);
                    if (value is < int.MinValue or > int.MaxValue)
                    {
                        AddNumericDiagnostic(argument, overflowSuffix, warning: false);
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

            async ValueTask ApplySpecificationAsync(FormatSpecification specification, int conversionOffset)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                                AddFatalDiagnostic(new Diagnostic("`", variable, "': not a valid identifier"));
                                return;
                            }

                            await ReportPendingDiagnosticsAsync().ConfigureAwait(false);
                            if (!_stop && !await assignCount(new PrintfVariableAssignment(
                                    variable.ToUtf8String(), _output.Length - _cycleOutputStart)).ConfigureAwait(false))
                                _stop = true;
                            return;
                        }
                    case 'T' when specification.TimeFormat is { } configuredFormat:
                        {
                            long seconds;
                            if (TryGetArgument(out var argument))
                                seconds = ParseSigned(argument);
                            else
                                seconds = -1;

                            if (seconds is -1 or -2)
                                seconds = timestamp.ToUnixTimeSeconds();

                            try
                            {
                                timeMemory.Resize(0);
                                // Strftime has ASCII directives and byte-valued literals. A reversible
                                // byte/character projection lets both APIs share the existing renderer.
                                timeMemory.Grow(32L + 2L * configuredFormat.Bytes.Length);
                                cancellationToken.ThrowIfCancellationRequested();
                                var timeFormat = configuredFormat.IsEmpty ? "%X"
                                    : Encoding.Latin1.GetString(configuredFormat.Bytes.Span);
                                const int MaximumTimeBytes = 127; // GNU's 128-byte strftime buffer includes its terminator.
                                if (!GnuDateFormatting.TryFormatUnixTime(
                                        seconds,
                                        timestamp.Offset,
                                        timeFormat,
                                        MaximumTimeBytes,
                                        out var text, count =>
                                        {
                                            timeMemory.Grow(GnuDateFormatting.EstimatedBytesPerCharacter * count);
                                            cancellationToken.ThrowIfCancellationRequested();
                                        }))
                                {
                                    // A full strftime buffer produces an empty value before outer field formatting.
                                    text = string.Empty;
                                }

                                cancellationToken.ThrowIfCancellationRequested();
                                // Invariant directive results are ASCII; projected literals map back exactly.
                                ApplyText(specification, Encoding.Latin1.GetBytes(text), ignorePrecision: false);
                            }
                            catch (GnuDateFormatException error)
                            {
                                AddFatalDiagnostic(error.Message);
                            }

                            return;
                        }
                    case 'd':
                    case 'i':
                        {
                            var value = ParseSigned(GetValueArgument());
                            var negative = value < 0;
                            var magnitude = negative ? unchecked((ulong)(-(value + 1)) + 1) : (ulong)value;
                            var digits = ApplyIntegerPrecision(magnitude.ToString(Culture), magnitude == 0, specification.Precision);
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
                            var value = ParseUnsigned(GetValueArgument());
                            var digitText = specification.Conversion switch
                            {
                                'o' => ConvertUnsigned(value, 8, uppercase: false),
                                'x' => ConvertUnsigned(value, 16, uppercase: false),
                                'X' => ConvertUnsigned(value, 16, uppercase: true),
                                _ => value.ToString(Culture)
                            };
                            var digits = ApplyIntegerPrecision(digitText, value == 0, specification.Precision);

                            var prefix = string.Empty;
                            if (specification.Flags.HasFlag(FormatFlags.Alternate))
                            {
                                if (specification.Conversion == 'o' && (digits.Length == 0
                                    || digits.Zeros == 0 && !digitText.StartsWith('0')))
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

                            ulong ParseUnsigned(ShellValue unsignedArgument)
                            {
                                if (unsignedArgument.IsEmpty)
                                    return 0;

                                if (TryParseCharacterConstant(unsignedArgument, out var character))
                                    return (ulong)character;

                                var parsed = ParseInteger(
                                    unsignedArgument,
                                    out var negative,
                                    out var valid,
                                    out var overflow);
                                if (!valid)
                                    AddNumericDiagnostic(unsignedArgument, NumericErrorSuffix(unsignedArgument), warning: false);
                                else if (overflow)
                                    AddNumericDiagnostic(unsignedArgument, ": Numerical result out of range", warning: true);

                                if (overflow)
                                    return ulong.MaxValue;

                                return negative ? unchecked(0UL - parsed) : parsed;
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
                            var argument = GetValueArgument();
                            numericMemory.Resize(0);
                            var result = new FloatingPointFormatResult(
                                argument,
                                specification.Conversion,
                                specification.Precision,
                                specification.Flags.HasFlag(FormatFlags.Alternate), numericMemory.Grow, cancellationToken);
                            if (result.Invalid)
                            {
                                AddNumericDiagnostic(argument, NumericErrorSuffix(argument), warning: false);
                            }
                            if (result.RangeError)
                                AddNumericDiagnostic(argument, ": Numerical result out of range", warning: true);

                            AppendNumber(specification, result.Number);
                            return;
                        }
                    default:
                        AddFatalDiagnostic(new Diagnostic("`", ShellValue.FromBytes(format.Slice(conversionOffset, 1)),
                            "': invalid format character"));
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

                static NumberText ApplyIntegerPrecision(string digits, bool isZero, int? precision)
                {
                    if (precision == 0 && isZero)
                        return new NumberText(string.Empty, 0, string.Empty);
                    return new NumberText(string.Empty,
                        precision is { } minimum ? Math.Max(0, minimum - digits.Length) : 0, digits);
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
                    var paddingLength = Math.Max(0L, numberSpecification.Width - length);
                    if (length + paddingLength > _output.RemainingLength)
                    {
                        FailOutputLimit();
                        return;
                    }
                    var padding = (int)paddingLength;
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
                    AppendText(number.Text.BeforeZeros);
                    AppendPadding(number.Text.Zeros, (byte)'0');
                    AppendText(number.Text.AfterZeros);

                    if (left)
                        AppendPadding(padding, (byte)' ');
                }

            }

            async ValueTask ReportPendingDiagnosticsAsync()
            {
                if (reportDiagnostic == null || diagnosticOutputClosed || _pendingDiagnostics is not { Count: > 0 } pending)
                    return;

                try
                {
                    foreach (var diagnostic in pending)
                    {
                        if (!await reportDiagnostic(diagnostic).ConfigureAwait(false))
                        {
                            diagnosticOutputClosed = true;
                            _stop = true;
                            break;
                        }
                    }
                }
                finally { pending.Clear(); }
            }

            (ReadOnlyMemory<byte> Output, int ExitCode) BuildResult() =>
                (_output.DetachSnapshot(), _failed || _conversionError ? 1 : 0);
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
        ShellValue? TimeFormat);
}

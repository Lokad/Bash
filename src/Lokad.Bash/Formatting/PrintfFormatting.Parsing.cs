using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private sealed partial class Formatter
    {
        public async ValueTask<(ReadOnlyMemory<byte> Output, int ExitCode, bool DiscardedSuffix)> RunAsync(
            Func<PrintfVariableAssignment, ValueTask<bool>> assignCount)
        {
            var diagnosticOutputClosed = false;
            (MemoryCapacity Capacity, Action<int> ReserveCharacters)? timeScratch = null;
            MemoryCapacity? numericMemory = null;
            Action<long>? reserveNumericBytes = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (format.Length == 0)
                    return BuildResult();

                do
                {
                    var argumentIndexAtCycleStart = _argumentIndex;
                    _cycleOutputStart = _output.Length;
                    _cycleCountAdjustment = 0;
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
                ReleaseTemporaryOutput();
                try { await ReportPendingDiagnosticsAsync().ConfigureAwait(false); }
                catch (MemoryBudgetExceededException) { } // Report the refusal without retrying a queued record.
                if (reportDiagnostic != null && !diagnosticOutputClosed)
                    await reportDiagnostic(Diagnostic.MemoryRefusal).ConfigureAwait(false);
                return (ReadOnlyMemory<byte>.Empty, 1, false);
            }
            finally
            {
                timeScratch?.Capacity.Dispose();
                numericMemory?.Dispose();
                _pendingDiagnostics?.Clear();
                _diagnosticMemory?.Dispose();
                ReleaseTemporaryOutput();
                _output.Dispose();
            }

            void ReleaseTemporaryOutput()
            {
                _temporaryOutput?.Dispose();
                _temporaryOutput = null;
            }

            static void ReleaseConversionScratch(MemoryCapacity capacity)
            {
                if (capacity.Bytes != 0)
                {
                    capacity.Resize(0);
                    capacity.ReleaseUnusedCapacity();
                }
            }

            async ValueTask FormatCycleAsync()
            {
                var nextLiteralScan = 0;
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
                        if (index < nextLiteralScan)
                        {
                            AppendByte((byte)current);
                            index++;
                        }
                        else
                        {
                            var length = AppendLiteralRun(_output, format.Span[index..], formatText: true);
                            index += length;
                            // Keep the byte path for a short window after a syntax-dense run.
                            if (length < 4)
                                nextLiteralScan = index + Math.Min(32, format.Length - index);
                        }
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
                if (flags.HasFlag(FormatFlags.LeftJustify))
                    flags &= ~FormatFlags.ZeroPad;

                var width = 0;
                var literalWidthOverflow = false;
                long literalWidth = 0;
                if (index < format.Length && (char)format.Span[index] == '*')
                {
                    index++;
                    width = ReadDynamicField();
                    if (width < 0)
                    {
                        flags |= FormatFlags.LeftJustify;
                        if (width != int.MinValue)
                            width = -width;
                    }
                }
                else if (TryReadDecimal(format, ref index, out literalWidth))
                    width = (int)literalWidth;
                else
                    literalWidthOverflow = true;

                int? precision = null;
                int? dynamicPrecision = null;
                long literalPrecision = 0;
                var literalPrecisionOverflow = false;
                ReadOnlyMemory<byte> signedLiteralPrecision = default;
                if (index < format.Length && (char)format.Span[index] == '.')
                {
                    index++;
                    if (index < format.Length && (char)format.Span[index] == '*')
                    {
                        index++;
                        dynamicPrecision = ReadDynamicField();
                    }
                    else
                    {
                        var precisionStart = index;
                        var signed = index < format.Length && format.Span[index] == (byte)'-';
                        if (signed)
                            index++;

                        var fits = TryReadDecimal(format, ref index, out literalPrecision);
                        if (signed)
                        {
                            signedLiteralPrecision = format[precisionStart..index];
                            precision = 0;
                        }
                        else if (fits)
                            precision = (int)literalPrecision;
                        else
                            literalPrecisionOverflow = true;
                    }
                }

                while (index < format.Length && (char)format.Span[index] is 'h' or 'j' or 'l' or 'L' or 't' or 'z')
                    index++;

                if (index >= format.Length)
                {
                    var incomplete = ShellValue.FromBytes(format[specificationStart..]);
                    AddFatalDiagnostic(new Diagnostic("`", incomplete, "': missing format character"));
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

                // %Q consumes the '*' argument and uses literal precision to limit the operand.
                if (dynamicPrecision is >= 0 && conversion != 'Q')
                    precision = dynamicPrecision;

                var suppressOutput = (literalWidthOverflow || literalPrecisionOverflow) && UsesSystemConversion(conversion);
                if (literalWidthOverflow && !suppressOutput)
                    width = ClampWrappedField(literalWidth);
                if (literalPrecisionOverflow && !suppressOutput)
                {
                    // %Q wraps at 32 bits before quoting. The outer text formatter wraps at 64 bits.
                    var wrapped = conversion == 'Q' ? unchecked((int)literalPrecision) : literalPrecision;
                    precision = ClampWrappedField(wrapped);
                }

                specification = new FormatSpecification(
                    flags,
                    width,
                    precision,
                    conversion,
                    timeFormat,
                    suppressOutput,
                    signedLiteralPrecision);
                return true;

                int ReadDynamicField()
                {
                    if (!TryGetArgument(out var argument))
                        return 0;

                    var value = ParseSigned(argument);
                    if (value is >= int.MinValue and <= int.MaxValue)
                        return (int)value;

                    if (_argumentIndex == arguments.Count)
                        return unchecked((int)value);
                    // The Bash field parser reports the next operand after consuming the width or precision.
                    AddNumericDiagnostic(arguments[_argumentIndex], ": Numerical result out of range", warning: true);
                    return value > 0 ? int.MaxValue : int.MinValue;
                }

                static int ClampWrappedField(long field) => field is >= 0 and <= int.MaxValue ? (int)field : int.MaxValue;

                bool TryReadDecimal(ReadOnlyMemory<byte> source, ref int cursor, out long value)
                {
                    value = 0;
                    var overflow = false;
                    while (cursor < source.Length && (char)source.Span[cursor] is >= '0' and <= '9')
                    {
                        if ((cursor & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var digit = (char)source.Span[cursor] - '0';
                        overflow |= value > (int.MaxValue - digit) / 10;
                        value = unchecked(value * 10 + digit);
                        cursor++;
                    }
                    return !overflow;
                }
            }

            async ValueTask ApplySpecificationAsync(FormatSpecification specification, int conversionOffset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (specification.Conversion)
                {
                    case 'c':
                        {
                            var argument = GetValueArgument().Bytes.Span;
                            ApplyText(specification, argument.IsEmpty ? "\0"u8 : argument[..1], ignorePrecision: true);
                            return;
                        }
                    case 's':
                        ApplyText(specification, GetValueArgument().Bytes.Span, ignorePrecision: false);
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
                            if (_stop)
                                return;

                            ReleaseTemporaryOutput();
                            if (!await assignCount(new PrintfVariableAssignment(
                                    variable.ToUtf8String(), _output.Length - _cycleOutputStart + _cycleCountAdjustment)).ConfigureAwait(false))
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
                                ApplyTime(specification, configuredFormat, seconds);
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
                            ApplyInteger(specification, magnitude,
                                negative ? FormattedNumberSign.Negative : FormattedNumberSign.Positive);
                            return;
                        }
                    case 'o':
                    case 'u':
                    case 'x':
                    case 'X':
                        {
                            var value = ParseUnsigned(GetValueArgument());
                            ApplyInteger(specification, value, FormattedNumberSign.None);
                            return;

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
                        ApplyFloatingPoint(specification);
                        return;
                    default:
                        AddFatalDiagnostic(new Diagnostic("`", ShellValue.FromBytes(format.Slice(conversionOffset, 1)),
                            "': invalid format character"));
                        return;
                }

                void AppendEscapedString(FormatSpecification escapedSpecification)
                {
                    var argument = GetValueArgument().Bytes;
                    var bytes = GetTemporaryOutput(argument.Length, _output.RemainingLength, escapedSpecification.Precision);
                    for (var index = 0; index < argument.Length;)
                    {
                        if (argument.Span[index] != (byte)'\\')
                        {
                            if (index + 1 == argument.Length || argument.Span[index + 1] == (byte)'\\')
                                TryAppendByte(bytes, argument.Span[index++]);
                            else
                                index += AppendLiteralRun(bytes, argument.Span[index..], formatText: false);
                            if (_failed)
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
                            ApplyTextWithLength(escapedSpecification, bytes.RetainedBytes.Span, bytes.Length, ignorePrecision: false);
                            _stop = true;
                            return;
                        }
                    }

                    ApplyTextWithLength(escapedSpecification, bytes.RetainedBytes.Span, bytes.Length, ignorePrecision: false);
                }

                void AppendQuotedString(FormatSpecification quotedSpecification)
                {
                    var argument = GetValueArgument().Bytes;
                    if (!quotedSpecification.SignedLiteralPrecision.IsEmpty)
                    {
                        ApplyText(quotedSpecification, ReadOnlySpan<byte>.Empty, ignorePrecision: false);
                        return;
                    }

                    var rawByteLimit = quotedSpecification.Conversion == 'Q'
                        && quotedSpecification.Precision is { } rawPrecision
                        ? rawPrecision
                        : int.MaxValue;
                    var bytes = argument[..Math.Min(argument.Length, rawByteLimit)];
                    var style = quotedSpecification.Flags.HasFlag(FormatFlags.Alternate)
                        ? ShellQuoteStyle.SingleQuoted : ShellQuoteStyle.Backslash;
                    var adjusted = quotedSpecification.Conversion == 'Q'
                        ? quotedSpecification with { Precision = null }
                        : quotedSpecification;
                    if (adjusted.Precision == null)
                    {
                        // Quoting cannot shorten the operand. Left alignment only pads after the quote.
                        var width = GetFieldWidth(adjusted.Width, contentLength: 1);
                        if (width <= _output.RemainingLength
                            && (width <= bytes.Length || adjusted.Flags.HasFlag(FormatFlags.LeftJustify)))
                        {
                            var start = _output.Length;
                            if (!_output.TryAppendCompleteQuote(bytes.Span, style))
                            {
                                FailOutputLimit();
                                return;
                            }
                            var padding = (int)width - (_output.Length - start);
                            if (padding > 0)
                                AppendPadding(padding, (byte)' ');
                            return;
                        }
                    }

                    // Empty %q syntax can be truncated by precision before consuming the final output budget.
                    var quoted = GetTemporaryOutput(bytes.IsEmpty ? 2 : bytes.Length,
                        bytes.IsEmpty ? 2 : _output.RemainingLength, adjusted.Precision);
                    if (!ShellArgumentEscaping.TryAppendQuoted(quoted, bytes.Span, style))
                    {
                        FailOutputLimit();
                        return;
                    }
                    ApplyTextWithLength(adjusted, quoted.RetainedBytes.Span, quoted.Length, ignorePrecision: false);
                }

                ShellValue GetValueArgument() =>
                    TryGetArgument(out var argument) ? argument : ShellValue.Empty;

                void ApplyInteger(FormatSpecification specification, ulong value, FormattedNumberSign sign)
                {
                    // A 64-bit unsigned value needs at most 22 octal digits.
                    Span<byte> buffer = stackalloc byte[22];
                    var conversion = specification.Conversion;
                    scoped ReadOnlySpan<byte> digits;
                    if (conversion == 'o')
                        digits = FormatOctal(value, buffer);
                    else
                    {
                        var format = conversion is 'x' or 'X' ? new StandardFormat(conversion) : default;
                        if (!Utf8Formatter.TryFormat(value, buffer, out var length, format))
                            throw new InvalidOperationException("Integer digits exceed their maximum format length.");
                        digits = buffer[..length];
                    }

                    var precision = specification.Precision;
                    if (precision == 0 && value == 0)
                        digits = [];
                    var zeros = precision is { } minimum ? Math.Max(0, minimum - digits.Length) : 0;
                    var prefix = string.Empty;
                    if ((specification.Flags & FormatFlags.Alternate) != 0)
                    {
                        prefix = conversion switch
                        {
                            'o' when digits.IsEmpty || zeros == 0 && value != 0 => "0",
                            'x' when value != 0 => "0x",
                            'X' when value != 0 => "0X",
                            _ => string.Empty
                        };
                    }

                    var padding = precision == null ? FormattedNumberPadding.ZeroAllowed : FormattedNumberPadding.SpaceOnly;
                    AppendNumber(specification,
                        new FormattedNumber(new NumberText(string.Empty, zeros, string.Empty), prefix, sign, padding), digits);

                    static ReadOnlySpan<byte> FormatOctal(ulong value, Span<byte> buffer)
                    {
                        var index = buffer.Length;
                        do
                        {
                            buffer[--index] = (byte)('0' + (value & 7));
                            value >>= 3;
                        } while (value != 0);
                        return buffer[index..];
                    }
                }

                void ApplyText(FormatSpecification textSpecification, ReadOnlySpan<byte> bytes, bool ignorePrecision) =>
                    ApplyTextWithLength(textSpecification, bytes, bytes.Length, ignorePrecision);

                void ApplyFloatingPoint(FormatSpecification numberSpecification)
                {
                    var argument = GetValueArgument();
                    var capacity = numericMemory ??= new MemoryCapacity(memoryBudget, 0);
                    try
                    {
                        var result = new FloatingPointFormatResult(
                            argument, numberSpecification.Conversion, numberSpecification.Precision,
                            numberSpecification.Flags.HasFlag(FormatFlags.Alternate), reserveNumericBytes ??= capacity.Grow,
                            cancellationToken);
                        if (result.Invalid)
                            AddNumericDiagnostic(argument, NumericErrorSuffix(argument), warning: false);
                        else if (result.RangeError)
                            AddNumericDiagnostic(argument, ": Numerical result out of range", warning: true);

                        AppendNumber(numberSpecification, result.Number, []);
                    }
                    finally { ReleaseConversionScratch(capacity); }
                }

                void ApplyTime(FormatSpecification timeSpecification, ShellValue configuredFormat, long seconds)
                {
                    var (capacity, reserveCharacters) = timeScratch ??= CreateTimeScratch();
                    try
                    {
                        // Strftime has ASCII directives and byte-valued literals. A reversible
                        // byte/character projection lets both APIs share the existing renderer.
                        capacity.Grow(32L + 2L * configuredFormat.Bytes.Length);
                        cancellationToken.ThrowIfCancellationRequested();
                        var timeFormat = configuredFormat.IsEmpty ? "%X"
                            : Encoding.Latin1.GetString(configuredFormat.Bytes.Span);
                        const int MaximumTimeBytes = 127; // GNU's 128-byte strftime buffer includes its terminator.
                        if (!GnuDateFormatting.TryFormatUnixTime(seconds, timestamp.Offset, timeFormat, MaximumTimeBytes,
                                out var text, reserveCharacters))
                        {
                            // A full strftime buffer produces an empty value before outer field formatting.
                            text = string.Empty;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        // Invariant directive results are ASCII; projected literals map back exactly.
                        ApplyText(timeSpecification, Encoding.Latin1.GetBytes(text), ignorePrecision: false);
                    }
                    finally { ReleaseConversionScratch(capacity); }

                    (MemoryCapacity, Action<int>) CreateTimeScratch()
                    {
                        var scratch = new MemoryCapacity(memoryBudget, 0);
                        return (scratch, count =>
                        {
                            scratch.Grow(GnuDateFormatting.EstimatedBytesPerCharacter * count);
                            cancellationToken.ThrowIfCancellationRequested();
                        });
                    }
                }

                void ApplyTextWithLength(
                    FormatSpecification textSpecification,
                    ReadOnlySpan<byte> prefix,
                    int length,
                    bool ignorePrecision)
                {
                    if (ApplyFieldOverride(textSpecification))
                        return;

                    if (!ignorePrecision
                        && textSpecification.Precision is { } precision
                        && precision < length)
                    {
                        length = precision;
                    }

                    var paddedLength = Math.Max(length, GetFieldWidth(textSpecification.Width, length));
                    if (paddedLength > _output.RemainingLength)
                    {
                        FailOutputLimit();
                        return;
                    }
                    var padding = (int)(paddedLength - length);
                    var left = textSpecification.Flags.HasFlag(FormatFlags.LeftJustify);
                    if (!left)
                        AppendPadding(padding, (byte)' ');
                    if (!_failed && !_output.TryAppendConversion(prefix[..Math.Min(prefix.Length, length)], length))
                        FailOutputLimit();
                    if (left)
                        AppendPadding(padding, (byte)' ');
                }

                void AppendNumber(
                    FormatSpecification numberSpecification,
                    FormattedNumber number,
                    scoped ReadOnlySpan<byte> trailingBytes)
                {
                    if (ApplyFieldOverride(numberSpecification))
                        return;

                    var flags = numberSpecification.Flags;
                    var sign = number.Sign switch
                    {
                        FormattedNumberSign.Negative => "-",
                        FormattedNumberSign.Positive
                            when (flags & FormatFlags.PlusSign) != 0 => "+",
                        FormattedNumberSign.Positive
                            when (flags & FormatFlags.SpaceSign) != 0 => " ",
                        _ => string.Empty
                    };
                    var length = sign.Length + number.Prefix.Length + number.Text.Length + trailingBytes.Length;
                    var paddingLength = Math.Max(0L, GetFieldWidth(numberSpecification.Width, length) - length);
                    if (length + paddingLength > _output.RemainingLength)
                    {
                        FailOutputLimit();
                        return;
                    }
                    var padding = (int)paddingLength;
                    var left = (flags & FormatFlags.LeftJustify) != 0;
                    var zero = number.Padding == FormattedNumberPadding.ZeroAllowed
                        && (flags & FormatFlags.ZeroPad) != 0
                        && !left;

                    if (!left && !zero)
                        AppendPadding(padding, (byte)' ');

                    AppendText(sign);
                    AppendText(number.Prefix);
                    if (zero)
                        AppendPadding(padding, (byte)'0');
                    AppendText(number.Text.BeforeZeros);
                    AppendPadding(number.Text.Zeros, (byte)'0');
                    if (trailingBytes.IsEmpty)
                        AppendText(number.Text.AfterZeros);
                    else
                        TryAppendBytes(_output, trailingBytes);

                    if (left)
                        AppendPadding(padding, (byte)' ');
                }

                static long GetFieldWidth(int width, long contentLength)
                {
                    // Bash's text output skips padding at the minimum signed width when the value is empty.
                    if (width == int.MinValue)
                        return contentLength == 0 ? 0 : -(long)width;
                    return width;
                }

                bool ApplyFieldOverride(FormatSpecification field)
                {
                    if (field.SuppressOutput)
                    {
                        // The Linux conversion contributes -1 to %n and can rewind the assignment buffer.
                        if (assignOutput && _output.Length > 0)
                            _output.RewindByte();
                        else
                            _cycleCountAdjustment--;
                        return true;
                    }

                    if (field.SignedLiteralPrecision.IsEmpty || !UsesSystemConversion(field.Conversion))
                        return false;

                    // Bash passes the minus to libc as an unknown conversion after a zero precision.
                    // Linux canonicalizes the prefix, then writes the rest of the format as text.
                    AppendByte((byte)'%');
                    if (field.Flags.HasFlag(FormatFlags.Alternate)) AppendByte((byte)'#');
                    if (field.Flags.HasFlag(FormatFlags.Grouping)) AppendByte((byte)'\'');
                    if (field.Flags.HasFlag(FormatFlags.PlusSign)) AppendByte((byte)'+');
                    else if (field.Flags.HasFlag(FormatFlags.SpaceSign)) AppendByte((byte)' ');
                    if (field.Flags.HasFlag(FormatFlags.LeftJustify)) AppendByte((byte)'-');
                    if (field.Flags.HasFlag(FormatFlags.ZeroPad)) AppendByte((byte)'0');
                    if (field.Width != 0)
                        AppendText(field.Width == int.MinValue
                            ? unchecked((ulong)(long)field.Width).ToString(Culture)
                            : field.Width.ToString(Culture));
                    TryAppendBytes(_output, ".0"u8);
                    TryAppendBytes(_output, field.SignedLiteralPrecision.Span);
                    if (field.Conversion is 'd' or 'i' or 'o' or 'u' or 'x' or 'X')
                        AppendByte((byte)'l');
                    else if (field.Conversion is not ('c' or 's'))
                        AppendByte((byte)'L');
                    AppendByte((byte)field.Conversion);
                    return true;
                }

            }

            async ValueTask ReportPendingDiagnosticsAsync()
            {
                if (reportDiagnostic == null || diagnosticOutputClosed || _pendingDiagnostics is not { Count: > 0 } pending)
                    return;

                ReleaseTemporaryOutput();
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

            (ReadOnlyMemory<byte> Output, int ExitCode, bool DiscardedSuffix) BuildResult() =>
                (_output.DetachSnapshot(), _failed || _conversionError ? 1 : 0, _output.HasDiscardedBytes);
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

    private static bool UsesSystemConversion(char conversion) => conversion is
        'c' or 's' or 'd' or 'i' or 'o' or 'u' or 'x' or 'X'
        or 'a' or 'A' or 'e' or 'E' or 'f' or 'F' or 'g' or 'G';

    private readonly record struct FormatSpecification(
        FormatFlags Flags,
        int Width,
        int? Precision,
        char Conversion,
        ShellValue? TimeFormat,
        bool SuppressOutput,
        ReadOnlyMemory<byte> SignedLiteralPrecision);
}

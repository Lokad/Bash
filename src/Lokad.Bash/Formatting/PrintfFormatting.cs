using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Formatting;

/// <summary>
/// Formats printf output and reports conversion events without owning shell state.
/// </summary>
public static partial class PrintfFormatting
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    /// <summary>
    /// Formats arguments without applying the variable assignments produced by <c>%n</c>.
    /// </summary>
    /// <remarks>
    /// This output-only entry point uses the Unix epoch for the special <c>-1</c> and <c>-2</c>
    /// timestamp arguments. Evaluator callers use <see cref="EvaluateAsync"/> with their configured timestamp.
    /// Timestamp operands outside the range supported by <see cref="DateTimeOffset"/> also fall back to
    /// the Unix epoch instead of using Bash's wider platform-dependent <c>time_t</c> range.
    /// </remarks>
    public static ReadOnlyMemory<byte> Format(ShellValue format, IReadOnlyList<ShellValue> arguments, IMemoryBudget memoryBudget) =>
        Evaluate(format, arguments, DateTimeOffset.UnixEpoch, memoryBudget).Output;

    /// <summary>
    /// Formats arguments and returns output, deferred variable assignments, and conversion diagnostics.
    /// </summary>
    internal static PrintfResult Evaluate(
        ShellValue format,
        IReadOnlyList<ShellValue> arguments,
        DateTimeOffset timestamp, IMemoryBudget memoryBudget) => EvaluateSynchronously(format, arguments, timestamp, allowAssignments: true, memoryBudget);

    /// <summary>Runs count writes and diagnostics in conversion order; false stops later formatting.</summary>
    internal static ValueTask<PrintfResult> EvaluateAsync(
        ShellValue format, IReadOnlyList<ShellValue> arguments, DateTimeOffset timestamp, IMemoryBudget memoryBudget,
        Func<PrintfVariableAssignment, ValueTask<bool>> assignCount,
        Func<ShellValue, ValueTask<bool>> reportDiagnostic) =>
        new Formatter(format, arguments, timestamp, allowAssignments: true, memoryBudget).RunAsync(assignCount, reportDiagnostic);

    /// <summary>Formats command output, rejecting the shell-only <c>%n</c> assignment.</summary>
    /// <remarks>Like <see cref="Format"/>, uses the Unix epoch for implicit timestamps.</remarks>
    public static PrintfResult EvaluateCommand(ShellValue format, IReadOnlyList<ShellValue> arguments, IMemoryBudget memoryBudget) =>
        EvaluateSynchronously(format, arguments, DateTimeOffset.UnixEpoch, allowAssignments: false, memoryBudget);

    private static PrintfResult EvaluateSynchronously(
        ShellValue format, IReadOnlyList<ShellValue> arguments, DateTimeOffset timestamp, bool allowAssignments, IMemoryBudget memoryBudget)
    {
        var assignments = new List<PrintfVariableAssignment>();
        var pending = new Formatter(format, arguments, timestamp, allowAssignments, memoryBudget)
            .RunAsync(CaptureCount, static _ => ValueTask.FromResult(true));
        // These callbacks never suspend; do not block the synchronous formatting API.
        if (!pending.IsCompleted)
            throw new InvalidOperationException("Synchronous printf callbacks must complete synchronously.");
        return pending.GetAwaiter().GetResult() with { Assignments = assignments };

        ValueTask<bool> CaptureCount(PrintfVariableAssignment assignment)
        {
            assignments.Add(assignment);
            return ValueTask.FromResult(true);
        }
    }

    private sealed partial class Formatter(
        ShellValue formatValue,
        IReadOnlyList<ShellValue> arguments,
        DateTimeOffset timestamp,
        bool allowAssignments, IMemoryBudget memoryBudget)
    {
        private readonly ReadOnlyMemory<byte> format = formatValue.Bytes;

        private const int MaximumOutputBytes = ByteMemoryBuilder.MaxMemoryBuilderBytes - 1;
        private const int MaximumFieldSize = 1 << 20;

        private readonly BoundedByteMemoryBuilder _output = new(
            0, MaximumOutputBytes, memoryBudget);
        private readonly List<ShellValue> _diagnostics = [];
        private BoundedByteMemoryBuilder? _temporaryOutput;
        private int _argumentIndex;
        private int _cycleOutputStart;
        private bool _conversionError;
        private bool _failed;
        private bool _stop;

        private bool TryGetArgument(out ShellValue argument)
        {
            if (_argumentIndex >= arguments.Count)
            {
                argument = ShellValue.Empty;
                return false;
            }

            argument = arguments[_argumentIndex++];
            return true;
        }

        private void AddWarning(string message) => _diagnostics.Add(ShellValue.FromText(message));

        private void AddConversionDiagnostic(string message)
        {
            _diagnostics.Add(ShellValue.FromText(message));
            _conversionError = true;
        }

        private void AddFatalDiagnostic(string message) => AddFatalDiagnostic(ShellValue.FromText(message));

        private void AddFatalDiagnostic(ShellValue message)
        {
            _diagnostics.Add(message);
            _failed = true;
        }

        private void AppendText(string text) => TryAppendUtf8Prefix(_output, text.AsSpan(), int.MaxValue);

        private void AppendByte(byte value) => TryAppendByte(_output, value);

        private void AppendPadding(int count, byte value)
        {
            if (_failed)
                return;

            if (!_output.TryAppend(count, value))
                FailOutputLimit();
        }

        private bool TryAppendByte(BoundedByteMemoryBuilder output, byte value)
        {
            if (_failed)
                return false;

            if (output.TryAppend(value))
                return true;

            FailOutputLimit();
            return false;
        }

        private bool TryAppendBytes(BoundedByteMemoryBuilder output, ReadOnlySpan<byte> values)
        {
            if (_failed)
                return false;

            if (output.TryAppend(values))
                return true;

            FailOutputLimit();
            return false;
        }

        private bool TryAppendUtf8Prefix(
            BoundedByteMemoryBuilder output,
            ReadOnlySpan<char> text,
            int maximumBytes)
        {
            const int CharacterChunkLength = 256;
            Span<byte> encoded = stackalloc byte[1024];
            var remainingBytes = maximumBytes;
            for (var index = 0; index < text.Length && remainingBytes > 0;)
            {
                var characterLength = Math.Min(CharacterChunkLength, text.Length - index);
                if (index + characterLength < text.Length
                    && char.IsHighSurrogate(text[index + characterLength - 1])
                    && char.IsLowSurrogate(text[index + characterLength]))
                {
                    characterLength--;
                }

                var encodedLength = Utf8.GetBytes(text.Slice(index, characterLength), encoded);
                var appendedLength = Math.Min(encodedLength, remainingBytes);
                if (!TryAppendBytes(output, encoded[..appendedLength]))
                    return false;

                remainingBytes -= appendedLength;
                index += characterLength;
                if (appendedLength < encodedLength)
                    break;
            }

            return true;
        }

        private BoundedByteMemoryBuilder GetTemporaryOutput(int requestedCapacity, int maximumLength)
        {
            if (_temporaryOutput == null)
                _temporaryOutput = new BoundedByteMemoryBuilder(
                    Math.Min(Math.Max(requestedCapacity, 16), 4096), maximumLength, memoryBudget);
            else
                _temporaryOutput.Reset(maximumLength);
            return _temporaryOutput;
        }

        private void FailOutputLimit()
        {
            AddFatalDiagnostic($"output exceeds {MaximumOutputBytes} bytes");
            _stop = true;
        }
    }
}

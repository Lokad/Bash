using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
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
    /// Explicit timestamp operands use the pinned Linux calendar range. An operand
    /// whose calendar year cannot be represented in that profile falls back to the Unix epoch.
    /// </remarks>
    public static ReadOnlyMemory<byte> Format(ShellValue format, IReadOnlyList<ShellValue> arguments, IMemoryBudget memoryBudget) =>
        RunSynchronously(format, arguments, DateTimeOffset.UnixEpoch, allowAssignments: true, memoryBudget,
            static _ => ValueTask.FromResult(true), reportDiagnostic: null).Output;

    /// <summary>
    /// Formats arguments and returns output, deferred variable assignments, and conversion diagnostics.
    /// </summary>
    internal static PrintfResult Evaluate(
        ShellValue format,
        IReadOnlyList<ShellValue> arguments,
        DateTimeOffset timestamp, IMemoryBudget memoryBudget) => EvaluateSynchronously(format, arguments, timestamp, allowAssignments: true, memoryBudget);

    /// <summary>Runs count writes and diagnostics in conversion order; false stops later formatting.</summary>
    /// <remarks>A null diagnostic callback discards records while retaining their effect on status.</remarks>
    internal static ValueTask<(ReadOnlyMemory<byte> Output, int ExitCode)> EvaluateAsync(
        ShellValue format, IReadOnlyList<ShellValue> arguments, DateTimeOffset timestamp, bool assignOutput, IMemoryBudget memoryBudget,
        Func<PrintfVariableAssignment, ValueTask<bool>> assignCount,
        Func<Diagnostic, ValueTask<bool>>? reportDiagnostic, CancellationToken cancellationToken) =>
        new Formatter(format, arguments, timestamp, allowAssignments: true, assignOutput, memoryBudget, reportDiagnostic, cancellationToken).RunAsync(assignCount);

    /// <summary>Formats command output, rejecting the shell-only <c>%n</c> assignment.</summary>
    /// <remarks>Like <see cref="Format"/>, uses the Unix epoch for implicit timestamps.</remarks>
    public static PrintfResult EvaluateCommand(ShellValue format, IReadOnlyList<ShellValue> arguments, IMemoryBudget memoryBudget) =>
        EvaluateSynchronously(format, arguments, DateTimeOffset.UnixEpoch, allowAssignments: false, memoryBudget);

    private static PrintfResult EvaluateSynchronously(
        ShellValue format, IReadOnlyList<ShellValue> arguments, DateTimeOffset timestamp, bool allowAssignments, IMemoryBudget memoryBudget)
    {
        var assignments = new List<PrintfVariableAssignment>();
        var diagnostics = new List<ShellValue>();
        using var resultMemory = new MemoryCapacity(memoryBudget, 0);
        var result = RunSynchronously(format, arguments, timestamp, allowAssignments, memoryBudget, CaptureCount, CaptureDiagnostic);
        resultMemory.DetachReservation();
        return new PrintfResult(result.Output, assignments, diagnostics, result.ExitCode);

        ValueTask<bool> CaptureCount(PrintfVariableAssignment assignment)
        {
            resultMemory.PrepareListCapacity(assignments, 1);
            assignments.Add(assignment);
            return ValueTask.FromResult(true);
        }

        ValueTask<bool> CaptureDiagnostic(Diagnostic diagnostic)
        {
            // Every admitted diagnostic leaves room to report a later refusal.
            if (diagnostic == Diagnostic.MemoryRefusal)
                diagnostics.Add(Diagnostic.MemoryRefusalValue);
            else
            {
                resultMemory.PrepareListCapacity(diagnostics, 2);
                diagnostics.Add(diagnostic.ToValue(memoryBudget));
            }
            return ValueTask.FromResult(true);
        }
    }

    private static (ReadOnlyMemory<byte> Output, int ExitCode) RunSynchronously(
        ShellValue format, IReadOnlyList<ShellValue> arguments, DateTimeOffset timestamp, bool allowAssignments, IMemoryBudget memoryBudget,
        Func<PrintfVariableAssignment, ValueTask<bool>> assignCount, Func<Diagnostic, ValueTask<bool>>? reportDiagnostic)
    {
        var pending = new Formatter(format, arguments, timestamp, allowAssignments, assignOutput: false, memoryBudget, reportDiagnostic, CancellationToken.None)
            .RunAsync(assignCount);
        // These callbacks never suspend; do not block the synchronous formatting API.
        if (!pending.IsCompleted)
            throw new InvalidOperationException("Synchronous printf callbacks must complete synchronously.");
        return pending.GetAwaiter().GetResult();
    }

    /// <summary>A diagnostic borrows its operand through the awaited reporting callback.</summary>
    internal readonly record struct Diagnostic(string Prefix, ShellValue Operand, string Suffix)
    {
        internal static readonly Diagnostic MemoryRefusal = FromText("shared memory budget exhausted");
        internal static readonly ShellValue MemoryRefusalValue = ShellValue.FromText(MemoryRefusal.Suffix);

        internal static Diagnostic FromText(string message) => new(string.Empty, ShellValue.Empty, message);

        internal ShellValue ToValue(IMemoryBudget memoryBudget)
        {
            var length = (long)Utf8Text.StrictUtf8.GetByteCount(Prefix) + Operand.Bytes.Length
                + Utf8Text.StrictUtf8.GetByteCount(Suffix);
            if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            var bytes = ScopedMemory.Allocate((int)length, memoryBudget);
            var start = Utf8Text.StrictUtf8.GetBytes(Prefix, bytes);
            Operand.Bytes.Span.CopyTo(bytes.AsSpan(start));
            Utf8Text.StrictUtf8.GetBytes(Suffix, bytes.AsSpan(start + Operand.Bytes.Length));
            return ShellValue.FromBytes(bytes);
        }
    }

    private sealed partial class Formatter(
        ShellValue formatValue,
        IReadOnlyList<ShellValue> arguments,
        DateTimeOffset timestamp,
        bool allowAssignments, bool assignOutput, IMemoryBudget memoryBudget, Func<Diagnostic, ValueTask<bool>>? reportDiagnostic,
        CancellationToken cancellationToken)
    {
        private readonly ReadOnlyMemory<byte> format = formatValue.Bytes;

        private const int MaximumOutputBytes = ByteMemoryBuilder.MaxMemoryBuilderBytes - 1;

        private readonly BoundedByteMemoryBuilder _output = new(
            0, MaximumOutputBytes, memoryBudget);
        private List<Diagnostic>? _pendingDiagnostics;
        private MemoryCapacity? _diagnosticMemory;
        private BoundedByteMemoryBuilder? _temporaryOutput;
        private int _argumentIndex;
        private int _cycleOutputStart;
        private int _cycleCountAdjustment;
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

        private void AddWarning(string message) => AddDiagnostic(Diagnostic.FromText(message));

        private void AddFatalDiagnostic(string message) => AddFatalDiagnostic(Diagnostic.FromText(message));

        private void AddFatalDiagnostic(Diagnostic diagnostic)
        {
            AddDiagnostic(diagnostic);
            _failed = true;
        }

        private void AddDiagnostic(Diagnostic diagnostic)
        {
            if (reportDiagnostic == null)
                return;
            var pending = _pendingDiagnostics ??= [];
            (_diagnosticMemory ??= new MemoryCapacity(memoryBudget, 0)).PrepareListCapacity(pending, 1);
            pending.Add(diagnostic);
        }

        private void AppendText(string text) => TryAppendUtf8Prefix(_output, text.AsSpan(), int.MaxValue);

        private void AppendByte(byte value) => TryAppendByte(_output, value);

        private void AppendPadding(int count, byte value)
        {
            if (_failed)
                return;

            if (count > _output.RemainingLength)
            {
                FailOutputLimit();
                return;
            }
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(count, 64 * 1024);
                if (!_output.TryAppend(length, value))
                {
                    FailOutputLimit();
                    return;
                }
                count -= length;
            }
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

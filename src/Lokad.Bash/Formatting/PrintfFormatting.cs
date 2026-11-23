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
    /// Supply a caller-owned <see cref="IMemoryScope"/> as <paramref name="memoryBudget"/>.
    /// Keep it alive until every consumer of the returned buffer finishes.
    ///
    /// This output-only entry point uses the Unix epoch for the special <c>-1</c> and <c>-2</c>
    /// timestamp arguments. Shell invocations use the timestamp supplied by the evaluator.
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
    /// <remarks>
    /// A null diagnostic callback discards records while retaining their effect on status.
    /// Assignment output contains the retained prefix and reports whether it discarded a suffix.
    /// </remarks>
    internal static ValueTask<(ReadOnlyMemory<byte> Output, int ExitCode, bool DiscardedSuffix)> EvaluateAsync(
        ShellValue format, IReadOnlyList<ShellValue> arguments, DateTimeOffset timestamp, bool assignOutput, IMemoryBudget memoryBudget,
        Func<PrintfVariableAssignment, ValueTask<bool>> assignCount,
        Func<Diagnostic, ValueTask<bool>>? reportDiagnostic, CancellationToken cancellationToken) =>
        EvaluateAsync(format, arguments, firstArgument: 0, timestamp, assignOutput, memoryBudget, assignCount,
            reportDiagnostic, cancellationToken);

    /// <summary>Borrows the expanded operand suffix through conversion and its callbacks.</summary>
    internal static ValueTask<(ReadOnlyMemory<byte> Output, int ExitCode, bool DiscardedSuffix)> EvaluateAsync(
        ShellValue format, IReadOnlyList<ShellValue> arguments, int firstArgument, DateTimeOffset timestamp, bool assignOutput,
        IMemoryBudget memoryBudget, Func<PrintfVariableAssignment, ValueTask<bool>> assignCount,
        Func<Diagnostic, ValueTask<bool>>? reportDiagnostic, CancellationToken cancellationToken) =>
        new Formatter(format, arguments, firstArgument, timestamp, allowAssignments: true, assignOutput, memoryBudget,
            reportDiagnostic, cancellationToken).RunAsync(assignCount);

    /// <summary>Formats command output, rejecting the shell-only <c>%n</c> assignment.</summary>
    /// <remarks>
    /// Supply a caller-owned <see cref="IMemoryScope"/> as <paramref name="memoryBudget"/>.
    /// Keep it alive while consuming the output and diagnostics. Like <see cref="Format"/>,
    /// this method uses the Unix epoch for implicit timestamps.
    /// </remarks>
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

    private static (ReadOnlyMemory<byte> Output, int ExitCode, bool DiscardedSuffix) RunSynchronously(
        ShellValue format, IReadOnlyList<ShellValue> arguments, DateTimeOffset timestamp, bool allowAssignments, IMemoryBudget memoryBudget,
        Func<PrintfVariableAssignment, ValueTask<bool>> assignCount, Func<Diagnostic, ValueTask<bool>>? reportDiagnostic)
    {
        var pending = new Formatter(format, arguments, firstArgument: 0, timestamp, allowAssignments, assignOutput: false,
            memoryBudget, reportDiagnostic, CancellationToken.None).RunAsync(assignCount);
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

    private enum OutputRetention { All, BeforeNul, None }

    /// <summary>Bounds the logical output and retains the bytes selected for publication.</summary>
    private sealed class OutputBuffer(int capacity, int maximumLength, OutputRetention retention, IMemoryBudget budget)
        : IByteMemoryWriter, IDisposable
    {
        private readonly ByteMemoryBuilder _bytes = new(Math.Min(capacity, maximumLength), budget);
        private int _maximumLength = maximumLength;
        private OutputRetention _retention = retention;
        private int _discarded;

        public int Length => _bytes.Length + _discarded;
        public int RemainingLength => _maximumLength - Length;
        public bool HasDiscardedBytes => _discarded != 0;
        public bool IsDiscarding => _retention == OutputRetention.None || HasDiscardedBytes;
        public ReadOnlyMemory<byte> RetainedBytes => _bytes.ToMemory();

        public bool TryAppend(byte value)
        {
            if (RemainingLength == 0)
                return false;
            if (!IsDiscarding && (_retention != OutputRetention.BeforeNul || value != 0))
                return _bytes.TryAppend(value);
            _discarded++;
            return true;
        }

        public bool TryAppend(int count, byte value)
        {
            if (count < 0 || count > RemainingLength)
                return false;
            if (!IsDiscarding && (_retention != OutputRetention.BeforeNul || value != 0))
                return _bytes.TryAppend(count, value);
            _discarded += count;
            return true;
        }

        public bool TryAppend(ReadOnlySpan<byte> bytes) => TryAppendConversion(bytes, bytes.Length);

        // A conversion can provide its retained prefix and the length including its discarded suffix.
        public bool TryAppendConversion(ReadOnlySpan<byte> prefix, int length)
        {
            if (length > RemainingLength)
                return false;
            if (!IsDiscarding)
            {
                var nul = _retention == OutputRetention.BeforeNul ? prefix.IndexOf((byte)0) : -1;
                if (nul >= 0)
                    prefix = prefix[..nul];
                if (!_bytes.TryAppend(prefix))
                    return false;
                length -= prefix.Length;
            }
            _discarded += length;
            return true;
        }

        public void RewindByte()
        {
            if (HasDiscardedBytes)
                _discarded--;
            else
                _bytes.Truncate(_bytes.Length - 1);
        }

        public void Reset(int lengthLimit, OutputRetention mode)
        {
            _bytes.Truncate(0);
            _maximumLength = lengthLimit;
            _retention = mode;
            _discarded = 0;
        }

        public ReadOnlyMemory<byte> DetachSnapshot() => _bytes.DetachSnapshot();
        public void Dispose() => _bytes.Dispose();
    }

    private sealed partial class Formatter(
        ShellValue formatValue,
        IReadOnlyList<ShellValue> arguments,
        int firstArgument,
        DateTimeOffset timestamp,
        bool allowAssignments, bool assignOutput, IMemoryBudget memoryBudget, Func<Diagnostic, ValueTask<bool>>? reportDiagnostic,
        CancellationToken cancellationToken)
    {
        private readonly ReadOnlyMemory<byte> format = formatValue.Bytes;

        private const int MaximumOutputBytes = ByteMemoryBuilder.MaxMemoryBuilderBytes - 1;

        private readonly OutputBuffer _output = new(
            0, MaximumOutputBytes, assignOutput ? OutputRetention.BeforeNul : OutputRetention.All, memoryBudget);
        private List<Diagnostic>? _pendingDiagnostics;
        private MemoryCapacity? _diagnosticMemory;
        private OutputBuffer? _temporaryOutput;
        private int _argumentIndex = firstArgument;
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
            var quantum = _output.IsDiscarding ? count : 64 * 1024;
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(count, quantum);
                if (!_output.TryAppend(length, value))
                {
                    FailOutputLimit();
                    return;
                }
                count -= length;
            }
        }

        private bool TryAppendByte(OutputBuffer output, byte value)
        {
            if (_failed)
                return false;

            if (output.TryAppend(value))
                return true;

            FailOutputLimit();
            return false;
        }

        private bool TryAppendBytes(OutputBuffer output, ReadOnlySpan<byte> values)
        {
            if (_failed)
                return false;

            if (output.TryAppend(values))
                return true;

            FailOutputLimit();
            return false;
        }

        private void AppendText(string text)
        {
            const int CharacterChunkLength = 256;
            Span<byte> encoded = stackalloc byte[1024];
            for (var index = 0; index < text.Length;)
            {
                var characterLength = Math.Min(CharacterChunkLength, text.Length - index);
                if (index + characterLength < text.Length
                    && char.IsHighSurrogate(text[index + characterLength - 1])
                    && char.IsLowSurrogate(text[index + characterLength]))
                {
                    characterLength--;
                }

                var encodedLength = Utf8.GetBytes(text.AsSpan(index, characterLength), encoded);
                if (!TryAppendBytes(_output, encoded[..encodedLength]))
                    return;
                index += characterLength;
            }
        }

        private OutputBuffer GetTemporaryOutput(int requestedCapacity, int maximumLength)
        {
            var retention = _output.IsDiscarding ? OutputRetention.None
                : assignOutput ? OutputRetention.BeforeNul : OutputRetention.All;
            if (_temporaryOutput == null)
                _temporaryOutput = new OutputBuffer(
                    retention == OutputRetention.None ? 0 : Math.Min(Math.Max(requestedCapacity, 16), 4096),
                    maximumLength, retention, memoryBudget);
            else
                _temporaryOutput.Reset(maximumLength, retention);
            return _temporaryOutput;
        }

        private void FailOutputLimit()
        {
            AddFatalDiagnostic($"output exceeds {MaximumOutputBytes} bytes");
            _stop = true;
        }
    }
}

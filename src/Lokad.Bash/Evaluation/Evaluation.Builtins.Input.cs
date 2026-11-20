using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    // Keep small read loops cheap without retaining a large line through later commands.
    private const int ReadScratchRetentionBytes = 64 * 1024;
    private ReadWorkspace? _readWorkspace;

    private (LogicalDescriptor? Number, FileDescriptor Handle) ResolveInputDescriptorOperand(ReadOnlySpan<byte> operand)
    {
        if (!Integers.TryParseBuiltinNumber(operand, out var number) || number is < 0 or > int.MaxValue)
            return (null, FileDescriptor.Closed);

        // Input borrows the reached logical mapping and leaves handle ownership with it.
        var logical = new LogicalDescriptor((int)number);
        return (logical, _state.CurrentRedirectionTable?.Resolve(logical) ?? CaptureDescriptorBindings().Resolve(logical));
    }

    private Task ReportInvalidInputDescriptorAsync(ShellValue command, ShellValue operand, LogicalDescriptor? number) =>
        number is { } logical
            ? ReportExpansionDiagnosticAsync(command, ShellValue.FromText(logical + ": invalid file descriptor: Bad file descriptor"))
            : ReportExpansionDiagnosticAsync([command], [operand, ShellValue.FromText(": invalid file descriptor specification")]);

    // Each evaluator owns its workspace: pipeline stages and command substitutions may overlap.
    private sealed class ReadWorkspace(IMemoryBudget budget) : IDisposable
    {
        public ByteMemoryBuilder Content { get; } = new(0, budget);
        public ByteMemoryBuilder? PhysicalLine { get; set; }

        public void Dispose()
        {
            Content.Dispose();
            PhysicalLine?.Dispose();
        }
    }

    private void ReleaseReadWorkspace()
    {
        var memory = _readWorkspace;
        _readWorkspace = null;
        memory?.Dispose();
    }

    /// <summary>Reads one logical record and assigns an array or a list of scalar destinations.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinReadAsync(
        IReadOnlyList<ShellValue> words, ScopedEvaluation scope, Variables.CommandBindings? bindings, bool negated)
    {
        using var diagnostics = new BuiltinDiagnosticScope(this);
        static string Usage() => "read: usage: " + BuiltinDocumentation.For(BuiltinKind.Read).Synopsis("read");
        var options = new ShortOptionCursor(words);
        var backslashMode = ReadBackslashMode.InterpretEscapes;
        var delimiter = (byte)'\n';
        var inputNumber = LogicalDescriptor.StdIn;
        var inputDescriptor = GetStdInDescriptor();
        ShellValue? arrayName = null;
        while (options.TryReadFlag(out var option))
        {
            var flag = option.Span[0];
            if (flag == (byte)'r')
            {
                backslashMode = ReadBackslashMode.Preserve;
                continue;
            }
            if (flag is not ((byte)'u' or (byte)'d' or (byte)'a'))
                return await FailUsageAsync([ShellValue.FromText("-"), ShellValue.FromBytes(option),
                    ShellValue.FromText(": invalid option\n" + Usage())]).ConfigureAwait(false);
            if (!options.TryReadOperand(out var operand))
                return await FailUsageAsync([ShellValue.FromText($"-{(char)flag}: option requires an argument\n" + Usage())])
                    .ConfigureAwait(false);

            if (flag == (byte)'d')
            {
                delimiter = operand.Bytes.IsEmpty ? (byte)0 : operand.Bytes.Span[0];
                continue;
            }
            if (flag == (byte)'a')
            {
                arrayName = operand;
                continue;
            }

            var (number, descriptor) = ResolveInputDescriptorOperand(operand.Bytes.Span);
            if (number is not { } logical || descriptor.IsClosed)
            {
                await ReportInvalidInputDescriptorAsync(ShellValue.FromText("read"), operand, number)
                    .ConfigureAwait(false);
                return await FinishAsync(1).ConfigureAwait(false);
            }
            inputNumber = logical;
            inputDescriptor = descriptor;
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Read, "read", negated)
                .ConfigureAwait(false);

        var argumentIndex = options.ArgumentIndex;
        var hasPositionals = argumentIndex < words.Count;
        var implicitReply = arrayName == null && !hasPositionals;
        var fieldCount = arrayName.HasValue ? 0 : implicitReply ? 1 : words.Count - argumentIndex;
        var firstName = implicitReply ? ShellValue.FromText("REPLY") : hasPositionals ? words[argumentIndex] : default;
        VariableReference firstReference = default;
        // A first positional operand is validated even when -a selects the only assignment destination.
        if ((hasPositionals || implicitReply) && !TryParseTarget(firstName, out firstReference))
            return await InvalidTargetAsync(firstName).ConfigureAwait(false);

        var memory = _readWorkspace ??= new ReadWorkspace(Memory.Budget);
        memory.Content.Truncate(0);
        var exitCode = 0;
        try
        {
            var input = await CollectInputAsync().ConfigureAwait(false);
            if (input.Failed)
                return await FinishAsync(input.ExitCode).ConfigureAwait(false);
            exitCode = input.ExitCode;
            IndexedInputTarget? arrayTarget = null;
            var fieldMode = implicitReply ? ReadFieldMode.PreserveInput : ReadFieldMode.Split;
            if (arrayName is { } destination)
            {
                // Bash consumes the record before checking an array destination.
                var (target, status) = await SelectIndexedInputTargetAsync(
                    words[0], destination, scope.Variables, warn: !GetStdErrDescriptor().IsClosed).ConfigureAwait(false);
                if (target == null)
                    return await FinishAsync(status == 0 ? exitCode : status).ConfigureAwait(false);
                arrayTarget = target;
                fieldMode = ReadFieldMode.SplitAll;
            }

            using var fieldMemory = new MemoryCapacity(Memory.Budget, 0);
            var fields = ReadFieldSplitter.Split(input.Content, fieldCount, new ReadBuiltinOptions(
                FieldSeparators.FromVariables(scope.Variables), backslashMode, fieldMode),
                fieldMemory, scope.Variables, Memory, _cancellationToken);
            if (arrayTarget is { } array)
            {
                var attributes = scope.Variables.GetAssignmentAttributes(array.Name);
                if ((attributes & global::Lokad.Bash.Evaluation.Variables.BindingAttributes.Integer) != 0)
                {
                    var assigned = await AssignIntegerIndexedInputAsync(array.Name, fields, null, scope, words[0])
                        .ConfigureAwait(false);
                    return await FinishAsync(assigned ? exitCode : 1).ConfigureAwait(false);
                }
                Memory.Charge((long)fields.Count * IndexedArray.ElementMemoryBytes);
                for (var index = 0; index < fields.Count; index++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var field = fields[index];
                    var converted = global::Lokad.Bash.Evaluation.Variables.ConvertAssignmentCase(attributes, field, Memory);
                    fields[index] = converted.Bytes.Equals(field.Bytes) ? RetainField(field, input.Content.Span) : converted;
                }
                var replacement = IndexedArray.FromSequential(fields);
                _cancellationToken.ThrowIfCancellationRequested();
                array.PrepareForPublication(scope.Variables);
                if (!scope.Variables.TryAssignResolvedIndexedArray(array.Name, replacement))
                    exitCode = 1;
                return await FinishAsync(exitCode).ConfigureAwait(false);
            }
            for (var index = 0; index < fieldCount; index++)
            {
                var name = implicitReply ? firstName : words[argumentIndex + index];
                var reference = firstReference;
                if (index != 0 && !TryParseTarget(name, out reference))
                    return await InvalidTargetAsync(name).ConfigureAwait(false);
                var assignment = await AssignBuiltinAsync(reference, RetainField(fields[index], input.Content.Span), scope,
                    bindings, BuiltinAssignmentKind.Read, ShellValue.FromText("read"), expansionWriteTarget: null).ConfigureAwait(false);
                if (assignment == BuiltinAssignmentResult.Failed)
                {
                    exitCode = 1;
                    break;
                }
            }
        }
        catch (MemoryBudgetExceededException)
        {
            ReleaseReadWorkspace();
            await ReportInputRefusalAsync().ConfigureAwait(false);
            exitCode = 1;
        }
        finally
        {
            if (memory.Content.Capacity > ReadScratchRetentionBytes)
                ReleaseReadWorkspace();
        }
        return await FinishAsync(exitCode).ConfigureAwait(false);

        ShellValue RetainField(ShellValue value, ReadOnlySpan<byte> record)
        {
            if (value.IsEmpty)
                return ShellValue.Empty;
            // Command scratch covers publication; the variable estimate keeps accepted values charged.
            Memory.Charge(global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(value));
            // Reuse compact decoded backing and detach slices of the reusable record.
            if (!value.Bytes.Span.Overlaps(record) && value.TryGetCompactArrayCapacity(out _))
                return value;
            return ShellValue.FromBytes(value.Bytes.ToArray());
        }

        Task ReportInputRefusalAsync() => ReportExpansionDiagnosticAsync(ShellValue.FromText("read"),
            ShellValue.FromText("shared memory budget exhausted"));

        Task<EvaluationResult> FinishAsync(int status) => FinalizeResultAsync(
            EvaluationResult.FromExitCode(diagnostics.Result.IsError ? 1 : status), negated);

        Task<EvaluationResult> FailUsageAsync(ReadOnlySpan<ShellValue> message) =>
            FinishFailureAsync(WriteDiagnosticUnlessStderrClosedAsync([ShellValue.FromText("read")], message, diagnostics), 2);

        async Task<EvaluationResult> FinishFailureAsync(Task diagnostic, int status)
        {
            await diagnostic.ConfigureAwait(false);
            return await FinishAsync(status).ConfigureAwait(false);
        }

        bool TryParseTarget(ShellValue name, out VariableReference reference) =>
            VariableReference.TryParse(ShellSource.FromBytes(name.Bytes), Memory, out reference)
            && (reference.Kind == VariableReferenceKind.Name || !reference.Subscript.IsEmpty);

        Task<EvaluationResult> InvalidTargetAsync(ShellValue name) =>
            FinishFailureAsync(WriteInvalidIdentifierDiagnosticAsync(ShellValue.FromText("read"), name,
                diagnostics, ClosedStderrHandling.AttemptWrite), 1);

        async Task<(int ExitCode, ReadOnlyMemory<byte> Content, bool Failed)> CollectInputAsync()
        {
            var content = memory.Content;
            var escaped = false;
            var assignedContentEnded = false;
            if (delimiter != (byte)'\n')
                return await CollectDelimitedInputAsync().ConfigureAwait(false);

            var physicalLine = memory.PhysicalLine ??= new ByteMemoryBuilder(0, Memory.Budget);
            try
            {
                var exitCode = 0;
                while (true)
                {
                    ReadResult result;
                    try
                    {
                        result = await ExecuteReadAsync(inputDescriptor, 1, destinationBudget: null, reusableLine: physicalLine)
                            .ConfigureAwait(false);
                    }
                    catch (ByteMemoryBuilder.LimitExceededException)
                    {
                        // A single physical line can exhaust the descriptor's buffer before it returns.
                        return await RejectInputAsync().ConfigureAwait(false);
                    }
                    if (result.IsError)
                        return result.Error is { } error
                            ? await ReadFailureAsync(error).ConfigureAwait(false)
                            : (1, default, true);

                    if (result.Content.IsEmpty)
                    {
                        exitCode = 1;
                        break;
                    }

                    var line = result.Content;
                    var hasTerminator = line.Span[^1] == (byte)'\n';
                    if (AppendRecord(hasTerminator ? line[..^1] : line, hasTerminator))
                    {
                        if (result.ReachedEoF)
                        {
                            exitCode = 1;
                            break;
                        }
                        continue;
                    }

                    if (result.ReachedEoF && !hasTerminator)
                        exitCode = 1;

                    break;
                }

                return (exitCode, content.ToMemory(), false);
            }
            catch (MemoryBudgetExceededException)
            {
                return await RejectInputAsync().ConfigureAwait(false);
            }
            finally
            {
                // Large physical lines need not overlap field decoding and publication.
                if (physicalLine.Capacity > ReadScratchRetentionBytes)
                {
                    physicalLine.Dispose();
                    memory.PhysicalLine = null;
                }
            }

            async Task<(int ExitCode, ReadOnlyMemory<byte> Content, bool Failed)> CollectDelimitedInputAsync()
            {
                OperationError? readError = null;
                using (var recordMemory = new MemoryCapacity(Memory.Budget, 0))
                {
                    var remaining = ByteMemoryBuilder.MaxMemoryBuilderBytes;
                    while (true)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        recordMemory.Resize(0);
                        var completion = new TaskCompletionSource<ReadRecordResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        PublishOperation(new ReadRecordOperation(completion, inputDescriptor, delimiter, remaining, recordMemory));
                        var result = await completion.Task.ConfigureAwait(false);
                        _cancellationToken.ThrowIfCancellationRequested();
                        if (result.Record is { ExceedsLimit: true })
                            break;
                        if (result.Failure.Error is { } error)
                        {
                            readError = error;
                            break;
                        }
                        if (result.Record is not { } record)
                            return (1, content.ToMemory(), false);

                        var sourceBytes = record.Content.Length + (record.HasTerminator ? 1 : 0);
                        if (sourceBytes > remaining)
                            break;
                        remaining -= sourceBytes;
                        if (!AppendRecord(record.Content, record.HasTerminator))
                            return (record.HasTerminator ? 0 : 1, content.ToMemory(), false);
                    }
                }

                return readError is { } failure
                    ? await ReadFailureAsync(failure).ConfigureAwait(false)
                    : await RejectInputAsync().ConfigureAwait(false);
            }

            // Normalize as we collect the record. Quoting still controls framing
            // after a quoted NUL has ended the assigned text.
            bool AppendRecord(ReadOnlyMemory<byte> input, bool hasTerminator)
            {
                if (backslashMode == ReadBackslashMode.Preserve)
                {
                    ShellValue.FromBytes(input).AppendWithoutNuls(content);
                    return false;
                }

                var bytes = input.Span;
                while (!bytes.IsEmpty)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (escaped)
                    {
                        AppendEscapedByte(bytes[0]);
                        bytes = bytes[1..];
                        continue;
                    }

                    var special = bytes.IndexOfAny((byte)0, (byte)'\\');
                    if (!assignedContentEnded)
                        content.Append(special < 0 ? bytes : bytes[..special]);
                    if (special < 0)
                        break;
                    escaped = bytes[special] == (byte)'\\';
                    bytes = bytes[(special + 1)..];
                }

                if (!hasTerminator)
                    return false;
                if (escaped)
                    AppendEscapedByte(delimiter);
                else if (delimiter == (byte)'\\')
                    escaped = true;
                else
                    return false;
                return true;
            }

            void AppendEscapedByte(byte value)
            {
                escaped = false;
                if (value == (byte)'\n' || assignedContentEnded)
                    return;

                content.Append((byte)'\\').Append(value);
                // The quoted NUL participates in splitting as a protected empty field.
                assignedContentEnded = value == 0;
            }

            async Task<(int ExitCode, ReadOnlyMemory<byte> Content, bool Failed)> ReadFailureAsync(OperationError error)
            {
                await ReportBuiltinOperationErrorAsync(ShellValue.FromText("read"), $"read error: {inputNumber}: ", path: null, error)
                    .ConfigureAwait(false);
                return (1, default, true);
            }

            async Task<(int ExitCode, ReadOnlyMemory<byte> Content, bool Failed)> RejectInputAsync()
            {
                ReleaseReadWorkspace();
                await ReportInputRefusalAsync().ConfigureAwait(false);
                return (1, default, true);
            }
        }
    }
}

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

    private (LogicalDescriptor? Number, DescriptorBinding Binding) ResolveInputDescriptorOperand(ReadOnlySpan<byte> operand)
    {
        if (!Integers.TryParseBuiltinNumber(operand, out var number) || number is < 0 or > int.MaxValue)
            return (null, DescriptorBinding.Closed);

        var logical = new LogicalDescriptor((int)number);
        return (logical, ResolveInputBinding(logical));
    }

    // Input borrows the reached logical mapping and leaves handle ownership with it.
    private DescriptorBinding ResolveInputBinding(LogicalDescriptor descriptor) =>
        _state.CurrentRedirectionTable?.ResolveBinding(descriptor) ?? CaptureDescriptorBindings().ResolveBinding(descriptor);

    private Task ReportInvalidInputDescriptorAsync(ShellValue command, ShellValue operand, LogicalDescriptor? number,
        EvaluationContext context) =>
        number is { } logical
            ? ReportBuiltinDiagnosticAsync(command, logical + ": invalid file descriptor: Bad file descriptor", context)
            : ReportBuiltinDiagnosticAsync([command], [operand, ShellValue.FromText(": invalid file descriptor specification")], context);

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
        var command = words[0];
        var options = new ShortOptionCursor(words);
        var backslashMode = ReadBackslashMode.InterpretEscapes;
        var delimiter = (byte)'\n';
        var inputNumber = LogicalDescriptor.StdIn;
        DescriptorBinding? selectedInput = null;
        ShellValue? arrayName = null;
        int? count = null;
        var ignoreDelimiter = false;
        while (options.TryReadFlag(out var option))
        {
            var flag = option.Span[0];
            if (flag == (byte)'r')
            {
                backslashMode = ReadBackslashMode.Preserve;
                continue;
            }
            // The non-interactive stream has no line editing or terminal echo.
            if (flag is (byte)'e' or (byte)'s')
                continue;
            if (flag is not ((byte)'u' or (byte)'d' or (byte)'a' or (byte)'n' or (byte)'N' or (byte)'i' or (byte)'p'))
                return await FailUsageAsync([ShellValue.FromText("-"), ShellValue.FromBytes(option),
                    ShellValue.FromText(": invalid option\n" + Usage())]).ConfigureAwait(false);
            if (!options.TryReadOperand(out var operand))
                return await FailUsageAsync([ShellValue.FromText($"-{(char)flag}: option requires an argument\n" + Usage())])
                    .ConfigureAwait(false);

            // Consume the editor text or prompt without changing the input stream.
            if (flag is (byte)'i' or (byte)'p')
                continue;
            if (flag is (byte)'n' or (byte)'N')
            {
                if (!Integers.TryParseBuiltinNumber(operand.Bytes.Span, out var length) || length is < 0 or > int.MaxValue)
                    return await FinishFailureAsync(WriteDiagnosticUnlessStderrClosedAsync([command],
                        [operand, ShellValue.FromText(": invalid number")], diagnostics, GetBuiltinDiagnosticPrefix(scope.Context)), 1)
                        .ConfigureAwait(false);
                count = (int)length;
                ignoreDelimiter |= flag == (byte)'N';
                continue;
            }
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

            var (number, binding) = ResolveInputDescriptorOperand(operand.Bytes.Span);
            if (number is not { } logical || binding.Handle.IsClosed)
            {
                await ReportInvalidInputDescriptorAsync(command, operand, number, scope.Context)
                    .ConfigureAwait(false);
                return await FinishAsync(1).ConfigureAwait(false);
            }
            inputNumber = logical;
            selectedInput = binding;
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Read, "read", negated)
                .ConfigureAwait(false);

        var argumentIndex = options.ArgumentIndex;
        var hasPositionals = argumentIndex < words.Count;
        var implicitReply = arrayName == null && !hasPositionals;
        var fieldCount = arrayName.HasValue ? 0 : words.Count - argumentIndex;
        var firstReference = implicitReply ? VariableReference.ForName("REPLY") : default;
        // A first positional operand is validated even when -a selects the only assignment destination.
        if (hasPositionals && !TryParseTarget(words[argumentIndex], out firstReference))
            return await InvalidTargetAsync(words[argumentIndex]).ConfigureAwait(false);

        var inputBinding = selectedInput ?? ResolveInputBinding(inputNumber);
        var memory = count == 0 ? null : _readWorkspace ??= new ReadWorkspace(Memory.Budget);
        var exitCode = 0;
        try
        {
            // Zero count uses the selected binding's access mode without a record buffer or borrow.
            var input = memory != null ? await CollectInputAsync(memory).ConfigureAwait(false)
                : (ExitCode: inputBinding.CannotRead ? 1 : 0, Content: ReadOnlyMemory<byte>.Empty,
                    FieldBackslashMode: ReadBackslashMode.Preserve, Failed: false);
            if (input.Failed)
                return await FinishAsync(input.ExitCode).ConfigureAwait(false);
            exitCode = input.ExitCode;
            if (implicitReply)
            {
                using var scratch = new MemoryCapacity(Memory.Budget, 0);
                var reply = ReadFieldSplitter.DecodeField(input.Content, input.FieldBackslashMode, scratch, _cancellationToken);
                var assignment = await AssignReadFieldAsync(firstReference, reply, input.Content).ConfigureAwait(false);
                return await FinishAsync(assignment == BuiltinAssignmentResult.Failed ? 1 : exitCode).ConfigureAwait(false);
            }

            IndexedInputTarget? arrayTarget = null;
            var fieldMode = ReadFieldMode.Split;
            if (arrayName is { } destination)
            {
                // Bash consumes the record before checking an array destination.
                var (target, status) = await SelectIndexedInputTargetAsync(
                    command, destination, scope, warn: !GetStdErrDescriptor().IsClosed).ConfigureAwait(false);
                if (target == null)
                    return await FinishAsync(status == 0 ? exitCode : status).ConfigureAwait(false);
                arrayTarget = target;
                fieldMode = ReadFieldMode.SplitAll;
            }

            using var fieldMemory = new MemoryCapacity(Memory.Budget, 0);
            var fields = ReadFieldSplitter.Split(input.Content, fieldCount, new ReadBuiltinOptions(
                ignoreDelimiter ? FieldSeparators.Empty : FieldSeparators.FromVariables(scope.Variables),
                input.FieldBackslashMode, fieldMode),
                fieldMemory, scope.Variables, Memory, _cancellationToken);
            if (arrayTarget is { } array)
            {
                var attributes = array.AssignmentAttributes;
                if ((attributes & global::Lokad.Bash.Evaluation.Variables.BindingAttributes.Integer) != 0)
                {
                    var assigned = await AssignIntegerIndexedInputAsync(array, fields, null, null, scope, command)
                        .ConfigureAwait(false);
                    return await FinishAsync(assigned ? exitCode : 1).ConfigureAwait(false);
                }
                var beforePublication = Memory.Workspace.Bytes;
                var published = false;
                try
                {
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
                    published = array.TryPublish(replacement);
                }
                finally
                {
                    if (!published)
                    {
                        fields.Clear();
                        Memory.Workspace.Resize(beforePublication);
                        Memory.ReleaseUnusedCapacity();
                    }
                }
                return await FinishAsync(published ? exitCode : 1).ConfigureAwait(false);
            }
            for (var index = 0; index < fieldCount; index++)
            {
                CheckCancellation();
                var reference = firstReference;
                if (index != 0 && !TryParseTarget(words[argumentIndex + index], out reference))
                    return await InvalidTargetAsync(words[argumentIndex + index]).ConfigureAwait(false);
                var field = index < fields.Count ? fields[index] : ShellValue.Empty;
                var assignment = await AssignReadFieldAsync(reference, field, input.Content).ConfigureAwait(false);
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
            if (memory != null && (memory.Content.Capacity > ReadScratchRetentionBytes
                || memory.PhysicalLine?.Capacity > ReadScratchRetentionBytes))
                ReleaseReadWorkspace();
        }
        return await FinishAsync(exitCode).ConfigureAwait(false);

        Task<BuiltinAssignmentResult> AssignReadFieldAsync(VariableReference reference, ShellValue value, ReadOnlyMemory<byte> record) =>
            AssignBuiltinAsync(reference, RetainField(value, record.Span), scope,
                bindings, BuiltinAssignmentKind.Read, command, expansionWriteTarget: null);

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

        Task ReportInputRefusalAsync() => ReportExpansionDiagnosticAsync(command,
            ShellValue.FromText("shared memory budget exhausted"));

        Task<EvaluationResult> FinishAsync(int status) => FinalizeResultAsync(
            EvaluationResult.FromExitCode(diagnostics.Result.IsError ? 1 : status), negated);

        Task<EvaluationResult> FailUsageAsync(ReadOnlySpan<ShellValue> message) =>
            FinishFailureAsync(WriteDiagnosticUnlessStderrClosedAsync([command], message, diagnostics,
                GetBuiltinDiagnosticPrefix(scope.Context)), 2);

        async Task<EvaluationResult> FinishFailureAsync(Task diagnostic, int status)
        {
            await diagnostic.ConfigureAwait(false);
            return await FinishAsync(status).ConfigureAwait(false);
        }

        bool TryParseTarget(ShellValue name, out VariableReference reference) =>
            VariableReference.TryParse(ShellSource.FromBytes(name.Bytes), Memory, out reference)
            && (reference.Kind == VariableReferenceKind.Name || !reference.Subscript.IsEmpty);

        Task<EvaluationResult> InvalidTargetAsync(ShellValue name) =>
            FinishFailureAsync(WriteInvalidIdentifierDiagnosticAsync(command, name,
                diagnostics, ClosedStderrHandling.AttemptWrite, GetBuiltinDiagnosticPrefix(scope.Context)), 1);

        async Task<(int ExitCode, ReadOnlyMemory<byte> Content, ReadBackslashMode FieldBackslashMode, bool Failed)>
            CollectInputAsync(ReadWorkspace memory)
        {
            var content = memory.Content;
            content.Truncate(0);
            if (count.HasValue || delimiter != (byte)'\n')
            {
                var completion = new TaskCompletionSource<ReadInputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                PublishOperation(new ReadInputOperation(completion, inputBinding.Handle,
                    ignoreDelimiter ? null : delimiter, count, backslashMode, content));
                try
                {
                    var result = await completion.Task.ConfigureAwait(false);
                    CheckCancellation();
                    return result.Error is { } error ? await ReadFailureAsync(error).ConfigureAwait(false)
                        : (result.Complete ? 0 : 1, content.ToMemory(), result.FieldBackslashMode, false);
                }
                catch (ByteMemoryBuilder.LimitExceededException)
                {
                    return await RejectInputAsync().ConfigureAwait(false);
                }
            }

            var normalization = new ReadInputBuffer(content, backslashMode);
            var physicalLine = memory.PhysicalLine ??= new ByteMemoryBuilder(0, Memory.Budget);
            var borrowsPhysicalLine = false;
            try
            {
                var exitCode = 0;
                while (true)
                {
                    ReadResult result;
                    try
                    {
                        result = await ExecuteReadAsync(inputBinding.Handle, 1, destinationBudget: null, reusableLine: physicalLine,
                                sourceDestination: null, retainImmutable: false)
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
                            : (1, default, ReadBackslashMode.Preserve, true);

                    if (result.Content.IsEmpty)
                    {
                        exitCode = 1;
                        break;
                    }

                    var line = result.Content;
                    var hasTerminator = line.Span[^1] == (byte)'\n';
                    if (normalization.AppendLine(hasTerminator ? line[..^1] : line,
                            hasTerminator, out var borrowed, _cancellationToken))
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

                    if (borrowed is { } record)
                    {
                        // Holding the line through assignment must fit the allowance the
                        // normalized record would have needed after retiring a large line.
                        if (physicalLine.Capacity <= ReadScratchRetentionBytes
                            || physicalLine.Capacity <= content.CapacityForLength(record.Length) - content.Capacity)
                        {
                            borrowsPhysicalLine = true;
                            return (exitCode, record, normalization.FieldBackslashMode, false);
                        }
                        content.Append(record);
                    }
                    break;
                }

                return (exitCode, content.ToMemory(), normalization.FieldBackslashMode, false);
            }
            catch (MemoryBudgetExceededException)
            {
                return await RejectInputAsync().ConfigureAwait(false);
            }
            finally
            {
                // Large physical lines need not overlap field decoding and publication.
                if (!borrowsPhysicalLine && physicalLine.Capacity > ReadScratchRetentionBytes)
                {
                    physicalLine.Dispose();
                    memory.PhysicalLine = null;
                }
            }

            async Task<(int, ReadOnlyMemory<byte>, ReadBackslashMode, bool)> ReadFailureAsync(OperationError error)
            {
                await ReportBuiltinOperationErrorAsync(command, $"read error: {inputNumber}: ", path: null, error,
                        GetBuiltinDiagnosticPrefix(scope.Context))
                    .ConfigureAwait(false);
                return (1, default, ReadBackslashMode.Preserve, true);
            }

            async Task<(int, ReadOnlyMemory<byte>, ReadBackslashMode, bool)> RejectInputAsync()
            {
                ReleaseReadWorkspace();
                await ReportInputRefusalAsync().ConfigureAwait(false);
                return (1, default, ReadBackslashMode.Preserve, true);
            }
        }
    }
}

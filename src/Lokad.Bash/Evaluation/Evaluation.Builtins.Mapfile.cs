using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Collects bounded byte records before replacing the selected mapfile/readarray binding.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinMapfileAsync(
        IReadOnlyList<ShellValue> arguments, ScopedEvaluation scope, bool negated)
    {
        var variables = scope.Variables;
        const int ReadBatchRecords = 128;
        const int RecordListEntryBytes = 64; // Includes list capacity and resize overlap.
        using var diagnostics = new BuiltinDiagnosticScope(this);
        var targetValue = ShellValue.FromText("MAPFILE");
        var delimiter = (byte)'\n';
        var trimDelimiter = false;
        uint count = 0;
        uint? origin = null;
        var targetSeen = false;
        var parseOptions = true;
        for (var argumentIndex = 1; argumentIndex < arguments.Count; argumentIndex++)
        {
            var argument = arguments[argumentIndex];
            var bytes = argument.Bytes.Span;
            if (parseOptions && bytes.SequenceEqual("--"u8))
            {
                parseOptions = false;
                continue;
            }

            if (parseOptions && bytes.Length > 1 && bytes[0] == (byte)'-')
            {
                for (var optionIndex = 1; optionIndex < bytes.Length; optionIndex++)
                {
                    var option = bytes[optionIndex];
                    if (option == (byte)'t')
                    {
                        trimDelimiter = true;
                        continue;
                    }
                    if (option is not ((byte)'d' or (byte)'n' or (byte)'O'))
                        return await InvalidArgumentsAsync(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                            .Append((byte)'-').Append(option).Append(": invalid option"u8).ToMemory())).ConfigureAwait(false);

                    var operand = argument.Bytes[(optionIndex + 1)..];
                    if (operand.IsEmpty)
                    {
                        if (++argumentIndex == arguments.Count)
                            return await InvalidArgumentsAsync(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                                .Append((byte)'-').Append(option).Append(": option requires an argument"u8).ToMemory()))
                                .ConfigureAwait(false);
                        operand = arguments[argumentIndex].Bytes;
                    }

                    if (option == (byte)'d')
                        delimiter = operand.IsEmpty ? (byte)0 : operand.Span[0];
                    else
                    {
                        // Bash's decimal conversion skips C whitespace before the number
                        // and accepts only a space or tab after it.
                        var numeric = operand.Span.TrimStart(" \t\n\r\v\f"u8).TrimEnd(" \t"u8);
                        if (!Integers.TryParseNumber(numeric, out var number) || number is < 0 or > uint.MaxValue)
                            return await InvalidNumberAsync(operand, option).ConfigureAwait(false);
                        if (option == (byte)'n')
                            count = (uint)number;
                        else
                            origin = (uint)number;
                    }
                    break;
                }

                continue;
            }

            if (targetSeen)
                continue;

            targetValue = argument;
            targetSeen = true;
            parseOptions = false;
        }

        var (selected, targetStatus) = await SelectIndexedInputTargetAsync(arguments[0], targetValue, variables, warn: true)
            .ConfigureAwait(false);
        if (selected is not { } target)
            return await FinishAsync(targetStatus).ConfigureAwait(false);
        var resolved = target.Name;

        var descriptor = GetStdInDescriptor();
        // Keep one owned copy per batch; array entries borrow slices for the same lifetime.
        using var valuesMemory = Memory.Budget.CreateScope();
        var values = new List<ShellValue>();
        long backingBytes = 0;
        using var inputMemory = Memory.Budget.Reserve(0);
        var assigned = false;
        try
        {
            // Bash treats an explicitly closed stdin as empty input; actual host read failures remain failures.
            var reachedEof = descriptor.IsClosed;
            while (!reachedEof && (count == 0 || (uint)values.Count < count))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var limit = count == 0 ? ReadBatchRecords : (int)Math.Min(ReadBatchRecords, count - (uint)values.Count);
                if (delimiter == (byte)'\n')
                {
                    var batch = await ExecuteReadAsync(descriptor, limit, valuesMemory).ConfigureAwait(false);
                    batch.ThrowIfError();
                    AddBatch(batch.Content, batch.LineStartOffsets);
                    reachedEof = batch.ReachedEoF;
                }
                else
                {
                    var completion = new TaskCompletionSource<DescriptorRecordBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PublishOperation(new ReadRecordBatchOperation(completion, descriptor, delimiter, limit, valuesMemory));
                    var batch = await completion.Task.ConfigureAwait(false);
                    CheckCancellation();
                    batch.Result.ThrowIfError();
                    AddBatch(batch.Content, batch.RecordStartOffsets);
                    reachedEof = batch.Result.ReachedEoF;
                }
            }
            if (variables.IsIntegerBinding(resolved))
                return await FinishAsync(await AssignIntegerIndexedInputAsync(resolved, values, origin, scope, arguments[0])
                    .ConfigureAwait(false) ? 0 : 1).ConfigureAwait(false);

            // Admit both collection representations while building the persistent array.
            long valueBytes = 0;
            foreach (var value in values)
                valueBytes = checked(valueBytes + global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(value));
            var retainedBytes = checked((long)values.Count * IndexedArray.ElementMemoryBytes + backingBytes + valueBytes);
            if (origin.HasValue && variables.Scalars.ContainsKey(resolved))
                retainedBytes += IndexedArray.ElementMemoryBytes;
            Memory.Charge(retainedBytes);

            if (origin.HasValue)
            {
                // Each replacement owns a compact value. Repeated writes at the same origin
                // retire the old values without accumulating charges for historic batches.
                for (var index = 0; index < values.Count; index++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var value = values[index];
                    if (!value.IsEmpty)
                        values[index] = ShellValue.FromBytes(value.Bytes.ToArray());
                }
            }

            _cancellationToken.ThrowIfCancellationRequested();
            // Publish only after the requested records have been read and admitted.
            target.PrepareForPublication(variables);
            if (origin is { } first)
                assigned = variables.TryUpdateResolvedIndexedArray(resolved, first, values, Memory, _cancellationToken);
            else
            {
                var array = IndexedArray.FromSequential(values);
                array.RetainedBackingBytes = backingBytes;
                assigned = variables.TryAssignResolvedIndexedArray(resolved, array);
            }
        }
        catch (ShellDescriptorReadException)
        {
            return await FinishAsync(1).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ByteMemoryBuilder.LimitExceededException or MemoryBudgetExceededException)
        {
            // Refuse before committing any collected records to the target variable.
            values.Clear();
            valuesMemory.Dispose();
            return await RejectInputAsync().ConfigureAwait(false);
        }
        return await FinishAsync(assigned ? 0 : 1).ConfigureAwait(false);

        void AddBatch(ReadOnlyMemory<byte> content, IReadOnlyList<int> recordStarts)
        {
            var start = 0;
            for (var index = 0; start < content.Length; index++)
            {
                var end = index < recordStarts.Count ? recordStarts[index] : content.Length;
                var record = content[start..end];
                start = end;
                // Even a short/NUL-truncated record pins its batch after the staging scope ends.
                if (!origin.HasValue)
                    backingBytes = checked(backingBytes + global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(
                        ShellValue.FromBytes(record)));
                // Admit geometric list growth, including replacement overlap, before allocation.
                if (values.Count == values.Capacity)
                {
                    var capacity = values.Capacity == 0 ? 4 : checked(2 * values.Capacity);
                    inputMemory.Resize((long)RecordListEntryBytes * capacity);
                    values.Capacity = capacity;
                }
                if (trimDelimiter && record.Span[^1] == delimiter)
                    record = record[..^1];
                values.Add(ShellValue.FromBytes(record).BeforeFirstNul());
            }
        }

        Task<EvaluationResult> FinishAsync(int status) => FinalizeResultAsync(
            EvaluationResult.FromExitCode(diagnostics.Result.IsError ? 1 : status), negated);

        async Task<EvaluationResult> RejectInputAsync()
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("mapfile"), ShellValue.FromText("shared memory budget exhausted")).ConfigureAwait(false);
            return await FinishAsync(2).ConfigureAwait(false);
        }

        async Task<EvaluationResult> InvalidArgumentsAsync(ShellValue detail)
        {
            // Advertise the supported subset, not Bash options whose behavior is intentionally absent.
            var commandName = arguments[0];
            var message = ByteMemoryBuilder.CreateUnaccounted(0).Append(commandName.Bytes).Append(": "u8).Append(detail.Bytes)
                .Append((byte)'\n').Append(commandName.Bytes).Append(": usage: "u8)
                .Append(commandName.Bytes).Append((byte)' ')
                .Append(Utf8Text.Encode(BuiltinDocumentation.For(BuiltinKind.Mapfile).Arguments)).Append((byte)'\n');
            var failure = (await ExecuteWriteAsync(GetStdErrDescriptor(), message.ToMemory()).ConfigureAwait(false)).IsError ? 1 : 0;
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(failure == 0 ? 2 : failure), negated).ConfigureAwait(false);
        }

        async Task<EvaluationResult> InvalidNumberAsync(ReadOnlyMemory<byte> operand, byte option)
        {
            using var detail = new ByteMemoryBuilder(0, Memory.Workspace);
            detail.Append(operand).Append(option == (byte)'n' ? ": invalid line count"u8 : ": invalid array origin"u8);
            await ReportExpansionDiagnosticAsync(arguments[0], ShellValue.FromBytes(detail.ToMemory())).ConfigureAwait(false);
            return await FinishAsync(1).ConfigureAwait(false);
        }
    }

    private async Task<bool> AssignIntegerIndexedInputAsync(
        BindingName name, IReadOnlyList<ShellValue> values, uint? origin, ScopedEvaluation scope, ShellValue command)
    {
        // The longest rendered signed decimal uses 20 bytes. Include each value's backing
        // along with sparse storage while the input records and saved binding remain live.
        const int RecordMemoryBytes = IndexedArray.ElementMemoryBytes + 96;
        var prepaidBytes = checked((long)values.Count * RecordMemoryBytes);
        Memory.Charge(prepaidBytes);
        CheckCancellation();
        using var assignment = new Variables.IndexedInputAssignment(scope.Variables, name, origin.HasValue, Memory);
        var first = (long)origin.GetValueOrDefault();
        for (var offset = 0; offset < values.Count; offset++)
        {
            CheckCancellation();
            var integer = await WordExpansion.EvaluateIntegerAssignmentAsync(values[offset], null, scope, command)
                .ConfigureAwait(false);
            if (!integer.HasValue)
            {
                // A shell arithmetic error leaves preceding writes visible to EXIT.
                // Refusal and cancellation throw through the staged assignment and restore the old binding.
                assignment.Commit();
                return false;
            }
            if (!IndexedArrayIndex.TryCreate(first + offset, out var position)
                || !scope.Variables.TrySetResolvedArrayElement(name, position, integer.Value, Memory, prepaidBytes))
                return false;
            // Keep consumed credit with the written positions and decimal values.
            prepaidBytes -= RecordMemoryBytes;
        }
        CheckCancellation();
        assignment.Commit();
        return true;
    }

    private enum IndexedInputTargetKind { ExistingOrNew, CircularNameref, UnassignedNameref }

    private readonly record struct IndexedInputTarget(BindingName Name, IndexedInputTargetKind Kind)
    {
        public void PrepareForPublication(Variables variables)
        {
            if (Kind == IndexedInputTargetKind.CircularNameref)
                variables.RecreateDeclarationArray(Name, ArrayKind.Indexed);
            else if (Kind == IndexedInputTargetKind.UnassignedNameref)
                variables.TryDeclareArray(Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue);
        }
    }

    /// <summary>Selects a whole indexed-array destination without altering its binding.</summary>
    private async Task<(IndexedInputTarget? Target, int ExitCode)> SelectIndexedInputTargetAsync(
        ShellValue command, ShellValue operand, Variables variables, bool warn)
    {
        if (!VariableReference.TryParse(ShellSource.FromBytes(operand.Bytes), Memory, out var reference)
            || reference.Kind != VariableReferenceKind.Name)
            return await InvalidTargetAsync(operand).ConfigureAwait(false);

        var target = reference.Name;
        var resolvedBinding = variables.TryResolveBinding(target, selectOwner: null, out var binding);
        var resolved = resolvedBinding ? binding.Name : target;
        if (resolvedBinding && binding.Reference.Kind != VariableReferenceKind.Name)
            return await InvalidTargetAsync(binding.Reference.ToValue(Memory)).ConfigureAwait(false);
        if (!resolved.IsIdentifier)
            return await InvalidTargetAsync(resolved.ToValue()).ConfigureAwait(false);

        var kind = !resolvedBinding ? IndexedInputTargetKind.CircularNameref
            : variables.TryGetNameref(resolved, out var referenced) && referenced.IsEmpty
                ? IndexedInputTargetKind.UnassignedNameref
                : IndexedInputTargetKind.ExistingOrNew;
        if (warn)
        {
            if (kind == IndexedInputTargetKind.CircularNameref)
                await ReportCircularNameReferenceAsync(target).ConfigureAwait(false);
            else if (kind == IndexedInputTargetKind.UnassignedNameref)
                await ReportRemovingNamerefAsync(target).ConfigureAwait(false);
        }
        if (!BuiltinDiagnosticsOpen)
            return (null, 0);

        // A cycle recreates the binding; otherwise readonly belongs to the selected array.
        if (resolvedBinding && variables.IsReadonly(resolved))
        {
            await ReportExpansionDiagnosticAsync(operand, ShellValue.FromText("readonly variable")).ConfigureAwait(false);
            return (null, 1);
        }
        if (variables.AssociativeArrays.ContainsKey(resolved))
        {
            await ReportExpansionDiagnosticAsync(command,
                ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append(operand.Bytes)
                    .Append(": not an indexed array"u8).ToMemory())).ConfigureAwait(false);
            return (null, 1);
        }

        return (new IndexedInputTarget(resolved, kind), 0);

        async Task<(IndexedInputTarget?, int)> InvalidTargetAsync(ShellValue name)
        {
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                .Append(ShellArgumentEscaping.QuoteDiagnosticOperand(name).Bytes).Append(": not a valid identifier"u8).ToMemory())).ConfigureAwait(false);
            return (null, 1);
        }
    }
}

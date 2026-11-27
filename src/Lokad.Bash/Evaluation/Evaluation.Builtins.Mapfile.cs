using System;
using System.Buffers.Text;
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
    /// <summary>Collects bounded byte records and evaluates reached mapfile/readarray callbacks.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinMapfileAsync(
        IReadOnlyList<ShellValue> arguments, ScopedEvaluation scope, bool negated)
    {
        const int ReadBatchRecords = 128;
        using var diagnostics = new BuiltinDiagnosticScope(this);
        DescriptorBinding? selectedInput = null;
        var delimiter = (byte)'\n';
        var trimDelimiter = false;
        uint count = 0;
        uint skip = 0;
        uint? origin = null;
        ShellValue? callback = null;
        uint quantum = 5000;
        EvaluationResult? pendingLoopControl = null;
        var options = new ShortOptionCursor(arguments);
        while (options.TryReadFlag(out var flag))
        {
            var option = flag.Span[0];
            if (option == (byte)'t')
            {
                trimDelimiter = true;
                continue;
            }
            if (option is not ((byte)'C' or (byte)'c' or (byte)'d' or (byte)'n' or (byte)'O' or (byte)'s' or (byte)'u'))
                return await InvalidArgumentsAsync(flag, ": invalid option").ConfigureAwait(false);
            if (!options.TryReadOperand(out var operand))
                return await InvalidArgumentsAsync(flag, ": option requires an argument").ConfigureAwait(false);

            if (option == (byte)'C')
                callback = operand;
            else if (option == (byte)'d')
                delimiter = operand.Bytes.IsEmpty ? (byte)0 : operand.Bytes.Span[0];
            else if (option == (byte)'u')
            {
                var (number, binding) = ResolveInputDescriptorOperand(operand.Bytes.Span);
                if (number == null || binding.Handle.IsClosed)
                {
                    await ReportInvalidInputDescriptorAsync(arguments[0], operand, number, scope.Context).ConfigureAwait(false);
                    return await FinishAsync(1).ConfigureAwait(false);
                }
                selectedInput = binding;
            }
            else
            {
                if (!Integers.TryParseBuiltinNumber(operand.Bytes.Span, out var number)
                    || number < (option == (byte)'c' ? 1 : 0) || number > uint.MaxValue)
                    return await InvalidNumberAsync(operand.Bytes, option).ConfigureAwait(false);
                if (option == (byte)'n')
                    count = (uint)number;
                else if (option == (byte)'s')
                    skip = (uint)number;
                else if (option == (byte)'O')
                    origin = (uint)number;
                else
                    quantum = (uint)number;
            }
        }
        if (options.RequestedHelp)
        {
            var name = arguments[0].Bytes.Span.SequenceEqual("readarray"u8) ? "readarray" : "mapfile";
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(arguments[0], BuiltinKind.Mapfile, name, negated)
                .ConfigureAwait(false);
        }

        var input = selectedInput ?? ResolveInputBinding(LogicalDescriptor.StdIn);
        var targetValue = options.ArgumentIndex < arguments.Count ? arguments[options.ArgumentIndex] : ShellValue.FromText("MAPFILE");
        var (selected, targetStatus) = await SelectIndexedInputTargetAsync(arguments[0], targetValue, scope, warn: true)
            .ConfigureAwait(false);
        if (selected is not { } target)
            return await FinishAsync(targetStatus).ConfigureAwait(false);
        var resolved = target.Name;
        var variables = target.Owner;

        var attributes = variables.GetAssignmentAttributes(resolved);
        var integer = (attributes & global::Lokad.Bash.Evaluation.Variables.BindingAttributes.Integer) != 0;
        var convertCase = (attributes & global::Lokad.Bash.Evaluation.Variables.BindingAttributes.Case) != 0;
        // Arithmetic consumes borrowed records after input settles. Ordinary records can
        // outlive their batch independently after publication.
        using var integerInputMemory = integer && callback == null ? Memory.Budget.CreateScope() : null;
        var values = callback.HasValue ? null : new List<ShellValue>();
        using var inputMemory = values != null ? new MemoryCapacity(Memory.Budget, 0) : null;
        List<(int LastOffset, IMemoryScope Owner)>? borrowedBatches = null;
        long stagedBytes = 0;
        var lastBorrowedOffset = -1;
        Variables.IndexedInputAssignment? callbackAssignment = null;
        var toCallback = quantum;
        uint written = 0;
        bool assigned;
        try
        {
            // A closed default input or a known write-only binding supplies an empty record sequence.
            var reachedEof = input.CannotRead;
            while (!reachedEof && (count == 0 || CollectedCount() < count))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var skipping = skip != 0;
                var remaining = count == 0 ? ReadBatchRecords : count - CollectedCount();
                // A callback may read the next record from the same descriptor.
                if (callback.HasValue && pendingLoopControl == null)
                    remaining = Math.Min(remaining, toCallback);
                var limit = (int)Math.Min(ReadBatchRecords, skipping ? skip : remaining);
                // A skipped batch never reaches arithmetic or array publication.
                var retainedInput = skipping ? null : integerInputMemory;
                var retainImmutable = skipping || (!integer && !callback.HasValue);
                var readMemory = (retainedInput ?? Memory.Budget).CreateScope();
                lastBorrowedOffset = -1;
                try
                {
                    ReadOnlyMemory<byte> content;
                    IReadOnlyList<int> recordStarts;
                    if (delimiter == (byte)'\n')
                    {
                        var batch = await ExecuteReadAsync(input.Handle, limit, readMemory, reusableLine: null,
                            sourceDestination: null, retainImmutable).ConfigureAwait(false);
                        batch.ThrowIfError();
                        content = batch.Content;
                        recordStarts = batch.LineStartOffsets;
                        reachedEof = batch.ReachedEoF;
                    }
                    else
                    {
                        var completion = new TaskCompletionSource<DescriptorRecordBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                        PublishOperation(new ReadRecordBatchOperation(completion, input.Handle, delimiter, limit, readMemory, retainImmutable));
                        var batch = await completion.Task.ConfigureAwait(false);
                        CheckCancellation();
                        batch.Result.ThrowIfError();
                        content = batch.Content;
                        recordStarts = batch.RecordStartOffsets;
                        reachedEof = batch.Result.ReachedEoF;
                    }
                    if (await AcceptBatchAsync(content, recordStarts).ConfigureAwait(false) is { } stop)
                        return stop;
                    if (lastBorrowedOffset >= 0)
                    {
                        borrowedBatches ??= [];
                        if (borrowedBatches.Count == borrowedBatches.Capacity)
                        {
                            var storage = inputMemory ?? throw new InvalidOperationException("Bulk records require a reservation.");
                            storage.PrepareListCapacity(borrowedBatches, 1);
                            storage.ReleaseUnusedCapacity();
                        }
                        borrowedBatches.Add((lastBorrowedOffset, readMemory));
                    }
                }
                finally
                {
                    // The integer input owner retains borrowed batches until conversion or unwind.
                    if (lastBorrowedOffset < 0)
                        readMemory.Dispose();
                }
            }
            if (values == null)
            {
                StartCallbackAssignment().Commit();
                return await FinishAsync(0).ConfigureAwait(false);
            }
            if (integer)
            {
                var converted = await AssignIntegerIndexedInputAsync(target, values, borrowedBatches, origin, scope, arguments[0])
                    .ConfigureAwait(false);
                values = null;
                borrowedBatches = null;
                integerInputMemory?.Dispose();
                inputMemory?.Dispose();
                return await FinishAsync(converted ? 0 : 1).ConfigureAwait(false);
            }

            // The staged values already have admission. The list and array coexist during publication.
            var retainedBytes = (long)values.Count * IndexedArray.ElementMemoryBytes;
            if (origin.HasValue && (target.NamerefValue.HasValue || variables.Scalars.ContainsKey(resolved)))
                retainedBytes += IndexedArray.ElementMemoryBytes;
            Memory.Charge(retainedBytes);
            stagedBytes += retainedBytes;

            _cancellationToken.ThrowIfCancellationRequested();
            // Publish only after the requested records have been read and admitted.
            target.PrepareForPublication();
            assigned = origin is { } first
                ? variables.TryUpdateResolvedIndexedArray(resolved, first, values, target.NamerefValue, Memory, _cancellationToken)
                : variables.TryAssignResolvedIndexedArray(resolved, IndexedArray.FromSequential(values));
        }
        catch (ShellDescriptorReadException)
        {
            return await FinishAsync(1).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ByteMemoryBuilder.LimitExceededException or MemoryBudgetExceededException)
        {
            callbackAssignment?.Dispose();
            callbackAssignment = null;
            if (values != null)
            {
                values.Clear();
                values.Capacity = 0;
            }
            borrowedBatches = null;
            inputMemory?.Dispose();
            integerInputMemory?.Dispose();
            Memory.Workspace.Resize(Memory.Workspace.Bytes - stagedBytes);
            Memory.ReleaseUnusedCapacity();
            return await RejectInputAsync().ConfigureAwait(false);
        }
        finally { callbackAssignment?.Dispose(); }
        return await FinishAsync(assigned ? 0 : 1).ConfigureAwait(false);

        uint CollectedCount() => values != null ? (uint)values.Count : written;

        Variables.IndexedInputAssignment StartCallbackAssignment() =>
            callbackAssignment ??= new Variables.IndexedInputAssignment(variables, resolved, origin.HasValue,
                resetAttributes: target.Kind == IndexedInputTargetKind.CircularNameref, target.NamerefValue, Memory);

        async ValueTask<EvaluationResult?> AcceptBatchAsync(ReadOnlyMemory<byte> content, IReadOnlyList<int> recordStarts)
        {
            if (skip != 0)
            {
                if (!content.IsEmpty)
                    skip = checked(skip - (uint)recordStarts.Count - 1);
                return null;
            }

            var start = 0;
            for (var index = 0; start < content.Length; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var end = index < recordStarts.Count ? recordStarts[index] : content.Length;
                var record = content[start..end];
                start = end;
                if (trimDelimiter && record.Span[^1] == delimiter)
                    record = record[..^1];
                var value = ShellValue.FromBytes(record).BeforeFirstNul();
                if (values == null)
                {
                    if (await AssignCallbackRecordAsync(callback.GetValueOrDefault(), value).ConfigureAwait(false) is { } stop)
                        return stop;
                    continue;
                }

                // Admit geometric list growth, including replacement overlap, before allocation.
                if (values.Count == values.Capacity)
                {
                    var storage = inputMemory ?? throw new InvalidOperationException("Bulk records require a reservation.");
                    storage.PrepareListCapacity(values, 1);
                    storage.ReleaseUnusedCapacity();
                }
                if (value.IsEmpty)
                    value = ShellValue.Empty;
                else if (integer)
                    lastBorrowedOffset = values.Count;
                else
                {
                    if (convertCase)
                    {
                        var beforeConversion = Memory.Workspace.Bytes;
                        try
                        {
                            var converted = global::Lokad.Bash.Evaluation.Variables.ConvertAssignmentCase(attributes, value, Memory);
                            if (!converted.Bytes.Equals(value.Bytes))
                            {
                                values.Add(converted);
                                continue;
                            }
                        }
                        finally
                        {
                            stagedBytes += Memory.Workspace.Bytes - beforeConversion;
                        }
                    }
                    var bytes = global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(value);
                    Memory.Charge(bytes);
                    stagedBytes += bytes;
                    value = ShellValue.FromBytes(value.Bytes.ToArray());
                }
                values.Add(value);
            }
            return null;
        }

        async ValueTask<EvaluationResult?> AssignCallbackRecordAsync(ShellValue action, ShellValue value)
        {
            var assignment = StartCallbackAssignment();
            var position = unchecked(origin.GetValueOrDefault() + written);
            if (pendingLoopControl == null && --toCallback == 0)
            {
                toCallback = quantum;
                var result = await RunMapfileCallbackAsync(action, unchecked((int)position), value, scope)
                    .ConfigureAwait(false);
                // The reader completes the requested input before applying loop control.
                if (result.Flow is FlowControlKind.Break or FlowControlKind.Continue)
                    pendingLoopControl = result;
                else if (result.Flow != FlowControlKind.None)
                {
                    assignment.Commit();
                    return result;
                }
                CheckCancellation();
            }

            var index = IndexedArrayIndex.FromUnsigned(position);
            bool stored;
            var writesInteger = (variables.GetAssignmentAttributes(resolved)
                & global::Lokad.Bash.Evaluation.Variables.BindingAttributes.Integer) != 0;
            if (writesInteger)
            {
                // A signed decimal result uses at most 20 bytes.
                Memory.PrepareRetainedStorage(assignment.SavedBytes
                    + global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(20));
                var coerced = await WordExpansion.EvaluateIntegerAssignmentAsync(value, null, scope, arguments[0])
                    .ConfigureAwait(false);
                if (!coerced.HasValue)
                {
                    assignment.Commit();
                    return await FinishAsync(1).ConfigureAwait(false);
                }
                CheckCancellation();
                var prepaidBytes = global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(coerced.Value);
                stored = variables.TrySetResolvedArrayElement(resolved, index, coerced.Value, Memory, prepaidBytes);
            }
            else
                stored = assignment.TryStoreBorrowedRecord(index, value, Memory);
            if (!stored)
                return await FinishAsync(1).ConfigureAwait(false);
            written++;
            return null;
        }

        Task<EvaluationResult> FinishAsync(int status)
        {
            var exitCode = diagnostics.Result.IsError ? 1 : status;
            return FinalizeResultAsync(pendingLoopControl?.WithExitCode(exitCode)
                ?? EvaluationResult.FromExitCode(exitCode), negated);
        }

        async Task<EvaluationResult> RejectInputAsync()
        {
            await ReportExpansionDiagnosticAsync(arguments[0], ShellValue.FromText("shared memory budget exhausted"))
                .ConfigureAwait(false);
            return await FinishAsync(2).ConfigureAwait(false);
        }

        async Task<EvaluationResult> InvalidArgumentsAsync(ReadOnlyMemory<byte> option, string detail)
        {
            var commandName = arguments[0];
            var result = await WriteDiagnosticUnlessStderrClosedAsync([commandName],
                [ShellValue.FromText("-"), ShellValue.FromBytes(option), ShellValue.FromText(detail + "\n"),
                    commandName, ShellValue.FromText(": usage: "), commandName,
                    ShellValue.FromText(" " + BuiltinDocumentation.For(BuiltinKind.Mapfile).Arguments)], scope: null,
                GetBuiltinDiagnosticPrefix(scope.Context)).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(result.IsError ? 1 : 2), negated).ConfigureAwait(false);
        }

        async Task<EvaluationResult> InvalidNumberAsync(ReadOnlyMemory<byte> operand, byte option)
        {
            var detail = option switch
            {
                (byte)'c' => ": invalid callback quantum",
                (byte)'O' => ": invalid array origin",
                _ => ": invalid line count"
            };
            await ReportBuiltinDiagnosticAsync([arguments[0]], [ShellValue.FromBytes(operand),
                ShellValue.FromText(detail)], scope.Context).ConfigureAwait(false);
            return await FinishAsync(1).ConfigureAwait(false);
        }
    }

    private async Task<EvaluationResult> RunMapfileCallbackAsync(
        ShellValue action, int index, ShellValue record, ScopedEvaluation scope)
    {
        Span<byte> indexText = stackalloc byte[11];
        if (!Utf8Formatter.TryFormat(index, indexText, out var digits))
            throw new InvalidOperationException("The callback index did not fit.");
        var quotedLength = ShellArgumentEscaping.GetLiteralSingleQuotedLength(record.Bytes.Span);
        var length = action.Bytes.Length + 2L + digits + quotedLength;
        if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        var sourceMemory = SourceMemory.CreateIndependentReader(Memory.Budget, scope.Context.SourceMemory);
        try
        {
            using var builder = new ByteMemoryBuilder((int)length, sourceMemory.Budget);
            builder.Append(action.Bytes).Append((byte)' ').Append(indexText[..digits]).Append((byte)' ');
            if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(builder, record.Bytes.Span, quotedLength))
                throw new ByteMemoryBuilder.LimitExceededException();
            // Bash evaluates the callback with errexit active even if the mapfile status is tested.
            var context = scope.Context with { SourceMemory = sourceMemory, ErrexitIgnored = false };
            return await EvaluateCommandUnitsAsync(ShellSource.FromBytes(builder.DetachSnapshot()),
                scope with { Context = context, InArrayReaderCallback = true }, ScriptExecutionKind.ArrayReaderCallback)
                .ConfigureAwait(false);
        }
        finally { sourceMemory.Release(); }
    }

    private async Task<bool> AssignIntegerIndexedInputAsync(
        IndexedInputTarget target, List<ShellValue> values, IReadOnlyList<(int LastOffset, IMemoryScope Owner)>? borrowedBatches,
        uint? origin, ScopedEvaluation scope, ShellValue command)
    {
        // The longest rendered signed decimal uses 20 bytes. Include each value's backing
        // along with sparse storage while the input records and saved binding remain live.
        const int RecordMemoryBytes = IndexedArray.ElementMemoryBytes + 96;
        var prepaidBytes = checked((long)values.Count * RecordMemoryBytes);
        Memory.Charge(prepaidBytes);
        CheckCancellation();
        using var assignment = new Variables.IndexedInputAssignment(target.Owner, target.Name, origin.HasValue,
            resetAttributes: false, target.NamerefValue, Memory);
        var first = origin.GetValueOrDefault();
        var batchIndex = 0;
        for (var offset = 0; offset < values.Count; offset++)
        {
            CheckCancellation();
            var integer = await WordExpansion.EvaluateIntegerAssignmentAsync(values[offset], null, scope, command)
                .ConfigureAwait(false);
            if (borrowedBatches != null && batchIndex < borrowedBatches.Count)
            {
                values[offset] = ShellValue.Empty;
                var batch = borrowedBatches[batchIndex];
                if (offset == batch.LastOffset)
                {
                    batch.Owner.Dispose();
                    batchIndex++;
                }
            }
            if (!integer.HasValue)
            {
                // A shell arithmetic error leaves preceding writes visible to EXIT.
                // Refusal and cancellation throw through the staged assignment and restore the old binding.
                assignment.Commit();
                return false;
            }
            // Like ordinary mapfile input, the destination wraps at the unsigned 32-bit boundary.
            var position = IndexedArrayIndex.FromUnsigned(unchecked(first + (uint)offset));
            if (!target.Owner.TrySetResolvedArrayElement(target.Name, position, integer.Value, Memory, prepaidBytes))
                return false;
            // Keep consumed credit with the written positions and decimal values.
            prepaidBytes -= RecordMemoryBytes;
        }
        CheckCancellation();
        assignment.Commit();
        return true;
    }

    private enum IndexedInputTargetKind { ExistingOrNew, CircularNameref, UnassignedNameref }

    private readonly record struct IndexedInputTarget(
        BindingName Name, Variables Owner, IndexedInputTargetKind Kind, ShellValue? NamerefValue)
    {
        public void PrepareForPublication()
        {
            if (Kind == IndexedInputTargetKind.CircularNameref)
                Owner.RecreateDeclarationArray(Name, ArrayKind.Indexed);
            else if (Kind == IndexedInputTargetKind.UnassignedNameref)
                Owner.TryDeclareArray(Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue);
        }
    }

    /// <summary>Selects a whole indexed-array destination without altering its binding.</summary>
    private async Task<(IndexedInputTarget? Target, int ExitCode)> SelectIndexedInputTargetAsync(
        ShellValue command, ShellValue operand, ScopedEvaluation scope, bool warn)
    {
        if (!VariableReference.TryParse(ShellSource.FromBytes(operand.Bytes), Memory, out var reference)
            || reference.Kind != VariableReferenceKind.Name)
            return await InvalidTargetAsync(operand).ConfigureAwait(false);

        var target = reference.Name;
        var resolvedBinding = scope.Variables.TryResolveBinding(target, selectOwner: null, out var binding);
        var resolved = resolvedBinding ? binding.Name : target;
        var rawTarget = ShellValue.Empty;
        var invalidNameref = resolvedBinding && binding.TryGetInvalidNameref(out rawTarget);
        if (invalidNameref && !rawTarget.IsEmpty)
            return await InvalidTargetAsync(rawTarget).ConfigureAwait(false);
        if (resolvedBinding && binding.Reference.Kind != VariableReferenceKind.Name)
            return await InvalidTargetAsync(binding.Reference.ToValue(Memory)).ConfigureAwait(false);
        if (!resolved.IsIdentifier)
            return await InvalidTargetAsync(resolved.ToValue()).ConfigureAwait(false);

        // A local cycle retries the authored name in the global store without following its nameref.
        var variables = resolvedBinding ? binding.Owner : scope.Context.GetGlobalBindingOwner(target, scope.Variables);
        var recreatesCycle = !resolvedBinding && ReferenceEquals(variables, scope.Variables);
        var kind = recreatesCycle ? IndexedInputTargetKind.CircularNameref
            : resolvedBinding && (binding.IsUnsetNameref || invalidNameref)
                ? IndexedInputTargetKind.UnassignedNameref
                : IndexedInputTargetKind.ExistingOrNew;
        ShellValue? namerefValue = !resolvedBinding && !recreatesCycle
            && variables.TryGetNameref(resolved, out var raw) && !raw.IsEmpty ? raw : null;
        if (warn)
        {
            if (!resolvedBinding)
                await ReportCircularNameReferenceAsync(target, scope).ConfigureAwait(false);
            else if (kind == IndexedInputTargetKind.UnassignedNameref && !invalidNameref)
                await ReportRemovingNamerefAsync(target).ConfigureAwait(false);
        }
        if (!BuiltinDiagnosticsOpen)
            return (null, 0);

        // A cycle recreates the binding; otherwise readonly belongs to the selected array.
        if (!recreatesCycle && variables.IsReadonlyBinding(resolved))
        {
            await ReportBuiltinDiagnosticAsync(operand, "readonly variable", scope.Context).ConfigureAwait(false);
            return (null, 1);
        }
        if (variables.AssociativeArrays.ContainsKey(resolved))
        {
            await ReportBuiltinDiagnosticAsync([command],
                [operand, ShellValue.FromText(": not an indexed array")], scope.Context).ConfigureAwait(false);
            return (null, 1);
        }

        return (new IndexedInputTarget(resolved, variables, kind, namerefValue), 0);

        async Task<(IndexedInputTarget?, int)> InvalidTargetAsync(ShellValue name)
        {
            await WriteInvalidIdentifierDiagnosticAsync(command, name, _builtinDiagnostics,
                ClosedStderrHandling.AttemptWrite, GetBuiltinDiagnosticPrefix(scope.Context)).ConfigureAwait(false);
            return (null, 1);
        }
    }
}

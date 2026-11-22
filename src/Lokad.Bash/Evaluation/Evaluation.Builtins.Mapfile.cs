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
        var descriptor = GetStdInDescriptor();
        var delimiter = (byte)'\n';
        var trimDelimiter = false;
        uint count = 0;
        uint skip = 0;
        uint? origin = null;
        var options = new ShortOptionCursor(arguments);
        while (options.TryReadFlag(out var flag))
        {
            var option = flag.Span[0];
            if (option == (byte)'t')
            {
                trimDelimiter = true;
                continue;
            }
            if (option is not ((byte)'c' or (byte)'d' or (byte)'n' or (byte)'O' or (byte)'s' or (byte)'u'))
                return await InvalidArgumentsAsync(flag, ": invalid option").ConfigureAwait(false);
            if (!options.TryReadOperand(out var operand))
                return await InvalidArgumentsAsync(flag, ": option requires an argument").ConfigureAwait(false);

            if (option == (byte)'d')
                delimiter = operand.Bytes.IsEmpty ? (byte)0 : operand.Bytes.Span[0];
            else if (option == (byte)'u')
            {
                var (number, handle) = ResolveInputDescriptorOperand(operand.Bytes.Span);
                if (number == null || handle.IsClosed)
                {
                    await ReportInvalidInputDescriptorAsync(arguments[0], operand, number).ConfigureAwait(false);
                    return await FinishAsync(1).ConfigureAwait(false);
                }
                descriptor = handle;
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
                // The quantum affects callbacks only.
            }
        }
        if (options.RequestedHelp)
        {
            var name = arguments[0].Bytes.Span.SequenceEqual("readarray"u8) ? "readarray" : "mapfile";
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(arguments[0], BuiltinKind.Mapfile, name, negated)
                .ConfigureAwait(false);
        }

        var targetValue = options.ArgumentIndex < arguments.Count ? arguments[options.ArgumentIndex] : ShellValue.FromText("MAPFILE");
        var (selected, targetStatus) = await SelectIndexedInputTargetAsync(arguments[0], targetValue, variables, warn: true)
            .ConfigureAwait(false);
        if (selected is not { } target)
            return await FinishAsync(targetStatus).ConfigureAwait(false);
        var resolved = target.Name;

        var attributes = variables.GetAssignmentAttributes(resolved);
        var integer = (attributes & global::Lokad.Bash.Evaluation.Variables.BindingAttributes.Integer) != 0;
        var convertCase = (attributes & global::Lokad.Bash.Evaluation.Variables.BindingAttributes.Case) != 0;
        // Arithmetic consumes borrowed records after input settles. Ordinary records can
        // outlive their batch independently after publication.
        using var integerInputMemory = integer ? Memory.Budget.CreateScope() : null;
        var values = new List<ShellValue>();
        long stagedBytes = 0;
        using var inputMemory = Memory.Budget.Reserve(0);
        var assigned = false;
        try
        {
            // Bash treats an explicitly closed stdin as empty input; actual host read failures remain failures.
            var reachedEof = descriptor.IsClosed;
            while (!reachedEof && (count == 0 || (uint)values.Count < count))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var skipping = skip != 0;
                var remaining = count == 0 ? ReadBatchRecords : count - (uint)values.Count;
                var limit = (int)Math.Min(ReadBatchRecords, skipping ? skip : remaining);
                // A skipped batch never reaches arithmetic or array publication.
                var retainedInput = skipping ? null : integerInputMemory;
                var readMemory = retainedInput ?? Memory.Budget.CreateScope();
                using var batchMemory = retainedInput == null ? readMemory : null;
                if (delimiter == (byte)'\n')
                {
                    var batch = await ExecuteReadAsync(descriptor, limit, readMemory).ConfigureAwait(false);
                    batch.ThrowIfError();
                    AcceptBatch(batch.Content, batch.LineStartOffsets);
                    reachedEof = batch.ReachedEoF;
                }
                else
                {
                    var completion = new TaskCompletionSource<DescriptorRecordBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PublishOperation(new ReadRecordBatchOperation(completion, descriptor, delimiter, limit, readMemory));
                    var batch = await completion.Task.ConfigureAwait(false);
                    CheckCancellation();
                    batch.Result.ThrowIfError();
                    AcceptBatch(batch.Content, batch.RecordStartOffsets);
                    reachedEof = batch.Result.ReachedEoF;
                }
            }
            if (integer)
                return await FinishAsync(await AssignIntegerIndexedInputAsync(resolved, values, origin, scope, arguments[0])
                    .ConfigureAwait(false) ? 0 : 1).ConfigureAwait(false);

            // The staged values already have admission. The list and array coexist during publication.
            var retainedBytes = (long)values.Count * IndexedArray.ElementMemoryBytes;
            if (origin.HasValue && variables.Scalars.ContainsKey(resolved))
                retainedBytes += IndexedArray.ElementMemoryBytes;
            Memory.Charge(retainedBytes);
            stagedBytes += retainedBytes;

            _cancellationToken.ThrowIfCancellationRequested();
            // Publish only after the requested records have been read and admitted.
            target.PrepareForPublication(variables);
            if (origin is { } first)
                assigned = variables.TryUpdateResolvedIndexedArray(resolved, first, values, Memory, _cancellationToken);
            else
                assigned = variables.TryAssignResolvedIndexedArray(resolved, IndexedArray.FromSequential(values));
        }
        catch (ShellDescriptorReadException)
        {
            return await FinishAsync(1).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ByteMemoryBuilder.LimitExceededException or MemoryBudgetExceededException)
        {
            // Refuse before committing any collected records to the target variable.
            values.Clear();
            values.Capacity = 0;
            inputMemory.Dispose();
            integerInputMemory?.Dispose();
            Memory.Workspace.Resize(Memory.Workspace.Bytes - stagedBytes);
            Memory.ReleaseUnusedCapacity();
            return await RejectInputAsync().ConfigureAwait(false);
        }
        return await FinishAsync(assigned ? 0 : 1).ConfigureAwait(false);

        void AcceptBatch(ReadOnlyMemory<byte> content, IReadOnlyList<int> recordStarts)
        {
            if (skip != 0)
            {
                if (!content.IsEmpty)
                    skip = checked(skip - (uint)recordStarts.Count - 1);
                return;
            }

            var start = 0;
            for (var index = 0; start < content.Length; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var end = index < recordStarts.Count ? recordStarts[index] : content.Length;
                var record = content[start..end];
                start = end;
                // Admit geometric list growth, including replacement overlap, before allocation.
                if (values.Count == values.Capacity)
                {
                    var capacity = values.Capacity == 0 ? 4 : checked(2 * values.Capacity);
                    inputMemory.Resize((long)RecordListEntryBytes * capacity);
                    values.Capacity = capacity;
                }
                if (trimDelimiter && record.Span[^1] == delimiter)
                    record = record[..^1];
                var value = ShellValue.FromBytes(record).BeforeFirstNul();
                if (value.IsEmpty)
                    value = ShellValue.Empty;
                else if (!integer)
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
        }

        Task<EvaluationResult> FinishAsync(int status) => FinalizeResultAsync(
            EvaluationResult.FromExitCode(diagnostics.Result.IsError ? 1 : status), negated);

        async Task<EvaluationResult> RejectInputAsync()
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("mapfile"), ShellValue.FromText("shared memory budget exhausted")).ConfigureAwait(false);
            return await FinishAsync(2).ConfigureAwait(false);
        }

        async Task<EvaluationResult> InvalidArgumentsAsync(ReadOnlyMemory<byte> option, string detail)
        {
            // Advertise the supported subset, not Bash options whose behavior is intentionally absent.
            var commandName = arguments[0];
            var result = await WriteDiagnosticUnlessStderrClosedAsync([commandName],
                [ShellValue.FromText("-"), ShellValue.FromBytes(option), ShellValue.FromText(detail + "\n"),
                    commandName, ShellValue.FromText(": usage: "), commandName,
                    ShellValue.FromText(" " + BuiltinDocumentation.For(BuiltinKind.Mapfile).Arguments)], scope: null).ConfigureAwait(false);
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
            await ReportExpansionDiagnosticAsync([arguments[0]], [ShellValue.FromBytes(operand),
                ShellValue.FromText(detail)]).ConfigureAwait(false);
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
        var first = origin.GetValueOrDefault();
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
            // Like ordinary mapfile input, the destination wraps at the unsigned 32-bit boundary.
            var position = IndexedArrayIndex.FromUnsigned(unchecked(first + (uint)offset));
            if (!scope.Variables.TrySetResolvedArrayElement(name, position, integer.Value, Memory, prepaidBytes))
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
            await ReportExpansionDiagnosticAsync([command],
                [operand, ShellValue.FromText(": not an indexed array")]).ConfigureAwait(false);
            return (null, 1);
        }

        return (new IndexedInputTarget(resolved, kind), 0);

        async Task<(IndexedInputTarget?, int)> InvalidTargetAsync(ShellValue name)
        {
            await WriteInvalidIdentifierDiagnosticAsync(command, name, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite)
                .ConfigureAwait(false);
            return (null, 1);
        }
    }
}

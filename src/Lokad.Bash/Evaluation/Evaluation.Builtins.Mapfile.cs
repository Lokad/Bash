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
        IReadOnlyList<ShellValue> arguments, Variables variables, bool negated)
    {
        const int ReadBatchLines = 128;
        const int RecordListEntryBytes = 64; // Includes list capacity and resize overlap.
        using var diagnostics = new BuiltinDiagnosticScope(this);
        var targetValue = ShellValue.FromText("MAPFILE");
        var delimiterPolicy = MapfileDelimiterPolicy.Keep;
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
                    if (bytes[optionIndex] == (byte)'t')
                        continue;
                    return await InvalidArgumentsAsync(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                        .Append((byte)'-').Append(bytes[optionIndex]).Append(": invalid option"u8).ToMemory())).ConfigureAwait(false);
                }

                delimiterPolicy = MapfileDelimiterPolicy.TrimNewline;
                continue;
            }

            if (targetSeen)
                continue;

            targetValue = argument;
            targetSeen = true;
            parseOptions = false;
        }

        if (!VariableReference.TryParse(ShellSource.FromBytes(targetValue.Bytes), Memory, out var reference)
            || reference.Kind != VariableReferenceKind.Name)
            return await InvalidTargetAsync(targetValue).ConfigureAwait(false);

        var target = reference.Name;
        var resolvedBinding = variables.TryResolveBinding(target, selectOwner: null, out var binding);
        BindingName resolved = resolvedBinding ? binding.Name : target;
        if (resolvedBinding && binding.Reference.Kind != VariableReferenceKind.Name)
            return await InvalidTargetAsync(binding.Reference.ToValue(Memory)).ConfigureAwait(false);
        if (!resolved.IsIdentifier)
            return await InvalidTargetAsync(resolved.ToValue()).ConfigureAwait(false);

        var unsetNameref = resolvedBinding && variables.TryGetNameref(resolved, out var referenced) && referenced.IsEmpty;
        if (!resolvedBinding)
            await ReportCircularNameReferenceAsync(target).ConfigureAwait(false);
        else if (unsetNameref)
            await ReportRemovingNamerefAsync(target).ConfigureAwait(false);
        if (!diagnostics.Result.CanAcceptMore)
            return await FinishAsync(0).ConfigureAwait(false);

        // A cycle recreates the binding; otherwise readonly belongs to the selected array, not its alias.
        if (resolvedBinding && variables.IsReadonly(resolved))
        {
            await ReportExpansionDiagnosticAsync(targetValue, ShellValue.FromText("readonly variable")).ConfigureAwait(false);
            return await FinishAsync(1).ConfigureAwait(false);
        }
        if (variables.AssociativeArrays.ContainsKey(resolved))
        {
            await ReportExpansionDiagnosticAsync(arguments[0],
                ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append(targetValue.Bytes)
                    .Append(": not an indexed array"u8).ToMemory())).ConfigureAwait(false);
            return await FinishAsync(1).ConfigureAwait(false);
        }

        var descriptor = GetStdInDescriptor();
        using var valuesMemory = Memory.Budget.CreateScope();
        // Keep one owned copy per batch; array entries borrow slices for the same lifetime.
        using var reader = new AsyncLineReader(
            (limit, memory, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                return new ValueTask<ReadResult>(AwaitOperationAsync(
                    ReadLineOperation.AsTask(PublishOperation, descriptor, limit, memory, reusableLine: null)));
            },
            _cancellationToken,
            batchSize: ReadBatchLines, valuesMemory);
        var values = new List<ShellValue>();
        long backingBytes = 0;
        using var inputMemory = Memory.Budget.Reserve(0);
        var assigned = false;
        try
        {
            // Bash treats an explicitly closed stdin as empty input; actual host read failures remain failures.
            while (!descriptor.IsClosed && !await reader.ReachedEof().ConfigureAwait(false))
            {
                var line = reader.ReadLine();
                // Even a short/NUL-truncated record pins its batch. Keep that backing charged
                // until the array is replaced, without tracking individual slices or aliases.
                backingBytes = checked(backingBytes + global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(
                    ShellValue.FromBytes(line.Raw)));
                // Admit geometric list growth, including replacement overlap, before allocation.
                if (values.Count == values.Capacity)
                {
                    var capacity = values.Capacity == 0 ? 4 : checked(2 * values.Capacity);
                    inputMemory.Resize((long)RecordListEntryBytes * capacity);
                    values.Capacity = capacity;
                }
                var record = delimiterPolicy == MapfileDelimiterPolicy.TrimNewline ? line.Content : line.Raw;
                record = ShellValue.FromBytes(record).BeforeFirstNul().Bytes;
                values.Add(ShellValue.FromBytes(record));
            }
            // Admit both collection representations while building the persistent array.
            long valueBytes = 0;
            foreach (var value in values)
                valueBytes = checked(valueBytes + global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(value));
            var retainedBytes = checked((long)values.Count * IndexedArray.ElementMemoryBytes + backingBytes + valueBytes);
            Memory.Charge(retainedBytes);
            var array = IndexedArray.FromSequential(values);
            array.RetainedBackingBytes = backingBytes;

            _cancellationToken.ThrowIfCancellationRequested();
            // Preserve even the original reference binding until every record has been read successfully.
            if (!resolvedBinding)
                variables.RecreateDeclarationArray(resolved, ArrayKind.Indexed);
            else if (unsetNameref)
                variables.TryDeclareArray(resolved, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue);
            assigned = variables.TryAssignResolvedIndexedArray(resolved, array);
        }
        catch (ShellDescriptorReadException)
        {
            return await FinishAsync(1).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ByteMemoryBuilder.LimitExceededException or MemoryBudgetExceededException)
        {
            // Refuse before committing any collected records to the target variable.
            values.Clear();
            reader.Dispose();
            valuesMemory.Dispose();
            return await RejectInputAsync().ConfigureAwait(false);
        }
        return await FinishAsync(assigned ? 0 : 1).ConfigureAwait(false);

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

        async Task<EvaluationResult> InvalidTargetAsync(ShellValue name)
        {
            await ReportExpansionDiagnosticAsync(arguments[0], ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                .Append(ShellArgumentEscaping.QuoteDiagnosticOperand(name).Bytes).Append(": not a valid identifier"u8).ToMemory())).ConfigureAwait(false);
            return await FinishAsync(1).ConfigureAwait(false);
        }
    }

    private enum MapfileDelimiterPolicy
    {
        Keep,
        TrimNewline
    }
}

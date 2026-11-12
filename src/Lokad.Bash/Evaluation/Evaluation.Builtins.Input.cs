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

    // Each evaluator owns its workspace: pipeline stages and command substitutions may overlap.
    private sealed class ReadWorkspace(IMemoryBudget budget) : IDisposable
    {
        public ByteMemoryBuilder Content { get; } = new(0, budget);
        public ByteMemoryBuilder? PhysicalLine { get; set; }
        public MemoryCapacity RecordMemory { get; } = new(budget, 0);
        public MemoryCapacity FieldListMemory { get; } = new(budget, 0);
        public MemoryCapacity DecodedFieldMemory { get; } = new(budget, 0);

        public void Dispose()
        {
            Content.Dispose();
            PhysicalLine?.Dispose();
            RecordMemory.Dispose();
            FieldListMemory.Dispose();
            DecodedFieldMemory.Dispose();
        }
    }

    private void ReleaseReadWorkspace()
    {
        var memory = _readWorkspace;
        _readWorkspace = null;
        memory?.Dispose();
    }

    /// <summary>Reads one logical record, then selects and writes each destination in operand order.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinReadAsync(
        IReadOnlyList<ShellValue> words, ScopedEvaluation scope, bool negated)
    {
        using var diagnostics = new BuiltinDiagnosticScope(this);
        static string Usage() => "read: usage: " + BuiltinDocumentation.For(BuiltinKind.Read).Synopsis("read");
        var argumentIndex = 1;
        var backslashMode = ReadBackslashMode.InterpretEscapes;
        var delimiter = (byte)'\n';
        var inputNumber = LogicalDescriptor.StdIn;
        var inputDescriptor = GetStdInDescriptor();
        while (argumentIndex < words.Count)
        {
            var option = words[argumentIndex].Bytes;
            if (option.Span.SequenceEqual("--"u8))
            {
                argumentIndex++;
                break;
            }
            if (option.Length < 2 || option.Span[0] != (byte)'-')
                break;
            for (var index = 1; index < option.Length; index++)
            {
                if (option.Span[index] == (byte)'r')
                {
                    backslashMode = ReadBackslashMode.Preserve;
                    continue;
                }
                var flag = option.Span[index];
                if (flag is not ((byte)'u' or (byte)'d'))
                    return await FailAsync(
                        ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append("-"u8).Append(option.Slice(index, 1))
                            .Append(Utf8Text.Encode(": invalid option\n" + Usage())).ToMemory()), 2).ConfigureAwait(false);

                ShellValue operand;
                if (index + 1 < option.Length)
                    operand = ShellValue.FromBytes(option[(index + 1)..]);
                else if (++argumentIndex < words.Count)
                    operand = words[argumentIndex];
                else
                    return await FailAsync(ShellValue.FromText($"-{(char)flag}: option requires an argument\n" + Usage()), 2)
                        .ConfigureAwait(false);

                if (flag == (byte)'d')
                {
                    delimiter = operand.Bytes.IsEmpty ? (byte)0 : operand.Bytes.Span[0];
                    break;
                }

                // Unlike redirection syntax, read accepts a sign and leading ASCII whitespace,
                // but only space/tab after the decimal descriptor number.
                if (!Integers.TryParseNumber(
                        operand.Bytes.Span.TrimStart(" \t\r\n\v\f"u8).TrimEnd(" \t"u8), out var number)
                    || number is < 0 or > int.MaxValue)
                    return await FailAsync(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append(operand.Bytes)
                        .Append(": invalid file descriptor specification"u8).ToMemory()), 1)
                        .ConfigureAwait(false);

                // Borrow the existing mapping; a logical number never exposes an opaque host handle.
                inputNumber = new LogicalDescriptor((int)number);
                inputDescriptor = _state.CurrentRedirectionTable?.Resolve(inputNumber)
                    ?? CaptureDescriptorBindings().Resolve(inputNumber);
                if (inputDescriptor.IsClosed)
                    return await FailAsync(ShellValue.FromText(inputNumber
                        + ": invalid file descriptor: Bad file descriptor"), 1).ConfigureAwait(false);
                break;
            }
            argumentIndex++;
        }

        var implicitReply = argumentIndex == words.Count;
        var fieldCount = implicitReply ? 1 : words.Count - argumentIndex;
        var firstName = implicitReply ? ShellValue.FromText("REPLY") : words[argumentIndex];
        if (!TryParseTarget(firstName, out var firstReference))
            return await InvalidTargetAsync(firstName).ConfigureAwait(false);

        var memory = _readWorkspace ??= new ReadWorkspace(Memory.Budget);
        memory.Content.Truncate(0);
        memory.FieldListMemory.Resize(0);
        memory.DecodedFieldMemory.Resize(0);
        var exitCode = 0;
        try
        {
            var input = await CollectInputAsync().ConfigureAwait(false);
            if (input.Failed)
                return await FinishAsync(input.ExitCode).ConfigureAwait(false);
            exitCode = input.ExitCode;
            var fields = ReadFieldSplitter.Split(input.Content, fieldCount, new ReadBuiltinOptions(
                FieldSeparators.FromVariables(scope.Variables), backslashMode,
                implicitReply ? ReadFieldMode.PreserveInput : ReadFieldMode.Split), memory.FieldListMemory, memory.DecodedFieldMemory);
            for (var index = 0; index < fieldCount; index++)
            {
                var name = implicitReply ? firstName : words[argumentIndex + index];
                var reference = firstReference;
                if (index != 0 && !TryParseTarget(name, out reference))
                    return await InvalidTargetAsync(name).ConfigureAwait(false);
                // Each retained field owns a compact copy. Command scratch covers publication;
                // the variable estimate keeps accepted fields charged after the command ends.
                Memory.Charge(global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(fields[index]));
                var copy = fields[index].Bytes.ToArray();
                var assignment = await AssignBuiltinAsync(reference, ShellValue.FromBytes(copy), scope,
                    persistentOutputs: null, BuiltinOutputVisibility.Caller, ShellValue.FromText("read")).ConfigureAwait(false);
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
            if (memory.Content.Capacity > ReadScratchRetentionBytes
                || memory.RecordMemory.Bytes > 2 * ReadScratchRetentionBytes
                || memory.PhysicalLine?.Capacity > ReadScratchRetentionBytes
                || memory.FieldListMemory.Bytes > ReadScratchRetentionBytes
                || memory.DecodedFieldMemory.Bytes > ReadScratchRetentionBytes)
                ReleaseReadWorkspace();
        }
        return await FinishAsync(exitCode).ConfigureAwait(false);

        Task ReportInputRefusalAsync() => ReportExpansionDiagnosticAsync(ShellValue.FromText("read"),
            ShellValue.FromText("shared memory budget exhausted"));

        Task<EvaluationResult> FinishAsync(int status) => FinalizeResultAsync(
            EvaluationResult.FromExitCode(diagnostics.Result.IsError ? 1 : status), negated);

        async Task<EvaluationResult> FailAsync(ShellValue message, int status)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("read"), message).ConfigureAwait(false);
            return await FinishAsync(status).ConfigureAwait(false);
        }

        bool TryParseTarget(ShellValue name, out VariableReference reference) =>
            VariableReference.TryParse(ShellSource.FromBytes(name.Bytes), Memory, out reference)
            && (reference.Kind == VariableReferenceKind.Name || !reference.Subscript.IsEmpty);

        async Task<EvaluationResult> InvalidTargetAsync(ShellValue name)
        {
            return await FailAsync(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                .Append(ShellArgumentEscaping.QuoteDiagnosticOperand(name).Bytes)
                .Append(": not a valid identifier"u8).ToMemory()), 1).ConfigureAwait(false);
        }

        async Task<(int ExitCode, ReadOnlyMemory<byte> Content, bool Failed)> CollectInputAsync()
        {
            if (delimiter != (byte)'\n')
                return await CollectDelimitedInputAsync().ConfigureAwait(false);

            var content = memory.Content;
            var physicalLine = memory.PhysicalLine ??= new ByteMemoryBuilder(0, Memory.Budget);
            try
            {
                var exitCode = 0;
                while (true)
                {
                    ReadResult result;
                    try
                    {
                        result = await AwaitOperationAsync(ReadLineOperation.AsTask(PublishOperation,
                            inputDescriptor, 1, destinationBudget: null, reusableLine: physicalLine)).ConfigureAwait(false);
                    }
                    catch (ByteMemoryBuilder.LimitExceededException)
                    {
                        // A single physical line can exhaust the descriptor's buffer before it returns.
                        return await RejectInputAsync().ConfigureAwait(false);
                    }
                    if (result.IsError)
                    {
                        if (result.Error is { } error)
                            await ReportExpansionDiagnosticAsync(ShellValue.FromText("read"),
                                ShellValue.FromText($"read error: {inputNumber}: {error.MessageWithoutTerminator}"))
                                .ConfigureAwait(false);
                        return (1, default, true);
                    }

                    if (result.Content.IsEmpty)
                    {
                        exitCode = 1;
                        break;
                    }

                    // Decide physical-line continuation before discarding NULs: they can separate
                    // a backslash from LF or interrupt a run of backslashes.
                    var line = result.Content;
                    if (backslashMode == ReadBackslashMode.InterpretEscapes
                        && TryGetContinuationContentLength(line, out var continuationContentLength))
                    {
                        ShellValue.FromBytes(line[..continuationContentLength]).AppendWithoutNuls(content);
                        if (result.ReachedEoF)
                        {
                            exitCode = 1;
                            break;
                        }
                        continue;
                    }

                    ShellValue.FromBytes(line).AppendWithoutNuls(content);
                    if (result.ReachedEoF && (line.Length == 0 || line.Span[^1] != '\n'))
                        exitCode = 1;

                    break;
                }

                var collected = content.ToMemory();
                if (!collected.IsEmpty && collected.Span[^1] == (byte)'\n')
                    collected = collected[..^1];
                return (exitCode, collected, false);
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
                var content = memory.Content;
                var remaining = ByteMemoryBuilder.MaxMemoryBuilderBytes;
                var status = 1;
                while (true)
                {
                    memory.RecordMemory.Resize(0);
                    var completion = new TaskCompletionSource<ReadRecordResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PublishOperation(new ReadRecordOperation(completion, inputDescriptor, delimiter, remaining, memory.RecordMemory));
                    var result = await AwaitOperationAsync(completion.Task).ConfigureAwait(false);
                    if (result.Record is { ExceedsLimit: true })
                        return await RejectInputAsync().ConfigureAwait(false);
                    if (result.Failure.Error is { } error)
                        return await ReadFailureAsync(error).ConfigureAwait(false);
                    if (result.Record is not { } record)
                        break;

                    var sourceBytes = record.Content.Length + (record.HasTerminator ? 1 : 0);
                    if (sourceBytes > remaining)
                        return await RejectInputAsync().ConfigureAwait(false);
                    remaining -= sourceBytes;
                    content.Append(record.Content);
                    if (record.HasTerminator && backslashMode == ReadBackslashMode.InterpretEscapes
                        && (delimiter == (byte)'\\' || HasTrailingEscape(record.Content)))
                    {
                        content.Append(delimiter);
                        continue;
                    }
                    status = record.HasTerminator ? 0 : 1;
                    break;
                }

                return (status, NormalizeInput(content.ToMemory()), false);

                ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> input)
                {
                    if (backslashMode == ReadBackslashMode.Preserve)
                    {
                        if (!input.Span.Contains((byte)0))
                            return input;
                        var unquoted = GetScratch();
                        ShellValue.FromBytes(input).AppendWithoutNuls(unquoted);
                        return unquoted.ToMemory();
                    }

                    ByteMemoryBuilder? cleaned = null;
                    var start = 0;
                    for (var index = 0; index < input.Length; index++)
                    {
                        var special = input.Span[index..].IndexOfAny((byte)0, (byte)'\\');
                        if (special < 0)
                            break;
                        index += special;
                        var removedBytes = 1;
                        if (input.Span[index] != 0)
                        {
                            if (index + 1 == input.Length)
                                break;
                            // Keep the quoted NUL through field splitting; discard the following input.
                            if (input.Span[index + 1] == 0)
                            {
                                if (cleaned is null)
                                    return input[..(index + 2)];
                                cleaned.Append(input[start..(index + 2)]);
                                return cleaned.ToMemory();
                            }
                            if (input.Span[index + 1] != (byte)'\n')
                            {
                                index++;
                                continue;
                            }
                            removedBytes = 2;
                        }
                        cleaned ??= GetScratch();
                        cleaned.Append(input[start..index]);
                        start = index + removedBytes;
                        index = start - 1;
                    }
                    if (cleaned is null)
                        return input;
                    cleaned.Append(input[start..]);
                    return cleaned.ToMemory();

                    ByteMemoryBuilder GetScratch()
                    {
                        var scratch = memory.PhysicalLine ??= new ByteMemoryBuilder(0, Memory.Budget);
                        scratch.Truncate(0);
                        return scratch;
                    }
                }
            }

            async Task<(int ExitCode, ReadOnlyMemory<byte> Content, bool Failed)> ReadFailureAsync(OperationError error)
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText("read"),
                    ShellValue.FromText($"read error: {inputNumber}: {error.MessageWithoutTerminator}"))
                    .ConfigureAwait(false);
                return (1, default, true);
            }

            async Task<(int ExitCode, ReadOnlyMemory<byte> Content, bool Failed)> RejectInputAsync()
            {
                ReleaseReadWorkspace();
                await ReportInputRefusalAsync().ConfigureAwait(false);
                return (1, default, true);
            }

            static bool TryGetContinuationContentLength(ReadOnlyMemory<byte> line, out int contentLength)
            {
                contentLength = 0;
                if (line.Length < 2 || line.Span[^1] != '\n')
                    return false;

                if (!HasTrailingEscape(line[..^1]))
                    return false;

                contentLength = line.Length - 2;
                return true;
            }

            static bool HasTrailingEscape(ReadOnlyMemory<byte> content)
            {
                var count = 0;
                for (var index = content.Length - 1; index >= 0 && content.Span[index] == (byte)'\\'; index--)
                    count++;
                return count % 2 != 0;
            }
        }
    }

    /// <summary>
    /// Handles the <c>getopts</c> builtin by parsing option state and setting the target variable.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinGetoptsAsync(
        IReadOnlyList<ShellValue> words,
        Variables commandVars,
        Variables.CommandBindings? bindings,
        EvaluationContext context,
        bool negated)
    {
        if (words.Count < 3)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        var optstring = words[1];
        if (!words[2].TryGetUtf8Text(out var name) || !VariableNames.IsValidVariableName(name.AsSpan()))
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        IReadOnlyList<ShellValue> arguments = commandVars.PositionalParameters;
        if (words.Count > 3)
        {
            var explicitArguments = new List<ShellValue>(words.Count - 3);
            for (var index = 3; index < words.Count; index++)
                explicitArguments.Add(words[index]);
            arguments = explicitArguments;
        }

        var exitCode = ExecuteGetopts();
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);

        bool TryAssignOutput(string target, ShellValue value) =>
            TryAssignBuiltinOutput(target, value, commandVars, bindings, context);

        int ExecuteGetopts()
        {
            var result = 0;
            var optind = 1;
            if (commandVars.TryGetScalar("OPTIND", out var optindValue)
                && Integers.TryParseExitStatus(optindValue.Bytes.Span, out var parsed)
                && parsed > 0)
            {
                optind = parsed;
            }

            if (optind > arguments.Count || arguments.Count == 0)
            {
                if (!TryAssignOutput(name, ShellValue.FromText("?")))
                    result = 1;
                if (!TryAssignOutput("OPTIND", ShellValue.FromText(FormatExitCode(optind))))
                    result = 1;
                return result == 0 ? 1 : result;
            }

            var current = arguments[optind - 1];
            if (current.Bytes.Length < 2 || current.Bytes.Span[0] != (byte)'-')
            {
                if (!TryAssignOutput(name, ShellValue.FromText("?")))
                    result = 1;
                if (!TryAssignOutput("OPTIND", ShellValue.FromText(FormatExitCode(optind))))
                    result = 1;
                return result == 0 ? 1 : result;
            }

            var option = current.Bytes.Span[1];
            if (!optstring.Bytes.Span.Contains(option))
            {
                if (!TryAssignOutput(name, ShellValue.FromText("?")))
                    result = 1;
                if (!TryAssignOutput("OPTIND", ShellValue.FromText(FormatExitCode(optind + 1))))
                    result = 1;
                return result == 0 ? 1 : result;
            }

            if (!TryAssignOutput(name, ShellValue.FromBytes(new byte[] { option })))
                result = 1;
            if (!TryAssignOutput("OPTIND", ShellValue.FromText(FormatExitCode(optind + 1))))
                result = 1;
            return result;
        }
    }

}

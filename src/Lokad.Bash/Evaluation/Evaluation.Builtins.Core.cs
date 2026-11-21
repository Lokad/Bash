using System;
using Lokad.Bash.Memory;
using System.Collections.Generic;
using System.Text;
using System.Text.Unicode;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Formatting;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private static readonly ReadOnlyMemory<byte> EmptyHashTableOutput = "hash: hash table empty\n"u8.ToArray();

    /// <summary>
    /// Handles the <c>test</c> and <c>[</c> builtins, evaluating predicates and filesystem tests as needed.
    /// </summary>
    /// <param name="commandName">The resolved command name.</param>
    /// <param name="words">The completed command name and operands.</param>
    /// <param name="scope">The active builtin variable bindings.</param>
    /// <param name="negated">Whether to negate the final exit status.</param>
    /// <returns>
    /// The finalized evaluation result.
    /// </returns>
    private async Task<EvaluationResult> EvaluateBuiltinTestAsync(
        string commandName,
        IReadOnlyList<ShellValue> words,
        ScopedEvaluation scope,
        bool negated)
    {
        var argumentLimit = words.Count;
        if (commandName == "[")
        {
            if (words.Count < 2 || !words[^1].Bytes.Span.SequenceEqual("]"u8))
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("missing `]'"))
                    .ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated);
            }

            argumentLimit--;
        }

        Memory.Charge(checked(128L * argumentLimit));
        var evaluation = TestEvaluation.ParseValues(words, firstArgument: 1, argumentLimit, _cancellationToken,
            _state.ErrexitEnabled, Memory);
        bool? mixed = null;
        if (evaluation.HasVariablePredicates)
        {
            mixed = await AwaitWordExpansionAsync(evaluation.EvaluateVariablePredicatesAsync(scope, TestFileAsync))
                .ConfigureAwait(false);
            if (!scope.CanExpand)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
        if (evaluation.HasParseError)
        {
            var name = ShellValue.FromText(commandName);
            var message = ShellValue.FromText(evaluation.ErrorMessage);
            if (evaluation.ErrorOperand is { } errorOperand)
                await ReportExpansionDiagnosticAsync([name], [errorOperand, ShellValue.FromText(": "), message]).ConfigureAwait(false);
            else
                await ReportExpansionDiagnosticAsync(name, message).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated);
        }

        if (mixed is { } matched)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(matched ? 0 : 1), negated).ConfigureAwait(false);
        var fileTests = evaluation.CollectFileTests(out var result);
        if (result.HasValue)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(result.Value ? 0 : 1), negated);

        var exitCode = await ExecuteFileTestsForEvaluationAsync(fileTests, evaluation.CompleteFileTests)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);

        async Task<bool> TestFileAsync(UnresolvedFileTest file) =>
            await ExecuteFileTestsForEvaluationAsync([file], results => results[0]).ConfigureAwait(false) == 0;
    }

    /// <summary>
    /// Writes the prepared <c>echo</c> bytes to stdout.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinEchoAsync(
        ReadOnlyMemory<byte> content,
        bool negated)
    {
        var write = content.IsEmpty ? DescriptorWriteResult.Open
            : await ExecuteWriteAsync(GetStdOutDescriptor(), content).ConfigureAwait(false);
        if (write.IsError)
        {
            using var diagnostics = new BuiltinDiagnosticScope(this);
            await ReportBuiltinWriteFailureAsync(ShellValue.FromText("echo"), write).ConfigureAwait(false);
        }
        var exitCode = write.IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    /// <summary>Handles reset and listing against the emulator's empty command cache.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinHashAsync(IReadOnlyList<ShellValue> words)
    {
        var command = words[0];
        var options = new ShortOptionCursor(words);
        var silent = false;
        while (options.TryReadFlag(out var flag))
        {
            CheckCancellation();
            if (flag.Span[0] is (byte)'l' or (byte)'r')
            {
                silent = true;
                continue;
            }

            await WriteInvalidShortOptionUnlessStderrClosedAsync(command, flag, BuiltinKind.Hash, "hash")
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated: false).ConfigureAwait(false);
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Hash, "hash", negated: false)
                .ConfigureAwait(false);

        var status = options.ArgumentIndex < words.Count ? 1 : 0;
        if (status == 0 && !silent)
        {
            var write = await WriteBuiltinOutputAsync(command, EmptyHashTableOutput).ConfigureAwait(false);
            status = write.IsError ? 1 : 0;
        }
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the <c>type</c> builtin by reporting how each name would be resolved.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinTypeAsync(
        IReadOnlyList<ShellValue> words,
        Variables commandVars,
        EvaluationContext context,
        bool negated)
    {
        var outputMode = CommandQueryOutputMode.TypeDescription;
        var lookupMode = CommandLookupMode.Normal;
        var nameIndex = 1;
        for (; nameIndex < words.Count; nameIndex++)
        {
            var operand = words[nameIndex].Bytes;
            if (operand.Length < 2 || operand.Span[0] != (byte)'-')
                break;
            if (operand.Span.SequenceEqual("--"u8))
            {
                nameIndex++;
                break;
            }
            if (operand.Span.SequenceEqual("--help"u8))
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Type, "type", negated)
                    .ConfigureAwait(false);
            for (var optionIndex = 1; optionIndex < operand.Length; optionIndex++)
            {
                switch (operand.Span[optionIndex])
                {
                    case (byte)'t': outputMode = CommandQueryOutputMode.TypeKind; break;
                    case (byte)'p': outputMode = CommandQueryOutputMode.TypePath; break;
                    case (byte)'P':
                        lookupMode = CommandLookupMode.HostOnly;
                        outputMode = CommandQueryOutputMode.TypePath;
                        break;
                    case (byte)'f':
                        if (lookupMode == CommandLookupMode.Normal)
                            lookupMode = CommandLookupMode.BypassFunctions;
                        break;
                    default: return await InvalidOptionAsync(ShellValue.FromBytes(operand.Slice(optionIndex, 1)))
                        .ConfigureAwait(false);
                }
            }
        }

        var exitCode = await EvaluateCommandQueryAsync(
                BuiltinKind.Type,
                words,
                nameIndex,
                outputMode,
                lookupMode,
                commandVars,
                context)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);

        async Task<EvaluationResult> InvalidOptionAsync(ShellValue option)
        {
            await WriteDiagnosticAsync(GetStdErrDescriptor(), [ShellValue.FromText("type")], [ShellValue.FromText("-"),
                option, ShellValue.FromText(": invalid option\ntype: usage: "
                    + BuiltinDocumentation.For(BuiltinKind.Type).Synopsis("type"))], scope: null).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }
    }

    /// <summary> Evaluates a command lookup and formats its requested query output. </summary>
    private async Task<int> EvaluateCommandQueryAsync(
        BuiltinKind builtin,
        IReadOnlyList<ShellValue> arguments,
        int firstName,
        CommandQueryOutputMode outputMode,
        CommandLookupMode lookupMode,
        Variables vars,
        EvaluationContext context)
    {
        var builtinName = ShellValue.FromText(builtin == BuiltinKind.Command ? "command" : "type");
        var nameCount = arguments.Count - firstName;
        if (nameCount == 0)
            return 0;

        // Cover the resolution reference and record, output offset, and function-owner
        // list capacity including growth. Definitions and host result text stay borrowed.
        const long ResolutionBytesPerName = 72;
        CheckCancellation();
        using var resolutionMemory = new MemoryCapacity(Memory.Budget, 128 + ResolutionBytesPerName * nameCount);
        List<FunctionBinding>? functionOwners = null;
        try
        {
            var resolutions = await ResolveCommandQueriesAsync().ConfigureAwait(false);
            using var output = new ByteMemoryBuilder(0, Memory.Budget);
            try
            {
                // Build the whole display payload before emitting any channel; endpoints retain operand order.
                var endOffsets = new int[nameCount];
                var foundCount = 0;
                var unresolvedCount = 0;
                var lookupFailed = false;
                for (var index = 0; index < nameCount; index++)
                {
                    CheckCancellation();
                    var name = arguments[firstName + index];
                    var resolution = resolutions[index] ?? throw new InvalidOperationException("Command query was not resolved.");
                    if (resolution is CommandQueryResolution.Missing or CommandQueryResolution.Failed)
                    {
                        unresolvedCount++;
                        var error = (resolution as CommandQueryResolution.Failed)?.Error;
                        lookupFailed |= error.HasValue;
                        if ((error.HasValue || outputMode == CommandQueryOutputMode.TypeDescription)
                            && (!output.TryAppend(builtinName.Bytes.Span) || !output.TryAppend(": "u8)
                                || !output.TryAppend(name.Bytes.Span) || !output.TryAppend(": "u8)
                                || !(error?.Message is { } message
                                    ? AppendText(message) : output.TryAppend("not found\n"u8))))
                            return await OutputLimitAsync().ConfigureAwait(false);

                        endOffsets[index] = output.Length;
                        continue;
                    }

                    foundCount++;
                    if (outputMode == CommandQueryOutputMode.TypePath && resolution is not CommandQueryResolution.Host)
                    {
                        endOffsets[index] = output.Length;
                        continue;
                    }
                    var appended = outputMode switch
                    {
                        CommandQueryOutputMode.CommandName or CommandQueryOutputMode.TypePath =>
                            resolution is CommandQueryResolution.Host hostCommand
                            ? AppendText(hostCommand.ResolvedName)
                            : output.TryAppend(name.Bytes.Span),
                        CommandQueryOutputMode.TypeKind => output.TryAppend(resolution switch
                            {
                                CommandQueryResolution.Function => "function"u8,
                                CommandQueryResolution.Builtin => "builtin"u8,
                                CommandQueryResolution.Keyword => "keyword"u8,
                                CommandQueryResolution.Host => "file"u8,
                                _ => throw new InvalidOperationException("Missing command reached found query output.")
                            }),
                        CommandQueryOutputMode.TypeDescription => output.TryAppend(name.Bytes.Span) && (resolution switch
                            {
                                CommandQueryResolution.Function => output.TryAppend(" is a function"u8),
                                CommandQueryResolution.Builtin => output.TryAppend(" is a shell builtin"u8),
                                CommandQueryResolution.Keyword => output.TryAppend(" is a shell keyword"u8),
                                CommandQueryResolution.Host hostDescription => output.TryAppend(" is "u8) && AppendText(hostDescription.ResolvedName),
                                _ => throw new InvalidOperationException("Missing command reached found query output.")
                            }),
                        _ => throw new ArgumentOutOfRangeException(nameof(outputMode))
                    };
                    if (!appended || !output.TryAppend((byte)'\n')
                        || outputMode == CommandQueryOutputMode.TypeDescription && resolution is CommandQueryResolution.Function function
                            && !FunctionDefinitionFormatter.TryAppend(function.Definition, output))
                        return await OutputLimitAsync().ConfigureAwait(false);
                    endOffsets[index] = output.Length;
                }

                var queryExitCode = lookupFailed ? 1 : builtin == BuiltinKind.Command
                    ? foundCount > 0 ? 0 : 1
                    : unresolvedCount == 0 ? 0 : 1;
                if (output.Length == 0)
                    return queryExitCode;

                var content = output.ToMemory();
                var startOffset = 0;
                var appendResult = DescriptorWriteResult.Open;
                var stdout = GetStdOutDescriptor();
                var stderr = GetStdErrDescriptor();
                var outputClosed = false;
                var errorClosed = false;
                for (var first = 0; first < nameCount;)
                {
                    var diagnostic = IsDiagnostic(first);
                    var last = first;
                    while (last + 1 < nameCount && IsDiagnostic(last + 1) == diagnostic)
                        last++;

                    var endOffset = endOffsets[last];
                    if (endOffset > startOffset && !(diagnostic ? errorClosed : outputClosed))
                    {
                        var descriptor = diagnostic ? stderr : stdout;
                        var result = await ExecuteWriteAsync(
                                descriptor,
                                content[startOffset..endOffset])
                            .ConfigureAwait(false);
                        // Downstream closure is final for this descriptor, including merged stdout/stderr.
                        if (!result.CanAcceptMore && !result.IsError)
                        {
                            outputClosed |= descriptor == stdout;
                            errorClosed |= descriptor == stderr;
                        }
                        // A failed diagnostic must not suppress later names or replace the query's status.
                        if (!diagnostic && !appendResult.IsError)
                            appendResult = result;
                    }
                    startOffset = endOffset;
                    first = last + 1;
                }
                if (builtin == BuiltinKind.Type && !errorClosed)
                    await ReportBuiltinWriteFailureAsync(builtinName, appendResult).ConfigureAwait(false);
                // Unlike type, command -v/-V report resolution success even when stdout cannot be written.
                return builtin == BuiltinKind.Command || !appendResult.IsError ? queryExitCode : 1;
            }
            catch (MemoryBudgetExceededException)
            {
                output.Dispose();
                await ReportExpansionDiagnosticAsync(builtinName, ShellValue.FromText("shared memory budget exhausted"))
                    .ConfigureAwait(false);
                return 1;
            }

            bool IsDiagnostic(int index) => resolutions[index] is CommandQueryResolution.Failed
                || outputMode == CommandQueryOutputMode.TypeDescription && resolutions[index] is CommandQueryResolution.Missing;

            bool AppendText(string text)
            {
                // Check the display limit before validating host text during encoding.
                var byteCount = Encoding.UTF8.GetByteCount(text);
                if (!output.CanAppend(byteCount))
                    return false;
                Utf8Text.StrictUtf8.GetBytes(text.AsSpan(), output.AppendSpan(byteCount));
                return true;
            }

            async Task<int> OutputLimitAsync()
            {
                await ReportExpansionDiagnosticAsync(builtinName, ShellValue.FromText("output limit exceeded"))
                    .ConfigureAwait(false);
                return 1;
            }

            async Task<CommandQueryResolution?[]> ResolveCommandQueriesAsync()
            {
                // Name and index list capacities include both generations during growth.
                const long HostRequestBytesPerName = 48;
                using var lookupMemory = new MemoryCapacity(Memory.Budget, 0);
                long retainedLookupBytes = 0;
                var resolutions = new CommandQueryResolution?[nameCount];
                var hostNames = new List<string>();
                var hostIndexes = new List<int>();
                for (var index = 0; index < nameCount; index++)
                {
                    CheckCancellation();
                    var value = arguments[firstName + index];
                    // Keywords take precedence in queries, even when a function shadows their spelling.
                    var hasTextName = Utf8.IsValid(value.Bytes.Span);
                    var decodeBytes = hasTextName && !value.IsEmpty
                        ? Math.Max(0, 32 + 2L * value.Bytes.Length
                            - (builtin == BuiltinKind.Command ? PreparedQueryDecodeBytes(value) : 0))
                        : 0;
                    // Only names selected for the host keep their text across iterations.
                    lookupMemory.Resize(checked(retainedLookupBytes + decodeBytes));
                    var name = hasTextName ? value.ToUtf8String() : string.Empty;
                    if (lookupMode != CommandLookupMode.HostOnly && hasTextName && Parser.IsSupportedKeyword(name.AsSpan()))
                    {
                        resolutions[index] = new CommandQueryResolution.Keyword();
                        continue;
                    }
                    var resolution = ResolveCommandName(value, name, lookupMode);
                    if (resolution is CommandResolution.Function function)
                    {
                        (functionOwners ??= []).Add(function.Binding);
                        function.Binding.Retain();
                        resolutions[index] = new CommandQueryResolution.Function(function.Binding.Definition);
                        continue;
                    }
                    // Host command names are UTF-8 paths; malformed bytes and empty names cannot resolve.
                    if (value.IsEmpty || !hasTextName)
                    {
                        resolutions[index] = new CommandQueryResolution.Missing();
                        continue;
                    }
                    if (resolution is CommandResolution.EvaluatorBuiltin)
                    {
                        resolutions[index] = new CommandQueryResolution.Builtin();
                        continue;
                    }

                    if (resolution is CommandResolution.Missing)
                    {
                        resolutions[index] = new CommandQueryResolution.Missing();
                        continue;
                    }

                    // Keep the builtin display classification, subject to the host confirming availability.
                    if (resolution is CommandResolution.HostBuiltin)
                        resolutions[index] = new CommandQueryResolution.Builtin();

                    retainedLookupBytes = checked(retainedLookupBytes + decodeBytes + HostRequestBytesPerName);
                    lookupMemory.Resize(retainedLookupBytes);
                    hostNames.Add(name);
                    hostIndexes.Add(index);
                }

                if (hostNames.Count > 0)
                {
                    lookupMemory.Resize(retainedLookupBytes);
                    lookupMemory.ReleaseUnusedCapacity();
                    var environment = BuildExportedEnvironment(vars, context);
                    CheckCancellation();
                    var hostResults = await ResolveCommandsOperation.AsTask(PublishOperation, hostNames, _state.CurrentDirectory, environment)
                        .ConfigureAwait(false);
                    CheckCancellation();
                    if (hostResults.Count != hostNames.Count)
                        throw new InvalidOperationException("Host returned an unexpected number of command lookup results.");

                    for (var index = 0; index < hostResults.Count; index++)
                    {
                        CheckCancellation();
                        resolutions[hostIndexes[index]] = hostResults[index] switch
                        {
                            CommandLookupResult.Found
                                when resolutions[hostIndexes[index]] is CommandQueryResolution.Builtin builtin => builtin,
                            CommandLookupResult.Found found => new CommandQueryResolution.Host(found.ResolvedName),
                            CommandLookupResult.Missing => new CommandQueryResolution.Missing(),
                            CommandLookupResult.Failed failed => new CommandQueryResolution.Failed(failed.Error),
                            _ => throw new InvalidOperationException("Host returned an unknown command lookup result.")
                        };
                    }
                }

                return resolutions;
            }
        }
        finally
        {
            if (functionOwners != null)
                foreach (var owner in functionOwners)
                    owner.Release();
        }
    }

    private static long PreparedQueryDecodeBytes(ShellValue value) => 4L * ((long)value.Bytes.Length + 1);
}

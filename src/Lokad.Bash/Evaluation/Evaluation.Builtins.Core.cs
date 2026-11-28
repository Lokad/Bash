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
                await ReportBuiltinDiagnosticAsync(ShellValue.FromText(commandName), "missing `]'", scope.Context)
                    .ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated);
            }

            argumentLimit--;
        }

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
        if (evaluation.ParseError is { } error)
        {
            await ReportBuiltinDiagnosticAsync([ShellValue.FromText(commandName)],
                [ShellValue.FromText(error.Prefix), error.Operand, ShellValue.FromText(error.Suffix)], scope.Context)
                .ConfigureAwait(false);
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
        EvaluationContext context,
        bool negated)
    {
        var write = content.IsEmpty ? DescriptorWriteResult.Open
            : await ExecuteWriteAsync(GetStdOutDescriptor(), content).ConfigureAwait(false);
        if (write.IsError)
        {
            using var diagnostics = new BuiltinDiagnosticScope(this);
            await ReportBuiltinWriteFailureAsync(ShellValue.FromText("echo"), write, GetBuiltinDiagnosticPrefix(context))
                .ConfigureAwait(false);
        }
        var exitCode = write.IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    /// <summary>Handles reset, deletion and queries against the emulator's empty command cache.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinHashAsync(IReadOnlyList<ShellValue> words, EvaluationContext context)
    {
        var command = words[0];
        var options = new ShortOptionCursor(words);
        var silent = false;
        var delete = false;
        var query = false;
        while (options.TryReadFlag(out var flag))
        {
            CheckCancellation();
            switch (flag.Span[0])
            {
                case (byte)'l' or (byte)'r': silent = true; continue;
                case (byte)'d': delete = true; continue;
                case (byte)'t': query = true; continue;
            }

            await WriteInvalidShortOptionUnlessStderrClosedAsync(command, ShellValue.FromText("-"), flag,
                    BuiltinKind.Hash, "hash", GetBuiltinDiagnosticPrefix(context))
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated: false).ConfigureAwait(false);
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Hash, "hash", negated: false)
                .ConfigureAwait(false);

        var status = 0;
        var firstName = options.ArgumentIndex;
        if (firstName < words.Count)
        {
            status = query || !delete ? 1 : 0;
            if (query)
            {
                var notFound = ShellValue.FromText(": not found");
                var prefix = GetBuiltinDiagnosticPrefix(context);
                for (var index = firstName; index < words.Count; index++)
                {
                    CheckCancellation();
                    var write = await WriteDiagnosticUnlessStderrClosedAsync([command], [words[index], notFound], scope: null, prefix)
                        .ConfigureAwait(false);
                    if (!write.CanAcceptMore)
                        break;
                }
            }
        }
        else if (delete || query)
        {
            var missingOption = ShellValue.FromText(delete ? "-d" : "-t");
            await WriteDiagnosticUnlessStderrClosedAsync([command], [missingOption, ShellValue.FromText(": option requires an argument")],
                    scope: null, GetBuiltinDiagnosticPrefix(context))
                .ConfigureAwait(false);
            status = 1;
        }
        else if (!silent)
        {
            var write = await WriteBuiltinOutputAsync(command, EmptyHashTableOutput, GetBuiltinDiagnosticPrefix(context))
                .ConfigureAwait(false);
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
        var listAll = false;
        var options = new ShortOptionCursor(words);
        while (options.TryReadFlag(out var flag))
        {
            switch (flag.Span[0])
            {
                case (byte)'a': listAll = true; break;
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
                default: return await InvalidOptionAsync(ShellValue.FromBytes(flag)).ConfigureAwait(false);
            }
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Type, "type", negated)
                .ConfigureAwait(false);

        var exitCode = await EvaluateCommandQueryAsync(
                BuiltinKind.Type,
                words,
                options.ArgumentIndex,
                outputMode,
                lookupMode,
                listAll,
                commandVars,
                context)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);

        async Task<EvaluationResult> InvalidOptionAsync(ShellValue option)
        {
            await WriteBuiltinOptionDiagnosticAsync(words[0], ShellValue.FromText("-"), option, "invalid option",
                BuiltinKind.Type, "type", GetBuiltinDiagnosticPrefix(context)).ConfigureAwait(false);
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
        bool listAll,
        Variables vars,
        EvaluationContext context)
    {
        var builtinName = ShellValue.FromText(builtin == BuiltinKind.Command ? "command" : "type");
        var nameCount = arguments.Count - firstName;
        if (nameCount == 0)
            return 0;

        // An all-matches operand can also hold a local definition record.
        const long ExtraResolutionBytesPerName = 32;
        var collectLocalMatches = listAll && lookupMode != CommandLookupMode.HostOnly;
        var extraBytes = collectLocalMatches
            ? checked(ExtraResolutionBytesPerName * nameCount + MemoryCapacity.ArrayBackingBytes<
                (bool, CommandQueryResolution.Function?, bool)>(nameCount))
            : 0;
        CheckCancellation();
        using var resolutionMemory = new MemoryCapacity(Memory.Budget, EstimateCommandQueryResolutionBytes(nameCount) + extraBytes);
        var localMatches = collectLocalMatches
            ? new (bool Keyword, CommandQueryResolution.Function? Function, bool Builtin)[nameCount]
            : null;
        List<FunctionBinding>? functionOwners = null;
        try
        {
            var resolutions = new CommandQueryResolution[nameCount];
            var hostResults = await ResolveCommandQueriesAsync(resolutions).ConfigureAwait(false);
            using var output = new ByteMemoryBuilder(0, Memory.Budget);
            MemoryCapacity? runMemory = null;
            try
            {
                // Build the whole display payload before emitting any channel; endpoints retain operand order.
                // Positive end offsets select stdout, and negative offsets select stderr.
                List<int>? earlierRunEnds = null;
                var lastRunEnd = 0;
                var foundCount = 0;
                var unresolvedCount = 0;
                var lookupFailed = false;
                var hostIndex = 0;
                SyntaxDiagnosticPrefix? sourcePrefix = null;
                for (var index = 0; index < nameCount; index++)
                {
                    CheckCancellation();
                    var name = arguments[firstName + index];
                    var resolution = resolutions[index];
                    CommandLookupResult.Found? hostCommand = null;
                    OperationError? error = null;
                    if (resolution is CommandQueryResolution.Host or CommandQueryResolution.HostBuiltin)
                    {
                        switch (hostResults[hostIndex++])
                        {
                            case CommandLookupResult.Found when resolution is CommandQueryResolution.HostBuiltin:
                                resolution = CommandQueryResolution.Builtin.Instance;
                                break;
                            case CommandLookupResult.Found found:
                                hostCommand = found;
                                break;
                            case CommandLookupResult.Missing:
                                resolution = CommandQueryResolution.Missing.Instance;
                                break;
                            case CommandLookupResult.Failed failed:
                                resolution = CommandQueryResolution.Missing.Instance;
                                error = failed.Error;
                                break;
                            default:
                                throw new InvalidOperationException("Host returned an unknown command lookup result.");
                        }
                    }

                    var local = localMatches == null ? default : localMatches[index];
                    var hasLocalMatch = local.Keyword || local.Function != null || local.Builtin;
                    if ((local.Keyword && !AppendFound(name, CommandQueryResolution.Keyword.Instance, null))
                        || (local.Function != null && !AppendFound(name, local.Function, null))
                        || (local.Builtin && !AppendFound(name, CommandQueryResolution.Builtin.Instance, null)))
                        return await OutputLimitAsync().ConfigureAwait(false);

                    if (resolution is CommandQueryResolution.Missing)
                    {
                        if (hasLocalMatch && !error.HasValue)
                        {
                            foundCount++;
                            FinishRun(diagnostic: false);
                            continue;
                        }
                        unresolvedCount++;
                        lookupFailed |= error.HasValue;
                        FinishRun(diagnostic: false);
                        if (error.HasValue || outputMode == CommandQueryOutputMode.TypeDescription)
                        {
                            var prefix = sourcePrefix ?? GetBuiltinDiagnosticPrefix(context);
                            sourcePrefix = prefix;
                            if (!output.CanAppend(prefix.Length))
                                return await OutputLimitAsync().ConfigureAwait(false);
                            prefix.AppendTo(output);
                            if (!output.TryAppend(builtinName.Bytes.Span) || !output.TryAppend(": "u8)
                                || !output.TryAppend(name.Bytes.Span) || !output.TryAppend(": "u8)
                                || !(error?.Message is { } message
                                    ? AppendText(message) : output.TryAppend("not found\n"u8)))
                                return await OutputLimitAsync().ConfigureAwait(false);
                            FinishRun(diagnostic: true);
                        }
                        continue;
                    }

                    foundCount++;
                    if (!AppendFound(name, resolution, hostCommand))
                        return await OutputLimitAsync().ConfigureAwait(false);
                    FinishRun(diagnostic: false);
                }
                bool AppendFound(ShellValue name, CommandQueryResolution resolution, CommandLookupResult.Found? hostCommand)
                {
                    if (outputMode == CommandQueryOutputMode.TypePath && hostCommand == null)
                        return true;
                    var appended = outputMode switch
                    {
                        CommandQueryOutputMode.CommandName or CommandQueryOutputMode.TypePath =>
                            hostCommand != null
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
                                CommandQueryResolution.Host when hostCommand != null => output.TryAppend(" is "u8) && AppendText(hostCommand.ResolvedName),
                                _ => throw new InvalidOperationException("Missing command reached found query output.")
                            }),
                        _ => throw new ArgumentOutOfRangeException(nameof(outputMode))
                    };
                    return appended && output.TryAppend((byte)'\n')
                        && (resolution is not CommandQueryResolution.Function { Definition: { } definition }
                            || FunctionDefinitionFormatter.TryAppend(definition, output));
                }

                void FinishRun(bool diagnostic)
                {
                    if (Math.Abs(lastRunEnd) == output.Length)
                        return;
                    if (lastRunEnd != 0 && (lastRunEnd < 0) != diagnostic)
                    {
                        var ends = earlierRunEnds ??= [];
                        (runMemory ??= new MemoryCapacity(Memory.Budget, 0)).PrepareListCapacity(ends, 1);
                        ends.Add(lastRunEnd);
                    }
                    lastRunEnd = diagnostic ? -output.Length : output.Length;
                }

                var queryExitCode = lookupFailed ? 1 : builtin == BuiltinKind.Command
                    ? foundCount > 0 ? 0 : 1
                    : unresolvedCount == 0 ? 0 : 1;
                if (output.Length == 0)
                    return queryExitCode;

                // The output has its own copy. Keep the selected function borrows through settlement.
                resolutions = [];
                localMatches = null;
                hostResults = [];
                resolutionMemory.Resize(MemoryCapacity.ArrayBackingBytes<FunctionBinding>(functionOwners?.Capacity ?? 0));
                resolutionMemory.ReleaseUnusedCapacity();
                runMemory?.ReleaseUnusedCapacity();
                var content = output.ToMemory();
                var startOffset = 0;
                var appendResult = DescriptorWriteResult.Open;
                var stdout = GetStdOutDescriptor();
                var stderr = GetStdErrDescriptor();
                var outputClosed = false;
                var errorClosed = false;
                var lastRunIndex = earlierRunEnds?.Count ?? 0;
                for (var index = 0; index <= lastRunIndex; index++)
                {
                    var runEnd = earlierRunEnds == null || index == lastRunIndex ? lastRunEnd : earlierRunEnds[index];
                    var diagnostic = runEnd < 0;
                    var endOffset = Math.Abs(runEnd);
                    if (!(diagnostic ? errorClosed : outputClosed))
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
                }
                if (builtin == BuiltinKind.Type && !errorClosed && appendResult.IsError)
                    await ReportBuiltinWriteFailureAsync(builtinName, appendResult, sourcePrefix ?? GetBuiltinDiagnosticPrefix(context))
                        .ConfigureAwait(false);
                // Unlike type, command -v/-V report resolution success even when stdout cannot be written.
                return builtin == BuiltinKind.Command || !appendResult.IsError ? queryExitCode : 1;
            }
            catch (MemoryBudgetExceededException)
            {
                output.Dispose();
                runMemory?.Dispose();
                await ReportExpansionDiagnosticAsync(builtinName, ShellValue.FromText("shared memory budget exhausted"))
                    .ConfigureAwait(false);
                return 1;
            }
            finally { runMemory?.Dispose(); }

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

            async Task<IReadOnlyList<CommandLookupResult>> ResolveCommandQueriesAsync(CommandQueryResolution[] resolutions)
            {
                using var lookupMemory = new MemoryCapacity(Memory.Budget, 0);
                List<string>? hostNames = null;
                for (var index = 0; index < nameCount; index++)
                {
                    CheckCancellation();
                    var value = arguments[firstName + index];
                    // Keywords take precedence when the query requests a single resolution.
                    var keyword = lookupMode != CommandLookupMode.HostOnly && Parser.IsSupportedKeyword(value.Bytes.Span);
                    if (localMatches == null && keyword)
                    {
                        resolutions[index] = CommandQueryResolution.Keyword.Instance;
                        continue;
                    }
                    var function = FindCommandFunction(value, lookupMode);
                    if (localMatches == null && function != null)
                    {
                        resolutions[index] = SelectFunction(function);
                        continue;
                    }
                    var builtinKind = FindCommandBuiltin(value, lookupMode)?.Kind;
                    var hostBuiltin = builtinKind == BuiltinKind.ApplyPatch;
                    var evaluatorBuiltin = builtinKind.HasValue && !hostBuiltin;
                    if (localMatches != null)
                    {
                        localMatches[index] = (keyword, function == null ? null : SelectFunction(function), evaluatorBuiltin);
                    }
                    else if (evaluatorBuiltin)
                    {
                        resolutions[index] = CommandQueryResolution.Builtin.Instance;
                        continue;
                    }

                    // Host command names must be nonempty UTF-8. Local names stay byte-valued.
                    if ((lookupMode == CommandLookupMode.BuiltinOnly && !hostBuiltin)
                        || value.IsEmpty || !Utf8.IsValid(value.Bytes.Span))
                    {
                        resolutions[index] = CommandQueryResolution.Missing.Instance;
                        continue;
                    }

                    resolutions[index] = hostBuiltin ? CommandQueryResolution.HostBuiltin.Instance : CommandQueryResolution.Host.Instance;
                    var decodeBytes = Math.Max(0, 32 + 2L * value.Bytes.Length
                        - (builtin == BuiltinKind.Command ? PreparedQueryDecodeBytes(value) : 0));
                    if (hostNames == null)
                    {
                        lookupMemory.Grow(checked(decodeBytes + MemoryCapacity.ArrayBackingBytes<string>(1)));
                        hostNames = new List<string>(1);
                    }
                    else
                    {
                        lookupMemory.PrepareListCapacity(hostNames, 1);
                        if (decodeBytes > 0)
                            lookupMemory.Grow(decodeBytes);
                    }
                    hostNames.Add(value.ToUtf8String());
                }

                if (hostNames == null)
                    return [];

                // Return unused command credit before admitting the lookup-owned environment.
                Memory.ReleaseUnusedCapacity();
                var environment = BuildExportedEnvironment(vars, context, lookupMemory, replacement: null);
                lookupMemory.ReleaseUnusedCapacity();
                CheckCancellation();
                var results = await ResolveCommandsOperation.AsTask(PublishOperation, hostNames, _state.CurrentDirectory, environment)
                    .ConfigureAwait(false);
                CheckCancellation();
                if (results.Count != hostNames.Count)
                    throw new InvalidOperationException("Host returned an unexpected number of command lookup results.");
                return results;
            }

            CommandQueryResolution.Function SelectFunction(FunctionBinding binding)
            {
                if (outputMode != CommandQueryOutputMode.TypeDescription)
                    return CommandQueryResolution.Function.Classification;
                (functionOwners ??= []).Add(binding);
                binding.Retain();
                return new CommandQueryResolution.Function(binding.Definition);
            }
        }
        finally
        {
            if (functionOwners != null)
                foreach (var owner in functionOwners)
                    owner.Release();
        }
    }

    // Covers resolution references, records and function-owner list growth.
    // Definitions and host result text stay borrowed.
    internal static long EstimateCommandQueryResolutionBytes(int nameCount) => 128 + 64L * nameCount;

    private static long PreparedQueryDecodeBytes(ShellValue value) => 4L * ((long)value.Bytes.Length + 1);
}

using System;
using Lokad.Bash.Memory;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Formatting;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Handles the <c>test</c> and <c>[</c> builtins, evaluating predicates and filesystem tests as needed.
    /// </summary>
    /// <param name="commandName">The resolved command name.</param>
    /// <param name="words">The completed command name and operands.</param>
    /// <param name="negated">Whether to negate the final exit status.</param>
    /// <returns>
    /// The finalized evaluation result.
    /// </returns>
    private async Task<EvaluationResult> EvaluateBuiltinTestAsync(
        string commandName,
        IReadOnlyList<ShellValue> words,
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
        var arguments = new List<ShellValue>(Math.Max(0, argumentLimit - 1));
        for (var index = 1; index < argumentLimit; index++)
            arguments.Add(words[index]);

        var evaluation = TestEvaluation.ParseValues(arguments, _cancellationToken, _state.ErrexitEnabled, Memory);
        if (evaluation.HasParseError)
        {
            using var diagnostic = new ByteMemoryBuilder(0, Memory.Budget);
            if (evaluation.ErrorOperand is { } errorOperand)
            {
                diagnostic.Append(errorOperand.Bytes);
                diagnostic.Append(": "u8);
            }
            diagnostic.Append(Utf8Text.Encode(evaluation.ErrorMessage));
            await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromBytes(diagnostic.ToMemory()))
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated);
        }

        var fileTests = evaluation.CollectFileTests(out var result);
        if (result.HasValue)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(result.Value ? 0 : 1), negated);

        var exitCode = await ExecuteFileTestsForEvaluationAsync(fileTests, evaluation.CompleteFileTests)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
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
        var nameIndex = 1;
        if (nameIndex < words.Count && words[nameIndex].Bytes.Span.SequenceEqual("-t"u8))
        {
            outputMode = CommandQueryOutputMode.TypeKind;
            nameIndex++;
        }

        if (nameIndex < words.Count && words[nameIndex].Bytes.Span.SequenceEqual("--"u8))
            nameIndex++;

        var names = new List<ShellValue>(words.Count - nameIndex);
        for (var index = nameIndex; index < words.Count; index++)
            names.Add(words[index]);
        var exitCode = await EvaluateCommandQueryAsync(
                BuiltinKind.Type,
                names,
                outputMode,
                commandVars,
                context)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    /// <summary> Evaluates a command lookup and formats its requested query output. </summary>
    private async Task<int> EvaluateCommandQueryAsync(
        BuiltinKind builtin,
        IReadOnlyList<ShellValue> arguments,
        CommandQueryOutputMode outputMode,
        Variables vars,
        EvaluationContext context)
    {
        var builtinName = ShellValue.FromText(builtin == BuiltinKind.Command ? "command" : "type");
        var names = arguments;

        if (names.Count == 0)
            return 0;

        var resolutions = await ResolveCommandQueriesAsync().ConfigureAwait(false);
        using var output = new ByteMemoryBuilder(0, Memory.Budget);
        using var encodingMemory = new MemoryCapacity(Memory.Budget, 0);
        try
        {
            // Build the whole display payload before emitting any channel; endpoints retain operand order.
            var endOffsets = new int[names.Count];
            var foundCount = 0;
            var unresolvedCount = 0;
            var lookupFailed = false;
            for (var index = 0; index < names.Count; index++)
            {
                var name = names[index];
                var resolution = resolutions[index];
                if (resolution is CommandQueryResolution.Missing or CommandQueryResolution.Failed)
                {
                    unresolvedCount++;
                    var error = (resolution as CommandQueryResolution.Failed)?.Error;
                    lookupFailed |= error.HasValue;
                    if ((error.HasValue || outputMode == CommandQueryOutputMode.TypeDescription)
                        && (!output.TryAppend(builtinName.Bytes.Span) || !AppendText(": ")
                            || !output.TryAppend(name.Bytes.Span) || !AppendText(": ")
                            || !AppendText(error?.Message ?? "not found\n")))
                        return await OutputLimitAsync().ConfigureAwait(false);

                    endOffsets[index] = output.Length;
                    continue;
                }

                foundCount++;
                var appended = outputMode switch
                {
                    CommandQueryOutputMode.CommandName => resolution is CommandQueryResolution.Host hostCommand
                        ? AppendText(hostCommand.ResolvedName)
                        : output.TryAppend(name.Bytes.Span),
                    CommandQueryOutputMode.TypeKind => AppendText(resolution switch
                        {
                            CommandQueryResolution.Function => "function",
                            CommandQueryResolution.Builtin => "builtin",
                            CommandQueryResolution.Keyword => "keyword",
                            CommandQueryResolution.Host => "file",
                            _ => throw new InvalidOperationException("Missing command reached found query output.")
                        }),
                    CommandQueryOutputMode.TypeDescription => output.TryAppend(name.Bytes.Span) && (resolution switch
                        {
                            CommandQueryResolution.Function => AppendText(" is a function"),
                            CommandQueryResolution.Builtin => AppendText(" is a shell builtin"),
                            CommandQueryResolution.Keyword => AppendText(" is a shell keyword"),
                            CommandQueryResolution.Host hostDescription => AppendText(" is ") && AppendText(hostDescription.ResolvedName),
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
            for (var first = 0; first < names.Count;)
            {
                var diagnostic = IsDiagnostic(first);
                var last = first;
                while (last + 1 < names.Count && IsDiagnostic(last + 1) == diagnostic)
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
            encodingMemory.Dispose();
            await ReportExpansionDiagnosticAsync(builtinName, ShellValue.FromText("shared memory budget exhausted"))
                .ConfigureAwait(false);
            return 1;
        }

        bool IsDiagnostic(int index) => resolutions[index] is CommandQueryResolution.Failed
            || outputMode == CommandQueryOutputMode.TypeDescription && resolutions[index] is CommandQueryResolution.Missing;

        bool AppendText(string text)
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (!output.CanAppend(bytes))
                return false;
            // Encoded fragments are copied immediately; reuse their prepaid allowance.
            encodingMemory.Resize(bytes);
            return output.TryAppend(Utf8Text.Encode(text).Span);
        }

        async Task<int> OutputLimitAsync()
        {
            await ReportExpansionDiagnosticAsync(builtinName, ShellValue.FromText("output limit exceeded"))
                .ConfigureAwait(false);
            return 1;
        }

        async Task<IReadOnlyList<CommandQueryResolution>> ResolveCommandQueriesAsync()
        {
            var resolutions = new CommandQueryResolution?[names.Count];
            var hostNames = new List<string>();
            var hostIndexes = new List<int>();
            for (var index = 0; index < names.Count; index++)
            {
                // Keywords take precedence in queries, even when a function shadows their spelling.
                var hasTextName = names[index].TryGetUtf8Text(out var name);
                if (hasTextName && Parser.IsSupportedKeyword(name.AsSpan()))
                {
                    resolutions[index] = new CommandQueryResolution.Keyword();
                    continue;
                }
                var resolution = ResolveCommandName(names[index], name, CommandLookupMode.Normal);
                if (resolution is CommandResolution.Function function)
                {
                    resolutions[index] = new CommandQueryResolution.Function(function.Definition);
                    continue;
                }
                // Host command names are UTF-8 paths; malformed bytes and empty names cannot resolve.
                if (names[index].IsEmpty || !hasTextName)
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

                hostNames.Add(name);
                hostIndexes.Add(index);
            }

            if (hostNames.Count > 0)
            {
                var environment = BuildExportedEnvironment(vars, context);
                var hostResults = await AwaitOperationAsync(
                        ResolveCommandsOperation.AsTask(PublishOperation, hostNames, _state.CurrentDirectory, environment))
                    .ConfigureAwait(false);
                if (hostResults.Count != hostNames.Count)
                    throw new InvalidOperationException("Host returned an unexpected number of command lookup results.");

                for (var index = 0; index < hostResults.Count; index++)
                {
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

            var completed = new List<CommandQueryResolution>(resolutions.Length);
            foreach (var resolution in resolutions)
                completed.Add(resolution ?? throw new InvalidOperationException("Command query was not resolved."));

            return completed;
        }
    }

}

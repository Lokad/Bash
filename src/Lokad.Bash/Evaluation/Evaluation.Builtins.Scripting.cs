using System;
using System.Collections.Generic;
using System.Text.Unicode;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private static readonly ShellValue SourceCallerName = ShellValue.FromText("source");

    /// <summary>
    /// Handles the <c>caller</c> builtin by selecting an active function or sourced invocation.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinCallerAsync(
        IReadOnlyList<ShellValue> words,
        EvaluationContext context,
        bool negated)
    {
        var command = words[0];
        var first = 1;
        if (first < words.Count)
        {
            var option = words[first].Bytes;
            if (option.Span.SequenceEqual("--"u8))
                first++;
            else if (option.Span.SequenceEqual("--help"u8))
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Caller, "caller", negated)
                    .ConfigureAwait(false);
            else if (option.Span is [(byte)'-', _, ..])
                return await ReportUsageAsync(ShellValue.FromBytes(option[..2]), ": invalid option").ConfigureAwait(false);
        }

        var index = -1L;
        if (first < words.Count)
        {
            var operand = words[first];
            if (!TryParseControlNumber(operand, out index))
                return await ReportUsageAsync(operand, operand.Bytes.Span.StartsWith("0x"u8)
                    ? ": invalid hex number" : ": invalid number").ConfigureAwait(false);
            if (index < 0)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
        var function = SkipScriptPrefixes(context.FunctionContext);
        var source = context.CallerSource;
        while (true)
        {
            CheckCancellation();
            ShellValue name;
            // At the same depth, the source was entered after the function.
            if (source != null && source.FunctionDepth >= (function?.Depth ?? 0))
            {
                name = SourceCallerName;
                source = source.Enclosing;
            }
            else if (function != null)
            {
                name = function.FunctionName;
                function = SkipScriptPrefixes(function.Enclosing);
            }
            else
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

            if (index-- >= 0)
                continue;
            using var output = new ByteMemoryBuilder(0, Memory.Budget);
            output.Append("0 "u8).Append(name.Bytes).Append((byte)'\n');
            await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
        }

        async Task<EvaluationResult> ReportUsageAsync(ShellValue operand, string reason)
        {
            await ReportExpansionDiagnosticAsync([command], [operand, ShellValue.FromText(reason)])
                .ConfigureAwait(false);
            var synopsis = BuiltinDocumentation.For(BuiltinKind.Caller).Synopsis("caller");
            await ReportExpansionDiagnosticAsync([command], [ShellValue.FromText("usage: "), ShellValue.FromText(synopsis)])
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }

        static FunctionContext? SkipScriptPrefixes(FunctionContext? frame)
        {
            while (frame?.Locals.IsTemporaryPrefixScope == true)
                frame = frame.Enclosing;
            return frame;
        }
    }

    /// <summary>
    /// Selects the script operands of the resolved builtin before parsing or opening any source.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinScriptingAsync(
        BuiltinKind kind,
        string commandName,
        IReadOnlyList<ShellValue> words,
        ScopedEvaluation scope,
        bool negated)
    {
        var first = 1;
        var command = words[0];
        if (first < words.Count)
        {
            var option = words[first].Bytes;
            if (option.Span.SequenceEqual("--"u8))
                first++;
            else if (option.Span.SequenceEqual("--help"u8))
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, kind, commandName, negated)
                    .ConfigureAwait(false);
            else if (option.Span is [(byte)'-', _, ..])
            {
                await WriteDiagnosticUnlessStderrClosedAsync([command],
                    [ShellValue.FromBytes(option[..2]), ShellValue.FromText(": invalid option")], _builtinDiagnostics)
                    .ConfigureAwait(false);
                return await FinishUsageErrorAsync().ConfigureAwait(false);
            }
        }

        if (kind == BuiltinKind.Eval)
        {
            if (first == words.Count)
                return ApplyNegation(EvaluationResult.FromExitCode(0), negated);

            var result = await EvaluateCopiedCommandUnitsAsync(words, first, scope, ScriptExecutionKind.Eval)
                .ConfigureAwait(false);
            return ApplyNegation(result, negated);
        }

        if (first == words.Count)
        {
            await WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("filename argument required")],
                _builtinDiagnostics)
                .ConfigureAwait(false);
            return await FinishUsageErrorAsync().ConfigureAwait(false);
        }
        if (words[first].IsEmpty)
        {
            // A supplied filename reaches the source boundary even when it is empty.
            scope.Variables.ClearPositionalReplacement();
            await ReportSourceOpenFailureAsync(commandName, string.Empty, new OperationError("No such file or directory\n"))
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }

        var path = await DecodeExpandedValueAsync(words[first]).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

        var argumentStart = first + 1;
        var argumentCount = words.Count - argumentStart;
        if (argumentCount > 0)
            Memory.Charge(checked(scope.Variables.PositionalMemoryBytes + 32L * argumentCount));
        else
            scope.Variables.ClearPositionalReplacement();
        var restorePolicy = scope.Context.FunctionContext == null
            ? PositionalParametersRestorePolicy.UnlessReplaced : PositionalParametersRestorePolicy.Always;
        using var positionalScope = argumentCount == 0 ? null : scope.Variables.EnterPositionalParametersScope(
            words, argumentStart, restorePolicy);
        return ApplyNegation(await ExecuteSourceAsync(commandName, path, scope).ConfigureAwait(false), negated);

        async Task<EvaluationResult> FinishUsageErrorAsync()
        {
            await WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("usage: "
                + BuiltinDocumentation.For(kind).Synopsis(commandName))], _builtinDiagnostics).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }
    }

    private Task ReportSourceOpenFailureAsync(string commandName, string path, OperationError error)
    {
        var standardError = GetStdErrDescriptor();
        if (standardError.IsClosed)
            return Task.CompletedTask;

        return error.Kind == OperationErrorKind.IsDirectory
            ? WriteOperationDiagnosticAsync(standardError, commandName + ": ", path, new OperationError("is a directory\n"))
            : WriteOperationDiagnosticAsync(standardError, string.Empty, path, error);
    }

    private async Task<(FileOpenResult Result, string Path)> SearchSourcePathAsync(
        RawShellPath fileName, ReadOnlyMemory<byte> searchPath, ScopedEvaluation scope, MemoryCapacity lookupMemory)
    {
        FileOpenResult? currentDirectoryAttempt = null;
        while (true)
        {
            CheckCancellation();
            var separator = searchPath.Span.IndexOf((byte)':');
            var component = separator < 0 ? searchPath : searchPath[..separator];
            if (BuildCandidate(component) is { } candidate)
            {
                var workingDirectory = ReferenceEquals(candidate, fileName);
                var open = workingDirectory && currentDirectoryAttempt is { } previous
                    ? previous : await OpenCandidateAsync(candidate).ConfigureAwait(false);
                if (workingDirectory)
                    currentDirectoryAttempt = open;
                if (open.Error is not { Kind: OperationErrorKind.NotFound or OperationErrorKind.NotDirectory
                    or OperationErrorKind.IsDirectory or OperationErrorKind.PermissionDenied })
                    return (open, candidate.Raw);
            }

            lookupMemory.Resize(0);
            if (separator < 0)
                break;
            searchPath = searchPath[(separator + 1)..];
        }

        return (currentDirectoryAttempt ?? await OpenCandidateAsync(fileName).ConfigureAwait(false),
            fileName.Raw);

        Task<FileOpenResult> OpenCandidateAsync(RawShellPath candidate)
        {
            if (!candidate.Raw.StartsWith('/'))
                Memory.Charge(lookupMemory, 32L + 2L * (CurrentDirectory.Path.Length + 1L + candidate.Raw.Length));
            return ExecuteOpenFileAsync(candidate, FileOpenMode.Read);
        }

        RawShellPath? BuildCandidate(ReadOnlyMemory<byte> component)
        {
            var (prefix, suffix) = WordExpansion.ExpandPathTilde(component, scope, lookupMemory);
            if (suffix.IsEmpty && (prefix.IsEmpty || prefix.Span.SequenceEqual("."u8)))
                return fileName;

            var joined = JoinSearchPathComponent(prefix, suffix, fileName.Raw, lookupMemory);
            return joined != null && TryReadFileOperand(joined, CurrentDirectory, out var raw) ? raw : null;
        }
    }

    /// <summary>Builds a host path from a tilde-expanded search component and a decoded operand.</summary>
    private string? JoinSearchPathComponent(ReadOnlyMemory<byte> prefix, ReadOnlyMemory<byte> suffix,
        string operand, MemoryCapacity lookupMemory)
    {
        if (prefix.IsEmpty && suffix.IsEmpty)
            return operand;
        if (!Utf8.IsValid(prefix.Span) || !Utf8.IsValid(suffix.Span))
            return null;

        var last = suffix.IsEmpty ? prefix.Span[^1] : suffix.Span[^1];
        var addSlash = last != (byte)'/';
        var encoding = Utf8Text.StrictUtf8;
        var length = (long)encoding.GetCharCount(prefix.Span) + encoding.GetCharCount(suffix.Span)
            + (addSlash ? 1 : 0) + operand.Length;
        if (length > PathResolution.MaximumPathCharacters)
            return null;
        Memory.Charge(lookupMemory, checked(96L + 2L * length));
        return string.Create((int)length, (prefix, suffix, addSlash, operand), static (target, parts) =>
        {
            var position = Utf8Text.StrictUtf8.GetChars(parts.prefix.Span, target);
            position += Utf8Text.StrictUtf8.GetChars(parts.suffix.Span, target[position..]);
            if (parts.addSlash)
                target[position++] = '/';
            parts.operand.AsSpan().CopyTo(target[position..]);
        });
    }

    private enum ScriptExecutionKind { Root, LegacyCapture, Source, Eval, ArrayReaderCallback, Trap }
    private const int BuiltinSyntaxErrorStatus = 257;

    /// <summary>Copies eval/trap input into an owner retained by any syntax that survives execution.</summary>
    private async Task<EvaluationResult> EvaluateCopiedCommandUnitsAsync(
        IReadOnlyList<ShellValue> words, int start, ScopedEvaluation scope, ScriptExecutionKind kind)
    {
        // Expansion is complete. Include a space between every pair of operands,
        // including empty ones, and admit the final backing before copying.
        long length = Math.Max(0, words.Count - start - 1);
        for (var index = start; index < words.Count; index++)
        {
            CheckCancellation();
            length += words[index].Bytes.Length;
        }
        if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        var sourceMemory = new SourceMemory(Memory.Budget.CreateScope());
        try
        {
            using var builder = new ByteMemoryBuilder((int)length, sourceMemory.Budget);
            for (var index = start; index < words.Count; index++)
            {
                CheckCancellation();
                if (index > start) builder.Append((byte)' ');
                builder.Append(words[index].Bytes);
            }
            var source = ShellSource.FromBytes(builder.DetachSnapshot());
            return await EvaluateCommandUnitsAsync(source,
                scope with { Context = scope.Context.WithSourceMemory(sourceMemory) }, kind).ConfigureAwait(false);
        }
        finally { sourceMemory.Release(); }
    }

    /// <summary>Shares complete-command parsing and execution across the public entry point, source, eval and EXIT actions.</summary>
    private async Task<EvaluationResult> EvaluateCommandUnitsAsync(
        ShellSource source, ScopedEvaluation scope, ScriptExecutionKind kind)
    {
        // A reached nested parser needs its own stack headroom for syntax guards,
        // independently of the caller's function/eval execution depth.
        if (kind is not (ScriptExecutionKind.Root or ScriptExecutionKind.LegacyCapture))
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (IsNestedLoadedReader(kind))
            scope = scope with
            {
                InNestedCommandUnits = true,
                LoadedReaderIgnoresErrexit = scope.Context.ErrexitIgnored
            };
        var lineOffset = kind switch
        {
            ScriptExecutionKind.Eval => Math.Max(1, scope.Context.Coordinates.EndLine) - 1,
            ScriptExecutionKind.ArrayReaderCallback => Math.Max(1, scope.Context.Coordinates.Line) - 1,
            ScriptExecutionKind.Source or ScriptExecutionKind.Trap => 0,
            _ => scope.Context.Coordinates.Offset
        };

        var result = EvaluationResult.FromExitCode(0);
        var scanContext = new ShellScanContext();
        using var commands = Parser.TryParseCommandUnits(source.Bytes, scanContext).GetEnumerator();
        while (true)
        {
            CheckCancellation();
            var parent = scope.Context.SourceMemory;
            var unitMemory = new SourceMemory((parent?.Budget ?? Memory.Budget).CreateScope(), parent);
            scanContext.SyntaxMemory = unitMemory.SyntaxMemory;
            try
            {
                if (!commands.MoveNext())
                    break;
                if (commands.Current.HereDocumentWarnings is { } warnings)
                    await ReportHereDocumentWarningsAsync(warnings, lineOffset).ConfigureAwait(false);
                var failedUnit = commands.Current as CommandUnitParseResult.Failed;
                var skipExecution = result.Flow is FlowControlKind.Break or FlowControlKind.Continue;
                ICommand command;
                if (failedUnit != null)
                {
                    if (failedUnit.Failure.HereDocumentEndOfInput is { } hereDocumentEnd)
                    {
                        await ReportCapturedHereDocumentEndOfInputAsync(failedUnit.Error, hereDocumentEnd, lineOffset)
                            .ConfigureAwait(false);
                    }
                    else if (failedUnit.Failure.UnexpectedToken is { } token)
                        await ReportUnexpectedSyntaxAsync(token, scope.Context.CallerSource?.Path,
                            kind == ScriptExecutionKind.Eval, lineOffset + failedUnit.DiagnosticLine).ConfigureAwait(false);
                    else if (failedUnit.Failure.ConditionalQuoteEnd is { } quoteEnd)
                        await ReportUnclosedConditionalQuoteAsync(quoteEnd).ConfigureAwait(false);
                    else
                        await ReportSyntaxErrorAsync(failedUnit.Error).ConfigureAwait(false);
                    if (failedUnit.Failure.IsDirectQuotedSubstitutionFailure)
                    {
                        PreparePipelineStatusStorage(1);
                        scope.Variables.SetPipelineStatuses([2], scope.Context, _cancellationToken);
                        _state.LastExitCode = 2;
                    }
                    if (failedUnit.CompletedPrefix is { } prefix && !skipExecution)
                        command = prefix;
                    else
                    {
                        if (kind == ScriptExecutionKind.LegacyCapture && failedUnit.Failure.ExitsSubstitution)
                            return EvaluationResult.Exit(1);
                        if (IsNestedLoadedReader(kind) && failedUnit.Failure.ExitsSubstitution)
                        {
                            // These child boundaries unwind the builtin before running their EXIT action.
                            if (_state.ForkKind is ShellForkKind.CommandSubstitution or ShellForkKind.PipelineSimpleCommand)
                                return EvaluationResult.Exit(1);

                            // In the invoking shell, EXIT sees the full parser status and the active
                            // builtin scope. The result crossing the shell boundary uses its low byte.
                            var status = failedUnit.Failure.Kind == ParserFailureKind.SubstitutionGrammar
                                ? BuiltinSyntaxErrorStatus : 2;
                            var exit = await RunExitTrapAsync(scope, EvaluationResult.Exit(status)).ConfigureAwait(false);
                            return exit.WithExitCode(exit.ExitCode & byte.MaxValue);
                        }
                        if (failedUnit.Failure.Kind is ParserFailureKind.ArrayQuotedSubstitutionForHeader
                            or ParserFailureKind.ArraySubstitutionDelimiter)
                            return result.WithExitCode(1);
                        if (kind == ScriptExecutionKind.Trap && _state.ErrexitEnabled && !scope.Context.ErrexitIgnored)
                            return EvaluationResult.Exit(2);
                        return result.WithExitCode(2);
                    }
                }
                else
                    command = ((CommandUnitParseResult.Parsed)commands.Current).Command;

                // Loop flow skips execution but still checks the remaining source/eval syntax.
                if (skipExecution)
                    continue;

                result = await EvaluateCommandAsync(command,
                    scope with
                    {
                        Context = scope.Context.WithSourceMemory(unitMemory).WithCoordinates(
                            new CommandCoordinates(commands.Current.Locations, lineOffset, lineOffset + 1, lineOffset + 1))
                    }, negated: false)
                    .ConfigureAwait(false);
                if (result.Flow == FlowControlKind.Return)
                    return kind == ScriptExecutionKind.Source ? EvaluationResult.FromExitCode(result.ExitCode) : result;
                if (_state.ForkKind == null
                    && (kind == ScriptExecutionKind.Root && result.DiscardsCommandUnit
                        || IsLoadedReader(kind) && result.DiscardsLoadedCommandUnit))
                    result = await RecoverDiscardedCommandUnitAsync(scope).ConfigureAwait(false);
                else if (result.Flow == FlowControlKind.Exit)
                {
                    // Recover only the expansion failures that end the current command unit.
                    // Explicit exit, errexit and unrecoverable expansion failures still terminate.
                    result = await RecoverScriptExpansionFailureAsync(result, kind, scope).ConfigureAwait(false);
                    result = await FinalizeResultAsync(result, negated: false).ConfigureAwait(false);
                    if (result.Flow == FlowControlKind.Exit)
                        return result;
                }
                if (failedUnit != null)
                    break;
            }
            finally
            {
                scanContext.SyntaxMemory = null;
                unitMemory.Release();
            }
        }
        return await RecoverScriptExpansionFailureAsync(result, kind, scope).ConfigureAwait(false);
    }

    /// <summary>Recovers unit-scoped expansion failures while preserving root versus nested status rules.</summary>
    private ValueTask<EvaluationResult> RecoverScriptExpansionFailureAsync(
        EvaluationResult result, ScriptExecutionKind kind, ScopedEvaluation scope) =>
        GetRecoverableScriptExpansionStatus(kind, scope) is { } status
            ? RecoverExpansionFailureAsync(status, scope, IsLoadedReader(kind))
            : ValueTask.FromResult(result);

    private int? GetRecoverableScriptExpansionStatus(ScriptExecutionKind kind, ScopedEvaluation scope)
    {
        if (CanRecoverCommandSubstitutionSyntax)
            return 1;
        if (kind == ScriptExecutionKind.Root)
        {
            // A list inside a loaded unit must unwind to that reader before it resumes.
            if (_state.FatalExpansion is { Kind: ExpansionFailureKind.AssignmentTarget }
                && (scope.InNestedCommandUnits || _state.Traps?.ExitTrapStatus != null))
                return null;
            if (RecoverableRootExpansionStatus is { } status)
                return status;
        }
        if (_state.FatalExpansion is
            { Kind: ExpansionFailureKind.ParameterDefaultAssignment or ExpansionFailureKind.AssignmentTarget } assignmentFailure)
        {
            // Loaded units recover at the invoking reader, including when it was tested.
            return _state.ForkKind == null && IsLoadedReader(kind)
                && (assignmentFailure.CanResumeUntestedUnit || scope.Context.ErrexitIgnored) ? 1 : null;
        }
        if (_state.FatalExpansion is not
            { Kind: ExpansionFailureKind.Arithmetic or ExpansionFailureKind.WordSyntax
                or ExpansionFailureKind.IndirectParameter or ExpansionFailureKind.NamerefDefault } failure)
            return null;

        // A fork ends on an indirect-name failure even inside a loaded reader.
        if (failure.Kind == ExpansionFailureKind.IndirectParameter && _state.ForkKind != null)
            return null;

        // Recovery uses the caller's errexit state, including for tested commands within the unit.
        if (_state.ErrexitEnabled && !scope.Context.ErrexitIgnored
            && failure.Kind is ExpansionFailureKind.WordSyntax or ExpansionFailureKind.IndirectParameter)
            return null;

        // Nameref defaults fail with 1 at script boundaries, but with 2 at the root.
        return failure.Kind == ExpansionFailureKind.NamerefDefault
            && kind is not (ScriptExecutionKind.Root or ScriptExecutionKind.LegacyCapture) ? 1 : failure.ExitCode;
    }

    private static bool IsLoadedReader(ScriptExecutionKind kind) =>
        kind == ScriptExecutionKind.Trap || IsNestedLoadedReader(kind);

    private static bool IsNestedLoadedReader(ScriptExecutionKind kind) =>
        kind is ScriptExecutionKind.Eval or ScriptExecutionKind.Source or ScriptExecutionKind.ArrayReaderCallback;

    private bool CanRecoverCommandSubstitutionSyntax =>
        _state.FatalExpansion is { Kind: ExpansionFailureKind.CommandSubstitutionSyntax, ExitCode: 1 };

    private int? RecoverableRootExpansionStatus => _state.ForkKind != null ? null : _state.FatalExpansion switch
    {
        { Kind: ExpansionFailureKind.IntegerAssignment or ExpansionFailureKind.ArrayIndex, ExitCode: var status } => status,
        { Kind: ExpansionFailureKind.ParameterDefaultAssignment, ExitCode: 2 } => 2,
        { Kind: ExpansionFailureKind.AssignmentTarget, CanResumeUntestedUnit: true } => 1,
        _ => null
    };

    private async ValueTask<EvaluationResult> RecoverDiscardedCommandUnitAsync(ScopedEvaluation scope)
    {
        // The reader keeps a previous failure and its pipeline statuses.
        var status = _state.LastExitCode.GetValueOrDefault();
        if (status != 0)
            return EvaluationResult.FromExitCode(status);

        await PublishRecoveredPipelineStatusAsync(1, scope).ConfigureAwait(false);
        _state.LastExitCode = 1;
        return EvaluationResult.FromExitCode(1);
    }

    private EvaluationResult CompleteDiscardInChild(EvaluationResult result) =>
        result.DiscardsCommandUnit && _state.LastExitCode is { } previous && previous != 0
            ? result.WithExitCode(previous) : result;

    private async ValueTask<EvaluationResult> RecoverExpansionFailureAsync(int exitCode, ScopedEvaluation scope, bool loadedReader)
    {
        var kind = _state.FatalExpansion?.Kind;
        var publishLoadedAssignment = loadedReader
            && kind is ExpansionFailureKind.ParameterDefaultAssignment or ExpansionFailureKind.AssignmentTarget;
        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);

        if (kind == ExpansionFailureKind.IntegerAssignment || publishLoadedAssignment)
        {
            // The caller's bindings and descriptors are visible again. Integer assignments
            // replace PIPESTATUS only after a previous success.
            if (publishLoadedAssignment || _state.LastExitCode.GetValueOrDefault() == 0)
                await PublishRecoveredPipelineStatusAsync(exitCode, scope).ConfigureAwait(false);
            _state.LastExitCode = exitCode;
        }
        return EvaluationResult.FromExitCode(exitCode);
    }

    private async ValueTask PublishRecoveredPipelineStatusAsync(int exitCode, ScopedEvaluation scope)
    {
        try
        {
            using var memory = Memory.BeginCommand(scope);
            PublishExpansionPipelineStatus(exitCode, scope);
        }
        catch (MemoryBudgetExceededException)
        {
            Memory.ReleaseUnusedCapacity();
            await ReportMemoryRefusalAsync("shell").ConfigureAwait(false);
        }
    }
}

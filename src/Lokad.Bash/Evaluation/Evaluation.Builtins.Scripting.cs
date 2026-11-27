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
            var field = ClassifyShortOptionField(option.Span);
            if (field == ShortOptionField.Terminator)
                first++;
            else if (field == ShortOptionField.Help)
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Caller, "caller", negated)
                    .ConfigureAwait(false);
            else if (field == ShortOptionField.Flags)
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
            await WriteDiagnosticAsync([command], [operand, ShellValue.FromText(reason)], _builtinDiagnostics,
                ClosedStderrHandling.AttemptWrite, GetNamedSourceDiagnosticPrefix(context))
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
            var field = ClassifyShortOptionField(option.Span);
            if (field == ShortOptionField.Terminator)
                first++;
            else if (field == ShortOptionField.Help)
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, kind, commandName, negated)
                    .ConfigureAwait(false);
            else if (field == ShortOptionField.Flags)
                return await ReportUsageAsync(ShellValue.FromBytes(option[..2]), ": invalid option").ConfigureAwait(false);
        }

        if (kind == BuiltinKind.Eval)
        {
            if (first == words.Count)
                return ApplyNegation(EvaluationResult.FromExitCode(0), negated);

            var result = await EvaluateExpandedCommandUnitsAsync(words, first, scope, ScriptExecutionKind.Eval)
                .ConfigureAwait(false);
            return ApplyNegation(result, negated);
        }

        if (first == words.Count)
            return await ReportUsageAsync(ShellValue.Empty, "filename argument required").ConfigureAwait(false);
        if (words[first].IsEmpty)
        {
            // A supplied filename reaches the source boundary even when it is empty.
            scope.Variables.ClearPositionalReplacement();
            await ReportSourceOpenFailureAsync(commandName, string.Empty, new OperationError("No such file or directory\n"),
                scope.Context)
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
        using var positionalScope = argumentCount == 0 ? null : scope.Variables.EnterSourcePositionals(
            words, argumentStart, restorePolicy);
        return ApplyNegation(await ExecuteSourceAsync(commandName, path, scope, positionalScope).ConfigureAwait(false), negated);

        async Task<EvaluationResult> ReportUsageAsync(ShellValue operand, string reason)
        {
            await WriteDiagnosticUnlessStderrClosedAsync([command], [operand, ShellValue.FromText(reason)], _builtinDiagnostics,
                GetNamedSourceDiagnosticPrefix(scope.Context)).ConfigureAwait(false);
            await WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("usage: "
                + BuiltinDocumentation.For(kind).Synopsis(commandName))], _builtinDiagnostics).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }
    }

    private Task ReportSourceOpenFailureAsync(string commandName, string path, OperationError error, EvaluationContext context)
    {
        var standardError = GetStdErrDescriptor();
        if (standardError.IsClosed)
            return Task.CompletedTask;

        var sourcePrefix = GetNamedSourceDiagnosticPrefix(context);
        return error.Kind == OperationErrorKind.IsDirectory
            ? WriteOperationDiagnosticAsync(standardError, ShellValue.Empty, commandName + ": ", path,
                new OperationError("is a directory\n"), sourcePrefix)
            : WriteOperationDiagnosticAsync(standardError, ShellValue.Empty, string.Empty, path, error, sourcePrefix);
    }

    private async Task<(FileOpenResult Result, string Path, bool CurrentSearchEntry)> SearchSourcePathAsync(
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
                    return (open, candidate.Raw, workingDirectory);
            }

            lookupMemory.Resize(0);
            if (separator < 0)
                break;
            searchPath = searchPath[(separator + 1)..];
        }

        return (currentDirectoryAttempt ?? await OpenCandidateAsync(fileName).ConfigureAwait(false),
            fileName.Raw, false);

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

    /// <summary>Admits eval/trap source through execution and any surviving syntax.</summary>
    private async Task<EvaluationResult> EvaluateExpandedCommandUnitsAsync(
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
            if (scope.Context.DiagnosticPath is { } diagnosticPath)
                sourceMemory.OwnDiagnosticPath(diagnosticPath, prefixCurrentDirectory: false);
            ShellSource source;
            if (words.Count - start == 1 && words[start].TryGetCompactArrayCapacity(out var capacity) && capacity == length)
            {
                // The reader's own reservation survives the original value's scope.
                CheckCancellation();
                _ = sourceMemory.Budget.Reserve(capacity);
                source = ShellSource.FromBytes(words[start].Bytes);
            }
            else
            {
                using var builder = new ByteMemoryBuilder((int)length, sourceMemory.Budget);
                for (var index = start; index < words.Count; index++)
                {
                    CheckCancellation();
                    if (index > start) builder.Append((byte)' ');
                    builder.Append(words[index].Bytes);
                }
                source = ShellSource.FromBytes(builder.DetachSnapshot());
            }
            return await EvaluateCommandUnitsAsync(source,
                scope with { Context = scope.Context.WithSourceMemory(sourceMemory) }, kind).ConfigureAwait(false);
        }
        finally { sourceMemory.Release(); }
    }

    private Task<EvaluationResult> EvaluateCommandUnitsAsync(
        ShellSource source, ScopedEvaluation scope, ScriptExecutionKind kind) =>
        EvaluateCommandUnitsAsync(source, scope, kind, kind switch
        {
            ScriptExecutionKind.Eval => SyntaxDiagnosticOrigin.Eval,
            ScriptExecutionKind.Trap => SyntaxDiagnosticOrigin.ExitTrap,
            _ => SyntaxDiagnosticOrigin.Script
        });

    /// <summary>Shares complete-command parsing and execution across the public entry point, source, eval and EXIT actions.</summary>
    private async Task<EvaluationResult> EvaluateCommandUnitsAsync(
        ShellSource source, ScopedEvaluation scope, ScriptExecutionKind kind, SyntaxDiagnosticOrigin diagnosticOrigin)
    {
        // A reached nested parser needs its own stack headroom for syntax guards,
        // independently of the caller's function/eval execution depth.
        if (kind is not (ScriptExecutionKind.Root or ScriptExecutionKind.LegacyCapture))
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var sourceOrEval = kind is ScriptExecutionKind.Source or ScriptExecutionKind.Eval;
        var pipelineReader = sourceOrEval && IsPipelineEntryScope(scope);
        var nestedPipelineReader = pipelineReader && scope.InNestedCommandUnits;
        var directPipelineReader = pipelineReader && !nestedPipelineReader;
        var nestedLoadedReader = IsNestedLoadedReader(kind);
        var outermostLoadedReader = nestedLoadedReader && !scope.InNestedCommandUnits;
        var enclosingReaderTiming = scope.ReaderEntryTiming;
        scope = scope with { InEvalReader = kind == ScriptExecutionKind.Eval };
        if (outermostLoadedReader && _state.ForkKind != ShellForkKind.CommandSubstitution)
            scope = scope with
            {
                PipelineExitReader = new PipelineExitBoundary(_state.RedirectionDepth,
                    scope.Context.FunctionContext?.Depth ?? 0, scope.Context.CallerSource)
            };
        if (nestedLoadedReader)
        {
            var redirectionDepth = _state.RedirectionDepth;
            var functionDepth = scope.Context.FunctionContext?.Depth ?? 0;
            var captureReader = scope.CaptureReader;
            if (_state.ForkKind == ShellForkKind.CommandSubstitution)
            {
                Memory.Charge(40);
                captureReader = new CaptureReaderFrame(redirectionDepth, functionDepth, scope.Context.CallerSource,
                    captureReader);
            }
            scope = scope with
            {
                InNestedCommandUnits = true,
                InnerReaderRedirectionDepth = !outermostLoadedReader || _state.ForkKind == ShellForkKind.CommandSubstitution
                    ? redirectionDepth : null,
                CaptureReader = captureReader,
                ReaderEntryTiming = scope.Timing,
                LoadedSyntaxFunctionDepth = functionDepth,
                LoadedReaderIgnoresErrexit = scope.Context.ErrexitIgnored
            };
        }
        var lineOffset = kind switch
        {
            ScriptExecutionKind.Eval => Math.Max(1, scope.Context.Coordinates.Line) - 1,
            ScriptExecutionKind.ArrayReaderCallback => Math.Max(1, scope.Context.Coordinates.Line) - 1,
            ScriptExecutionKind.Source or ScriptExecutionKind.Trap => 0,
            _ => scope.Context.Coordinates.Offset
        };
        var readerLineOffset = kind == ScriptExecutionKind.Eval
            ? Math.Max(1, scope.Context.Coordinates.LastWordLine) - 1 : lineOffset;

        var result = EvaluationResult.FromExitCode(0);
        var scanContext = new ShellScanContext
        {
            ReadsCommandString = kind != ScriptExecutionKind.Root || diagnosticOrigin == SyntaxDiagnosticOrigin.CommandSubstitution
        };
        using var commands = Parser.TryParseCommandUnits(source.Bytes, scanContext).GetEnumerator();
        var streamedRoot = kind == ScriptExecutionKind.Root && diagnosticOrigin == SyntaxDiagnosticOrigin.Script;
        var usesImmediateParserExit = streamedRoot || sourceOrEval || kind == ScriptExecutionKind.Trap;
        while (true)
        {
            CheckCancellation();
            var parent = scope.Context.SourceMemory;
            var unitMemory = new SourceMemory((parent?.Budget ?? Memory.Budget).CreateScope(), parent);
            scanContext.SyntaxMemory = unitMemory.SyntaxMemory;
            // A completed unit can change errexit before the next parse.
            var stopAfterPrimaryDiagnostic = usesImmediateParserExit && _state.ErrexitEnabled && !scope.Context.ErrexitIgnored;
            scanContext.ReadQuotedRecovery = !stopAfterPrimaryDiagnostic;
            scanContext.ReadArrayRecovery = (streamedRoot || sourceOrEval)
                && _state.ForkKind == null && !stopAfterPrimaryDiagnostic;
            try
            {
                if (!commands.MoveNext())
                    break;
                var failedUnit = commands.Current as CommandUnitParseResult.Failed;
                var path = unitMemory.DiagnosticPath;
                var diagnosticLine = failedUnit == null ? 0 : lineOffset + failedUnit.DiagnosticLine;
                var readDiagnostics = commands.Current.ReadDiagnostics;
                var unitCoordinates = BeginCommandUnit(
                    new CommandCoordinates(commands.Current.Locations, lineOffset, lineOffset + 1, lineOffset + 1)
                    {
                        UnitReadLine = commands.Current.UnitReadLine == 0 ? 0 : readerLineOffset + commands.Current.UnitReadLine
                    });
                ApplyReadFunctionBraceLine(lineOffset, readDiagnostics.FunctionBraceLine);
                if (readDiagnostics.HereDocumentWarnings is { } warnings)
                {
                    var adjacentCapture = kind == ScriptExecutionKind.Root && diagnosticOrigin == SyntaxDiagnosticOrigin.CommandSubstitution;
                    await ReportHereDocumentWarningsAsync(warnings, lineOffset, path,
                        useDocumentLine: adjacentCapture, fixedPrefix: null).ConfigureAwait(false);
                }
                if (readDiagnostics.CompoundItems is { } items
                    && await ReportCompoundItemReadDiagnosticsAsync(items, scope, lineOffset).ConfigureAwait(false) is { } readFailure)
                    return nestedLoadedReader
                        ? await ExitLoadedExpansionSyntaxAsync(scope, readFailure).ConfigureAwait(false)
                        : EvaluationResult.Exit(readFailure.ExitCode);
                var skipExecution = result.Flow is FlowControlKind.Break or FlowControlKind.Continue;
                ICommand command;
                if (failedUnit != null)
                {
                    var failure = failedUnit.Failure;
                    var detail = stopAfterPrimaryDiagnostic ? SyntaxDiagnosticDetail.PrimaryOnly : SyntaxDiagnosticDetail.Complete;
                    if (failure.CaptureWarnings is { } captureWarnings)
                        await ReportCaptureHereDocumentWarningsAsync(captureWarnings, path, lineOffset,
                            failure.UnclosedDelimiter == null ? diagnosticLine : diagnosticLine - 1).ConfigureAwait(false);
                    if (failure.UnclosedDelimiter is { } captureEnd)
                    {
                        await ReportUnclosedDelimiterAsync(captureEnd, diagnosticLine, path, diagnosticOrigin, detail).ConfigureAwait(false);
                    }
                    else if (failure.UnexpectedToken is { } token)
                        await ReportUnexpectedSyntaxAsync(token, path, diagnosticOrigin, diagnosticLine, detail)
                            .ConfigureAwait(false);
                    else if (failure.ArithmeticForHeader is { } header)
                        await ReportArithmeticForHeaderAsync(header, path, diagnosticOrigin, diagnosticLine, detail).ConfigureAwait(false);
                    else if (failure.ConditionalEndOfInput is { } conditionalEnd)
                        await ReportConditionalEndOfInputAsync(conditionalEnd, path, diagnosticOrigin, lineOffset, detail).ConfigureAwait(false);
                    else if (failedUnit.DiagnosticLine != 0 && (failure.UnexpectedEndOfInput
                        || diagnosticOrigin == SyntaxDiagnosticOrigin.CommandSubstitution
                        || failure.Kind == ParserFailureKind.RedirectionOperand))
                    {
                        var eofOrigin = failure.UnexpectedEndOfInput && diagnosticOrigin == SyntaxDiagnosticOrigin.Script
                            ? SyntaxDiagnosticOrigin.ScriptLine : diagnosticOrigin;
                        await ReportReaderGrammarEndOfInputAsync(path, eofOrigin, diagnosticLine).ConfigureAwait(false);
                    }
                    else
                        await ReportSyntaxErrorAsync(failedUnit.Error).ConfigureAwait(false);
                    if (stopAfterPrimaryDiagnostic)
                    {
                        // The parser exits from the active reader, abandoning its pending timing reports.
                        SuppressPendingTimings(scope.Timing);
                        return await RunExitTrapAsync(scope, EvaluationResult.Exit(2)).ConfigureAwait(false);
                    }
                    if (failedUnit.QuotedRecovery is { } recovery)
                        await ReportUnclosedDelimiterAsync(recovery.Delimiter, lineOffset + recovery.Line, path,
                            diagnosticOrigin, detail).ConfigureAwait(false);
                    var quotedArrayFailure = failure.Kind == ParserFailureKind.ArrayQuotedSubstitutionForHeader;
                    var arrayDelimiterFailure = failure.Kind == ParserFailureKind.ArrayDelimiter
                        || quotedArrayFailure && failedUnit.QuotedRecovery.HasValue;
                    var parserStatus = arrayDelimiterFailure ? 1 : 2;
                    var exitsSubstitution = failure.ExitsSubstitution || quotedArrayFailure && !arrayDelimiterFailure;
                    var exitsLoadedReader = nestedLoadedReader && (exitsSubstitution
                        || sourceOrEval && arrayDelimiterFailure && _state.ForkKind.HasValue);
                    var reportsGrammarStatus = failure.UnexpectedToken != null || failure.ConditionalEndOfInput is { DoubleQuoted: null }
                        || failedUnit.DiagnosticLine != 0 && failure.UnclosedDelimiter == null;
                    var publishesPipelineReaderStatus = directPipelineReader && !exitsLoadedReader
                        && (failure.ArithmeticForHeader.HasValue || reportsGrammarStatus);
                    var publishesLoadedParserStatus = sourceOrEval && exitsLoadedReader
                        || publishesPipelineReaderStatus;
                    var loadedParserStatus = failure.Kind == ParserFailureKind.SubstitutionGrammar
                        || publishesPipelineReaderStatus && !failure.ArithmeticForHeader.HasValue
                        ? BuiltinSyntaxErrorStatus : parserStatus;
                    if (failedUnit.ResumesAfterArrayToken || failure.IsDirectQuotedSubstitutionFailure
                        || publishesLoadedParserStatus || exitsLoadedReader && failure.ArithmeticForHeader.HasValue
                        || streamedRoot && (arrayDelimiterFailure || failure.ArithmeticForHeader.HasValue || reportsGrammarStatus))
                    {
                        var publishedStatus = publishesLoadedParserStatus ? loadedParserStatus : parserStatus;
                        PreparePipelineStatusStorage(1);
                        scope.Variables.SetPipelineStatuses([publishedStatus], scope.Context, _cancellationToken);
                        _state.LastExitCode = publishedStatus;
                    }
                    if (failedUnit.ResumesAfterArrayToken)
                    {
                        result = result.WithExitCode(parserStatus);
                        continue;
                    }
                    if (failedUnit.CompletedPrefix is { } prefix && !skipExecution)
                        command = prefix;
                    else
                    {
                        if (kind == ScriptExecutionKind.LegacyCapture && exitsSubstitution)
                            return EvaluationResult.Exit(1);
                        if (exitsLoadedReader)
                        {
                            // Unwind inner readers before running EXIT in the directly invoked builtin.
                            if (nestedPipelineReader)
                                return EvaluationResult.ExitAfterNestedPipelineReader();
                            // A pipeline builtin can exit with 1 while PIPESTATUS still holds the parser status.
                            var status = directPipelineReader ? 1 : loadedParserStatus;
                            return await ExitLoadedSyntaxFailureAsync(scope, status, substitutionStatus: 1,
                                unwindPipelineBuiltin: !sourceOrEval).ConfigureAwait(false);
                        }
                        return result.WithExitCode(parserStatus);
                    }
                }
                else
                    command = ((CommandUnitParseResult.Parsed)commands.Current).Command;

                // Loop flow skips execution but still checks the remaining source/eval syntax.
                if (skipExecution)
                    continue;

                // The completed unit can outlive this reader through a function definition.
                unitMemory.SyntaxMemory.ReleaseUnusedCapacityOnPublication();
                result = await EvaluateCommandAsync(command,
                    scope with
                    {
                        Context = scope.Context.WithSourceMemory(unitMemory).WithCoordinates(unitCoordinates)
                    }, negated: false)
                    .ConfigureAwait(false);
                if (result.Flow == FlowControlKind.Return)
                    return kind == ScriptExecutionKind.Source ? EvaluationResult.FromExitCode(result.ExitCode) : result;
                if (_state.ForkKind == null
                    && (kind == ScriptExecutionKind.Root && result.DiscardsCommandUnit
                        || IsLoadedReader(kind) && result.DiscardsLoadedCommandUnit))
                {
                    // The parser retains physical source locations. Rebase only this
                    // reader's projection after the remainder of the unit is discarded.
                    if (commands.Current.UnitReadLine != 0)
                    {
                        var adjustment = result.ReaderLineAdjustment(lineOffset + commands.Current.UnitReadLine);
                        lineOffset += adjustment;
                        readerLineOffset += adjustment;
                    }
                    result = await RecoverDiscardedCommandUnitAsync(scope, result, IsLoadedReader(kind)).ConfigureAwait(false);
                }
                else if (result.Flow == FlowControlKind.Exit)
                {
                    if (nestedLoadedReader && _state.FatalExpansion is { UnwindsLoadedReader: true })
                    {
                        // Timers around this invocation run through its still-active descriptors.
                        await ReportPendingTimingsAsync(scope.Timing, enclosingReaderTiming).ConfigureAwait(false);
                        if (outermostLoadedReader && _state.ForkKind != ShellForkKind.CommandSubstitution)
                            return await RunExitTrapAsync(scope with { Timing = null }, result, ExitTrapKind.Expansion)
                                .ConfigureAwait(false);
                        return result;
                    }
                    if (outermostLoadedReader && result.DefersLoadedErrexit && result.DeferredErrexit == null)
                        return await RunErrexitExitTrapAsync(scope, result, fromLoadedReader: true).ConfigureAwait(false);
                    if (directPipelineReader && result.ExitsNestedPipelineReader)
                        return await RunExitTrapAsync(scope, result).ConfigureAwait(false);
                    if (nestedLoadedReader
                        && _state.FatalExpansion is { Kind: ExpansionFailureKind.ReaderSyntax } failure)
                        return await ExitLoadedExpansionSyntaxAsync(scope, failure).ConfigureAwait(false);
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

    private ValueTask<EvaluationResult> ExitLoadedExpansionSyntaxAsync(ScopedEvaluation scope, ExpansionFailure failure)
    {
        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        var status = ExitActionStatus(failure);
        return ExitLoadedSyntaxFailureAsync(scope, status, substitutionStatus: status & byte.MaxValue, unwindPipelineBuiltin: false);
    }

    private async ValueTask<EvaluationResult> ExitLoadedSyntaxFailureAsync(ScopedEvaluation scope, int status,
        int substitutionStatus, bool unwindPipelineBuiltin)
    {
        // A capturing child unwinds the builtin before running its own EXIT action.
        if (_state.ForkKind == ShellForkKind.CommandSubstitution)
            return EvaluationResult.Exit(substitutionStatus);
        if (unwindPipelineBuiltin && _state.ForkKind == ShellForkKind.PipelineSimpleCommand)
            return EvaluationResult.Exit(1);

        // In the invoking shell, EXIT sees the full parser status and the active
        // builtin scope. The result crossing the shell boundary uses its low byte.
        var exit = await RunExitTrapAsync(scope, EvaluationResult.Exit(status)).ConfigureAwait(false);
        return exit.WithExitCode(exit.ExitCode & byte.MaxValue);
    }

    /// <summary>Recovers unit-scoped expansion failures while preserving root versus nested status rules.</summary>
    private ValueTask<EvaluationResult> RecoverScriptExpansionFailureAsync(
        EvaluationResult result, ScriptExecutionKind kind, ScopedEvaluation scope)
    {
        // Loaded readers in a fork abort the child after publishing to its active bindings.
        if (_state.ForkKind != null && IsNestedLoadedReader(kind)
            && _state.FatalExpansion is { Kind: ExpansionFailureKind.BarePipelinePrefixSyntax })
            return PublishAndExitAsync();

        return GetRecoverableScriptExpansionStatus(kind, scope) is { } status
            ? RecoverExpansionFailureAsync(status, scope, IsLoadedReader(kind))
            : ValueTask.FromResult(result);

        async ValueTask<EvaluationResult> PublishAndExitAsync()
        {
            await RecoverExpansionFailureAsync(result.ExitCode, scope, loadedReader: true).ConfigureAwait(false);
            return result;
        }
    }

    private int? GetRecoverableScriptExpansionStatus(ScriptExecutionKind kind, ScopedEvaluation scope)
    {
        if (RecoverableCommandSubstitutionStatus is { } substitutionStatus)
            return substitutionStatus;
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
                or ExpansionFailureKind.ParameterTarget or ExpansionFailureKind.NamerefDefault } failure)
            return null;

        // These expansion failures terminate the fork's reader, including a capture's source or eval.
        if (_state.ForkKind != null
            && failure.Kind is ExpansionFailureKind.Arithmetic or ExpansionFailureKind.WordSyntax or ExpansionFailureKind.ParameterTarget)
            return null;

        // Recovery uses the caller's errexit state, including for tested commands within the unit.
        if (_state.ErrexitEnabled && !scope.Context.ErrexitIgnored
            && failure.Kind is ExpansionFailureKind.WordSyntax or ExpansionFailureKind.ParameterTarget)
            return null;

        // Nameref defaults fail with 1 at script boundaries, but with 2 at the root.
        return failure.Kind == ExpansionFailureKind.NamerefDefault
            && kind is not (ScriptExecutionKind.Root or ScriptExecutionKind.LegacyCapture) ? 1 : failure.ExitCode;
    }

    private static bool IsLoadedReader(ScriptExecutionKind kind) =>
        kind == ScriptExecutionKind.Trap || IsNestedLoadedReader(kind);

    private static bool IsNestedLoadedReader(ScriptExecutionKind kind) =>
        kind is ScriptExecutionKind.Eval or ScriptExecutionKind.Source or ScriptExecutionKind.ArrayReaderCallback;

    private int? RecoverableCommandSubstitutionStatus => _state.FatalExpansion switch
    {
        { Kind: ExpansionFailureKind.CommandSubstitutionSyntax, ExitCode: 1, UseInheritedPipelineExitAction: false } => 1,
        { Kind: ExpansionFailureKind.BarePipelinePrefixSyntax, ExitCode: var status } when _state.ForkKind == null => status,
        _ => null
    };

    private int? RecoverableRootExpansionStatus => _state.ForkKind != null ? null : _state.FatalExpansion switch
    {
        { Kind: ExpansionFailureKind.IntegerAssignment or ExpansionFailureKind.ArrayIndex, ExitCode: var status } => status,
        { Kind: ExpansionFailureKind.ParameterDefaultAssignment, ExitCode: 2 } => 2,
        { Kind: ExpansionFailureKind.AssignmentTarget, CanResumeUntestedUnit: true } => 1,
        _ => null
    };

    private ValueTask<EvaluationResult> RecoverDiscardedCommandUnitAsync(
        ScopedEvaluation scope, EvaluationResult result, bool loadedReader) =>
        RecoverDiscardedCommandUnitAsync(scope, resetStatus: result.ResetsReaderStatus || loadedReader && result.DiscardsHereDocumentUnit);

    private async ValueTask<EvaluationResult> RecoverDiscardedCommandUnitAsync(ScopedEvaluation scope, bool resetStatus)
    {
        // Loaded readers and word syntax failures can reset a previously nonzero status.
        var status = _state.LastExitCode.GetValueOrDefault();
        if (status != 0 && !resetStatus)
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

        if (kind == ExpansionFailureKind.BarePipelinePrefixSyntax)
        {
            // A fresh root has no previous foreground status to replace.
            if (loadedReader || _state.LastExitCode.HasValue)
                return await RecoverDiscardedCommandUnitAsync(scope, resetStatus: loadedReader).ConfigureAwait(false);
            _state.LastExitCode = exitCode;
        }
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

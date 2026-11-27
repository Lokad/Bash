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
    internal long RetainedTrapMemoryBytes => _state.Traps?.EstimatedMemoryBytes ?? 0;

    private async Task<EvaluationResult> EvaluateBuiltinTrapAsync(IReadOnlyList<ShellValue> words, EvaluationContext context)
    {
        var command = words[0];
        var sourcePrefix = GetBuiltinDiagnosticPrefix(context);
        var list = false;
        var options = new ShortOptionCursor(words);
        while (options.TryReadFlag(out var flag))
        {
            if (flag.Span[0] != (byte)'p')
                return await ReportUsageAsync(flag).ConfigureAwait(false);
            list = true;
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Trap, "trap", negated: false)
                .ConfigureAwait(false);

        var index = options.ArgumentIndex;
        var traps = _state.Traps;
        var status = 0;
        if (list || index == words.Count)
        {
            var stdout = GetStdOutDescriptor();
            var output = DescriptorWriteResult.Open;
            if (index == words.Count)
            {
                if (traps == null)
                    return EvaluationResult.FromExitCode(0);
                foreach (var condition in ShellTraps.Conditions)
                {
                    output = await PrintAsync(condition, stdout).ConfigureAwait(false);
                    if (!output.CanAcceptMore)
                        break;
                }
            }
            else
            {
                for (; index < words.Count; index++)
                {
                    if (!ShellTraps.TryParse(words[index], out var condition))
                    {
                        await InvalidConditionAsync(words[index]).ConfigureAwait(false);
                        status = 1;
                    }
                    else if (output.CanAcceptMore)
                    {
                        output = await PrintAsync(condition, stdout).ConfigureAwait(false);
                        // Bash checks later signal names before reporting a shell-closed stdout.
                        if (!output.CanAcceptMore && !(stdout.IsClosed && output.IsError))
                            break;
                    }
                }
            }
            if (output.IsError)
            {
                await ReportBuiltinWriteFailureAsync(command, output, sourcePrefix).ConfigureAwait(false);
                status = 1;
            }
            return EvaluationResult.FromExitCode(status);
        }

        // The pinned Bash builds use this numeric range for the legacy reset syntax,
        // including numbers outside the emulator's supported condition set.
        const int MaximumLegacySignalNumber = 64;
        var action = words[index++];
        var actionBytes = action.Bytes.Span;
        var numericReset = actionBytes.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0
            && Integers.TryParseNumber(actionBytes, out var number) && number <= MaximumLegacySignalNumber;
        var reset = numericReset || actionBytes.SequenceEqual("-"u8);
        if (index == words.Count)
        {
            if (!ShellTraps.TryParse(action, out _))
                return await ReportUsageAsync(invalidFlag: default).ConfigureAwait(false);
            reset = true;
            index--;
        }
        else if (numericReset)
            index--;

        traps?.DiscardInheritedListing();
        try
        {
            for (; index < words.Count; index++)
            {
                if (!ShellTraps.TryParse(words[index], out var condition))
                {
                    await InvalidConditionAsync(words[index]).ConfigureAwait(false);
                    status = 1;
                }
                else if (reset)
                    traps?.Reset(condition);
                else
                    _state.SetTrap(condition, action, Memory);
            }
        }
        finally
        {
            _state.RetireEmptyTraps();
        }
        return EvaluationResult.FromExitCode(status);

        async Task<EvaluationResult> ReportUsageAsync(ReadOnlyMemory<byte> invalidFlag)
        {
            var diagnostic = invalidFlag.IsEmpty
                ? WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("usage: "),
                    ShellValue.FromText(BuiltinDocumentation.For(BuiltinKind.Trap).Synopsis("trap"))], scope: null)
                : WriteInvalidShortOptionUnlessStderrClosedAsync(command, ShellValue.FromByte((byte)'-'), invalidFlag,
                    BuiltinKind.Trap, "trap", sourcePrefix);
            var written = await diagnostic.ConfigureAwait(false);
            return EvaluationResult.FromExitCode(written.IsError ? 1 : 2);
        }

        Task InvalidConditionAsync(ShellValue operand) =>
            WriteDiagnosticAsync([command], [operand, ShellValue.FromText(": invalid signal specification")],
                _builtinDiagnostics, ClosedStderrHandling.AttemptWrite, sourcePrefix);

        async ValueTask<DescriptorWriteResult> PrintAsync(TrapCondition condition, FileDescriptor stdout)
        {
            if (traps == null || !traps.ListedHandlers.TryGetValue(condition, out var handler))
                return DescriptorWriteResult.Open;
            var name = ShellTraps.GetNameBytes(condition);
            var handlerBytes = handler.Bytes.Span;
            var quotedLength = ShellArgumentEscaping.GetBashSingleQuotedLength(handlerBytes);
            var length = "trap -- "u8.Length + quotedLength + 1 + name.Length + 1;
            if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            using var output = new BoundedByteMemoryBuilder((int)length, (int)length, Memory.Budget);
            if (!output.TryAppend("trap -- "u8)
                || !ShellArgumentEscaping.TryAppendBashSingleQuoted(output, handlerBytes, quotedLength)
                || !output.TryAppend((byte)' ')
                || !output.TryAppend(name)
                || !output.TryAppend((byte)'\n'))
                throw new InvalidOperationException("Trap listing exceeded its reserved size.");
            return await ExecuteWriteAsync(stdout, output.ToMemory()).ConfigureAwait(false);
        }
    }

    private enum ExitTrapKind { Completion, Errexit, LoadedErrexit, Expansion }

    private static int ExitActionStatus(ExpansionFailure failure) =>
        failure is { Kind: ExpansionFailureKind.ReaderSyntax, ExitCode: 1 } ? BuiltinSyntaxErrorStatus : failure.ExitCode;

    private ValueTask<EvaluationResult> RunExitTrapAsync(ScopedEvaluation scope, EvaluationResult result) =>
        RunExitTrapAsync(scope, result, ExitTrapKind.Completion);

    private ValueTask<EvaluationResult> RunErrexitExitTrapAsync(ScopedEvaluation scope, EvaluationResult result,
        bool fromLoadedReader) =>
        RunExitTrapAsync(scope, result.WithDeferredErrexit(null, fromLoadedReader: false),
            fromLoadedReader ? ExitTrapKind.LoadedErrexit : ExitTrapKind.Errexit);

    /// <summary>Runs EXIT once with the bindings and descriptors selected by the reason for termination.</summary>
    private async ValueTask<EvaluationResult> RunExitTrapAsync(ScopedEvaluation scope, EvaluationResult result, ExitTrapKind kind)
    {
        var failure = _state.FatalExpansion;
        var useInheritedPipelineAction = kind == ExitTrapKind.Completion && failure is { UseInheritedPipelineExitAction: true };
        var parameterTransform = useInheritedPipelineAction
            && failure is { Kind: ExpansionFailureKind.InvalidParameterTransform };
        var status = kind == ExitTrapKind.Completion && result.Flow == FlowControlKind.Exit
            && failure is { Kind: ExpansionFailureKind.ReaderSyntax } readerFailure
                ? ExitActionStatus(readerFailure) : result.ExitCode;
        if (result.SkipsExitTrap || _state.Traps is not { } traps || _state.ExitedThroughExec
            || !traps.TryBeginExit(status, useInheritedPipelineAction, out var action))
            return result;

        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        _state.LastExitCode = status;
        try
        {
            if (kind != ExitTrapKind.Completion)
                await ReportPendingTimingsAsync(scope.Timing, stop: null).ConfigureAwait(false);

            var function = scope.Context.FunctionContext;
            var prefixes = scope.Context.GlobalPrefixes;
            var callerSource = scope.Context.CallerSource;
            var returnTarget = scope.Context.ReturnTarget;
            var sourceMemory = scope.Context.SourceMemory;
            var readerDepth = kind == ExitTrapKind.LoadedErrexit ? scope.InnerReaderRedirectionDepth : null;
            var readerUnwind = readerDepth is { } reachedDepth ? new LoadedExitUnwind(scope, reachedDepth) : default;
            var keepsInheritedBindings = parameterTransform
                && (_state.ErrexitEnabled || _pipelineExitBoundary.KeepsReachedBindings);
            if (useInheritedPipelineAction && !keepsInheritedBindings)
            {
                var retainDepth = scope.LoadedSyntaxFunctionDepth;
                if (parameterTransform)
                {
                    if (_pipelineExitRedirections is { } redirections)
                        _state.RestorePipelineExitRedirections(redirections, Memory);
                    retainDepth = _pipelineExitBoundary.FunctionDepth;
                    callerSource = _pipelineExitBoundary.Source;
                }
                (function, prefixes) = RestoreBindings(scope, retainDepth, restorePositionals: true);
                var functionDepth = function?.Depth ?? 0;
                returnTarget = callerSource?.FunctionDepth == functionDepth ? ReturnTargetKind.Source
                    : function != null ? ReturnTargetKind.Function : ReturnTargetKind.None;
            }
            else if (kind is ExitTrapKind.Errexit or ExitTrapKind.LoadedErrexit)
            {
                if (readerDepth.HasValue)
                    (returnTarget, sourceMemory) = RestoreLoadedReaderExitFrame(scope, readerUnwind);
                if (!_state.KeepsLocalsOnErrexit || kind == ExitTrapKind.LoadedErrexit && function != null)
                {
                    (function, prefixes) = RestoreBindings(scope, retainDepth: null, restorePositionals: false);
                    callerSource = null;
                }
            }

            // EXIT runs independently of its caller's tested-status suppression. Keep
            // the reached flow targets and diagnostic source while copying the action.
            var trapScope = scope with
            {
                Context = new EvaluationContext(scope.Context.LoopDepth, function, returnTarget)
                {
                    GlobalPrefixes = prefixes,
                    CallerSource = callerSource,
                    SourceMemory = sourceMemory
                },
                Timing = null,
                ReaderEntryTiming = null,
                ErrexitRedirections = null,
                InDirectPipelineReader = false
            };
            var handler = readerDepth > scope.PipelineExitReader.RedirectionDepth
                ? await EvaluateWithReaderRedirectionsAsync(trapScope, readerUnwind).ConfigureAwait(false)
                : await EvaluateExpandedCommandUnitsAsync([action], 0, trapScope, ScriptExecutionKind.Trap).ConfigureAwait(false);
            // A discarded action leaves the pending exit status in effect.
            if (!handler.DiscardsCommandUnit && (handler.Flow is FlowControlKind.Exit or FlowControlKind.Return)
                && (_state.FatalExpansion == null
                    || _state.ErrexitEnabled && _state.FatalExpansion is not
                    { Kind: ExpansionFailureKind.IntegerAssignment or ExpansionFailureKind.ArrayIndex
                        or ExpansionFailureKind.ParameterDefaultAssignment }))
                result = result.WithExitCode(handler.ExitCode);
            return result;
        }
        catch (MemoryBudgetExceededException)
        {
            Memory.ReleaseUnusedCapacity();
            await ReportMemoryRefusalAsync("shell").ConfigureAwait(false);
            result = result.WithExitCode(1);
            return result;
        }
        finally
        {
            traps.EndExit();
            // Later command finalization reads the fatal status as the result unwinds.
            _state.FatalExpansion = kind == ExitTrapKind.Expansion
                && failure is { IsTerminalParameterFailure: true } parameterFailure
                    ? parameterFailure with { ExitCode = result.ExitCode } : failure;
            _state.LastExitCode = result.ExitCode;
        }

        (ReturnTargetKind Target, SourceMemory? DiagnosticSource) RestoreLoadedReaderExitFrame(
            ScopedEvaluation active, LoadedExitUnwind unwind)
        {
            var returnTarget = active.Context.ReturnTarget;
            var diagnosticSource = active.Context.SourceMemory;
            // Calls between the reader entries unwind their control targets. The arguments
            // and diagnostic source each unwind from the newest reached call.
            var callbacks = new ExitCallFrames(active.Context);
            var savedArguments = callbacks;
            var diagnosticFrames = callbacks;
            var preserveCurrentArguments = active.Variables.GlobalPositionalsReplaced;
            Variables.PositionalParametersScope? argumentsToRestore = null;
            var unwindsArguments = false;
            while (unwind.TryTake(out var outer, out var inner))
            {
                callbacks.TrimTo(inner);
                while (callbacks.TryPopAbove(outer, out var call))
                {
                    returnTarget = callbacks.ReturnTarget;
                    if (diagnosticFrames.TryPop(out var diagnostic))
                        diagnosticSource = diagnostic.CallerSourceMemory;
                    if (call.Positionals == null)
                        continue;

                    // The reached command can have left saved arguments above the
                    // callbacks selected by the reader unwind.
                    while (savedArguments.TryPop(out var saved))
                    {
                        if (saved.Positionals == null)
                            continue;
                        if (!call.IsSource || !preserveCurrentArguments)
                            argumentsToRestore = saved.Positionals;
                        preserveCurrentArguments = false;
                        unwindsArguments = true;
                        break;
                    }
                }
            }
            if (argumentsToRestore != null)
                argumentsToRestore.CopySavedParametersTo(active.Variables, Memory);
            else if (unwindsArguments)
                active.Variables.ClearPositionalReplacement();
            return (returnTarget, diagnosticSource);
        }

        async Task<EvaluationResult> EvaluateWithReaderRedirectionsAsync(ScopedEvaluation active, LoadedExitUnwind unwind)
        {
            using var memory = Memory.BeginCommand(active);
            var table = _state.CaptureLoadedExitTable(unwind, Memory);
            if (table == null)
                return await EvaluateExpandedCommandUnitsAsync([action], 0, active, ScriptExecutionKind.Trap).ConfigureAwait(false);

            // The enclosing command scopes still own the borrowed handles through the action.
            _state.PushRedirectionTable(table, new DescriptorHandleOwner(), new HashSet<LogicalDescriptor>(), Memory);
            var redirections = new RedirectionScope(this, active: true, [], []);
            var handler = EvaluationResult.FromExitCode(1);
            OperationError? closeError;
            try
            {
                handler = await EvaluateExpandedCommandUnitsAsync([action], 0, active, ScriptExecutionKind.Trap).ConfigureAwait(false);
            }
            finally
            {
                closeError = await redirections.CloseAsync(handler.ExitCode != 0).ConfigureAwait(false);
            }
            return await ApplyCloseErrorAsync(handler, closeError).ConfigureAwait(false);
        }

        (FunctionContext? Function, GlobalPrefixBindings? Prefixes) RestoreBindings(
            ScopedEvaluation active, int? retainDepth, bool restorePositionals)
        {
            var prefix = active.Context.GlobalPrefixes;
            var frame = active.Context.FunctionContext;
            Variables.PositionalParametersScope? positionals = null;
            var positionalFunctionDepth = int.MaxValue;
            for (; frame != null && frame.Depth > retainDepth.GetValueOrDefault(); frame = frame.Enclosing)
            {
                if (frame.CallerPositionals is { } savedPositionals)
                {
                    positionals = savedPositionals;
                    positionalFunctionDepth = frame.Depth;
                }
                var temporaryScriptBindings = frame.Locals.IsTemporaryPrefixScope;
                var bindings = temporaryScriptBindings && prefix?.FunctionDepth == frame.Depth
                    ? prefix.Bindings : frame.PrefixBindings;
                CompletePromotions(frame, frame.Enclosing, active.Variables, bindings);
                if (!temporaryScriptBindings)
                    frame.Locals.Restore();
                frame.BindingsRestoredForExit = true;
                frame.PrefixBindings?.Dispose();

                while (prefix?.FunctionDepth == frame.Depth)
                {
                    prefix.Bindings.Dispose();
                    prefix = prefix.Enclosing;
                }
            }
            for (; prefix != null && (retainDepth == null || prefix.FunctionDepth > retainDepth.Value); prefix = prefix.Enclosing)
                prefix.Bindings.Dispose();
            if (restorePositionals)
            {
                if (parameterTransform)
                {
                    // A source's saved arguments are newer than its containing function's saved arguments.
                    for (var source = active.Context.CallerSource;
                         source != null && !ReferenceEquals(source, _pipelineExitBoundary.Source);
                         source = source.Enclosing)
                    {
                        if (source.FunctionDepth < positionalFunctionDepth && source.CallerPositionals is { } savedPositionals)
                            positionals = savedPositionals;
                    }
                }
                positionals?.CopySavedParametersTo(active.Variables, Memory);
            }
            return (frame, prefix);
        }
    }

    private readonly record struct ExitCall(bool IsSource,
        Variables.PositionalParametersScope? Positionals, SourceMemory? CallerSourceMemory);

    private struct ExitCallFrames(EvaluationContext context)
    {
        private FunctionContext? _function = context.FunctionContext;
        private SourceCallerFrame? _source = context.CallerSource;

        private readonly bool SourceIsNewest =>
            _source != null && _source.FunctionDepth >= (_function?.Depth ?? 0);

        public ReturnTargetKind ReturnTarget
        {
            get
            {
                SkipTemporaryFunctions();
                return SourceIsNewest ? ReturnTargetKind.Source : _function != null ? ReturnTargetKind.Function : ReturnTargetKind.None;
            }
        }

        public void TrimTo(PipelineExitBoundary boundary)
        {
            var depth = boundary.FunctionDepth.GetValueOrDefault();
            while (_function is { } function && function.Depth > depth)
                _function = function.Enclosing;
            while (_source is { } source && !ReferenceEquals(source, boundary.Source))
                _source = source.Enclosing;
            while (_source is { } source && source.FunctionDepth > depth)
                _source = source.Enclosing;
        }

        public bool TryPopAbove(PipelineExitBoundary boundary, out ExitCall call)
        {
            SkipTemporaryFunctions();
            if (SourceIsNewest && _source is { } source)
            {
                if (!ReferenceEquals(source, boundary.Source))
                {
                    _source = source.Enclosing;
                    call = new ExitCall(IsSource: true, source.CallerPositionals, source.CallerSourceMemory);
                    return true;
                }
            }
            else if (_function is { } function && function.Depth > boundary.FunctionDepth.GetValueOrDefault())
            {
                _function = function.Enclosing;
                call = new ExitCall(IsSource: false, function.CallerPositionals, function.CallerSourceMemory);
                return true;
            }
            call = default;
            return false;
        }

        public bool TryPop(out ExitCall call) => TryPopAbove(default, out call);

        private void SkipTemporaryFunctions()
        {
            while (_function is { CallerPositionals: null } frame)
                _function = frame.Enclosing;
        }
    }
}

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

    private async Task<EvaluationResult> EvaluateBuiltinTrapAsync(IReadOnlyList<ShellValue> words)
    {
        var command = words[0];
        var index = 1;
        var list = false;
        if (index < words.Count && words[index].Bytes.Span is [(byte)'-', _, ..])
        {
            var options = new CommandLineOptionParser(words, allowSingleDashLongOptions: false);
            options.TryReadRaw(out _);
            index = words.Count;
            while (options.TryRead(out var option))
            {
                if (option.Kind == CommandLineOptionKind.Argument)
                {
                    index = options.NextArgumentIndex - 1;
                    break;
                }
                if (option.Kind == CommandLineOptionKind.ShortOption && option.ShortName == (byte)'p')
                {
                    list = true;
                    continue;
                }
                if (option.Kind == CommandLineOptionKind.LongOption && !option.HasValue
                    && option.LongName.Bytes.Span.SequenceEqual("help"u8))
                    return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Trap, "trap", negated: false)
                        .ConfigureAwait(false);

                var invalid = option.Kind == CommandLineOptionKind.ShortOption
                    ? ShellValue.FromBytes(new byte[] { (byte)'-', option.ShortName }) : ShellValue.FromText("--");
                return await ReportUsageAsync(invalid).ConfigureAwait(false);
            }
        }

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
                await ReportBuiltinWriteFailureAsync(command, output).ConfigureAwait(false);
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
                return await ReportUsageAsync(invalidOption: null).ConfigureAwait(false);
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

        async Task<EvaluationResult> ReportUsageAsync(ShellValue? invalidOption)
        {
            var synopsis = ShellValue.FromText(BuiltinDocumentation.For(BuiltinKind.Trap).Synopsis("trap"));
            var diagnostic = invalidOption is { } operand
                ? WriteDiagnosticUnlessStderrClosedAsync([command],
                    [operand, ShellValue.FromText(": invalid option\ntrap: usage: "), synopsis], scope: null)
                : WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("usage: "), synopsis], scope: null);
            var written = await diagnostic.ConfigureAwait(false);
            return EvaluationResult.FromExitCode(written.IsError ? 1 : 2);
        }

        Task InvalidConditionAsync(ShellValue operand) =>
            ReportExpansionDiagnosticAsync([command], [operand, ShellValue.FromText(": invalid signal specification")]);

        async ValueTask<DescriptorWriteResult> PrintAsync(TrapCondition condition, FileDescriptor stdout)
        {
            if (traps == null || !traps.ListedHandlers.TryGetValue(condition, out var handler))
                return DescriptorWriteResult.Open;
            var name = ShellTraps.GetNameBytes(condition);
            var length = "trap -- "u8.Length + ShellArgumentEscaping.GetLiteralSingleQuotedLength(handler.Bytes.Span)
                + 1 + name.Length + 1;
            if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            using var output = new BoundedByteMemoryBuilder((int)length, (int)length, Memory.Budget);
            if (!output.TryAppend("trap -- "u8)
                || !ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, handler.Bytes.Span)
                || !output.TryAppend((byte)' ')
                || !output.TryAppend(name)
                || !output.TryAppend((byte)'\n'))
                throw new InvalidOperationException("Trap listing exceeded its reserved size.");
            return await ExecuteWriteAsync(stdout, output.ToMemory()).ConfigureAwait(false);
        }
    }

    private enum ExitTrapKind { Completion, Errexit, Expansion }

    private static int ExitActionStatus(ExpansionFailure failure) =>
        failure is { Kind: ExpansionFailureKind.ReaderSyntax, ExitCode: 1 } ? BuiltinSyntaxErrorStatus : failure.ExitCode;

    private ValueTask<EvaluationResult> RunExitTrapAsync(ScopedEvaluation scope, EvaluationResult result) =>
        RunExitTrapAsync(scope, result, ExitTrapKind.Completion);

    /// <summary>Runs EXIT once with the bindings and descriptors selected by the reason for termination.</summary>
    private async ValueTask<EvaluationResult> RunExitTrapAsync(ScopedEvaluation scope, EvaluationResult result, ExitTrapKind kind)
    {
        var failure = _state.FatalExpansion;
        var useInheritedPipelineAction = kind == ExitTrapKind.Completion && failure is { UseInheritedPipelineExitAction: true };
        var status = kind == ExitTrapKind.Completion && result.Flow == FlowControlKind.Exit
            && failure is { Kind: ExpansionFailureKind.ReaderSyntax } readerFailure
                ? ExitActionStatus(readerFailure) : result.ExitCode;
        if (_state.Traps is not { } traps || _state.ExitedThroughExec
            || !traps.TryBeginExit(status, useInheritedPipelineAction, out var action))
            return result;

        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        _state.LastExitCode = status;
        try
        {
            if (kind != ExitTrapKind.Completion)
                for (var timing = scope.Timing; timing != null; timing = timing.Enclosing)
                    await ReportTimingAsync(timing).ConfigureAwait(false);

            var function = scope.Context.FunctionContext;
            var prefixes = scope.Context.GlobalPrefixes;
            var callerSource = scope.Context.CallerSource;
            var returnTarget = scope.Context.ReturnTarget;
            if (useInheritedPipelineAction)
            {
                (function, prefixes) = RestoreBindings(scope, scope.LoadedSyntaxFunctionDepth, restorePositionals: true);
                var functionDepth = function?.Depth ?? 0;
                returnTarget = callerSource?.FunctionDepth == functionDepth ? ReturnTargetKind.Source
                    : function != null ? ReturnTargetKind.Function : ReturnTargetKind.None;
            }
            else if (kind == ExitTrapKind.Errexit && (!_state.KeepsLocalsOnErrexit || scope.InNestedCommandUnits))
            {
                (function, prefixes) = RestoreBindings(scope, retainDepth: null, restorePositionals: false);
                callerSource = null;
            }

            // EXIT runs independently of its caller's tested-status suppression. Keep
            // positional and flow targets, while copied action bytes own their syntax.
            var trapScope = scope with
            {
                Context = new EvaluationContext(scope.Context.LoopDepth, function, returnTarget)
                {
                    GlobalPrefixes = prefixes,
                    CallerSource = callerSource
                },
                Timing = null,
                ErrexitRedirections = null
            };
            var handler = await EvaluateCopiedCommandUnitsAsync([action], 0, trapScope, ScriptExecutionKind.Trap)
                .ConfigureAwait(false);
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
            _state.FatalExpansion = failure;
            _state.LastExitCode = result.ExitCode;
        }

        (FunctionContext? Function, GlobalPrefixBindings? Prefixes) RestoreBindings(
            ScopedEvaluation active, int? retainDepth, bool restorePositionals)
        {
            var prefix = active.Context.GlobalPrefixes;
            var frame = active.Context.FunctionContext;
            Variables.PositionalParametersScope? positionals = null;
            for (; frame != null && frame.Depth > retainDepth.GetValueOrDefault(); frame = frame.Enclosing)
            {
                positionals = frame.CallerPositionals ?? positionals;
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
                positionals?.CopySavedParametersTo(active.Variables, Memory);
            return (frame, prefix);
        }
    }
}

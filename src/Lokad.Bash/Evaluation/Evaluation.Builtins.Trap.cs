using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    internal long RetainedTrapMemoryBytes => _state.Traps?.EstimatedMemoryBytes ?? 0;

    private async Task<EvaluationResult> EvaluateBuiltinTrapAsync(IReadOnlyList<ShellValue> words)
    {
        var index = 1;
        var list = false;
        while (index < words.Count)
        {
            var option = words[index].Bytes.Span;
            if (option.SequenceEqual("--"u8))
            {
                index++;
                break;
            }
            if (option.SequenceEqual("-p"u8))
            {
                list = true;
                index++;
                continue;
            }
            if (option.Length < 2 || option[0] != (byte)'-')
                break;

            return await UsageAsync(option.SequenceEqual("--help"u8)).ConfigureAwait(false);
        }

        var traps = _state.GetOrCreateTraps();
        var status = 0;
        if (list || index == words.Count)
        {
            if (index == words.Count)
            {
                foreach (var condition in ShellTraps.Conditions)
                    if (!await PrintAsync(condition).ConfigureAwait(false))
                        return EvaluationResult.FromExitCode(1);
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
                    else if (!await PrintAsync(condition).ConfigureAwait(false))
                        return EvaluationResult.FromExitCode(1);
                }
            }
            return EvaluationResult.FromExitCode(status);
        }

        var action = words[index++];
        var numericReset = !action.Bytes.IsEmpty && action.Bytes.Span.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0;
        var reset = numericReset || action.Bytes.Span.SequenceEqual("-"u8);
        if (index == words.Count)
        {
            if (!ShellTraps.TryParse(action, out _))
                return await UsageAsync(standardOutput: false).ConfigureAwait(false);
            reset = true;
            index--;
        }
        else if (numericReset)
            index--;

        traps.DiscardInheritedListing();
        for (; index < words.Count; index++)
        {
            if (!ShellTraps.TryParse(words[index], out var condition))
            {
                await InvalidConditionAsync(words[index]).ConfigureAwait(false);
                status = 1;
            }
            else if (reset)
                traps.Reset(condition);
            else
                traps.Set(condition, action, Memory);
        }
        return EvaluationResult.FromExitCode(status);

        async Task<EvaluationResult> UsageAsync(bool standardOutput)
        {
            var documentation = BuiltinDocumentation.For(BuiltinKind.Trap);
            var output = Utf8Text.Encode("trap: " + documentation.Synopsis("trap") + "\n    " + documentation.Description + "\n");
            var written = await ExecuteWriteAsync(standardOutput ? GetStdOutDescriptor() : GetStdErrDescriptor(), output).ConfigureAwait(false);
            return EvaluationResult.FromExitCode(written.IsError ? 1 : 2);
        }

        Task InvalidConditionAsync(ShellValue operand) =>
            ReportExpansionDiagnosticAsync([ShellValue.FromText("trap")],
                [operand, ShellValue.FromText(": invalid signal specification")]);

        async Task<bool> PrintAsync(TrapCondition condition)
        {
            if (!traps.ListedHandlers.TryGetValue(condition, out var handler))
                return true;
            var name = Utf8Text.Encode(ShellTraps.GetName(condition));
            var length = "trap -- "u8.Length + ShellArgumentEscaping.GetLiteralSingleQuotedLength(handler.Bytes.Span)
                + 1 + name.Length + 1;
            if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            using var output = new BoundedByteMemoryBuilder((int)length, (int)length, Memory.Budget);
            if (!output.TryAppend("trap -- "u8)
                || !ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, handler.Bytes.Span)
                || !output.TryAppend((byte)' ')
                || !output.TryAppend(name.Span)
                || !output.TryAppend((byte)'\n'))
                throw new InvalidOperationException("Trap listing exceeded its reserved size.");
            return !(await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false)).IsError;
        }
    }

    private enum ExitTrapKind { Completion, Errexit, Expansion }

    private ValueTask<EvaluationResult> RunExitTrapAsync(ScopedEvaluation scope, EvaluationResult result) =>
        RunExitTrapAsync(scope, result, ExitTrapKind.Completion);

    /// <summary>Runs EXIT once with the bindings and descriptors selected by the reason for termination.</summary>
    private async ValueTask<EvaluationResult> RunExitTrapAsync(ScopedEvaluation scope, EvaluationResult result, ExitTrapKind kind)
    {
        if (_state.Traps is not { } traps || _state.ExitedThroughExec || !traps.TryBeginExit(result.ExitCode, out var action))
            return result;

        var failure = _state.FatalExpansion;
        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        _state.LastExitCode = result.ExitCode;
        try
        {
            if (kind != ExitTrapKind.Completion)
                for (var timing = scope.Timing; timing != null; timing = timing.Enclosing)
                    await ReportTimingAsync(timing).ConfigureAwait(false);

            var restoreLocals = kind == ExitTrapKind.Errexit && (!_state.KeepsLocalsOnErrexit || scope.InNestedCommandUnits);
            if (restoreLocals)
                RestoreErrexitBindings(scope);

            // EXIT runs independently of its caller's tested-status suppression. Keep
            // positional and flow targets, while copied action bytes own their syntax.
            var trapScope = scope with
            {
                Context = new EvaluationContext(scope.Context.LoopDepth,
                    restoreLocals ? null : scope.Context.FunctionContext, scope.Context.ReturnTarget)
                {
                    GlobalPrefixes = restoreLocals ? null : scope.Context.GlobalPrefixes,
                    CallerSource = restoreLocals ? null : scope.Context.CallerSource
                },
                Timing = null,
                ErrexitRedirections = null
            };
            var handler = await EvaluateCopiedCommandUnitsAsync([action], 0, trapScope, ScriptExecutionKind.Trap)
                .ConfigureAwait(false);
            if ((handler.Flow is FlowControlKind.Exit or FlowControlKind.Return)
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

        void RestoreErrexitBindings(ScopedEvaluation active)
        {
            var prefix = active.Context.GlobalPrefixes;
            for (var function = active.Context.FunctionContext; function != null; function = function.Enclosing)
            {
                var temporaryScriptBindings = function.Locals.IsTemporaryPrefixScope;
                var bindings = temporaryScriptBindings && prefix?.FunctionDepth == function.Depth
                    ? prefix.Bindings : function.PrefixBindings;
                CompletePromotions(function, function.Enclosing, active.Variables, bindings);
                if (!temporaryScriptBindings)
                    function.Locals.Restore();
                function.BindingsRestoredForExit = true;
                function.PrefixBindings?.Dispose();

                while (prefix?.FunctionDepth == function.Depth)
                {
                    prefix.Bindings.Dispose();
                    prefix = prefix.Enclosing;
                }
            }
            for (; prefix != null; prefix = prefix.Enclosing)
                prefix.Bindings.Dispose();
        }
    }
}

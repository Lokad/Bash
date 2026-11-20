using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Dispatches evaluation based on the concrete command kind, handling negation chaining.
    /// </summary>
    private async Task<EvaluationResult> EvaluateCommandAsync(
        ICommand command,
        ScopedEvaluation scope,
        bool negated)
    {
        // Async calls can all complete synchronously. Resume on a fresh stack
        // before nested functions/source/eval exhaust the embedding thread's stack.
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        while (command is NegatedCommand negatedCommand)
        {
            negated = !negated;
            command = negatedCommand.Inner;
        }
        if (negated ^ command is PipelineCommand { Negated: true })
            scope = scope with { Context = scope.Context.WithErrexitIgnored() };

        var coordinates = scope.Context.Coordinates;
        var lineScope = default(Variables.LineNumberScope);
        if (coordinates.Locations is { } locations && locations.TryGet(command, out var location))
        {
            coordinates = coordinates.At(command, location);
            scope = scope with { Context = scope.Context.WithCoordinates(coordinates) };
            lineScope = scope.Variables.EnterLineNumber(coordinates.Line);
        }
        using var activeLine = lineScope;

        EvaluationResult result;
        try
        {
            using var memory = Memory.BeginCommand(scope);
            result = await EvaluateCommandCoreAsync(command, scope, negated).ConfigureAwait(false);
            // These compound forms publish the final (possibly negated) status only
            // if command execution returns normally to the caller.
            if (command is ArithmeticCommand or ConditionalCommand && result.Flow == FlowControlKind.None
                && !ShouldExitForFailure(result))
                scope.Variables.SetPipelineStatuses([result.ExitCode], scope.Context, _cancellationToken);
        }
        catch (MemoryBudgetExceededException)
        {
            Memory.ReleaseUnusedCapacity();
            await ReportMemoryRefusalAsync("shell").ConfigureAwait(false);
            result = await FinalizeResultAsync(EvaluationResult.FromExitCode(1),
                negated ^ command is PipelineCommand { Negated: true }).ConfigureAwait(false);
        }

        // A simple command (including a function, eval or source invocation) exposes a
        // fresh status. Compound commands retain the tested origin of their final status.
        if (command is SimpleCommand)
            result = result.WithTestedStatus(false);
        var hasExitAction = !_state.ExitedThroughExec && _state.Traps?.HasPendingExitAction == true;
        if (hasExitAction)
        {
            if (result.DeferredErrexit is { IsActive: false })
                return await RunExitTrapAsync(scope, result.WithDeferredErrexit(null), ExitTrapKind.Errexit).ConfigureAwait(false);
            if (result.Flow == FlowControlKind.Exit && _state.ErrexitEnabled
                && _state.FatalExpansion is { Kind: ExpansionFailureKind.RequiredParameter })
                return await RunExitTrapAsync(scope, result, ExitTrapKind.Expansion).ConfigureAwait(false);
        }
        if (result.Flow == FlowControlKind.None && result.ExitCode != 0)
        {
            if (scope.Context.ErrexitIgnored)
                result = result.WithTestedStatus(true);
            else if (ShouldExitForFailure(result))
            {
                result = EvaluationResult.Exit(result.ExitCode);
                if (hasExitAction)
                {
                    // Simple failures restore the nearest saved invocation before EXIT.
                    if (command is SimpleCommand && scope.ErrexitRedirections is { IsActive: true } pending)
                        return result.WithDeferredErrexit(pending);
                    return await RunExitTrapAsync(scope, result, ExitTrapKind.Errexit).ConfigureAwait(false);
                }
            }
        }
        return result;

        bool ShouldExitForFailure(EvaluationResult status) =>
            status.ExitCode != 0 && _state.ErrexitEnabled && !scope.Context.ErrexitIgnored && !status.TestedStatus;

        async Task<EvaluationResult> EvaluateCommandCoreAsync(
            ICommand command, ScopedEvaluation scope, bool negated)
        {
            CheckCancellation();
            if (_state.FatalExpansion is { } failure)
                return EvaluationResult.Exit(failure.ExitCode);

            switch (command)
            {
                case ScriptCommand scriptCommand:
                    return ApplyNegation(await EvaluateCommandUnitsAsync(scriptCommand.Source, scope,
                            scriptCommand.IsLegacyCapture ? ScriptExecutionKind.LegacyCapture : ScriptExecutionKind.Root)
                        .ConfigureAwait(false), negated);
                case ListCommand listCommand:
                    return await EvaluateListAsync(listCommand, scope, negated).ConfigureAwait(false);
                case TimedCommand timedCommand:
                    return await EvaluateTimedCommandAsync(timedCommand).ConfigureAwait(false);
                case GroupCommand groupCommand:
                    return await EvaluateGroupAsync(groupCommand, scope, negated).ConfigureAwait(false);
                case RedirectedCommand redirectedCommand:
                    return await EvaluateRedirectedCommandAsync(redirectedCommand, scope, negated)
                        .ConfigureAwait(false);
                case SubshellCommand subshellCommand:
                    return await EvaluateSubshellAsync(subshellCommand, scope, negated).ConfigureAwait(false);
                case IfCommand ifCommand:
                    return await EvaluateIfAsync(ifCommand, scope, negated).ConfigureAwait(false);
                case WhileCommand whileCommand:
                    return await EvaluateLoopAsync(
                            LoopKind.While,
                            whileCommand.Condition,
                            whileCommand.Body,
                            scope,
                            negated)
                        .ConfigureAwait(false);
                case UntilCommand untilCommand:
                    return await EvaluateLoopAsync(
                            LoopKind.Until,
                            untilCommand.Condition,
                            untilCommand.Body,
                            scope,
                            negated)
                        .ConfigureAwait(false);
                case ForCommand forCommand:
                    return await EvaluateForAsync(forCommand, scope, negated).ConfigureAwait(false);
                case FunctionDefinition functionDefinition:
                    if (functionDefinition.Name.Syntax.HasFlag(FunctionNameSyntax.Invalid))
                    {
                        await ReportInvalidIdentifierAsync(functionDefinition.Name.Value).ConfigureAwait(false);
                        return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
                    }
                    if (!_state.TryRegisterFunction(functionDefinition, scope.Context, Memory))
                    {
                        await ReportExpansionDiagnosticAsync(functionDefinition.Name.Value, ShellValue.FromText("readonly function")).ConfigureAwait(false);
                        return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
                    }
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);
                case ArithmeticForCommand arithmeticForCommand:
                    return await EvaluateArithmeticForAsync(arithmeticForCommand, scope, negated).ConfigureAwait(false);
                case PipelineCommand pipelineCommand:
                    return await EvaluatePipelineAsync(pipelineCommand, scope, negated).ConfigureAwait(false);
                case ArithmeticCommand arithmeticCommand:
                    PreparePipelineStatusStorage(1);
                    var arithmeticResult = await EvaluateArithmeticCommandOperandAsync(arithmeticCommand.Syntax, scope)
                        .ConfigureAwait(false);
                    var arithmeticStatus = arithmeticResult is null or 0 ? 1 : 0;
                    return await FinalizeResultAsync(
                        EvaluationResult.FromExitCode(arithmeticStatus), negated);
                case ConditionalCommand conditionalCommand:
                    return await EvaluateConditionalCommandAsync(conditionalCommand, scope, negated).ConfigureAwait(false);
                case CaseCommand caseCommand:
                    return await EvaluateCaseAsync(caseCommand, scope, negated).ConfigureAwait(false);
                case SimpleCommand simpleCommand:
                    var simpleResult = await EvaluateSimpleCommandAsync(simpleCommand, scope).ConfigureAwait(false);
                    return ApplyNegation(simpleResult, negated);
                default:
                    throw new NotSupportedException("Only simple commands, list control operators, pipelines, groups, subshells, if/elif/else, loops, for-in, arithmetic for, case, function definitions, arithmetic commands, conditional commands, and ! negation are supported in this evaluation stage.");
            }

            async Task<EvaluationResult> EvaluateTimedCommandAsync(TimedCommand timed)
            {
                Memory.Charge(128);
                var started = await AwaitOperationAsync(ReadTimingOperation.AsTask(PublishOperation)).ConfigureAwait(false);
                var timing = new ActiveTiming(started, timed.Portable, scope.Variables, scope.Timing);
                var result = await EvaluateCommandAsync(timed.Inner, scope with { Timing = timing }, negated).ConfigureAwait(false);
                // Bash's return unwinds past the timer; the calling function's timer still reports.
                if (!_state.ExitedThroughExec && result.Flow != FlowControlKind.Return)
                    await ReportTimingAsync(timing).ConfigureAwait(false);
                // Ordinary formatting diagnostics preserve status; memory refusal reaches the command boundary.
                return result;
            }

            async Task<EvaluationResult> EvaluateListAsync(
                ListCommand listCommand,
                ScopedEvaluation listScope,
                bool listNegated)
            {
                var traversal = listCommand.GetTraversal(CancellationToken).Steps;
                var result = EvaluationResult.FromExitCode(0);
                for (var index = 0; index < traversal.Count; index++)
                {
                    CheckCancellation();
                    switch (traversal[index])
                    {
                        case ListTraversal.Step.Execute execute:
                            var currentScope = execute.ErrexitIgnored
                                ? listScope with { Context = listScope.Context.WithErrexitIgnored() } : listScope;
                            result = await EvaluateCommandAsync(execute.Command, currentScope, negated: false).ConfigureAwait(false);
                            if (result.Flow != FlowControlKind.None)
                            {
                                if (result.Flow != FlowControlKind.Exit
                                    || GetRecoverableScriptExpansionStatus(ScriptExecutionKind.Root, listScope) is not { } recoveryStatus)
                                    return ApplyNegation(result, listNegated);
                                while (index + 1 < traversal.Count && traversal[index + 1] is not ListTraversal.Step.Execute { StartsCommandUnit: true })
                                {
                                    CheckCancellation();
                                    index++;
                                }
                                if (index + 1 == traversal.Count)
                                    return ApplyNegation(result, listNegated);
                                result = ApplyNegation(await RecoverExpansionFailureAsync(recoveryStatus, listScope, loadedReader: false)
                                    .ConfigureAwait(false), negated: false);
                            }
                            break;
                        case ListTraversal.Step.Select select when !EvaluationRules.ShouldExecuteRight(select.Operator, result.ExitCode):
                            index = select.EndExclusive - 1;
                            break;
                    }
                }
                return ApplyNegation(result, listNegated);
            }
        }
    }

    internal sealed class ActiveTiming(
        ShellTimingSnapshot started, bool portable, Variables variables, ActiveTiming? enclosing)
    {
        internal ShellTimingSnapshot Started { get; } = started;
        internal bool Portable { get; } = portable;
        internal Variables Variables { get; } = variables;
        internal ActiveTiming? Enclosing { get; } = enclosing;
        internal bool Reported { get; set; }
    }

    private async ValueTask ReportTimingAsync(ActiveTiming timing)
    {
        if (timing.Reported)
            return;
        timing.Reported = true;
        var ended = await AwaitOperationAsync(ReadTimingOperation.AsTask(PublishOperation)).ConfigureAwait(false);
        var format = timing.Portable
            ? ShellTimeFormatting.PortableFormat
            : timing.Variables.TryGetExpansionValue("TIMEFORMAT", out var value)
                ? value
                : ShellTimeFormatting.DefaultFormat;
        using var outputMemory = Memory.Budget.CreateScope();
        if (ShellTimeFormatting.TryFormat(format, ended - timing.Started, out var output, out var error, outputMemory))
        {
            if (!output.IsEmpty)
                await ExecuteWriteAsync(GetStdErrDescriptor(), output).ConfigureAwait(false);
        }
        else
            await ExecuteWriteAsync(GetStdErrDescriptor(), error).ConfigureAwait(false);
    }

    /// <summary>Admits the replacement binding before foreground execution can publish effects.</summary>
    private void PreparePipelineStatusStorage(int count)
    {
        if (count >= Array.MaxLength)
            throw new ByteMemoryBuilder.LimitExceededException();
        Memory.Charge(checked(512L + 256L * count));
    }

    private const string MemoryRefusalMessage = "shared memory budget exhausted";
    private static readonly ReadOnlyMemory<byte> ShellMemoryRefusalDiagnostic =
        Utf8Text.Encode("shell: " + MemoryRefusalMessage + "\n");

    private async Task ReportMemoryRefusalAsync(string failedCommand)
    {
        var nameByteLimit = SmallDiagnosticBytes - MemoryRefusalMessage.Length - DiagnosticPunctuationBytes;
        var name = failedCommand.Length > nameByteLimit || Utf8Text.StrictUtf8.GetByteCount(failedCommand) > nameByteLimit
            ? "shell" : failedCommand;
        try
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText(name), ShellValue.FromText(MemoryRefusalMessage))
                .ConfigureAwait(false);
        }
        catch (MemoryBudgetExceededException)
        {
            // Reporting a refusal must not require another successful admission.
        }
    }
}

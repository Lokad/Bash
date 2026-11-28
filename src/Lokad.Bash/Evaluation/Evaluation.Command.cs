using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private static readonly ShellValue ArithmeticDiagnosticName = ShellValue.FromText("((");
    private static readonly ShellValue ConditionalDiagnosticName = ShellValue.FromText("[[");

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

        if (scope.Context.Coordinates.FunctionBraceBeforeUnit == null && command is not ListCommand)
            scope = BeginPreparedCommandUnit(scope, command, beginsExecutionUnit: true);
        var coordinates = scope.Context.Coordinates;
        var lineScope = default(Variables.LineNumberScope);
        var locationCommand = _pipelineStageSource is { } source && IsPipelineStageCommand(command) ? source : command;
        if (coordinates.Locations is { } locations && locations.TryGet(locationCommand, out var location))
        {
            coordinates = coordinates.At(locationCommand, location);
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
                return await RunErrexitExitTrapAsync(scope, result, result.DefersLoadedErrexit).ConfigureAwait(false);
            if (result.Flow == FlowControlKind.Exit && _state.ErrexitEnabled
                && _state.FatalExpansion is { Kind: ExpansionFailureKind.RequiredParameter or ExpansionFailureKind.UnboundParameter
                    or ExpansionFailureKind.InvalidParameterTransform
                    or ExpansionFailureKind.CompoundArrayIndex or ExpansionFailureKind.ArrayRead,
                    UseInheritedPipelineExitAction: false, UnwindsLoadedReader: false })
                return await RunExitTrapAsync(scope, result, ExitTrapKind.Expansion).ConfigureAwait(false);
        }
        var callbackLoopFlow = scope.InArrayReaderCallback && result.Flow is FlowControlKind.Break or FlowControlKind.Continue;
        if ((result.Flow == FlowControlKind.None || callbackLoopFlow) && result.ExitCode != 0)
        {
            if (scope.Context.ErrexitIgnored)
                result = result.WithTestedStatus(true);
            else if (ShouldExitForFailure(result))
            {
                if (callbackLoopFlow)
                    return EvaluationResult.ExitFromArrayReaderLoopFailure(result.ExitCode);

                result = EvaluationResult.Exit(scope.InDirectPipelineReader ? 1 : result.ExitCode);
                if (hasExitAction)
                {
                    // Simple failures restore the nearest saved invocation. In a reader
                    // at global scope, other pending redirections unwind with its command units.
                    var pending = command is SimpleCommand ? scope.ErrexitRedirections : null;
                    if (pending is { IsActive: true })
                        return result.WithDeferredErrexit(pending, scope.InNestedCommandUnits);
                    if (scope.InNestedCommandUnits && scope.Context.FunctionContext == null)
                        return result.WithDeferredErrexit(null, fromLoadedReader: true);
                    return await RunErrexitExitTrapAsync(scope, result, scope.InNestedCommandUnits).ConfigureAwait(false);
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
            var readDiagnostics = scope.Context.Coordinates.Locations?.GetReadDiagnostics(command) ?? default;
            ApplyReadFunctionBraceLine(scope.Context.Coordinates.Offset, readDiagnostics.FunctionBraceLine);
            if (readDiagnostics.HereDocumentWarnings is { } warnings)
                await ReportHereDocumentWarningsAsync(warnings, scope.Context.Coordinates.Offset,
                    scope.Context.DiagnosticName, useDocumentLine: false, fixedPrefix: GetHereDocumentWarningPrefix(scope)).ConfigureAwait(false);
            if (readDiagnostics.CompoundItems is { } items
                && await ReportCompoundItemReadDiagnosticsAsync(items, scope, scope.Context.Coordinates.Offset)
                    .ConfigureAwait(false) is { } readFailure)
                return EvaluationResult.Exit(readFailure.ExitCode);

            switch (command)
            {
                case ScriptCommand scriptCommand:
                    return ApplyNegation(await EvaluateCommandUnitsAsync(scriptCommand.Source, scope,
                            scriptCommand.SourceReader == ScriptCommand.Reader.LegacyCapture
                                ? ScriptExecutionKind.LegacyCapture : ScriptExecutionKind.Root,
                            scriptCommand.SourceReader != ScriptCommand.Reader.Root ? SyntaxDiagnosticOrigin.CommandSubstitution
                                : scope.Context.SourceMemory?.Kind == SourceMemory.ReaderKind.CommandString ? SyntaxDiagnosticOrigin.CommandString
                                : SyntaxDiagnosticOrigin.Script)
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
                    return await EvaluateSubshellAsync(subshellCommand, scope, negated, redirections: null).ConfigureAwait(false);
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
                        await ReportInvalidIdentifierAsync(functionDefinition.Name.Value, scope,
                            scope.Context.Coordinates.UnitReadLine)
                            .ConfigureAwait(false);
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
                    _state.PreparationDiagnosticName = ArithmeticDiagnosticName;
                    PreparePipelineStatusStorage(1);
                    var arithmeticResult = await EvaluateArithmeticCommandOperandAsync(arithmeticCommand.Syntax, scope)
                        .ConfigureAwait(false);
                    var arithmeticStatus = arithmeticResult is null or 0 ? 1 : 0;
                    return await FinalizeResultAsync(
                        EvaluationResult.FromExitCode(arithmeticStatus), negated);
                case ConditionalCommand conditionalCommand:
                    _state.PreparationDiagnosticName = ConditionalDiagnosticName;
                    return await EvaluateConditionalCommandAsync(conditionalCommand, scope, negated).ConfigureAwait(false);
                case CaseCommand caseCommand:
                    return await EvaluateCaseAsync(caseCommand, scope, negated).ConfigureAwait(false);
                case SimpleCommand captureInput when ReferenceEquals(captureInput, _finalCaptureInput):
                    var captureResult = await ReadInputSubstitutionAsync(captureInput.Redirections, scope, writeToCapture: true)
                        .ConfigureAwait(false);
                    return EvaluationResult.FromExitCode(captureResult.ExitCode);
                case SimpleCommand simpleCommand:
                    var simpleResult = await EvaluateSimpleCommandAsync(simpleCommand, scope).ConfigureAwait(false);
                    return ApplyNegation(simpleResult, negated);
                default:
                    throw new NotSupportedException($"Cannot evaluate command kind {command.GetType().Name}.");
            }

            async Task<EvaluationResult> EvaluateTimedCommandAsync(TimedCommand timed)
            {
                Memory.Charge(128);
                var started = await AwaitOperationAsync(ReadTimingOperation.AsTask(PublishOperation)).ConfigureAwait(false);
                // Timing reports after the inner command restores its execution line.
                var coordinates = scope.Context.Coordinates;
                var line = coordinates.EnclosingExecutionLine != 0
                    ? coordinates.EnclosingExecutionLine : coordinates.UnitReadLine;
                if (line == 0)
                    line = coordinates.FunctionBodyLine != 0 ? coordinates.FunctionBodyLine : coordinates.Line;
                var inner = timed.Inner;
                while (inner is NegatedCommand negation)
                    inner = negation.Inner;
                var timedSubshell = inner as SubshellCommand ?? (inner as RedirectedCommand)?.Body as SubshellCommand;
                var timing = new ActiveTiming(started, timed.Portable, scope.Variables, scope.Timing,
                    scope.Context.DiagnosticName, coordinates.Locations == null ? 0 : line, timedSubshell);
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
                var unitScope = listScope;
                for (var index = 0; index < traversal.Count; index++)
                {
                    CheckCancellation();
                    var step = traversal[index];
                    if (step.Command is ListCommand branch)
                    {
                        if (!EvaluationRules.ShouldExecuteRight(branch.Operator, result.ExitCode))
                            index = step.EndExclusive - 1;
                        continue;
                    }

                    if (step.StartsCommandUnit || unitScope.Context.Coordinates.FunctionBraceBeforeUnit == null)
                        unitScope = BeginPreparedCommandUnit(listScope, step.Command, beginsExecutionUnit: true);
                    else if (step.StartsReaderUnit)
                        unitScope = BeginPreparedCommandUnit(unitScope, step.Command, beginsExecutionUnit: false);
                    var currentScope = step.ErrexitIgnored
                        ? unitScope with { Context = unitScope.Context.WithErrexitIgnored() } : unitScope;
                    result = await EvaluateCommandAsync(step.Command, currentScope, negated: false).ConfigureAwait(false);
                    if (result.Flow != FlowControlKind.None)
                    {
                        var recoveryStatus = result.Flow == FlowControlKind.Exit && !result.DiscardsCommandUnit
                            ? GetRecoverableScriptExpansionStatus(ScriptExecutionKind.Root, listScope) : null;
                        var resumeDiscard = result.DiscardsCommandUnit && _state.ForkKind == null;
                        if (recoveryStatus == null && !resumeDiscard)
                            return ApplyNegation(result, listNegated);
                        while (index + 1 < traversal.Count && !traversal[index + 1].StartsCommandUnit)
                        {
                            CheckCancellation();
                            index++;
                        }
                        if (index + 1 == traversal.Count)
                            return ApplyNegation(result, listNegated);
                        var adjustment = result.ReaderLineAdjustment(unitScope.Context.Coordinates.UnitReadLine);
                        if (adjustment != 0)
                        {
                            var location = listScope.Context.Coordinates;
                            listScope = listScope with
                            {
                                Context = listScope.Context.WithCoordinates(location with { Offset = location.Offset + adjustment })
                            };
                        }
                        result = resumeDiscard
                            ? await RecoverDiscardedCommandUnitAsync(listScope, result, listScope.InNestedCommandUnits).ConfigureAwait(false)
                            : ApplyNegation(await RecoverExpansionFailureAsync(recoveryStatus.GetValueOrDefault(), listScope,
                                loadedReader: false).ConfigureAwait(false), negated: false);
                    }
                }
                return ApplyNegation(result, listNegated);
            }
        }
    }

    private ScopedEvaluation BeginPreparedCommandUnit(ScopedEvaluation scope, ICommand command, bool beginsExecutionUnit)
    {
        var coordinates = scope.Context.Coordinates;
        if (coordinates.Locations is { } locations
            && locations.TryGet(CommandLocations.UnitLocationAnchor(command), out var location)
            && location.UnitReadLine != 0)
            coordinates = coordinates with { UnitReadLine = coordinates.Offset + location.UnitReadLine };
        if (beginsExecutionUnit)
            coordinates = BeginCommandUnit(coordinates);
        return scope with { Context = scope.Context.WithCoordinates(coordinates) };
    }

    private CommandCoordinates BeginCommandUnit(CommandCoordinates coordinates) => coordinates with
    {
        FunctionBraceBeforeUnit = _state.FunctionBraceLine
    };

    private void ApplyReadFunctionBraceLine(int offset, int braceLine)
    {
        if (braceLine != 0)
            _state.FunctionBraceLine = offset + braceLine;
    }

    internal sealed class ActiveTiming(
        ShellTimingSnapshot started, bool portable, Variables variables, ActiveTiming? enclosing,
        ShellValue? diagnosticName, int diagnosticLine, SubshellCommand? subshell)
    {
        internal ShellTimingSnapshot Started { get; } = started;
        internal bool Portable { get; } = portable;
        internal Variables Variables { get; } = variables;
        internal ActiveTiming? Enclosing { get; } = enclosing;
        internal ShellValue? DiagnosticName { get; } = diagnosticName;
        internal int DiagnosticLine { get; } = diagnosticLine;
        internal SubshellCommand? Subshell { get; } = subshell;
        internal bool Settled { get; set; }
    }

    private static void SuppressPendingTimings(ActiveTiming? timing)
    {
        for (; timing != null; timing = timing.Enclosing)
            timing.Settled = true;
    }

    private async ValueTask ReportPendingTimingsAsync(ActiveTiming? timing, ActiveTiming? stop)
    {
        for (; timing != null && !ReferenceEquals(timing, stop); timing = timing.Enclosing)
            await ReportTimingAsync(timing).ConfigureAwait(false);
    }

    private async ValueTask ReportTimingAsync(ActiveTiming timing)
    {
        if (timing.Settled)
            return;
        timing.Settled = true;
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
        {
            var descriptor = GetStdErrDescriptor();
            var prefix = !descriptor.IsClosed && timing.DiagnosticLine > 0
                ? new SyntaxDiagnosticPrefix(timing.DiagnosticName, SyntaxDiagnosticOrigin.ScriptLine, timing.DiagnosticLine)
                : default;
            await WriteSyntaxDiagnosticLineAsync(descriptor, prefix, error).ConfigureAwait(false);
        }
    }

    /// <summary>Admits the replacement binding before foreground execution can publish effects.</summary>
    private void PreparePipelineStatusStorage(int count)
    {
        if (count >= Array.MaxLength)
            throw new ByteMemoryBuilder.LimitExceededException();
        Memory.Charge(checked(512L + 256L * count));
    }

    private async Task ReportMemoryRefusalAsync(string failedCommand)
    {
        var nameByteLimit = ShellDiagnostic.SmallBytes - ShellDiagnostic.MemoryRefusalMessage.Length - DiagnosticPunctuationBytes;
        var name = failedCommand.Length > nameByteLimit || Utf8Text.StrictUtf8.GetByteCount(failedCommand) > nameByteLimit
            ? "shell" : failedCommand;
        try
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText(name), ShellValue.FromText(ShellDiagnostic.MemoryRefusalMessage))
                .ConfigureAwait(false);
        }
        catch (MemoryBudgetExceededException)
        {
            // Reporting a refusal must not require another successful admission.
        }
    }
}

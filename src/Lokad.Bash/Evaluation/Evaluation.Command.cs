using System;
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
        while (command is NegatedCommand negatedCommand)
        {
            negated = !negated;
            command = negatedCommand.Inner;
        }
        if (negated ^ command is PipelineCommand { Negated: true })
            scope = scope with { Context = scope.Context.WithErrexitIgnored() };

        EvaluationResult result;
        try
        {
            using var memory = Memory.BeginCommand(scope);
            result = await EvaluateCommandCoreAsync(command, scope, negated).ConfigureAwait(false);
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
        if (result.Flow == FlowControlKind.None && result.ExitCode != 0)
        {
            if (scope.Context.ErrexitIgnored)
                result = result.WithTestedStatus(true);
            else if (_state.ErrexitEnabled && !result.TestedStatus)
                result = EvaluationResult.Exit(result.ExitCode);
        }
        return result;

        async Task<EvaluationResult> EvaluateCommandCoreAsync(
            ICommand command, ScopedEvaluation scope, bool negated)
        {
            CheckCancellation();
            if (_state.FatalExpansion is { } failure)
                return EvaluationResult.Exit(failure.ExitCode);

            switch (command)
            {
                case ScriptCommand scriptCommand:
                    return ApplyNegation(await EvaluateCommandUnitsAsync(scriptCommand.Source, scope, ScriptExecutionKind.Root)
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
                        await ReportExpansionDiagnosticAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(functionDefinition.Name.Value), ShellValue.FromText("not a valid identifier"))
                            .ConfigureAwait(false);
                        return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
                    }
                    if (!_state.TryRegisterFunction(functionDefinition))
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
                    var arithmeticResult = await EvaluateArithmeticCommandOperandAsync(arithmeticCommand.Syntax, scope)
                        .ConfigureAwait(false);
                    var arithmeticStatus = arithmeticResult is null or 0 ? 1 : 0;
                    if (_state.FatalExpansion == null)
                        scope.Variables.SetPipelineStatuses([arithmeticStatus], Memory, _cancellationToken);
                    return await FinalizeResultAsync(
                        EvaluationResult.FromExitCode(arithmeticStatus), negated);
                case ConditionalCommand conditionalCommand:
                    return await EvaluateConditionalCommandAsync(conditionalCommand, scope, negated).ConfigureAwait(false);
                case CaseCommand caseCommand:
                    return await EvaluateCaseAsync(caseCommand, scope, negated).ConfigureAwait(false);
                case SimpleCommand simpleCommand:
                    var simpleResult = await EvaluateSimpleCommandAsync(simpleCommand, scope).ConfigureAwait(false);
                    if (simpleResult.Flow != FlowControlKind.Exit
                        && (simpleCommand.Words.Count != 0 || simpleCommand.Assignments.Count != 0 || simpleCommand.Redirections.Count != 0))
                        scope.Variables.SetPipelineStatuses([simpleResult.ExitCode], Memory, _cancellationToken);
                    return ApplyNegation(simpleResult, negated);
                default:
                    throw new NotSupportedException("Only simple commands, list control operators, pipelines, groups, subshells, if/elif/else, loops, for-in, arithmetic for, case, function definitions, arithmetic commands, conditional commands, and ! negation are supported in this evaluation stage.");
            }

            async Task<EvaluationResult> EvaluateTimedCommandAsync(TimedCommand timed)
            {
                var started = await AwaitOperationAsync(ReadTimingOperation.AsTask(PublishOperation)).ConfigureAwait(false);
                var result = await EvaluateCommandAsync(timed.Inner, scope, negated).ConfigureAwait(false);
                // Bash's return unwinds past the timer; the calling function's timer still reports.
                if (_state.ExitedThroughExec || result.Flow == FlowControlKind.Return)
                    return result;
                var ended = await AwaitOperationAsync(ReadTimingOperation.AsTask(PublishOperation)).ConfigureAwait(false);
                var format = timed.Portable
                    ? ShellTimeFormatting.PortableFormat
                    : scope.Variables.TryGetExpansionValue("TIMEFORMAT", out var value)
                        ? value
                        : ShellTimeFormatting.DefaultFormat;
                using var outputMemory = Memory.Budget.CreateScope();
                if (ShellTimeFormatting.TryFormat(format, ended - started, out var output, out var error, outputMemory))
                {
                    if (!output.IsEmpty)
                        await ExecuteWriteAsync(GetStdErrDescriptor(), output).ConfigureAwait(false);
                }
                else
                    await ExecuteWriteAsync(GetStdErrDescriptor(), error).ConfigureAwait(false);

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
                                return ApplyNegation(result, listNegated);
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

    private async Task ReportMemoryRefusalAsync(string failedCommand)
    {
        try
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText(failedCommand), ShellValue.FromText("shared memory budget exhausted"))
                .ConfigureAwait(false);
        }
        catch (MemoryBudgetExceededException)
        {
            // Reporting a refusal must not require another successful admission.
        }
    }
}

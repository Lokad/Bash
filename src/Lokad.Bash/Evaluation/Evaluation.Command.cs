using System;
using System.Collections.Generic;
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
        try
        {
            using var memory = Memory.BeginCommand(scope);
            return await EvaluateCommandCoreAsync(command, scope, negated).ConfigureAwait(false);
        }
        catch (MemoryBudgetExceededException)
        {
            Memory.ReleaseUnusedCapacity();
            await ReportMemoryRefusalAsync("shell").ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1),
                negated ^ command is PipelineCommand { Negated: true }).ConfigureAwait(false);
        }

        async Task<EvaluationResult> EvaluateCommandCoreAsync(
            ICommand command, ScopedEvaluation scope, bool negated)
        {
            CheckCancellation();
            if (_state.FatalExpansion is { } failure)
                return EvaluationResult.Exit(failure.ExitCode);

            switch (command)
            {
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
                    var arithmeticResult = await EvaluateArithmeticCommandOperandAsync(arithmeticCommand.Expression, scope)
                        .ConfigureAwait(false);
                    return await FinalizeResultAsync(
                        EvaluationResult.FromExitCode(arithmeticResult is null or 0 ? 1 : 0), negated);
                case ConditionalCommand conditionalCommand:
                    return await EvaluateConditionalCommandAsync(conditionalCommand, negated).ConfigureAwait(false);
                case CaseCommand caseCommand:
                    return await EvaluateCaseAsync(caseCommand, scope, negated).ConfigureAwait(false);
                case SimpleCommand simpleCommand:
                    return ApplyNegation(await EvaluateSimpleCommandAsync(simpleCommand, scope).ConfigureAwait(false), negated);
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
                // The parser builds left-associated trees. Keep pending right sides off the call
                // stack so long scripts remain safe even when commands complete synchronously.
                var pending = new Stack<ListCommand>();
                ICommand currentCommand = listCommand;
                while (true)
                {
                    while (currentCommand is ListCommand list)
                    {
                        CheckCancellation();
                        pending.Push(list);
                        currentCommand = list.Left;
                    }

                    var result = await EvaluateCommandAsync(currentCommand, listScope, negated: false).ConfigureAwait(false);
                    if (result.Flow != FlowControlKind.None)
                        return ApplyNegation(result, listNegated);

                    while (true)
                    {
                        if (!pending.TryPop(out var next))
                            return ApplyNegation(result, listNegated);

                        CheckCancellation();
                        if (EvaluationRules.ShouldExecuteRight(next.Operator, result.ExitCode))
                        {
                            currentCommand = next.Right;
                            break;
                        }
                    }
                }
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

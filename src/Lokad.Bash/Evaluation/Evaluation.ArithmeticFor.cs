using System.Threading.Tasks;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Evaluates an arithmetic for loop by running init, checking the condition, executing the body, and applying updates.
    /// </summary>
    /// <param name="arithmeticForCommand">The arithmetic for command to execute.</param>
    /// <param name="scope">The active bindings and lexical evaluation context.</param>
    /// <param name="negated">Whether to negate the final exit status.</param>
    /// <returns>The evaluation result, including flow control and exit status.</returns>
    private async Task<EvaluationResult> EvaluateArithmeticForAsync(
        ArithmeticForCommand arithmeticForCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        arithmeticForCommand.Split(
            out var initExpression,
            out var conditionExpression,
            out var updateExpression);

        if (await EvaluateOperandAsync(initExpression).ConfigureAwait(false) == null)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        var loopContext = scope.Context.WithLoopDepth(scope.Context.LoopDepth + 1);
        var lastBodyResult = EvaluationResult.FromExitCode(0);

        while (true)
        {
            CheckCancellation();
            // Only an omitted condition is true; an explicit arithmetic zero is false.
            if (!conditionExpression.IsEmpty)
            {
                var condition = await EvaluateOperandAsync(conditionExpression)
                    .ConfigureAwait(false);
                if (condition == null)
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
                if (condition == 0)
                    break;
            }

            var bodyResult = await EvaluateCommandAsync(arithmeticForCommand.Body, scope with { Context = loopContext }, negated: false)
                .ConfigureAwait(false);
            if (bodyResult.Flow == FlowControlKind.None)
            {
                lastBodyResult = bodyResult;
            }
            else if (bodyResult.Flow == FlowControlKind.Continue)
            {
                if (bodyResult.Level > 1)
                {
                    return ApplyNegation(bodyResult.WithLevel(bodyResult.Level - 1), negated);
                }

                lastBodyResult = EvaluationResult.FromExitCode(bodyResult.ExitCode);
            }
            else if (bodyResult.Flow == FlowControlKind.Break)
            {
                if (bodyResult.Level > 1)
                {
                    return ApplyNegation(bodyResult.WithLevel(bodyResult.Level - 1), negated);
                }

                return await FinalizeResultAsync(EvaluationResult.FromExitCode(bodyResult.ExitCode), negated);
            }
            else
            {
                return ApplyNegation(bodyResult, negated);
            }

            if (await EvaluateOperandAsync(updateExpression).ConfigureAwait(false) == null)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
        }

        return await FinalizeResultAsync(lastBodyResult, negated);

        async Task<long?> EvaluateOperandAsync(ShellSource expression)
        {
            // The condition and increment finish each iteration, just like the body command.
            using var memory = Memory.BeginCommand(scope);
            return await EvaluateArithmeticCommandOperandAsync(expression, scope).ConfigureAwait(false);
        }
    }
}

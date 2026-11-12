using System.Threading.Tasks;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Evaluates while/until loops with proper flow-control handling.
    /// </summary>
    private async Task<EvaluationResult> EvaluateLoopAsync(
        LoopKind kind,
        ICommand condition,
        ICommand body,
        ScopedEvaluation scope,
        bool negated)
    {
        var loopContext = scope.Context.WithLoopDepth(scope.Context.LoopDepth + 1);
        var lastBodyResult = EvaluationResult.FromExitCode(0);

        while (true)
        {
            CheckCancellation();
            var conditionResult = await EvaluateCommandAsync(condition,
                    scope with { Context = loopContext.WithErrexitIgnored() }, negated: false)
                .ConfigureAwait(false);
            if (conditionResult.Flow != FlowControlKind.None)
            {
                if (conditionResult.Flow == FlowControlKind.Break && conditionResult.Level > 1)
                {
                    return ApplyNegation(conditionResult.WithLevel(conditionResult.Level - 1), negated);
                }

                if (conditionResult.Flow == FlowControlKind.Continue && conditionResult.Level > 1)
                {
                    return ApplyNegation(conditionResult.WithLevel(conditionResult.Level - 1), negated);
                }

                return ApplyNegation(conditionResult, negated);
            }

            var shouldRun = kind == LoopKind.While
                ? conditionResult.ExitCode == 0
                : conditionResult.ExitCode != 0;
            if (!shouldRun)
            {
                return await FinalizeResultAsync(lastBodyResult, negated);
            }

            var bodyResult = await EvaluateCommandAsync(body, scope with { Context = loopContext }, negated: false).ConfigureAwait(false);
            if (bodyResult.Flow == FlowControlKind.None)
            {
                lastBodyResult = bodyResult;
                continue;
            }

            if (bodyResult.Flow == FlowControlKind.Continue)
            {
                if (bodyResult.Level > 1)
                {
                    return ApplyNegation(bodyResult.WithLevel(bodyResult.Level - 1), negated);
                }

                lastBodyResult = EvaluationResult.FromExitCode(bodyResult.ExitCode);
                continue;
            }

            if (bodyResult.Flow == FlowControlKind.Break)
            {
                if (bodyResult.Level > 1)
                {
                    return ApplyNegation(bodyResult.WithLevel(bodyResult.Level - 1), negated);
                }

                return await FinalizeResultAsync(EvaluationResult.FromExitCode(bodyResult.ExitCode), negated);
            }

            return ApplyNegation(bodyResult, negated);
        }
    }
}

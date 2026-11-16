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
            if (conditionResult.Flow is FlowControlKind.Return or FlowControlKind.Exit)
                return ApplyNegation(conditionResult, negated);

            var shouldRun = kind == LoopKind.While
                ? conditionResult.ExitCode == 0
                : conditionResult.ExitCode != 0;
            if (conditionResult.Flow is FlowControlKind.Break or FlowControlKind.Continue)
            {
                // A failed gate retains the previous body status. If the gate admits
                // the body, pending flow skips it and leaves the tested condition status.
                var settled = shouldRun ? conditionResult.ConsumeLoopFlow().WithTestedStatus(true) : lastBodyResult;
                var flow = conditionResult.WithExitCode(settled.ExitCode).WithTestedStatus(settled.TestedStatus);
                if (flow.TryPropagateLoopFlow(out var outward))
                    return ApplyNegation(outward, negated);
                if (!shouldRun || flow.Flow == FlowControlKind.Break)
                    return await FinalizeResultAsync(settled, negated).ConfigureAwait(false);

                lastBodyResult = settled;
                continue;
            }
            if (!shouldRun)
            {
                return await FinalizeResultAsync(lastBodyResult, negated);
            }

            var bodyResult = await EvaluateCommandAsync(body, scope with { Context = loopContext }, negated: false).ConfigureAwait(false);
            if (bodyResult.TryPropagateLoopFlow(out var bodyOutward))
                return ApplyNegation(bodyOutward, negated);

            lastBodyResult = bodyResult.ConsumeLoopFlow();
            if (bodyResult.Flow == FlowControlKind.Break)
                return await FinalizeResultAsync(lastBodyResult, negated).ConfigureAwait(false);
        }
    }
}

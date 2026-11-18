using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Evaluates a for-in loop by expanding items and executing the body for each value.
    /// </summary>
    private async Task<EvaluationResult> EvaluateForAsync(
        ForCommand forCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        var items = new List<ShellValue>(forCommand.Items.Count);
        foreach (var added in forCommand.Items)
        {
            items.AddRange(await ExpandToWordsAsync(added, scope).ConfigureAwait(false));
            if (_state.HasPendingExpansionError)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }

        var loopContext = scope.Context.WithLoopDepth(scope.Context.LoopDepth + 1);
        if (items.Count == 0)
        {
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);
        }

        BindingName name = forCommand.Name;
        var lastBodyResult = EvaluationResult.FromExitCode(0);
        foreach (var item in items)
        {
            CheckCancellation();
            var failure = scope.Variables.AssignForIteration(name, item, Memory);
            if (failure != ForAssignmentFailure.None)
            {
                if (failure == ForAssignmentFailure.InvalidTarget)
                {
                    await ReportExpansionDiagnosticAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(item),
                        ShellValue.FromText("not a valid identifier")).ConfigureAwait(false);
                    // This assignment failure ends the loop without triggering errexit at the loop boundary.
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(1).WithTestedStatus(true), negated)
                        .ConfigureAwait(false);
                }
                if (failure == ForAssignmentFailure.Readonly)
                {
                    await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText("readonly variable")).ConfigureAwait(false);
                    // A readonly loop assignment exits with errexit enabled, even in a tested position.
                    if (_state.ErrexitEnabled)
                        return EvaluationResult.Exit(1);
                }
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
            }

            var bodyResult = await EvaluateCommandAsync(forCommand.Body, scope with { Context = loopContext }, negated: false)
                .ConfigureAwait(false);
            if (bodyResult.TryPropagateLoopFlow(out var outward))
                return ApplyNegation(outward, negated);

            lastBodyResult = bodyResult.ConsumeLoopFlow();
            if (bodyResult.Flow == FlowControlKind.Break)
                return await FinalizeResultAsync(lastBodyResult, negated).ConfigureAwait(false);
        }

        return await FinalizeResultAsync(lastBodyResult, negated);
    }
}

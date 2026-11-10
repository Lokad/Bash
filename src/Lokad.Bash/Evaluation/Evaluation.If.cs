using System.Threading.Tasks;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Evaluates if/elif/else by executing the first body whose condition succeeds.
    /// </summary>
    private async Task<EvaluationResult> EvaluateIfAsync(
        IfCommand ifCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        var conditionResult = await EvaluateCommandAsync(ifCommand.Condition, scope, negated: false)
            .ConfigureAwait(false);
        if (conditionResult.Flow != FlowControlKind.None)
        {
            return ApplyNegation(conditionResult, negated);
        }

        if (conditionResult.ExitCode == 0)
        {
            var thenResult = await EvaluateCommandAsync(ifCommand.ThenBody, scope, negated: false)
                .ConfigureAwait(false);
            return ApplyNegation(thenResult, negated);
        }

        for (var index = 0; index < ifCommand.ElifClauses.Count; index++)
        {
            var clause = ifCommand.ElifClauses[index];
            var elifCondition = await EvaluateCommandAsync(clause.Condition, scope, negated: false)
                .ConfigureAwait(false);
            if (elifCondition.Flow != FlowControlKind.None)
            {
                return ApplyNegation(elifCondition, negated);
            }

            if (elifCondition.ExitCode == 0)
            {
                var elifBody = await EvaluateCommandAsync(clause.Body, scope, negated: false)
                    .ConfigureAwait(false);
                return ApplyNegation(elifBody, negated);
            }
        }

        if (ifCommand.ElseBody != null)
        {
            var elseResult = await EvaluateCommandAsync(ifCommand.ElseBody, scope, negated: false)
                .ConfigureAwait(false);
            return ApplyNegation(elseResult, negated);
        }

        // With no selected body, the if command succeeds regardless of its failed conditions.
        return ApplyNegation(EvaluationResult.FromExitCode(0), negated);
    }
}

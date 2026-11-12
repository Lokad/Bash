using System.Collections.Generic;
using System.Threading.Tasks;
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

        int? lastBodyExit = null;
        foreach (var item in items)
        {
            CheckCancellation();
            // A nameref loop variable selects a new referent for each item, rather than writing through it.
            var assigned = scope.Variables.TryGetNameref(forCommand.Name, out _)
                ? scope.Variables.TrySetNameref(forCommand.Name, item, Memory)
                : TrySetVariable(forCommand.Name, item, scope.Variables);
            if (!assigned)
            {
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
            }

            var bodyResult = await EvaluateCommandAsync(forCommand.Body, scope with { Context = loopContext }, negated: false)
                .ConfigureAwait(false);
            if (bodyResult.Flow == FlowControlKind.None)
            {
                lastBodyExit = bodyResult.ExitCode;
                continue;
            }

            if (bodyResult.Flow == FlowControlKind.Continue)
            {
                if (bodyResult.Level > 1)
                {
                    return ApplyNegation(bodyResult.WithLevel(bodyResult.Level - 1), negated);
                }

                lastBodyExit = bodyResult.ExitCode;
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

        return await FinalizeResultAsync(EvaluationResult.FromExitCode(lastBodyExit ?? 0), negated);
    }
}

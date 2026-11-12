using System;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Expands case patterns only until the first match, then executes its body.</summary>
    private async Task<EvaluationResult> EvaluateCaseAsync(
        CaseCommand caseCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        var subject = await ExpandWordAsync(caseCommand.Subject, scope).ConfigureAwait(false);
        if (_state.HasPendingExpansionError)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

        foreach (var item in caseCommand.Items)
        {
            foreach (var pattern in item.Patterns)
            {
                var expanded = await ExpandWordAsync(pattern, scope).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
                var mask = default(GlobMaskSpan);
                if (TextGlobMatcher.MatchText(expanded.AsSpan(), in mask, subject.AsSpan(), _cancellationToken) != GlobMatchResult.Match)
                    continue;

                var result = await EvaluateCommandAsync(item.Body, scope, negated: false).ConfigureAwait(false);
                return ApplyNegation(result, negated);
            }
        }

        return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
    }
}

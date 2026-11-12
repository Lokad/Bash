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
        var subjectWord = await AwaitWordExpansionAsync(WordExpansion.ExpandUnsplitWordAsync(
            caseCommand.Subject, scope, ExpansionPurpose.Scalar, allowLeadingTilde: true)).ConfigureAwait(false);
        var subject = subjectWord.ToValue(Memory);
        if (_state.HasPendingExpansionError)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

        foreach (var item in caseCommand.Items)
        {
            foreach (var pattern in item.Patterns)
            {
                var expanded = await AwaitWordExpansionAsync(WordExpansion.ExpandUnsplitWordAsync(
                    pattern, scope, ExpansionPurpose.Scalar, allowLeadingTilde: true)).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
                var prepared = ShellPattern.FromWord(expanded, Memory, _cancellationToken);
                if (!prepared.IsMatch(subject.Bytes.Span, _cancellationToken))
                    continue;

                var result = await EvaluateCommandAsync(item.Body, scope, negated: false).ConfigureAwait(false);
                return ApplyNegation(result, negated);
            }
        }

        return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
    }
}

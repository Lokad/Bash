using System.Threading.Tasks;
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
        var body = await SelectBodyAsync().ConfigureAwait(false);
        if (body == null)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(_state.HasPendingExpansionError ? 1 : 0), negated)
                .ConfigureAwait(false);

        var result = await EvaluateCommandAsync(body, scope, negated: false).ConfigureAwait(false);
        return ApplyNegation(result, negated);

        async Task<ICommand?> SelectBodyAsync()
        {
            using var subjectMemory = Memory.BeginCommand(scope);
            var fields = await AwaitWordExpansionAsync(WordExpansion.ExpandWithoutBraceAsync(
                caseCommand.Subject, scope, allowWordSplitting: false, ExpansionPurpose.Scalar, TildeExpansion.Argument))
                .ConfigureAwait(false);
            var subject = ExpandedWord.JoinValuesWithSpaces(fields, Memory);
            if (_state.HasPendingExpansionError)
                return null;

            using var patternMemory = Memory.BeginCommand(scope);
            foreach (var item in caseCommand.Items)
            {
                foreach (var pattern in item.Patterns)
                {
                    var expanded = await AwaitWordExpansionAsync(WordExpansion.ExpandWithoutBraceAsync(
                        pattern, scope, allowWordSplitting: false, ExpansionPurpose.CasePattern, TildeExpansion.Leading))
                        .ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return null;
                    if (ShellPattern.FromWords(expanded, Memory, _cancellationToken)
                        .IsMatch(subject.Bytes.Span, _cancellationToken))
                        return item.Body;

                    // Each alternative consumes its expanded pattern. Binding effects
                    // and the subject snapshot survive reuse of the pattern workspace.
                    patternMemory.ResetWorkspace();
                }
            }
            return null;
        }
    }
}

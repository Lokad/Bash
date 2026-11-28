using System.Threading.Tasks;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Matches a fixed subject and executes each reached arm through the ordinary command path.</summary>
    private async Task<EvaluationResult> EvaluateCaseAsync(
        CaseCommand caseCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        EvaluationMemory.Command? subjectMemory = Memory.BeginCommand(scope);
        bool? subjectHasMalformedUtf8 = null;
        try
        {
            var subject = ExpandedWord.JoinValuesWithSpaces(
                await AwaitWordExpansionAsync(WordExpansion.ExpandWithoutBraceAsync(
                    caseCommand.Subject, scope, allowWordSplitting: false, ExpansionPurpose.Scalar, TildeExpansion.Argument))
                    .ConfigureAwait(false), Memory);
            if (_state.HasPendingExpansionError)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

            var items = caseCommand.Items;
            var result = EvaluationResult.FromExitCode(0);
            var subjectScratchRetired = false;
            for (var index = 0; index < items.Count;)
            {
                index = await FindMatchAsync(subject, index).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
                if (index == items.Count)
                    break;

                var last = index;
                while (last + 1 < items.Count && items[last].Continuation == CaseContinuation.FallThrough)
                {
                    CheckCancellation();
                    last++;
                }
                var retest = last + 1 < items.Count && items[last].Continuation == CaseContinuation.Retest;
                if (!retest)
                {
                    // No later pattern uses the subject. Retire it before entering the body chain.
                    subject = ShellValue.Empty;
                    var owner = subjectMemory;
                    subjectMemory = null;
                    owner?.Dispose();
                }
                else if (!subjectScratchRetired)
                {
                    // A later match needs the bytes, while preparation scratch has already finished.
                    if (caseCommand.Subject.LiteralFields != null || subject.IsEmpty)
                        subjectMemory?.ResetWorkspace();
                    else if (subject.TryGetCompactArrayCapacity(out var capacity))
                        subjectMemory?.ResetWorkspace(MemoryCapacity.ArrayBackingBytes<byte>(capacity));
                    subjectScratchRetired = true;
                }
                for (; index <= last; index++)
                {
                    CheckCancellation();
                    var body = items[index].Body;
                    // An empty arm contributes status zero without publishing a command status.
                    result = body is SimpleCommand { Words.Count: 0, Assignments.Count: 0, Redirections.Count: 0 }
                        ? EvaluationResult.FromExitCode(0)
                        : await EvaluateCommandAsync(body, scope, negated: false).ConfigureAwait(false);
                    if (result.Flow != FlowControlKind.None)
                        return ApplyNegation(result, negated);
                    if (_state.HasPendingExpansionError)
                        return await FinalizeResultAsync(result, negated).ConfigureAwait(false);
                }
                if (!retest)
                    break;
            }
            return ApplyNegation(result, negated);
        }
        finally
        {
            subjectMemory?.Dispose();
        }

        async ValueTask<int> FindMatchAsync(ShellValue subject, int first)
        {
            using var patternMemory = Memory.BeginCommand(scope);
            for (var index = first; index < caseCommand.Items.Count; index++)
            {
                foreach (var pattern in caseCommand.Items[index].Patterns)
                {
                    var expanded = await AwaitWordExpansionAsync(WordExpansion.ExpandWithoutBraceAsync(
                        pattern, scope, allowWordSplitting: false, ExpansionPurpose.CasePattern, TildeExpansion.Leading))
                        .ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return caseCommand.Items.Count;
                    if (ShellPattern.MatchesWords(expanded, subject.Bytes.Span, Memory, _cancellationToken,
                        IgnoreCaseInPatterns, ref subjectHasMalformedUtf8))
                        return index;

                    // Each alternative consumes its expanded pattern. Binding effects
                    // and the subject snapshot survive reuse of the pattern workspace.
                    patternMemory.ResetWorkspace();
                }
            }
            return caseCommand.Items.Count;
        }
    }
}

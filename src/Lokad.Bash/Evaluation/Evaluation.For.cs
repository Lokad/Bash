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
        if (!forCommand.InvalidName.IsEmpty)
            return await RejectIdentifierAsync(forCommand.InvalidName).ConfigureAwait(false);
        BindingName name = forCommand.Name;
        if (!name.IsIdentifier)
            return await RejectIdentifierAsync(name.ToValue()).ConfigureAwait(false);

        var items = new List<ShellValue>();
        foreach (var word in forCommand.Items)
        {
            await AppendOperandValuesAsync(word, scope, items).ConfigureAwait(false);
            if (_state.HasPendingExpansionError)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }

        var loopContext = scope.Context.WithLoopDepth(scope.Context.LoopDepth + 1);
        if (items.Count == 0)
        {
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);
        }

        var lastBodyResult = EvaluationResult.FromExitCode(0);
        foreach (var item in items)
        {
            CheckCancellation();
            var value = item;
            if (scope.Variables.IsIntegerBinding(name) && !scope.Variables.TryGetNameref(name, out _)
                && !scope.Variables.IsReadonlyBinding(name))
            {
                var integer = await AwaitWordExpansionAsync(
                    WordExpansion.EvaluateIntegerAssignmentAsync(item, null, scope, ShellValue.Empty).AsTask()).ConfigureAwait(false);
                if (!integer.HasValue)
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
                value = integer.Value;
            }
            var failure = scope.Variables.AssignForIteration(name, value, Memory);
            if (failure == ForAssignmentFailure.InvalidTarget)
                return await RejectIdentifierAsync(item).ConfigureAwait(false);
            if (failure == ForAssignmentFailure.IncompatibleAttributes)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
            if (failure == ForAssignmentFailure.Readonly)
            {
                await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText("readonly variable")).ConfigureAwait(false);
                // A readonly loop assignment exits with errexit enabled, even in a tested position.
                if (_state.ErrexitEnabled)
                    return EvaluationResult.Exit(1);
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

        async Task<EvaluationResult> RejectIdentifierAsync(ShellValue invalid)
        {
            await ReportExpansionDiagnosticAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(invalid),
                ShellValue.FromText("not a valid identifier")).ConfigureAwait(false);
            // Identifier failures end the loop without triggering errexit at the loop boundary.
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1).WithTestedStatus(true), negated)
                .ConfigureAwait(false);
        }
    }
}

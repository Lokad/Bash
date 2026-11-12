using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Evaluates a function invocation, managing positional parameters and local variables.
    /// </summary>
    private async Task<EvaluationResult> EvaluateFunctionAsync(
        FunctionDefinition functionDefinition,
        IReadOnlyList<ShellValue> arguments,
        ScopedEvaluation scope,
        Variables commandVars,
        IReadOnlySet<BindingName>? prefixNames,
        bool negated)
    {
        // Keep the inactive caller frame admitted and reserve the callee's owned entry array.
        Memory.Charge(checked(scope.Variables.PositionalMemoryBytes + 32L * arguments.Count));
        using var bindings = prefixNames == null
            ? null
            : new Variables.CommandBindings(scope.Variables, commandVars, prefixNames);
        var functionContext = new FunctionContext(
            functionDefinition.Name.Value, new Variables.LocalBindings(scope.Variables, parent: null, bindings), scope.Context.FunctionContext)
        {
            PrefixBindings = bindings
        };
        using var positionalParametersScope = scope.Variables.EnterPositionalParametersScope(
            arguments,
            PositionalParametersRestorePolicy.Always);
        EvaluationResult functionResult;
        try
        {
            functionResult = await EvaluateCommandAsync(
                    functionDefinition.Body,
                    // A function can only break/continue its own loops, not the caller's loops.
                    scope with { Context = new EvaluationContext(0, functionContext, ReturnTargetKind.Function) { GlobalPrefixes = scope.Context.GlobalPrefixes, ErrexitIgnored = scope.Context.ErrexitIgnored } },
                    negated: false)
                .ConfigureAwait(false);

            CompletePromotions(functionContext, scope.Context.FunctionContext, scope.Variables, bindings);
        }
        finally
        {
            functionContext.Locals.Restore();
        }

        if (functionResult.Flow == FlowControlKind.Return)
        {
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(functionResult.ExitCode), negated);
        }

        return ApplyNegation(functionResult, negated);
    }

    /// <summary>Retains eligible bindings before their command or function scope restores them.</summary>
    private static void CompletePromotions(
        FunctionContext completed,
        FunctionContext? enclosing,
        Variables variables,
        Variables.CommandBindings? bindings)
    {
        foreach (var name in completed.PromotedNames)
        {
            var isPrefix = bindings?.Contains(name) == true;
            if (completed.Locals.BlocksPromotion(name)
                || (completed.Locals.Owns(name) && !isPrefix))
                continue;

            var survives = variables.HasBinding(name);
            // A promoted function-call prefix returns to its caller as an exported binding.
            if (isPrefix && survives && completed.Locals.ExportSnapshot != null)
                variables.SetExported(name, true);
            if (survives)
                bindings?.RetainBinding(name);
            if (isPrefix && survives)
                enclosing?.Locals.RecordPrefixPromotion(name);
            if (!isPrefix || survives)
                enclosing?.PromotedNames.Add(name);
        }
    }
}

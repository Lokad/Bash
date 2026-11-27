using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private const int FunctionContinuationInterval = 8;

    /// <summary>
    /// Evaluates a function invocation, managing positional parameters and local variables.
    /// </summary>
    private async Task<EvaluationResult> EvaluateFunctionAsync(
        FunctionBinding function,
        IReadOnlyList<ShellValue> arguments,
        ScopedEvaluation scope,
        Variables commandVars,
        HashSet<BindingName>? prefixNames,
        bool negated)
    {
        // Command entry checks the CLR's stack estimate. A periodic continuation
        // also limits synchronous function nesting between those checks.
        var callerDepth = scope.Context.FunctionContext?.Depth ?? 0;
        var yieldAtBoundary = (callerDepth + 1) % FunctionContinuationInterval == 0;
        if (yieldAtBoundary)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            CheckCancellation();
        }

        var functionDefinition = function.Definition;
        // Keep the inactive caller frame admitted and reserve the callee's owned entry array.
        Memory.Charge(checked(scope.Variables.PositionalMemoryBytes + 32L * arguments.Count));
        using var bindings = prefixNames == null
            ? null
            : new Variables.CommandBindings(scope.Variables, commandVars, prefixNames,
                _state.SuppressTemporaryLookup, Memory, _cancellationToken);
        bindings?.CaptureFunctionEntryGetoptsCursor();
        using var functionContext = new FunctionContext(
            functionDefinition.Name.Value, new Variables.LocalBindings(scope.Variables, parent: null, bindings), scope.Context.FunctionContext)
        {
            PrefixBindings = bindings,
            CallerSourceMemory = scope.Context.SourceMemory
        };
        using var positionalParametersScope = scope.Variables.EnterFunctionPositionals(arguments);
        functionContext.CallerPositionals = positionalParametersScope;
        var functionScope = scope with
        {
            // A function can only break/continue its own loops, not the caller's loops.
            Context = new EvaluationContext(0, functionContext, ReturnTargetKind.Function)
            {
                GlobalPrefixes = scope.Context.GlobalPrefixes,
                CallerSource = scope.Context.CallerSource,
                ErrexitIgnored = scope.Context.ErrexitIgnored,
                SourceMemory = function.SourceMemory,
                Coordinates = function.Coordinates
            }
        };
        EvaluationResult functionResult;
        var preserveExitOptions = false;
        try
        {
            try
            {
                functionResult = await EvaluateCommandAsync(functionDefinition.Body, functionScope, negated: false)
                    .ConfigureAwait(false);
            }
            finally
            {
                // A completed body can resume callers inline, including during failure unwinding.
                if (yieldAtBoundary)
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }

            if (IsDirectPipelineStageScope(scope) && _state.Traps?.HasPendingExitAction == true)
            {
                if (functionResult.Flow is FlowControlKind.None or FlowControlKind.Return)
                    functionResult = await RunExitTrapAsync(functionScope, functionResult).ConfigureAwait(false);
                else if (functionResult.Flow == FlowControlKind.Exit && _state.FatalExpansion is { Kind: ExpansionFailureKind.ReaderSyntax })
                {
                    // A child action installed by the function sees its arguments after the return target unwinds.
                    var exitScope = functionScope with { Context = functionScope.Context with { ReturnTarget = scope.Context.ReturnTarget } };
                    functionResult = await RunExitTrapAsync(exitScope, functionResult).ConfigureAwait(false);
                    _state.FatalExpansion = null;
                    _state.TryConsumeExpansionError(out _);
                }
            }

            // A terminating exit or errexit leaves the active options visible to EXIT.
            // Control-operand and expansion errors restore local options as they unwind.
            preserveExitOptions = functionResult.Flow == FlowControlKind.Exit
                && !functionResult.RestoresLocalOptions && _state.FatalExpansion == null;
            CompletePromotions(functionContext, scope.Context.FunctionContext, scope.Variables, bindings);
        }
        finally
        {
            if (!preserveExitOptions && functionContext.SavedErrexit is { } savedErrexit)
                _state.ErrexitEnabled = savedErrexit;
            functionContext.Locals.Restore();
        }

        if (functionResult.Flow == FlowControlKind.Return)
        {
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(functionResult.ExitCode), negated).ConfigureAwait(false);
        }

        // A function entered as a pipeline command uses the compound child boundary.
        if (_state.ForkKind == ShellForkKind.PipelineSimpleCommand && !scope.InNestedCommandUnits)
            functionResult = CompleteDiscardInChild(functionResult);

        return ApplyNegation(functionResult, negated);
    }

    /// <summary>Retains eligible bindings before their command or function scope restores them.</summary>
    private void CompletePromotions(
        FunctionContext completed,
        FunctionContext? enclosing,
        Variables variables,
        Variables.CommandBindings? bindings)
    {
        if (completed.BindingsRestoredForExit)
            return;
        foreach (var name in completed.PromotedNames)
        {
            var isPrefix = bindings?.Contains(name) == true;
            var promotedPrefix = completed.Locals.PromotedPrefixes.Contains(name);
            if (completed.Locals.BlocksPromotion(name)
                || (completed.Locals.Owns(name) && !isPrefix && !promotedPrefix))
                continue;

            var survives = variables.HasBinding(name);
            // A promoted function-call prefix returns to its caller as an exported binding.
            if (isPrefix && survives && completed.Locals.SavedBindings != null)
                variables.SetExported(name, true);
            if (survives)
                bindings?.RetainBinding(name, Memory, _cancellationToken);
            if ((isPrefix || promotedPrefix) && survives)
                enclosing?.Locals.RecordPrefixPromotion(name);
            if (!isPrefix || survives)
                enclosing?.PromotedNames.Add(name);
        }
    }
}

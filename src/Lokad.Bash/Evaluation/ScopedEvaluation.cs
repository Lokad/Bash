using System.Collections.Generic;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Selects active bindings and lexical context for evaluator-owned effects.
/// Creating a scope neither clones state nor transfers ownership.
/// </summary>
internal readonly record struct ScopedEvaluation(
    Evaluation Effects,
    Variables Variables,
    Evaluation.EvaluationContext Context)
{
    /// <summary>Bounds nested numeric evaluation and shares its stack-safe work pump.</summary>
    internal ArithmeticContext? Arithmetic { get; init; }

    /// <summary>
    /// Receives prefix-RHS writes in the caller, or caller redirection writes in temporary command bindings.
    /// Child evaluations construct their own scopes and do not inherit this target.
    /// </summary>
    public ExpansionBindings? ExpansionWriteTarget { get; init; }

    /// <summary>Retains builtin selector writes that belong to the caller rather than a temporary prefix.</summary>
    internal Variables.CommandBindings? SelectorBindings { get; init; }

    /// <summary>A builtin's closed diagnostic output stops its expansion without making later commands fatal.</summary>
    internal bool CanExpand => !Effects.HasPendingExpansionError && Effects.BuiltinDiagnosticsOpen;

    /// <summary>The current simple command owns the prefix-name set, adding names as their bindings are prepared.</summary>
    internal readonly record struct ExpansionBindings(Variables Variables, HashSet<BindingName> PrefixNames);

    public void SetExpansionValueIgnoringReadonly(BindingName name, ShellValue value)
    {
        if (SelectorBindings is { } bindings && Context.FunctionContext?.IsLocal(name) == true
            && Variables.TryGetNameref(name, out _))
        {
            var resolved = Variables.ResolveNamerefName(name);
            if (!resolved.IsEmpty && bindings.RetainSelectorBinding(resolved) is { } retained)
            {
                // Scalar writes through local namerefs reach the caller; subsequent reads still see the prefix.
                retained.SetResolvedValuePreservingArray(resolved, value);
                return;
            }
        }

        Variables.SetExpansionValueIgnoringReadonly(name, value, Effects.ExpansionBudget.Words);
        if (SelectorBindings != null)
        {
            var resolved = Variables.ResolveNamerefName(name);
            GetSelectorWriteTarget(name, resolved)?.SetResolvedValuePreservingArray(resolved, value);
        }
        GetExpansionWriteTarget(name)?.SetExpansionValueIgnoringReadonly(name, value, Effects.ExpansionBudget.Words);
    }

    /// <summary>Retains selected builtin writes, except indirect writes into temporary prefixes.</summary>
    internal Variables? GetSelectorWriteTarget(BindingName sourceName, BindingName selectedName)
    {
        if (SelectorBindings is not { } bindings || IsIndirectPrefixWrite(sourceName, bindings.PrefixNames))
            return null;
        return bindings.RetainSelectorBinding(selectedName);
    }

    /// <summary>Selects the additional bindings receiving this expansion write, if any.</summary>
    public Variables? GetExpansionWriteTarget(BindingName name)
    {
        if (ExpansionWriteTarget is not { } target)
            return null;

        return IsIndirectPrefixWrite(name, target.PrefixNames) ? null : target.Variables;
    }

    // Except for the local scalar case above, a nameref to a prefix updates only the expansion's own scope.
    // Direct writes cross the boundary, including assignments of the same value.
    private bool IsIndirectPrefixWrite(BindingName name, IReadOnlySet<BindingName> prefixes) =>
        Variables.TryGetNameref(name, out _)
        && Variables.TryResolveBinding(name, selectOwner: null, out var binding)
        && prefixes.Contains(binding.Name);
}

/// <summary>Shares one pump across a numeric expression and its nested selectors.</summary>
internal sealed record ArithmeticContext(int Depth)
{
    public ArithmeticWorkQueue Work { get; } = new();
}

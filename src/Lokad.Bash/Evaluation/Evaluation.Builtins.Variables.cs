using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Handles <c>unset</c> namespace selection and readonly protection.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinUnsetAsync(
        IReadOnlyList<ShellValue> words,
        Variables commandVars,
        ScopedEvaluation scope,
        Variables.CommandBindings? bindings,
        bool negated)
    {
        var context = scope.Context;
        var functions = false;
        var variables = false;
        var namerefs = false;
        var argumentIndex = 1;
        for (; argumentIndex < words.Count; argumentIndex++)
        {
            var argument = words[argumentIndex];
            if (argument.Bytes.Span.SequenceEqual("--"u8))
            {
                argumentIndex++;
                break;
            }
            if (argument.Bytes.Length < 2 || argument.Bytes.Span[0] != (byte)'-')
                break;
            var parser = new CommandLineOptionParser([argument], allowSingleDashLongOptions: false);
            while (parser.TryRead(out var item))
            {
                if (item.Kind == CommandLineOptionKind.ShortOption)
                {
                    switch (item.ShortName)
                    {
                        case (byte)'f': functions = true; continue;
                        case (byte)'v': variables = true; continue;
                        case (byte)'n': namerefs = true; continue;
                    }
                }
                var option = item.Kind == CommandLineOptionKind.ShortOption
                    ? ShellValue.FromBytes(new byte[] { (byte)'-', item.ShortName }) : ShellValue.FromText("--");
                return await FailAsync(WordExpansion.AppendAssignmentValue(option,
                    ShellValue.FromText(": invalid option\nunset: usage: " + BuiltinDocumentation.For(BuiltinKind.Unset).Synopsis("unset")), scope), 2)
                    .ConfigureAwait(false);
            }
        }
        if (functions && variables)
            return await FailAsync(ShellValue.FromText("cannot simultaneously unset a function and a variable"), 1).ConfigureAwait(false);

        var exitCode = 0;
        for (var index = argumentIndex; index < words.Count; index++)
        {
            var nameValue = words[index];
            var name = BindingNameExpansion.FromValue(nameValue, Memory);
            var directReference = default(VariableReference);
            var directSelector = !functions && !namerefs && !commandVars.HasBinding(name)
                && context.FunctionContext?.IsLocal(name) != true && nameValue.Bytes.Span.Contains((byte)'[')
                && VariableReference.TryParse(ShellSource.FromBytes(nameValue.Bytes), Memory, out directReference)
                && directReference.Kind != VariableReferenceKind.Name && !directReference.Subscript.IsEmpty;
            var resolvedBinding = commandVars.TryResolveBinding(name, selectOwner: null, out var binding);
            // Explicit selectors never fall back to functions; element namerefs still can.
            // Whole-name lookup follows namerefs and retains unset locals' namespace protection.
            var hidesFunction = directSelector || resolvedBinding && binding.Reference.Kind == VariableReferenceKind.Name
                && !binding.IsUnsetNameref && (binding.Owner.HasBinding(binding.Name)
                    || context.FunctionContext?.IsLocal(binding.Name) == true);
            // -n is a no-op for valid non-nameref names; other spellings can still select a function.
            if (functions || !variables && (!namerefs || !name.IsIdentifier)
                && !hidesFunction && _state.Functions.ContainsKey(nameValue))
            {
                if (!_state.TryUnsetFunction(nameValue))
                {
                    exitCode = 1;
                    await ReportExpansionDiagnosticAsync(ShellValue.FromText("unset"),
                        WordExpansion.AppendAssignmentValue(nameValue, ShellValue.FromText(": cannot unset: readonly function"), scope)).ConfigureAwait(false);
                }
                continue;
            }
            if (namerefs && !commandVars.TryGetNameref(name, out _))
            {
                if (variables && !name.IsIdentifier)
                {
                    exitCode = 1;
                    await ReportExpansionDiagnosticAsync(ShellValue.FromText("unset"),
                        WordExpansion.AppendAssignmentValue(ShellArgumentEscaping.QuoteDiagnosticOperand(nameValue),
                            ShellValue.FromText(": not a valid identifier"), scope)).ConfigureAwait(false);
                }
                continue;
            }
            var unsetFlags = namerefs ? UnsetFlags.NamerefOnly : UnsetFlags.None;
            if (!namerefs && (directSelector || resolvedBinding && binding.Reference.Kind != VariableReferenceKind.Name))
            {
                var elementResult = await WordExpansion.UnsetElementAsync(directSelector ? directReference : binding.Reference,
                        directSelector ? WordExpansion.UnsetReferenceKind.Direct : WordExpansion.UnsetReferenceKind.Nameref,
                        scope with { Variables = commandVars, ExpansionWriteTarget = null }).ConfigureAwait(false);
                if (!elementResult.Succeeded)
                    exitCode = 1;
                if (HasPendingExpansionError) break;
                if (!elementResult.Succeeded || elementResult.ScalarToUnset.IsEmpty)
                    continue;
                // Scalar [0] removal needs the same local/prefix cleanup as a whole-name unset.
                name = elementResult.ScalarToUnset;
                unsetFlags = UnsetFlags.ScalarOnly;
            }
            var resolved = namerefs ? name : commandVars.ResolveNamerefName(name);
            var removedName = resolved.IsEmpty ? name : resolved;
            var keepExport = commandVars.IsExported(removedName)
                && context.FunctionContext?.Locals.IsDeclaredPrefix(removedName) == true;
            if (!commandVars.TryUnset(name, unsetFlags))
            {
                exitCode = 1;
                if (namerefs || commandVars.IsReadonly(name))
                    await ReportExpansionDiagnosticAsync(ShellValue.FromText("unset"),
                        WordExpansion.AppendAssignmentValue(removedName.ToValue(), ShellValue.FromText(": cannot unset: readonly variable"), scope)).ConfigureAwait(false);
                continue;
            }

            if (bindings?.Contains(removedName) != true)
            {
                // A declared prefix keeps its current export flag, including an earlier +x/export -n.
                if (keepExport) commandVars.SetExported(removedName, true);
                context.FunctionContext?.Locals.RecordUnset(removedName);
                HideSavedPrefixExport(removedName);
            }
        }

        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);

        async Task<EvaluationResult> FailAsync(ShellValue message, int status)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("unset"), message).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated);
        }

        void HideSavedPrefixExport(BindingName name)
        {
            var prefix = context.GlobalPrefixes;
            for (var function = context.FunctionContext; function != null; function = function.Enclosing)
            {
                while (prefix != null && prefix.FunctionDepth == function.Depth)
                {
                    if (prefix.Names.Contains(name)) return;
                    prefix = prefix.Enclosing;
                }
                if (function.Locals.ExportSnapshot is { } saved && function.Locals.SavedNames.Contains(name))
                {
                    // This saved call prefix is discarded on return; outer bindings must retain their exports.
                    if (function.PrefixNames.Contains(name)) saved.SetExported(name, false);
                    return;
                }
                if (function.PrefixNames.Contains(name)) return;
            }
        }
    }

    /// <summary>
    /// Handles the <c>shift</c> builtin by removing positional parameters.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinShiftAsync(
        IReadOnlyList<string> words,
        Variables commandVars,
        bool negated)
    {
        var exitCode = 0;
        var count = 1;
        if (words.Count > 1)
        {
            var word = words[1];

            if (!Integers.TryParseExitStatus(word.AsSpan(), out count))
            {
                exitCode = 2;
            }
        }

        if (exitCode == 0 && count > 0)
        {
            if (!commandVars.TryShiftPositionalParameters(count))
                exitCode = 1;
        }

        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    /// <summary>
    /// Handles the <c>set</c> builtin by updating positional parameters or options.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinSetAsync(
        IReadOnlyList<ShellValue> words,
        Variables commandVars,
        bool negated)
    {
        var arguments = new List<ShellValue>(Math.Max(0, words.Count - 1));
        for (var index = 1; index < words.Count; index++)
            arguments.Add(words[index]);

        if (arguments.Count == 0)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);

        var argumentIndex = 0;
        var option = arguments[argumentIndex];

        if (option.Bytes.Span.Length == 1 && option.Bytes.Span[0] == '-')
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);

        if (option.Bytes.Span.Length == 2
            && option.Bytes.Span[0] == '-'
            && option.Bytes.Span[1] == '-')
        {
            argumentIndex++;
            if (argumentIndex >= arguments.Count)
            {
                commandVars.ReplacePositionalParameters([]);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);
            }
        }
        else if (!option.IsEmpty && option.Bytes.Span[0] == '-')
        {
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
        }

        if (argumentIndex >= arguments.Count)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);

        commandVars.ReplacePositionalParameters(arguments.GetRange(argumentIndex, arguments.Count - argumentIndex));
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);
    }

    /// <summary>
    /// Handles the <c>let</c> builtin by evaluating arithmetic expressions.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinLetAsync(
        IReadOnlyList<ShellValue> words,
        Variables commandVars,
        Variables.CommandBindings? bindings,
        EvaluationContext context,
        bool negated)
    {
        long? lastResult = 0;
        for (var index = 1; index < words.Count; index++)
        {
            var expression = words[index];

            lastResult = await AwaitWordExpansionAsync(WordExpansion.EvaluateArithmeticAsync(
                ArithmeticOperand.FromValue(expression), new ScopedEvaluation(this, commandVars, context) { SelectorBindings = bindings },
                WordExpansion.ArithmeticFailureMode.Diagnostic, AssignOutput))
                .ConfigureAwait(false);
            if (lastResult == null)
                break;
        }

        var exitCode = lastResult is null or 0 ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);

        bool AssignOutput(VariableReference reference, WordExpansion.ResolvedVariableReference target, ShellValue value)
        {
            var sourceName = reference.Name;
            if (reference.Kind == VariableReferenceKind.Name
                && commandVars.TryResolveBinding(reference.Name, selectOwner: null, out var selected))
            {
                if (selected.Reference.Kind == VariableReferenceKind.Name)
                    return TryAssignBuiltinOutput(reference, value, commandVars, bindings, context);
                // Keep an intermediate alias: its prefix-owned write must not reach the caller.
                sourceName = selected.Name;
            }
            // A prefix hiding a caller nameref stays temporary, just like other builtin outputs.
            var outputBindings = ReferenceEquals(bindings?.SelectOutputVariables(target.Name), commandVars) ? null : bindings;
            return !commandVars.IsReadonly(reference.Name)
                && target.TryAssign(new ScopedEvaluation(this, commandVars, context) { SelectorBindings = outputBindings },
                    sourceName, value, AssignmentOperator.Replace);
        }
    }

    /// <summary>Routes builtin output operands to caller bindings, respecting dynamic nameref ownership.</summary>
    private bool TryAssignBuiltinOutput(
        string name, ShellValue value, Variables variables, Variables.CommandBindings? bindings,
        EvaluationContext context)
    {
        if (!VariableReference.TryParse(name, out var reference))
            return false;
        return TryAssignBuiltinOutput(reference, value, variables, bindings, context);
    }

    private bool TryAssignBuiltinOutput(
        VariableReference reference, ShellValue value, Variables variables, Variables.CommandBindings? bindings,
        EvaluationContext context)
    {
        var resolution = SelectBuiltinReference(reference, variables, context, out var binding);
        if (resolution != BuiltinReferenceResolution.Write)
            return resolution == BuiltinReferenceResolution.NoWrite;
        return bindings != null && ReferenceEquals(binding.Owner, variables)
            ? bindings.TryApplyOutputAssignment(binding.Reference, value, Memory)
            : binding.Owner.TryApplyAssignment(binding.Reference, value, Memory);
    }

    private enum BuiltinReferenceResolution { Write, NoWrite, Invalid }

    /// <summary>Local namerefs select caller bindings; indirect unset chains may create a local shadow.</summary>
    private BuiltinReferenceResolution SelectBuiltinReference(
        VariableReference reference, Variables variables, EvaluationContext context, out VariableBinding selected)
    {
        selected = new VariableBinding(reference, variables);
        if (context.FunctionContext?.IsLocal(reference.Name) != true || !variables.TryGetNameref(reference.Name, out _))
            return BuiltinReferenceResolution.Write;
        if (!variables.TryResolveBinding(reference.Name, selectOwner: null, out var binding)
            || reference.Kind != VariableReferenceKind.Name
                && (binding.IsUnsetNameref || binding.Reference.Kind != VariableReferenceKind.Name))
            return BuiltinReferenceResolution.Invalid;
        if (reference.Kind == VariableReferenceKind.Name && binding.IsUnsetNameref && binding.Name != reference.Name)
        {
            if (!context.TryResolveLocalNamerefAssignment(reference.Name, variables, Memory, out var fallback)
                || fallback is not LocalNamerefAssignment.Write write)
                return BuiltinReferenceResolution.NoWrite;
            binding = write.Binding;
        }
        selected = new VariableBinding(reference.Kind == VariableReferenceKind.Name
            ? binding.Reference : VariableReference.ForElement(binding.Name, reference.Subscript), binding.Owner);
        return BuiltinReferenceResolution.Write;
    }

    private enum BuiltinOutputVisibility { Caller, CommandAndCaller }
    private enum BuiltinAssignmentResult { Assigned, NoWrite, Failed }

    /// <summary>Shares selected array/nameref writes between read and printf without replaying selectors.</summary>
    private async Task<BuiltinAssignmentResult> AssignBuiltinAsync(
        VariableReference reference, ShellValue value, ScopedEvaluation scope,
        Variables.CommandBindings? persistentOutputs, BuiltinOutputVisibility visibility, ShellValue command)
    {
        var resolution = SelectBuiltinReference(reference, scope.Variables, scope.Context, out var binding);
        if (resolution == BuiltinReferenceResolution.NoWrite)
            return BuiltinAssignmentResult.NoWrite;
        if (resolution == BuiltinReferenceResolution.Invalid)
            return reference.Kind == VariableReferenceKind.Name
                ? BuiltinAssignmentResult.Failed : await InvalidElementTargetAsync().ConfigureAwait(false);
        var destination = binding.Owner;
        reference = binding.Reference;
        if (ReferenceEquals(destination, scope.Variables))
            destination = persistentOutputs?.SelectOutputVariables(reference.Name) ?? destination;
        if (reference.Kind != VariableReferenceKind.Name
            && destination.TryResolveBinding(reference.Name, selectOwner: null, out var selected)
            && selected.IsUnsetNameref)
            return await InvalidElementTargetAsync().ConfigureAwait(false);
        var target = await WordExpansion.ResolveBuiltinReferenceAsync(reference, scope, destination, binding.Owner).ConfigureAwait(false);
        if (HasPendingExpansionError || !BuiltinDiagnosticsOpen || target == null || target.Name.IsEmpty)
            return BuiltinAssignmentResult.Failed;
        if (destination.IsReadonly(target.Name))
        {
            await ReportExpansionDiagnosticAsync(target.Name.ToValue(), ShellValue.FromText("readonly variable")).ConfigureAwait(false);
            return BuiltinAssignmentResult.Failed;
        }
        var failure = destination.GetNamerefAssignmentFailure(reference.Name, target.Name, value, Memory);
        if (failure != NamerefAssignmentFailure.None)
        {
            if (failure == NamerefAssignmentFailure.InvalidTarget)
                await ReportExpansionDiagnosticAsync(command, ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                    .Append(ShellArgumentEscaping.QuoteDiagnosticOperand(value).Bytes).Append(": not a valid identifier"u8).ToMemory())).ConfigureAwait(false);
            return BuiltinAssignmentResult.Failed;
        }
        var assigned = target.TryAssign(scope with
        {
            Variables = destination,
            ExpansionWriteTarget = null,
            SelectorBindings = null
        }, reference.Name, value, AssignmentOperator.Replace);
        if (assigned && visibility == BuiltinOutputVisibility.CommandAndCaller && !ReferenceEquals(destination, binding.Owner))
        {
            // Count writes are visible to later selectors, without replaying an element selector
            // or imposing the caller's array kind on a temporary scalar prefix.
            assigned = reference.Kind == VariableReferenceKind.Name
                ? binding.Owner.TryApplyAssignment(reference, value, Memory)
                : target.TryAssign(scope with { Variables = binding.Owner, ExpansionWriteTarget = null, SelectorBindings = null },
                    reference.Name, value, AssignmentOperator.Replace);
        }
        if (assigned)
            persistentOutputs?.CompleteOutput(reference.Name, destination);
        return assigned ? BuiltinAssignmentResult.Assigned : BuiltinAssignmentResult.Failed;

        async Task<BuiltinAssignmentResult> InvalidElementTargetAsync()
        {
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("`': not a valid identifier")).ConfigureAwait(false);
            return BuiltinAssignmentResult.Failed;
        }
    }

    /// <summary>Explicit attributes can promote a function's prefix; ordinary builtin writes cannot.</summary>
    private static void PromoteAttributeBinding(
        Variables variables,
        BindingName name,
        EvaluationContext context,
        Variables.CommandBindings? bindings)
    {
        var resolved = StripArraySubscript(variables.ResolveNamerefName(name));
        if (resolved.IsEmpty)
            return;

        var storedName = StripArraySubscript(name);
        if (bindings?.Contains(storedName) == true)
        {
            // This builtin owns the named prefix; it must not promote an enclosing function's prefix too.
            bindings.RetainBinding(storedName);
            return;
        }

        if (context.FunctionContext is { } function)
        {
            function.Locals.RecordAttributePromotion(resolved);
            function.PromotedNames.Add(resolved);
            bindings?.RetainBinding(resolved);
        }
    }
}

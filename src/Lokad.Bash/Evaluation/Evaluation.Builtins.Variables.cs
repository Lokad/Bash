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
        var options = new ShortOptionCursor(words);
        while (options.TryReadFlag(out var flag))
        {
            switch (flag.Span[0])
            {
                case (byte)'f': functions = true; continue;
                case (byte)'v': variables = true; continue;
                case (byte)'n': namerefs = true; continue;
            }

            return await FailAsync([ShellValue.FromText("-"), ShellValue.FromBytes(flag),
                ShellValue.FromText(": invalid option\nunset: usage: " + BuiltinDocumentation.For(BuiltinKind.Unset).Synopsis("unset"))], 2)
                .ConfigureAwait(false);
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Unset, "unset", negated)
                .ConfigureAwait(false);
        if (functions && variables)
            return await FailAsync([ShellValue.FromText("cannot simultaneously unset a function and a variable")], 1).ConfigureAwait(false);

        var exitCode = 0;
        for (var index = options.ArgumentIndex; index < words.Count; index++)
        {
            var nameValue = words[index];
            if (functions)
            {
                await UnsetFunctionAsync(nameValue).ConfigureAwait(false);
                continue;
            }

            var name = BindingNameExpansion.FromValue(nameValue, Memory);
            var isIdentifier = name.IsIdentifier;
            var canSelectElement = !namerefs && !isIdentifier;
            var literalBinding = canSelectElement
                && (commandVars.HasBinding(name) || context.FunctionContext?.IsLocal(name) == true);
            var directReference = default(VariableReference);
            var directSelector = canSelectElement && !literalBinding && nameValue.Bytes.Span.Contains((byte)'[')
                && VariableReference.TryParse(ShellSource.FromBytes(nameValue.Bytes), Memory, out directReference)
                && directReference.Kind != VariableReferenceKind.Name && !directReference.Subscript.IsEmpty;
            var mayFallBackToFunction = !variables && (!namerefs || !isIdentifier) && !directSelector;
            var binding = default(VariableBinding);
            var resolvedBinding = (!namerefs || mayFallBackToFunction)
                && commandVars.TryResolveBinding(name, selectOwner: null, out binding);
            // Explicit selectors never fall back to functions; element namerefs still can.
            // Whole-name lookup follows namerefs and retains unset locals' namespace protection.
            var hidesFunction = mayFallBackToFunction && resolvedBinding && binding.Reference.Kind == VariableReferenceKind.Name
                && !binding.IsUnsetNameref && (binding.Owner.HasBinding(binding.Name)
                    || context.FunctionContext?.IsLocal(binding.Name) == true);
            // -n is a no-op for valid non-nameref names; other spellings can still select a function.
            if (mayFallBackToFunction && !hidesFunction && _state.Functions.ContainsKey(nameValue))
            {
                await UnsetFunctionAsync(nameValue).ConfigureAwait(false);
                continue;
            }
            if (variables && !isIdentifier && !directSelector && !literalBinding)
            {
                exitCode = 1;
                await WriteInvalidIdentifierDiagnosticAsync(ShellValue.FromText("unset"), nameValue,
                    _builtinDiagnostics, ClosedStderrHandling.AttemptWrite).ConfigureAwait(false);
                continue;
            }
            if (name.IsEmpty || namerefs && !commandVars.TryGetNameref(name, out _))
                continue;
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
                // Selector evaluation can change bindings before whole-name cleanup of scalar [0].
                name = elementResult.ScalarToUnset;
                resolvedBinding = commandVars.TryResolveBinding(name, selectOwner: null, out binding);
                unsetFlags = UnsetFlags.ScalarOnly;
            }
            // An uninitialized terminal has no referent. After namespace fallback,
            // ordinary unset can remove the named reference without touching the terminal.
            var target = commandVars.SelectUnsetTarget(name,
                !namerefs && resolvedBinding ? binding : null, checkUninitializedTerminal: false);
            // -n removes the named reference. A readonly terminal with a target blocks that removal.
            if (namerefs && commandVars.TryResolveBinding(name, selectOwner: null, BindingResolution.LastNameref, out var terminal)
                && !terminal.IsUnsetNameref && terminal.Owner.IsReadonlyBinding(terminal.Name))
                target = target with { ReadonlyName = terminal.Name };
            var removedName = target.Name;
            var keepExport = commandVars.IsExported(removedName)
                && context.FunctionContext?.Locals.IsDeclaredPrefix(removedName) == true;
            if (!commandVars.TryUnsetSelected(target, unsetFlags))
            {
                exitCode = 1;
                if (!target.ReadonlyName.IsEmpty)
                    await ReportExpansionDiagnosticAsync([ShellValue.FromText("unset")],
                        [target.ReadonlyName.ToValue(), ShellValue.FromText(": cannot unset: readonly variable")]).ConfigureAwait(false);
                continue;
            }

            if (bindings?.Contains(removedName) != true)
            {
                if (ExposeUnsetPrefix(removedName))
                    continue;
                if (context.FunctionContext?.IsLocal(removedName) == true)
                    commandVars.TryDeclareName(removedName);
                // A declared prefix keeps its current export flag, including an earlier +x/export -n.
                if (keepExport) commandVars.SetExported(removedName, true);
                context.FunctionContext?.Locals.RecordUnset(removedName);
                HideSavedPrefixExport(removedName);
            }
        }

        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);

        Task UnsetFunctionAsync(ShellValue name)
        {
            if (_state.TryUnsetFunction(name))
                return Task.CompletedTask;
            exitCode = 1;
            return ReportExpansionDiagnosticAsync([ShellValue.FromText("unset")],
                [name, ShellValue.FromText(": cannot unset: readonly function")]);
        }

        Task<EvaluationResult> FailAsync(ReadOnlySpan<ShellValue> message, int status) =>
            FinishFailureAsync(ReportExpansionDiagnosticAsync([ShellValue.FromText("unset")], message), status);

        async Task<EvaluationResult> FinishFailureAsync(Task diagnostic, int status)
        {
            await diagnostic.ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated);
        }

        bool ExposeUnsetPrefix(BindingName name)
        {
            var prefix = context.GlobalPrefixes;
            for (var function = context.FunctionContext; function != null; function = function.Enclosing)
            {
                if (function.Locals.Declares(name))
                    return false;
                while (prefix != null && prefix.FunctionDepth == function.Depth)
                {
                    if (Expose(prefix.Bindings))
                    {
                        function.PromotedNames.Remove(name);
                        return true;
                    }
                    prefix = prefix.Enclosing;
                }
                if (function.PrefixBindings is { } call && Expose(call))
                {
                    function.PromotedNames.Remove(name);
                    return true;
                }
            }
            while (prefix != null)
            {
                if (Expose(prefix.Bindings)) return true;
                prefix = prefix.Enclosing;
            }
            return false;

            bool Expose(Variables.CommandBindings temporary)
            {
                if (!temporary.Contains(name)) return false;
                temporary.ExposeBinding(name, temporary.SavedVariables);
                return true;
            }
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
                if (function.Locals.SavedBindings is { } saved && function.Locals.SavedNames.Contains(name))
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
        IReadOnlyList<ShellValue> words,
        ScopedEvaluation scope,
        bool negated)
    {
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
            return await WriteControlHelpAsync(words[0], BuiltinKind.Shift, "shift", negated).ConfigureAwait(false);

        var first = words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--"u8) ? 2 : 1;
        var count = 1L;
        if (first < words.Count)
        {
            if (!TryParseControlNumber(words[first], out count))
                return await ReportCountErrorAsync(words[first], ": numeric argument required").ConfigureAwait(false);
            if (words.Count - first > 1)
                return await DiscardForExtraControlArgumentsAsync(ShellValue.FromText("shift")).ConfigureAwait(false);
            // Bash reports the original first operand for a negative count, including a leading --.
            if (count < 0)
                return await ReportCountErrorAsync(words[1], ": shift count out of range").ConfigureAwait(false);
        }

        var exitCode = scope.Variables.TryShiftPositionalParameters(count) ? 0 : 1;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated).ConfigureAwait(false);

        async Task<EvaluationResult> ReportCountErrorAsync(ShellValue operand, string message)
        {
            await ReportExpansionDiagnosticAsync([ShellValue.FromText("shift")],
                [operand, ShellValue.FromText(message)]).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
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
        // Validate the leading groups before publishing option changes. A bare
        // plus ends validation but is skipped when applying the options.
        for (var candidate = 1; candidate < words.Count; candidate++)
        {
            CheckCancellation();
            var field = words[candidate];
            var bytes = field.Bytes.Span;
            if (!IsSetOption(bytes) || bytes.SequenceEqual("--"u8))
                break;
            if (bytes.SequenceEqual("--help"u8))
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Set, "set", negated)
                    .ConfigureAwait(false);

            var firstOtherFlag = bytes[1..].IndexOfAnyExcept((byte)'e') + 1;
            if (firstOtherFlag == 0)
                continue;
            if (bytes[firstOtherFlag] != (byte)'o')
                return await InvalidOptionAsync(field, ": unsupported option", 2).ConfigureAwait(false);
            if (firstOtherFlag == bytes.Length - 1 && candidate + 1 < words.Count
                && !IsSetOption(words[candidate + 1].Bytes.Span))
                candidate++;
        }

        var index = 1;
        var replacePositionals = false;
        var status = 0;
        while (index < words.Count)
        {
            CheckCancellation();
            var option = words[index];
            var bytes = option.Bytes.Span;
            if (bytes.SequenceEqual("--"u8))
            {
                replacePositionals = true;
                index++;
                break;
            }
            if (bytes.SequenceEqual("-"u8))
            {
                index++;
                break;
            }
            if (bytes.SequenceEqual("+"u8))
            {
                index++;
                continue;
            }
            if (!IsSetOption(bytes))
                break;

            var enabled = bytes[0] == (byte)'-';
            for (var flagIndex = 1; flagIndex < option.Bytes.Length; flagIndex++)
            {
                var flag = option.Bytes.Span[flagIndex];
                if (flag == (byte)'e')
                {
                    _state.ErrexitEnabled = enabled;
                    continue;
                }
                if (flag != (byte)'o')
                    return await InvalidOptionAsync(option, ": unsupported option", 1).ConfigureAwait(false);
                if (index + 1 < words.Count && words[index + 1].Bytes.Span is [not ((byte)'-' or (byte)'+'), ..])
                {
                    var name = words[++index];
                    if (!name.Bytes.Span.SequenceEqual("errexit"u8))
                        return await InvalidOptionAsync(name, ": invalid option name", 2).ConfigureAwait(false);
                    _state.ErrexitEnabled = enabled;
                    continue;
                }

                var inspection = enabled
                    ? (_state.ErrexitEnabled ? "errexit\ton\n" : "errexit\toff\n")
                    : (_state.ErrexitEnabled ? "set -o errexit\n" : "set +o errexit\n");
                var written = await WriteBuiltinOutputAsync(words[0], Utf8Text.Encode(inspection)).ConfigureAwait(false);
                if (written.IsError)
                    status = 1;
            }
            index++;
        }

        if (replacePositionals || index < words.Count)
        {
            Memory.Charge(checked(32L * (words.Count - index)));
            commandVars.ReplacePositionalParametersFromSet(words, index);
        }
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated).ConfigureAwait(false);

        static bool IsSetOption(ReadOnlySpan<byte> bytes) =>
            bytes.Length > 1 && bytes[0] is (byte)'-' or (byte)'+';

        async Task<EvaluationResult> InvalidOptionAsync(ShellValue option, string message, int exitCode)
        {
            await ReportExpansionDiagnosticAsync([words[0]], [option, ShellValue.FromText(message)]).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated).ConfigureAwait(false);
        }
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
        var first = 1;
        if (first < words.Count)
        {
            var option = words[first].Bytes.Span;
            if (option.SequenceEqual("--"u8))
                first++;
            else if (option.SequenceEqual("--help"u8))
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Let, "let", negated)
                    .ConfigureAwait(false);
        }

        if (first == words.Count)
            await ReportExpansionDiagnosticAsync(words[0], ShellValue.FromText("expression expected"))
                .ConfigureAwait(false);

        long? lastResult = 0;
        for (var index = first; index < words.Count; index++)
        {
            var expression = words[index];

            lastResult = await AwaitWordExpansionAsync(WordExpansion.EvaluateArithmeticAsync(
                ArithmeticOperand.FromValue(expression), new ScopedEvaluation(this, commandVars, context) { SelectorBindings = bindings },
                WordExpansion.ArithmeticFailureMode.Diagnostic, AssignOutput, words[0]))
                .ConfigureAwait(false);
            if (lastResult == null)
                break;
        }

        var exitCode = lastResult is null or 0 ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated).ConfigureAwait(false);

        bool AssignOutput(VariableReference reference, WordExpansion.ResolvedVariableReference target, ShellValue value)
        {
            if (target.ReadOwner is { } owner && !ReferenceEquals(owner, commandVars))
                return target.TryPublishValue(new ScopedEvaluation(this, owner, context), reference.Name, value);
            var sourceName = reference.Name;
            if (reference.Kind == VariableReferenceKind.Name
                && commandVars.TryResolveBinding(reference.Name, selectOwner: null, out var selected))
            {
                if (selected.Reference.Kind == VariableReferenceKind.Name)
                {
                    var resolution = SelectBuiltinReference(reference, commandVars, context, out var binding);
                    if (resolution != BuiltinReferenceResolution.Write)
                        return resolution == BuiltinReferenceResolution.NoWrite;
                    return bindings != null && ReferenceEquals(binding.Owner, commandVars)
                        ? bindings.TryApplyOutputAssignment(binding.Reference, value, Memory, _cancellationToken)
                        : binding.Owner.TryApplyAssignment(binding.Reference, value, Memory);
                }
                // Keep an intermediate alias: its prefix-owned write must not reach the caller.
                sourceName = selected.Name;
            }
            // A prefix hiding a caller nameref stays temporary, just like other builtin outputs.
            var outputBindings = bindings?.CanRetainOutput(target.Name) == true ? bindings : null;
            return !commandVars.IsReadonly(reference.Name)
                && target.TryPublishValue(new ScopedEvaluation(this, commandVars, context) { SelectorBindings = outputBindings },
                    sourceName, value);
        }
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
            && !context.TryResolveCircularBinding(reference.Name, variables, out binding)
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

    internal enum BuiltinAssignmentKind { Value, Read, Getopts, PrintfCount, Descriptor, Directory }
    internal enum BuiltinAssignmentResult { Assigned, NoWrite, Failed, Readonly }

    /// <summary>Shares selected output writes without replaying selectors; null publishes an unassigned value.</summary>
    internal async Task<BuiltinAssignmentResult> AssignBuiltinAsync(
        VariableReference reference, ShellValue? value, ScopedEvaluation scope,
        Variables.CommandBindings? persistentOutputs, BuiltinAssignmentKind kind, ShellValue command,
        ScopedEvaluation.ExpansionBindings? expansionWriteTarget)
    {
        var sourceName = reference.Name;
        var circular = reference.Kind == VariableReferenceKind.Name
            && !scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out _)
            && scope.Context.TryResolveCircularBinding(reference.Name, scope.Variables, out _);
        var activeCycle = circular;
        var resolution = SelectBuiltinReference(reference, scope.Variables, scope.Context, out var binding);
        if (resolution == BuiltinReferenceResolution.NoWrite)
            return BuiltinAssignmentResult.NoWrite;
        if (resolution == BuiltinReferenceResolution.Invalid)
            return reference.Kind == VariableReferenceKind.Name
                ? BuiltinAssignmentResult.Failed : await InvalidElementTargetAsync().ConfigureAwait(false);
        var destination = binding.Owner;
        reference = binding.Reference;
        if (kind == BuiltinAssignmentKind.Read)
        {
            // Only a whole-name local nameref output selects the binding saved by this read's prefix.
            // Later operands and their selectors still see the active command bindings.
            if (!circular && reference.Kind == VariableReferenceKind.Name && sourceName != reference.Name
                && ReferenceEquals(destination, scope.Variables) && persistentOutputs?.Contains(reference.Name) == true)
                destination = persistentOutputs.SavedVariables;
            persistentOutputs = null;
        }
        else if (kind != BuiltinAssignmentKind.Directory && ReferenceEquals(destination, scope.Variables))
            destination = persistentOutputs?.SelectOutputVariables(reference.Name, Memory, _cancellationToken) ?? destination;
        var caller = kind != BuiltinAssignmentKind.Directory && persistentOutputs?.Contains(reference.Name) == true
            ? persistentOutputs.SavedVariables : destination;
        if (reference.Kind == VariableReferenceKind.Name
            && !caller.TryResolveBinding(reference.Name,
                ReferenceEquals(caller, persistentOutputs?.SavedVariables) ? SelectCallerOwner : null, out _)
            && scope.Context.TryResolveCircularBinding(reference.Name, scope.Variables, out var fallback))
        {
            circular = true;
            destination = fallback.Owner;
            reference = fallback.Reference;
        }
        if (circular)
        {
            await ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
            if (kind == BuiltinAssignmentKind.Descriptor || kind == BuiltinAssignmentKind.PrintfCount && activeCycle)
                await ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
            if (HasPendingExpansionError || !BuiltinDiagnosticsOpen)
                return BuiltinAssignmentResult.Failed;
            // Bash allocates the descriptor but leaves a circular local alias untouched.
            if (kind == BuiltinAssignmentKind.Descriptor)
                return BuiltinAssignmentResult.NoWrite;
        }
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
            return kind is BuiltinAssignmentKind.Directory or BuiltinAssignmentKind.Getopts
                ? BuiltinAssignmentResult.Readonly : BuiltinAssignmentResult.Failed;
        }
        var failure = value is { } assignedValue
            ? destination.GetNamerefAssignmentFailure(reference.Name, target.Name, assignedValue, Memory)
            : NamerefAssignmentFailure.None;
        if (failure != NamerefAssignmentFailure.None)
        {
            if (failure == NamerefAssignmentFailure.InvalidTarget && kind != BuiltinAssignmentKind.Getopts)
                await WriteInvalidIdentifierDiagnosticAsync(command, value.GetValueOrDefault(), _builtinDiagnostics,
                    ClosedStderrHandling.AttemptWrite).ConfigureAwait(false);
            return kind == BuiltinAssignmentKind.Directory ? BuiltinAssignmentResult.NoWrite : BuiltinAssignmentResult.Failed;
        }
        var writeScope = scope with
        {
            ExpansionWriteTarget = expansionWriteTarget,
            SelectorBindings = null
        };
        if (value == null && kind == BuiltinAssignmentKind.Getopts && destination.IsIntegerBinding(target.Name))
            value = ShellValue.Empty;
        var assigned = value is { } written
            ? await target.AssignValueAsync(writeScope, destination, reference.Name, written, AssignmentOperator.Replace,
                command, WordExpansion.DeclarationWrite.Ordinary).ConfigureAwait(false) is not null
            : target.TryAssignUnassigned(destination, Memory);
        if (assigned && kind == BuiltinAssignmentKind.PrintfCount && !ReferenceEquals(destination, binding.Owner))
        {
            // Count writes are visible to later selectors, without replaying an element selector
            // or imposing the caller's array kind on a temporary scalar prefix.
            assigned = reference.Kind == VariableReferenceKind.Name
                ? binding.Owner.TryApplyAssignment(reference, value.GetValueOrDefault(), Memory)
                : target.TryPublishValue(scope with { Variables = binding.Owner, ExpansionWriteTarget = null, SelectorBindings = null },
                    reference.Name, value.GetValueOrDefault());
        }
        if (assigned)
        {
            if (kind != BuiltinAssignmentKind.Directory)
                persistentOutputs?.CompleteOutput(reference.Name, destination);
            else if (persistentOutputs?.Contains(target.Name) == true && ReferenceEquals(destination, scope.Variables)
                && reference.Kind == VariableReferenceKind.Name && reference.Name == target.Name)
            {
                // Local namerefs were selected before resolving the write. Global nameref writes stay temporary.
                if (persistentOutputs.CanRetainOutput(sourceName) || scope.Context.FunctionContext?.IsLocal(sourceName) == true)
                    persistentOutputs.RetainBinding(target.Name, Memory, _cancellationToken);
            }
        }
        return assigned ? BuiltinAssignmentResult.Assigned : BuiltinAssignmentResult.Failed;

        Variables SelectCallerOwner(BindingName name) => persistentOutputs?.Contains(name) == true
            ? persistentOutputs.SavedVariables : scope.Variables;

        async Task<BuiltinAssignmentResult> InvalidElementTargetAsync()
        {
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("`': not a valid identifier")).ConfigureAwait(false);
            return BuiltinAssignmentResult.Failed;
        }
    }

    /// <summary>Explicit attributes can promote a function's prefix; ordinary builtin writes cannot.</summary>
    private void PromoteAttributeBinding(
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
            bindings.RetainBinding(storedName, Memory, _cancellationToken);
            return;
        }

        if (context.FunctionContext is { } function)
        {
            function.Locals.RecordAttributePromotion(resolved);
            function.PromotedNames.Add(resolved);
            bindings?.RetainBinding(resolved, Memory, _cancellationToken);
        }
    }
}

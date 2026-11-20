using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using static Lokad.Bash.Evaluation.ShellPredicates;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    internal enum DeclarationWrite { Ordinary, ActiveGlobalIndexed }

    /// <summary>A selected location; its typed index is not expanded again when read.</summary>
    internal abstract record ResolvedVariableReference(BindingName Name)
    {
        // Circular local reads can select the hidden global store. Assignment selection
        // remains separate; this owner only supplies the already selected read value.
        internal Variables? ReadOwner { get; init; }
        internal bool IsWholeBindingRead { get; init; }
        internal sealed record Scalar(BindingName VariableName) : ResolvedVariableReference(VariableName);
        internal sealed record Indexed(BindingName VariableName, IndexedArrayIndex Index) : ResolvedVariableReference(VariableName);
        internal sealed record Associative(BindingName VariableName, AssociativeArrayKey Key) : ResolvedVariableReference(VariableName);

        public bool TryRead(Variables variables, out ShellValue value)
        {
            variables = ReadOwner ?? variables;
            value = ShellValue.Empty;
            switch (this)
            {
                case Scalar:
                    return variables.TryGetScalar(Name, out value);
                case Indexed indexed:
                    if (variables.IndexedArrays.TryGetValue(Name, out var array)
                        && array.TryGet(indexed.Index, out var element))
                    {
                        value = element;
                        return true;
                    }
                    return indexed.Index.Value == 0 && variables.TryGetScalar(Name, out value);
                case Associative associative:
                    if (variables.AssociativeArrays.TryGetValue(Name, out var map)
                        && map.TryGet(associative.Key, out var entry))
                    {
                        value = entry;
                        return true;
                    }
                    return false;
                default:
                    throw new InvalidOperationException("Unknown variable reference.");
            }
        }

        /// <summary>Assigns absence without deleting the selected binding, key, or its attributes.</summary>
        internal bool TryAssignUnassigned(Variables variables, EvaluationMemory memory)
        {
            if (Name.IsEmpty || variables.IsReadonly(Name))
                return false;
            return this switch
            {
                Scalar => variables.TrySetResolvedUnassigned(Name),
                Indexed indexed => variables.TrySetResolvedArrayElement(Name, indexed.Index, null, memory),
                Associative associative => variables.TrySetResolvedArrayElement(Name, associative.Key, null, memory),
                _ => throw new InvalidOperationException("Unknown variable reference."),
            };
        }

        internal ValueTask<ShellValue?> AssignValueAsync(
            ScopedEvaluation scope, BindingName sourceName, ShellValue value, AssignmentOperator operation) =>
            AssignValueAsync(scope, scope.Variables, sourceName, value, operation, ShellValue.Empty, DeclarationWrite.Ordinary);

        internal async ValueTask<ShellValue?> AssignValueAsync(
            ScopedEvaluation scope, Variables destination, BindingName sourceName, ShellValue value,
            AssignmentOperator operation, ShellValue command, DeclarationWrite declarationWrite)
        {
            if (Name.IsEmpty)
                return null;
            ShellValue? previous = operation == AssignmentOperator.Append && TryRead(destination, out var stored)
                ? stored : null;
            var attributes = destination.GetAssignmentAttributes(Name);
            if ((attributes & Variables.BindingAttributes.Integer) != 0)
            {
                if (declarationWrite != DeclarationWrite.ActiveGlobalIndexed && destination.IsReadonly(Name))
                    return null;
                var evaluated = await EvaluateIntegerAssignmentAsync(value, previous, scope, command).ConfigureAwait(false);
                if (!evaluated.HasValue || !scope.CanExpand)
                    return null;
                value = evaluated.Value;
            }
            else if (previous is { } prefix)
                value = AppendAssignmentValue(prefix, value, scope);
            if (!TryPublishValue(scope with { Variables = destination }, sourceName, value, declarationWrite))
                return null;
            return (attributes & Variables.BindingAttributes.Case) != 0 && TryRead(destination, out var assigned)
                ? assigned : value;
        }

        internal bool TryPublishValue(ScopedEvaluation scope, BindingName sourceName, ShellValue value) =>
            TryPublishValue(scope, sourceName, value, DeclarationWrite.Ordinary);

        private bool TryPublishValue(
            ScopedEvaluation scope, BindingName sourceName, ShellValue value, DeclarationWrite declarationWrite)
        {
            if (!TryAssignTo(scope.Variables, Name))
                return false;

            if (scope.GetSelectorWriteTarget(sourceName, Name) is { } retained)
            {
                if (this is Scalar)
                    retained.SetResolvedValuePreservingArray(Name, value, scope.Effects.Memory);
                else if (!TryForwardTo(retained, Name))
                    return false;
            }

            if (scope.GetExpansionWriteTarget(sourceName) is not { } target)
                return true;

            if (declarationWrite == DeclarationWrite.ActiveGlobalIndexed)
            {
                target.SetExpansionValueIgnoringReadonly(sourceName, value, scope.Effects.Memory);
                return true;
            }

            // Forward the selected location without replaying effects or imposing the temporary binding's kind.
            var forwardedName = scope.Variables.TryResolveBinding(sourceName, selectOwner: null, out var binding)
                && binding.Reference.Kind != VariableReferenceKind.Name ? Name : sourceName;
            return TryForwardTo(target, forwardedName);

            // Keep the receiver's array kind, even when a temporary scalar was indexed.
            bool TryForwardTo(Variables variables, BindingName name) => this switch
            {
                Scalar => variables.TrySetScalar(name, value, scope.Effects.Memory),
                Indexed indexed => variables.TryAssignArrayElement(name,
                    ShellValue.FromInteger(indexed.Index.Value), value, ArrayAssignmentKind.Auto, scope.Effects.Memory),
                Associative associative => variables.TryAssignArrayElement(name, associative.Key.Value, value, ArrayAssignmentKind.Auto, scope.Effects.Memory),
                _ => throw new InvalidOperationException("Unknown variable reference."),
            };

            // Selection already resolved namerefs; local bindings can retain a literal name such as "a[1]".
            bool TryAssignTo(Variables variables, BindingName name) => !name.IsEmpty
                && (declarationWrite == DeclarationWrite.ActiveGlobalIndexed || !variables.IsReadonly(name)) && this switch
            {
                Scalar => variables.TrySetResolvedScalar(name, value, scope.Effects.Memory),
                Indexed indexed => variables.TrySetResolvedArrayElement(name, indexed.Index, value, scope.Effects.Memory),
                Associative associative => variables.TrySetResolvedArrayElement(name, associative.Key, value, scope.Effects.Memory),
                _ => throw new InvalidOperationException("Unknown variable reference."),
            };
        }
    }

    internal enum UnsetReferenceKind { Direct, Nameref }

    /// <summary>A nonempty ScalarToUnset defers whole-binding removal to the caller's scope cleanup.</summary>
    internal readonly record struct ElementUnsetResult(bool Succeeded, BindingName ScalarToUnset);

    /// <summary>Removes array elements; scalar removal is returned to the builtin that owns local/prefix cleanup.</summary>
    internal static async Task<ElementUnsetResult> UnsetElementAsync(
        VariableReference reference, UnsetReferenceKind kind, ScopedEvaluation scope)
    {
        var variables = scope.Variables;
        var name = reference.Name;
        // Deliberately stronger than Bash: an element nameref cannot bypass readonly
        // protection, and a rejected unset does not evaluate its selector.
        if (variables.IsReadonly(name))
        {
            await scope.Effects.ReportExpansionDiagnosticAsync(
                [ShellValue.FromText("unset: "), name.ToValue()],
                [ShellValue.FromText("cannot unset: readonly variable")]).ConfigureAwait(false);
            return new(false, BindingName.Empty);
        }
        var resolvedName = variables.ResolveNamerefName(name);
        if (!variables.HasBinding(resolvedName))
            return new(true, BindingName.Empty);

        if (reference.IsAggregate && variables.IndexedArrays.TryGetValue(resolvedName, out var aggregate))
            return new(aggregate.Count == 0 || variables.TryAssignResolvedArray(resolvedName, [], ArrayKind.Indexed), BindingName.Empty);
        if (kind == UnsetReferenceKind.Direct && reference.IsAggregate && !variables.AssociativeArrays.ContainsKey(resolvedName))
            return await NotAnArrayAsync().ConfigureAwait(false);

        var target = await ResolveReferenceAsync(reference, ReferenceUse.Unset, scope, ExpansionPurpose.ArithmeticSource).ConfigureAwait(false);
        if (!scope.CanExpand || target == null)
            return new(false, BindingName.Empty);
        using var bindingMemory = variables.TrackBindingMemory(target.Name);
        switch (target)
        {
            case ResolvedVariableReference.Indexed indexed:
                if (variables.IndexedArrays.TryGetValue(indexed.Name, out var array))
                    array.Remove(indexed.Index);
                else if (indexed.Index.Value == 0)
                    return new(true, indexed.Name);
                else if (kind == UnsetReferenceKind.Direct)
                    return await NotAnArrayAsync().ConfigureAwait(false);
                return new(true, BindingName.Empty);
            case ResolvedVariableReference.Associative associative:
                if (variables.AssociativeArrays.TryGetValue(associative.Name, out var map))
                    map.Remove(associative.Key);
                return new(true, BindingName.Empty);
            default:
                throw new InvalidOperationException("An element selector must resolve to an array location.");
        }

        async Task<ElementUnsetResult> NotAnArrayAsync()
        {
            await scope.Effects.ReportExpansionDiagnosticAsync(
                [ShellValue.FromText("unset: "), name.ToValue()],
                [ShellValue.FromText("not an array variable")]).ConfigureAwait(false);
            return new(false, BindingName.Empty);
        }
    }

    /// <summary>Applies a direct assignment after its RHS has expanded in the active scope.</summary>
    internal static async Task<bool> TryAssignAsync(
        ShellSource source, ShellValue value, AssignmentOperator operation, ScopedEvaluation scope)
    {
        if (!VariableReference.TryParse(source, scope.Effects.Memory, out var reference))
            return false;

        if (reference.Kind == VariableReferenceKind.Name)
        {
            var action = await PrepareNamerefAssignmentAsync(reference.Name, value, operation, scope, ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
            if (action.Action != NamerefAssignmentAction.Assign)
                return action.Action == NamerefAssignmentAction.Handled;
        }
        var target = await ResolveReferenceAsync(reference, ReferenceUse.DirectAssign, scope, ExpansionPurpose.ArithmeticSource)
            .ConfigureAwait(false);
        if (!scope.CanExpand || target == null)
            return false;
        if (await target.AssignValueAsync(scope, reference.Name, value, operation).ConfigureAwait(false) is not null)
            return true;
        if (scope.Variables.IsReadonly(target.Name))
            await scope.Effects.ReportFatalExpansionAsync(
                (reference.Kind == VariableReferenceKind.Name ? target.Name : reference.Name).ToValue(),
                ShellValue.FromText("readonly variable"), ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
        return false;
    }

    private enum NamerefAssignmentAction { Assign, Handled, Fail }
    private readonly record struct NamerefAssignmentResult(NamerefAssignmentAction Action, ShellValue Value);

    private static async Task<NamerefAssignmentResult> PrepareNamerefAssignmentAsync(
        BindingName name, ShellValue value, AssignmentOperator operation, ScopedEvaluation scope, ExpansionFailureKind failure)
    {
        if (!scope.Variables.TryResolveBinding(name, selectOwner: null, out _)
            && scope.Context.TryResolveCircularBinding(name, scope.Variables, out var circularBinding))
        {
            await scope.Effects.ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
            return await AssignSelectedAsync(circularBinding).ConfigureAwait(false);
        }
        switch (scope.Variables.GetNamerefAssignmentFailure(name, name, value, scope.Effects.Memory))
        {
            case NamerefAssignmentFailure.None:
                return new(NamerefAssignmentAction.Assign, value);
            case NamerefAssignmentFailure.InvalidTarget:
                return await RejectAssignmentAsync(NamerefAssignmentFailure.InvalidTarget).ConfigureAwait(false);
            case NamerefAssignmentFailure.IndirectUnsetTarget:
                if (scope.Context.TryResolveLocalNamerefAssignment(name, scope.Variables, scope.Effects.Memory, out var fallback))
                {
                    VariableBinding? selected = null;
                    if (fallback is Evaluation.LocalNamerefAssignment.Circular circular)
                    {
                        if (circular.LookupWarning is { } warning)
                            await scope.Effects.ReportCircularNameReferenceAsync(warning).ConfigureAwait(false);
                        await scope.Effects.ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
                        if (!scope.CanExpand)
                            return new(NamerefAssignmentAction.Fail, value);
                        // Retry the original binding globally, then follow its referent in the active scope.
                        if (!scope.Context.GetGlobalBindingOwner(name, scope.Variables)
                                .TryResolveBinding(name, _ => scope.Variables, out var global))
                        {
                            scope.Effects.SetFatalExpansionError(failure);
                            return new(NamerefAssignmentAction.Fail, value);
                        }
                        // Retrying does not make an indirectly reached unset nameref a direct initializer.
                        var retryFailure = global.Owner.GetNamerefAssignmentFailure(name, global.Name, value, scope.Effects.Memory);
                        if (retryFailure != NamerefAssignmentFailure.None)
                            return await RejectAssignmentAsync(retryFailure).ConfigureAwait(false);
                        selected = global;
                    }
                    else if (fallback is Evaluation.LocalNamerefAssignment.Write write)
                        selected = write.Binding;
                    if (selected is { } binding)
                    {
                        // Bash rejects element references on a circular retry before expanding their selectors.
                        var reference = binding.Reference;
                        if (fallback is Evaluation.LocalNamerefAssignment.Circular && reference.Kind != VariableReferenceKind.Name)
                        {
                            scope.Effects.SetFatalExpansionError(failure);
                            return new(NamerefAssignmentAction.Fail, value);
                        }
                        return await AssignSelectedAsync(binding).ConfigureAwait(false);
                    }
                    return new(NamerefAssignmentAction.Handled, value);
                }
                return await RejectAssignmentAsync(NamerefAssignmentFailure.IndirectUnsetTarget).ConfigureAwait(false);
        }
        return new(NamerefAssignmentAction.Fail, value);

        async Task<NamerefAssignmentResult> AssignSelectedAsync(VariableBinding binding)
        {
            var reference = binding.Reference;
            var target = await ResolveDeclarationReferenceAsync(reference, scope, binding.Owner, binding.Owner).ConfigureAwait(false);
            if (!scope.CanExpand)
                return new(NamerefAssignmentAction.Fail, value);
            if (target == null)
            {
                // Declaration selection already diagnosed the subscript; this is a direct/default write.
                scope.Effects.SetFatalExpansionError(failure);
                return new(NamerefAssignmentAction.Fail, value);
            }
            if (binding.Owner.IsReadonly(target.Name))
            {
                await scope.Effects.ReportFatalExpansionAsync(target.Name.ToValue(), ShellValue.FromText("readonly variable"), failure).ConfigureAwait(false);
                return new(NamerefAssignmentAction.Fail, value);
            }
            var written = await target.AssignValueAsync(scope with { ExpansionWriteTarget = null, SelectorBindings = null },
                binding.Owner, reference.Name, value, operation, ShellValue.Empty, DeclarationWrite.Ordinary)
                .ConfigureAwait(false);
            if (!written.HasValue)
                return new(NamerefAssignmentAction.Fail, value);
            value = written.Value;
            // A hidden array-element default returns the array's scalar value, not its assigned slot.
            if (failure == ExpansionFailureKind.NamerefDefault
                && target is ResolvedVariableReference.Indexed or ResolvedVariableReference.Associative)
                binding.Owner.TryGetExpansionValue(target.Name, out value);
            return new(NamerefAssignmentAction.Handled, value);
        }

        async Task<NamerefAssignmentResult> RejectAssignmentAsync(NamerefAssignmentFailure reason)
        {
            if (reason == NamerefAssignmentFailure.InvalidTarget)
                await scope.Effects.ReportFatalExpansionAsync(
                    [ShellValue.FromText("`"), value, ShellValue.FromText("'")],
                    [ShellValue.FromText("not a valid identifier")], failure).ConfigureAwait(false);
            else
                scope.Effects.SetFatalExpansionError(failure);
            return new(NamerefAssignmentAction.Fail, value);
        }
    }

    internal static ShellValue AppendAssignmentValue(ShellValue previous, ShellValue value, ScopedEvaluation scope)
    {
        var length = (long)previous.Bytes.Length + value.Bytes.Length;
        if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();
        scope.Effects.Memory.Charge(checked(4L * length));
        var bytes = new byte[(int)length];
        previous.Bytes.Span.CopyTo(bytes);
        value.Bytes.Span.CopyTo(bytes.AsSpan(previous.Bytes.Length));
        return ShellValue.FromBytes(bytes);
    }

    internal static Task<ResolvedVariableReference?> ResolveCompoundElementAsync(
        VariableReference reference, ShellValue expandedSubscript, ScopedEvaluation scope, Variables targetVariables) =>
        ResolveReferenceAsync(reference, ReferenceUse.CompoundAssign, scope, ExpansionPurpose.ArithmeticSource, expandedSubscript,
            targetVariables, targetVariables);

    /// <summary>Reads a selected binding through ordinary nameref/array expansion rules.</summary>
    internal static Task<ResolvedVariableReference?> ResolveReadReferenceAsync(VariableReference reference, ScopedEvaluation scope) =>
        ResolveReferenceAsync(reference, ReferenceUse.Read, scope, ExpansionPurpose.ArithmeticSource);

    internal static Task<bool> TestVariableAsync(UnaryPredicateKind predicate, ShellValue operand, ScopedEvaluation scope)
    {
        if (!scope.CanExpand)
            return Task.FromResult(false);
        if (predicate == UnaryPredicateKind.VariableIsSet
            && Integers.TryParseNumber(Integers.TrimWhitespace(operand.Bytes.Span), out var position))
            return Task.FromResult(position >= 0 && position <= scope.Variables.PositionalParameters.Count);
        if (!VariableReference.TryParse(ShellSource.FromBytes(operand.Bytes), scope.Effects.Memory, out var reference))
            return Task.FromResult(false);
        if (predicate == UnaryPredicateKind.NamerefIsSet)
            return Task.FromResult(reference.Kind == VariableReferenceKind.Name
                && scope.Variables.TryGetNameref(reference.Name, out var target) && !target.IsEmpty);
        return IsVariableSetAsync(reference, scope, null);
    }

    /// <summary>Expands a lexical conditional selector once, then reads the selected location.</summary>
    internal static async Task<bool> TestVariableElementAsync(Word name, Word selector, ScopedEvaluation scope)
    {
        var operandName = await ExpandUnsplitValueAsync(name, scope, ExpansionPurpose.Scalar, TildeExpansion.Leading)
            .ConfigureAwait(false);
        var subscript = await ExpandUnsplitValueAsync(selector, scope, ExpansionPurpose.Scalar, TildeExpansion.None)
            .ConfigureAwait(false);
        if (!scope.CanExpand || !VariableNames.IsValidVariableName(operandName.Bytes.Span))
            return false;
        scope.Effects.Memory.Charge(checked(4L * operandName.Bytes.Length));
        var reference = VariableReference.ForElement(operandName.ToUtf8String(), ShellSource.FromBytes(subscript.Bytes));
        return await IsVariableSetAsync(reference, scope, new ExpandedArraySubscript(subscript, QuoteForArithmetic: false))
            .ConfigureAwait(false);
    }

    /// <summary>Checks assignment presence, retaining element selectors until reached.</summary>
    private static async Task<bool> IsVariableSetAsync(
        VariableReference reference, ScopedEvaluation scope, ExpandedArraySubscript? preparedSubscript)
    {
        var variables = scope.Variables;
        if (reference.Kind == VariableReferenceKind.Name)
            return variables.TryGetExpansionValue(reference.Name, out _);
        if (reference.IsAggregate)
        {
            var binding = await ResolveReadBindingAsync(reference, ReferenceUse.Read, scope, variables).ConfigureAwait(false);
            if (binding is not { Reference.Kind: VariableReferenceKind.Name } selected)
                return false;
            if (!selected.Owner.AssociativeArrays.ContainsKey(selected.Name))
                return selected.Owner.IndexedArrays.TryGetValue(selected.Name, out var indexed)
                    ? indexed.Count != 0
                    : selected.Owner.Scalars.ContainsKey(selected.Name);
        }
        var element = await ResolveReferenceAsync(reference, ReferenceUse.Presence, scope, ExpansionPurpose.ArithmeticSource,
                null, variables, variables, preparedSubscript, ShellValue.Empty)
            .ConfigureAwait(false);
        return element != null && element.TryRead(variables, out _);
    }

    /// <summary>Selects a builtin destination once, while expanding its selector in the active command scope.</summary>
    internal static Task<ResolvedVariableReference?> ResolveBuiltinReferenceAsync(
        VariableReference reference, ScopedEvaluation scope, Variables destination, Variables indexVariables) =>
        ResolveReferenceAsync(reference, ReferenceUse.BuiltinAssign, scope,
            ExpansionPurpose.ArithmeticSource, null, destination, indexVariables);

    /// <summary>Separates array storage selection from the binding supplying negative-index bounds.</summary>
    internal static Task<ResolvedVariableReference?> ResolveDeclarationReferenceAsync(
        VariableReference reference, ScopedEvaluation scope, Variables targetVariables, Variables indexVariables) =>
        ResolveReferenceAsync(reference, ReferenceUse.DeclarationAssign, scope,
            ExpansionPurpose.ArithmeticSource, null, targetVariables, indexVariables);

    private static Task<ResolvedVariableReference?> ResolveReferenceAsync(
        VariableReference reference,
        ReferenceUse use,
        ScopedEvaluation scope,
        ExpansionPurpose arithmeticPurpose) =>
        ResolveReferenceAsync(reference, use, scope, arithmeticPurpose, null, scope.Variables, scope.Variables);

    /// <summary>Resolves a typed location, retaining staged keys and applying the arithmetic selector pass.</summary>
    private static Task<ResolvedVariableReference?> ResolveReferenceAsync(
        VariableReference reference,
        ReferenceUse use,
        ScopedEvaluation scope,
        ExpansionPurpose arithmeticPurpose,
        ShellValue? stagedSubscript,
        Variables targetVariables,
        Variables indexVariables) =>
        ResolveReferenceAsync(reference, use, scope, arithmeticPurpose, stagedSubscript, targetVariables, indexVariables,
            null, ShellValue.Empty);

    private static async Task<ResolvedVariableReference?> ResolveReferenceAsync(
        VariableReference reference,
        ReferenceUse use,
        ScopedEvaluation scope,
        ExpansionPurpose arithmeticPurpose,
        ShellValue? stagedSubscript,
        Variables targetVariables,
        Variables indexVariables,
        ExpandedArraySubscript? preparedSubscript,
        ShellValue arithmeticCommand)
    {
        if (!scope.CanExpand)
            return null;
        var convertedNameref = false;
        if (use is ReferenceUse.Read or ReferenceUse.Length or ReferenceUse.Presence && reference.Kind != VariableReferenceKind.Name
            && !targetVariables.TryResolveBinding(reference.Name, selectOwner: null, out _))
        {
            var selected = await ResolveReadBindingAsync(reference, use, scope, targetVariables).ConfigureAwait(false);
            if (selected is { } fallback)
            {
                targetVariables = fallback.Owner;
                indexVariables = fallback.Owner;
                reference = VariableReference.ForElement(fallback.Name, reference.Subscript);
            }
        }
        if (reference.Kind == VariableReferenceKind.Name)
        {
            var sourceName = reference.Name;
            VariableBinding binding;
            if (use is ReferenceUse.Read or ReferenceUse.Length)
            {
                var selected = await ResolveReadBindingAsync(reference, use, scope, targetVariables).ConfigureAwait(false);
                if (selected is not { } readBinding)
                    return new ResolvedVariableReference.Scalar(BindingName.Empty);
                binding = readBinding;
                targetVariables = binding.Owner;
            }
            else if (!targetVariables.TryResolveBinding(reference.Name, selectOwner: null, out binding))
            {
                if (use is ReferenceUse.ArithmeticRead or ReferenceUse.ArithmeticAssign)
                {
                    await scope.Effects.ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
                    if (use == ReferenceUse.ArithmeticAssign && scope.CanExpand)
                        await scope.Effects.ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
                    if (!scope.Context.TryResolveCircularBinding(reference.Name, targetVariables, out binding))
                        return null;
                    targetVariables = binding.Owner;
                }
                else
                    return new ResolvedVariableReference.Scalar(BindingName.Empty);
            }
            if (binding.Reference.Kind == VariableReferenceKind.Name)
                return ResolveNameReference(binding.Name, targetVariables);
            // Bash's length expansion does not select an element reached through a nameref.
            if (use == ReferenceUse.Length)
                return null;
            reference = binding.Reference;
            if (use is ReferenceUse.DirectAssign or ReferenceUse.Assign or ReferenceUse.DeclarationAssign or ReferenceUse.BuiltinAssign or ReferenceUse.ArithmeticAssign
                && !(use == ReferenceUse.ArithmeticAssign && scope.Context.FunctionContext?.IsLocal(sourceName) == true)
                && targetVariables.TryGetNameref(reference.Name, out _))
            {
                // Writing through r -> b[index] converts b, except for local arithmetic references.
                await scope.Effects.ReportRemovingNamerefAsync(reference.Name).ConfigureAwait(false);
                if (!scope.CanExpand)
                    return null;
                // Prefix-RHS writes also convert the caller's binding before selector effects.
                var forwardedVariables = scope.GetExpansionWriteTarget(sourceName);
                if (!targetVariables.TryDeclareResolvedArray(reference.Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue)
                    || forwardedVariables != null && !ReferenceEquals(forwardedVariables, targetVariables)
                        && !forwardedVariables.TryDeclareResolvedArray(reference.Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue))
                {
                    // Arithmetic publication owns readonly rejection and its command/expansion status.
                    if (use == ReferenceUse.ArithmeticAssign && targetVariables.IsReadonly(reference.Name))
                        return new ResolvedVariableReference.Scalar(reference.Name);
                    if (use is ReferenceUse.DeclarationAssign or ReferenceUse.BuiltinAssign)
                        await scope.Effects.ReportExpansionDiagnosticAsync(reference.Name.ToValue(), ShellValue.FromText("readonly variable")).ConfigureAwait(false);
                    else
                        await scope.Effects.ReportFatalExpansionAsync(reference.Name.ToValue(), ShellValue.FromText("readonly variable"),
                            use == ReferenceUse.Assign ? ExpansionFailureKind.Assignment : ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
                    return null;
                }
                convertedNameref = true;
            }
        }

        // Compound preparation has already selected the binding.
        var name = use == ReferenceUse.CompoundAssign ? reference.Name : targetVariables.ResolveNamerefName(reference.Name);
        var associative = targetVariables.AssociativeArrays.ContainsKey(name);
        // Prepared selectors validate expanded bytes; compound diagnostics retain source spelling.
        var selector = preparedSubscript is { } preparedSelector
            ? VariableReference.ForElement(reference.Name, ShellSource.FromBytes(preparedSelector.Value.Bytes))
            : stagedSubscript is { } staged
            ? VariableReference.ForElement(reference.Name, ShellSource.FromBytes(staged.Bytes))
            : reference;
        if (selector.Subscript.IsEmpty || selector.IsAggregate && !associative)
        {
            if (use == ReferenceUse.Presence && selector.Subscript.IsEmpty)
                return null;
            if (use == ReferenceUse.ArithmeticAssign && selector.Subscript.IsEmpty)
            {
                await ReportEmptyArithmeticAssignmentAsync().ConfigureAwait(false);
                return null;
            }
            await ReportBadSubscriptAsync().ConfigureAwait(false);
            // Bash diagnoses an empty indexed read twice, before any attempted write.
            if (use == ReferenceUse.ArithmeticRead && selector.Subscript.IsEmpty && scope.CanExpand)
                await ReportBadSubscriptAsync().ConfigureAwait(false);
            return null;
        }

        // A declaration alone does not enable length-selector effects; even an empty assignment does.
        if (use == ReferenceUse.Length && !targetVariables.Scalars.ContainsKey(name)
            && (!targetVariables.IndexedArrays.TryGetValue(name, out var indexed) || !indexed.IsInitialized)
            && (!targetVariables.AssociativeArrays.TryGetValue(name, out var map) || !map.IsInitialized))
            return null;

        var purpose = associative ? ExpansionPurpose.Scalar : arithmeticPurpose;
        // Indexed compound items have a word-expansion pass before replacement, then an
        // arithmetic-source pass against the array being built. Associative keys are already final.
        var expanded = preparedSubscript is { } prepared ? prepared.Value
            : associative && stagedSubscript is { } keyValue
            ? keyValue
            : await ExpandSubscriptAsync(selector.Subscript, scope, purpose,
                allowLeadingTilde: associative && use == ReferenceUse.DirectAssign).ConfigureAwait(false);
        if (!scope.CanExpand)
            return null;
        if (associative)
        {
            if (AssociativeArrayKey.TryCreate(expanded, out var key))
                return new ResolvedVariableReference.Associative(name, key) { ReadOwner = targetVariables };
            if (use == ReferenceUse.Presence)
                return null;
        }
        else
        {
            // Index arithmetic starts after expansion of the selector's source bytes.
            var operand = preparedSubscript is { QuoteForArithmetic: true }
                ? ArithmeticOperand.FromExpandedIndex(QuoteExpandedArithmeticIndex(expanded, scope))
                : ArithmeticOperand.FromValue(expanded);
            var evaluated = await EvaluateArithmeticAsync(operand, scope, ArithmeticFailureMode.ArrayIndex).ConfigureAwait(false);
            if (evaluated is not { } index)
                return null;

            if (index < 0)
            {
                var maximum = indexVariables.IndexedArrays.TryGetValue(name, out var array)
                    ? array.MaxIndex
                    : use is ReferenceUse.Assign or ReferenceUse.DirectAssign or ReferenceUse.DeclarationAssign or ReferenceUse.BuiltinAssign or ReferenceUse.ArithmeticAssign
                        && indexVariables.Scalars.ContainsKey(name) ? 0L : (long?)null;
                // Conversion discards the alias value but retains its scalar index-zero bound.
                if (convertedNameref && maximum == null)
                    maximum = 0;
                if (maximum is { } last)
                    index = last + index + 1;
            }
            if (IndexedArrayIndex.TryCreate(index, out var position))
                return new ResolvedVariableReference.Indexed(name, position) { ReadOwner = targetVariables };
        }

        if (use != ReferenceUse.Length || !associative)
            await ReportBadSubscriptAsync().ConfigureAwait(false);
        return null;

        Task ReportEmptyArithmeticAssignmentAsync()
        {
            var referenceName = reference.Name.ToValue();
            ReadOnlySpan<ShellValue> subject = arithmeticCommand.IsEmpty
                ? [ShellValue.FromText("`"), referenceName, ShellValue.FromText("[]'")]
                : [arithmeticCommand, ShellValue.FromText(": `"), referenceName, ShellValue.FromText("[]'")];
            return scope.Effects.ReportExpansionDiagnosticAsync(subject, [ShellValue.FromText("not a valid identifier")]);
        }

        Task ReportBadSubscriptAsync()
        {
            var message = ShellValue.FromText("bad array subscript");
            var referenceName = reference.Name.ToValue();
            if (use is ReferenceUse.Read or ReferenceUse.Presence
                || use == ReferenceUse.ArithmeticRead && !selector.Subscript.IsEmpty && !selector.IsAggregate)
                return scope.Effects.ReportExpansionDiagnosticAsync(referenceName, message);

            var subscript = preparedSubscript is { } prepared
                ? prepared.Value
                : ShellValue.FromBytes(reference.Subscript.Bytes);
            var closingBracket = ShellValue.FromText("]");
            if (use == ReferenceUse.Length)
                return scope.Effects.ReportFatalExpansionAsync([subscript, closingBracket], [message], ExpansionFailureKind.Arithmetic);

            var prefix = use == ReferenceUse.Unset ? ShellValue.FromText("unset: ") : ShellValue.Empty;
            ReadOnlySpan<ShellValue> subject = [prefix, referenceName, ShellValue.FromText("["), subscript, closingBracket];
            return use switch
            {
                ReferenceUse.Unset or ReferenceUse.CompoundAssign or ReferenceUse.DeclarationAssign
                    or ReferenceUse.BuiltinAssign or ReferenceUse.ArithmeticAssign or ReferenceUse.ArithmeticRead =>
                    scope.Effects.ReportExpansionDiagnosticAsync(subject, [message]),
                ReferenceUse.Assign => scope.Effects.ReportParameterDefaultFailureAsync(subject, [message], scope),
                ReferenceUse.DirectAssign => scope.Effects.ReportFatalExpansionAsync(subject, [message], ExpansionFailureKind.AssignmentTarget),
                _ => throw new InvalidOperationException("Unknown reference use."),
            };
        }

        static ResolvedVariableReference ResolveNameReference(BindingName name, Variables variables)
        {
            if (variables.IndexedArrays.ContainsKey(name))
                return new ResolvedVariableReference.Indexed(name, default) { ReadOwner = variables, IsWholeBindingRead = true };
            if (variables.AssociativeArrays.ContainsKey(name)
                && AssociativeArrayKey.TryCreate(ShellValue.FromText("0"), out var key))
                return new ResolvedVariableReference.Associative(name, key) { ReadOwner = variables, IsWholeBindingRead = true };
            return new ResolvedVariableReference.Scalar(name) { ReadOwner = variables, IsWholeBindingRead = true };
        }
    }

    /// <summary>Warns at a reached read, then selects Bash's global fallback for a local cycle.</summary>
    private static async ValueTask<VariableBinding?> ResolveReadBindingAsync(
        VariableReference reference, ReferenceUse use, ScopedEvaluation scope, Variables variables)
    {
        if (variables.TryResolveBinding(reference.Name, selectOwner: null, out var binding))
            return binding;
        await scope.Effects.ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
        var found = scope.Context.TryResolveCircularBinding(reference.Name, variables, out binding);
        if (reference.Kind != VariableReferenceKind.Name && use != ReferenceUse.Length
            || reference.Kind == VariableReferenceKind.Name && use == ReferenceUse.Length && !found)
            await scope.Effects.ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
        return found ? binding : null;
    }

    private enum ReferenceUse
    {
        Read,
        Length,
        Presence,
        Assign,
        DirectAssign,
        DeclarationAssign,
        CompoundAssign,
        BuiltinAssign,
        Unset,
        ArithmeticRead,
        ArithmeticAssign,
    }

    private enum SpecialParameterKind { None, Status, Count, At, Star, Positional, BackgroundProcess, Flags }

    private static SpecialParameterKind GetSpecialParameterKind(ReadOnlySpan<byte> name)
    {
        if (!name.IsEmpty && name.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0)
            return SpecialParameterKind.Positional;
        return name switch
        {
            [(byte)'?'] => SpecialParameterKind.Status,
            [(byte)'#'] => SpecialParameterKind.Count,
            [(byte)'@'] => SpecialParameterKind.At,
            [(byte)'*'] => SpecialParameterKind.Star,
            [(byte)'!'] => SpecialParameterKind.BackgroundProcess,
            [(byte)'-'] => SpecialParameterKind.Flags,
            _ => SpecialParameterKind.None,
        };
    }

    private sealed record ParameterValue(
        VariableReferenceKind Kind,
        IReadOnlyList<ShellValue> Fields,
        bool IsSet,
        VariableReference? Source,
        ResolvedVariableReference? Target)
    {
        public ParameterSource? RangeSource { get; init; }
        public ParameterFieldJoin FieldJoin { get; init; }
        internal VariableBinding? InspectionBinding { get; init; }
        public IEnumerable<ShellValue> SelectedFields => RangeSource?.Fields ?? Fields;
        public int FieldCount => RangeSource?.Count ?? Fields.Count;
        public ShellValue Value => Fields.Count == 0 ? ShellValue.Empty : Fields[0];
    }

}

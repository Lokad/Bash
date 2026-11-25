using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using static Lokad.Bash.Evaluation.ShellPredicates;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    // The two Bash 5.2 references expose the empty indexed array's signed maximum index in this position.
    internal static readonly ShellValue IndexedArrayNamerefSpelling =
        ShellValue.FromBytes(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff });

    internal enum DeclarationWrite { Ordinary, ActiveGlobalIndexed, ReadonlyInitializer, PromotedPrefix }

    /// <summary>A selected location; its typed index is not expanded again when read.</summary>
    internal abstract record ResolvedVariableReference(BindingName Name)
    {
        // The selected store can differ from the active scope through a fallback or a prepared prefix.
        internal Variables? ReadOwner { get; init; }
        internal bool IsWholeBindingRead { get; init; }
        internal sealed record Scalar(BindingName VariableName) : ResolvedVariableReference(VariableName);
        internal sealed record Indexed(BindingName VariableName, IndexedArrayIndex Index) : ResolvedVariableReference(VariableName)
        {
            // A local directory write can select an index in a temporary scalar and publish it to the saved binding.
            internal bool ReplaceAssociative { get; init; }
            // A reached arithmetic selector can establish the write target after selecting an unset read.
            internal bool ReadAsUnset { get; init; }
        }
        internal sealed record Associative(BindingName VariableName, AssociativeArrayKey Key) : ResolvedVariableReference(VariableName);

        /// <summary>Uses the selected element to write through a prepared scalar prefix to its caller.</summary>
        internal ResolvedVariableReference SelectPrefixElementWrite(VariableReference source, ScopedEvaluation scope)
        {
            if (this is Scalar || scope.ExpansionWriteTarget is not { PreparingPrefixes: true } prefix
                || !prefix.PrefixNames.Contains(Name)
                || source.Kind != VariableReferenceKind.Name && source.Name != Name)
                return this;

            var owner = prefix.Variables;
            if (this is Indexed indexed && owner.AssociativeArrays.ContainsKey(Name)
                && AssociativeArrayKey.TryCreate(ShellValue.FromInteger(indexed.Index.Value), out var key))
                return new Associative(Name, key) { ReadOwner = owner };

            return this with { ReadOwner = owner };
        }

        public bool TryRead(Variables variables, out ShellValue value)
        {
            variables = ReadOwner ?? variables;
            value = ShellValue.Empty;
            switch (this)
            {
                case Scalar:
                    return variables.TryGetScalar(Name, out value);
                case Indexed indexed:
                    if (variables.TryReadArrayElement(Name, indexed.Index, out value))
                        return true;
                    return indexed.Index.Value == 0 && variables.TryGetScalar(Name, out value);
                case Associative associative:
                    return variables.TryReadArrayElement(Name, associative.Key, out value);
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
                Indexed indexed => variables.TrySetResolvedArrayElement(Name, indexed.Index, null, memory, indexed.ReplaceAssociative),
                Associative associative => variables.TrySetResolvedArrayElement(Name, associative.Key, null, memory),
                _ => throw new InvalidOperationException("Unknown variable reference."),
            };
        }

        internal ValueTask<ShellValue?> AssignValueAsync(
            ScopedEvaluation scope, BindingName sourceName, ShellValue value, AssignmentOperator operation) =>
            AssignValueAsync(scope, scope.Variables, sourceName, value, operation, ShellValue.Empty, DeclarationWrite.Ordinary);

        internal ValueTask<ShellValue?> AssignValueAsync(
            ScopedEvaluation scope, Variables destination, BindingName sourceName, ShellValue value,
            AssignmentOperator operation, ShellValue command, DeclarationWrite declarationWrite) =>
            AssignValueAsync(scope, destination, sourceName, value, operation, command, declarationWrite, scope);

        internal async ValueTask<ShellValue?> AssignValueAsync(
            ScopedEvaluation scope, Variables destination, BindingName sourceName, ShellValue value,
            AssignmentOperator operation, ShellValue command, DeclarationWrite declarationWrite,
            ScopedEvaluation valueScope)
        {
            if (Name.IsEmpty)
                return null;
            ShellValue? previous = operation == AssignmentOperator.Append && TryRead(destination, out var stored)
                ? stored : null;
            var attributes = destination.GetAssignmentAttributes(Name);
            if ((attributes & Variables.BindingAttributes.Integer) != 0
                && declarationWrite == DeclarationWrite.Ordinary && destination.IsReadonly(Name))
                return null;
            if (await PrepareAssignmentValueAsync(value, previous, attributes, valueScope, command)
                .ConfigureAwait(false) is not { } prepared)
                return null;
            value = prepared;
            if (!TryPublishValue(scope with { Variables = destination }, sourceName, value, declarationWrite))
                return null;
            return (attributes & Variables.BindingAttributes.Case) != 0 && TryRead(destination, out var assigned)
                ? assigned : value;
        }

        internal bool TryPublishValue(ScopedEvaluation scope, BindingName sourceName, ShellValue value) =>
            TryPublishValue(scope, sourceName, value, DeclarationWrite.Ordinary);

        internal bool TryPublishValue(
            ScopedEvaluation scope, BindingName sourceName, ShellValue value, DeclarationWrite declarationWrite)
        {
            if (IsWholeBindingRead && this is Indexed or Associative && scope.Variables.IsArrayNameref(Name))
                return !scope.Variables.IsReadonly(Name);
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
            if (ReferenceEquals(target, scope.Variables))
                return true;

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
                && (declarationWrite != DeclarationWrite.Ordinary || !variables.IsReadonly(name)) && this switch
            {
                Scalar => variables.TrySetResolvedScalar(name, value, scope.Effects.Memory,
                    initializeReadonly: declarationWrite == DeclarationWrite.ReadonlyInitializer),
                Indexed indexed => variables.TrySetResolvedArrayElement(name, indexed.Index, value, scope.Effects.Memory, indexed.ReplaceAssociative),
                Associative associative => variables.TrySetResolvedArrayElement(name, associative.Key, value, scope.Effects.Memory),
                _ => throw new InvalidOperationException("Unknown variable reference."),
            };
        }
    }

    /// <summary>Prepares an element or scalar value using its selected binding's arithmetic and append rules.</summary>
    internal static async ValueTask<ShellValue?> PrepareAssignmentValueAsync(
        ShellValue value, ShellValue? previous, Variables.BindingAttributes attributes, ScopedEvaluation scope, ShellValue command)
    {
        if ((attributes & Variables.BindingAttributes.Integer) == 0)
            return previous is { } prefix ? AppendAssignmentValue(prefix, value, scope) : value;
        var evaluated = await EvaluateIntegerAssignmentAsync(value, previous, scope, command).ConfigureAwait(false);
        return scope.CanExpand ? evaluated : null;
    }

    internal enum UnsetReferenceKind { Direct, Nameref }

    /// <summary>A nonempty BindingToUnset defers whole-binding removal to the caller's scope cleanup.</summary>
    internal readonly record struct ElementUnsetResult(bool Succeeded, BindingName BindingToUnset);

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
            await scope.Effects.ReportReadonlyUnsetAsync(name.ToValue(), function: false, scope.Context).ConfigureAwait(false);
            return new(false, BindingName.Empty);
        }
        var resolvedName = variables.ResolveNamerefName(name);
        if (resolvedName.IsEmpty && kind == UnsetReferenceKind.Direct
            && variables.TryResolveBinding(name, selectOwner: null, out var binding)
            && binding.TryGetInvalidNameref(out var rawTarget))
            return new(true, rawTarget.IsEmpty ? name : BindingName.Empty);
        if (!variables.HasBinding(resolvedName) || variables.IsArrayNameref(resolvedName))
            return new(true, BindingName.Empty);

        // The unset policy is selected before the selector can change the binding's kind.
        var arrayMetadata = variables.GetArrayMetadata(resolvedName);
        if (arrayMetadata is null)
            return await UnsetScalarElementAsync().ConfigureAwait(false);
        if (reference.IsAggregate && arrayMetadata is { Kind: ArrayKind.Indexed } aggregate)
            return new(aggregate.Count == 0
                || variables.TryAssignResolvedArray(resolvedName, [], ArrayKind.Indexed, initializeReadonly: false), BindingName.Empty);

        var target = await ResolveReferenceAsync(reference, ReferenceUse.Unset, scope, ExpansionPurpose.ArithmeticSource).ConfigureAwait(false);
        // A nameref unset ignores an element-removal failure, while expansion failures still reach the reader.
        if (!scope.CanExpand || target == null)
            return new(kind == UnsetReferenceKind.Nameref && !scope.Effects.HasPendingExpansionError, BindingName.Empty);
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

        async Task<ElementUnsetResult> UnsetScalarElementAsync()
        {
            if (reference.IsAggregate)
                return kind == UnsetReferenceKind.Nameref
                    ? new(true, BindingName.Empty)
                    : await NotAnArrayAsync().ConfigureAwait(false);

            var expanded = await ExpandSubscriptAsync(reference.Subscript, scope, ExpansionPurpose.ArithmeticSource,
                allowLeadingTilde: false).ConfigureAwait(false);
            if (!scope.CanExpand)
                return new(false, BindingName.Empty);
            var index = await EvaluateArithmeticAsync(ArithmeticOperand.FromValue(expanded), scope, ArithmeticFailureMode.ArrayIndex)
                .ConfigureAwait(false);
            if (index is null)
                return new(false, BindingName.Empty);
            if (index.Value == 0)
                return new(true, resolvedName);

            if (kind == UnsetReferenceKind.Nameref)
                return new(true, BindingName.Empty);
            if (variables.GetArrayMetadata(resolvedName) != null)
                return new(false, BindingName.Empty);
            return await NotAnArrayAsync().ConfigureAwait(false);
        }

        async Task<ElementUnsetResult> NotAnArrayAsync()
        {
            await scope.Effects.ReportBuiltinDiagnosticAsync([ShellValue.FromText("unset")],
                [resolvedName.ToValue(), ShellValue.FromText(": not an array variable")], scope.Context).ConfigureAwait(false);
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
            await scope.Effects.ReportAssignmentTargetFailureAsync(
                (reference.Kind == VariableReferenceKind.Name ? target.Name : reference.Name).ToValue(),
                "readonly variable", scope).ConfigureAwait(false);
        return false;
    }

    private enum NamerefAssignmentAction { Assign, Handled, Fail }
    private readonly record struct NamerefAssignmentResult(NamerefAssignmentAction Action, ShellValue Value);

    private static async Task<NamerefAssignmentResult> PrepareNamerefAssignmentAsync(
        BindingName name, ShellValue value, AssignmentOperator operation, ScopedEvaluation scope, ExpansionFailureKind failure)
    {
        if (!scope.Variables.TryResolveBinding(name, selectOwner: null, out var selectedBinding))
        {
            await scope.Effects.ReportCircularNameReferenceAsync(name, scope).ConfigureAwait(false);
            if (!scope.CanExpand)
                return new(NamerefAssignmentAction.Fail, value);
            if (scope.Context.TryResolveCircularBinding(name, scope.Variables, out var circularBinding))
                return await AssignSelectedAsync(circularBinding).ConfigureAwait(false);
            scope.Effects.SetFatalExpansionError(failure);
            return new(NamerefAssignmentAction.Fail, value);
        }
        if (selectedBinding.TryGetInvalidNameref(out var rawTarget))
            return await RejectAssignmentAsync(rawTarget.IsEmpty ? null : rawTarget).ConfigureAwait(false);
        switch (Variables.GetNamerefAssignmentFailure(name, selectedBinding, value, scope.Effects.Memory))
        {
            case NamerefAssignmentFailure.None:
                return new(NamerefAssignmentAction.Assign, value);
            case NamerefAssignmentFailure.InvalidTarget:
                return await RejectAssignmentAsync(value).ConfigureAwait(false);
            case NamerefAssignmentFailure.IndirectUnsetTarget:
                if (scope.Context.TryResolveLocalNamerefAssignment(name, scope.Variables, scope.Effects.Memory, out var fallback))
                {
                    VariableBinding? selected = null;
                    if (fallback is Evaluation.LocalNamerefAssignment.Circular circular)
                    {
                        if (circular.LookupWarning is { } warning)
                            await scope.Effects.ReportCircularNameReferenceAsync(warning, scope).ConfigureAwait(false);
                        await scope.Effects.ReportCircularNameReferenceAsync(name, scope).ConfigureAwait(false);
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
                            return await RejectAssignmentAsync(retryFailure == NamerefAssignmentFailure.InvalidTarget ? value : null)
                                .ConfigureAwait(false);
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
                return await RejectAssignmentAsync(null).ConfigureAwait(false);
        }
        return new(NamerefAssignmentAction.Fail, value);

        async Task<NamerefAssignmentResult> AssignSelectedAsync(VariableBinding binding)
        {
            if (binding.TryGetInvalidNameref(out var spelling))
                return await RejectAssignmentAsync(spelling.IsEmpty ? null : spelling).ConfigureAwait(false);
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
                await scope.Effects.ReportNamerefAssignmentFailureAsync([target.Name.ToValue()],
                    [ShellValue.FromText("readonly variable")], failure, scope).ConfigureAwait(false);
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

        async Task<NamerefAssignmentResult> RejectAssignmentAsync(ShellValue? invalidTarget)
        {
            if (invalidTarget is { } spelling)
                await scope.Effects.ReportNamerefAssignmentFailureAsync(
                    [ShellValue.FromText("`"), spelling, ShellValue.FromText("'")],
                    [ShellValue.FromText("not a valid identifier")], failure, scope).ConfigureAwait(false);
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
        VariableReference reference, ShellValue expandedSubscript, ShellValue value, AssignmentOperator operation,
        ScopedEvaluation scope, Variables targetVariables) =>
        ResolveReferenceAsync(reference, ReferenceUse.CompoundAssign, scope, ExpansionPurpose.ArithmeticSource,
            new CompoundElement(expandedSubscript, value, operation), targetVariables, targetVariables);

    private readonly record struct CompoundElement(ShellValue Subscript, ShellValue Value, AssignmentOperator Operator);

    /// <summary>Reads a selected binding through ordinary nameref/array expansion rules.</summary>
    internal static Task<ResolvedVariableReference?> ResolveReadReferenceAsync(VariableReference reference, ScopedEvaluation scope) =>
        ResolveReferenceAsync(reference, ReferenceUse.BindingRead, scope, ExpansionPurpose.ArithmeticSource);

    internal static Task<bool> TestVariableAsync(UnaryPredicateKind predicate, ShellValue operand, ScopedEvaluation scope)
    {
        if (!scope.CanExpand)
            return Task.FromResult(false);
        if (predicate == UnaryPredicateKind.VariableIsSet
            && Integers.TryParseBuiltinNumber(operand.Bytes.Span, scope.Effects.CancellationToken, out var position))
            return Task.FromResult(position >= 0 && position <= scope.Variables.PositionalParameters.Count);
        if (!VariableReference.TryParseExpandedOperand(ShellSource.FromBytes(operand.Bytes), scope.Effects.Memory.Workspace, out var reference))
            return Task.FromResult(false);
        if (predicate == UnaryPredicateKind.NamerefIsSet)
            return Task.FromResult(reference.Kind == VariableReferenceKind.Name
                && scope.Variables.IsNamerefSet(reference.Name));
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
            var array = selected.Owner.GetArrayMetadata(selected.Name);
            if (array is not { Kind: ArrayKind.Associative })
                return array is { } indexed
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
        ResolveBuiltinReferenceAsync(reference, scope, destination, indexVariables, ShellValue.Empty);

    internal static Task<ResolvedVariableReference?> ResolveBuiltinReferenceAsync(
        VariableReference reference, ScopedEvaluation scope, Variables destination, Variables indexVariables, ShellValue command) =>
        ResolveReferenceAsync(reference, ReferenceUse.BuiltinAssign, scope,
            ExpansionPurpose.ArithmeticSource, null, destination, indexVariables, null, command);

    /// <summary>Separates array storage selection from the binding supplying negative-index bounds.</summary>
    internal static Task<ResolvedVariableReference?> ResolveDeclarationReferenceAsync(
        VariableReference reference, ScopedEvaluation scope, Variables targetVariables, Variables indexVariables) =>
        ResolveReferenceAsync(reference, ReferenceUse.DeclarationAssign, scope,
            ExpansionPurpose.ArithmeticSource, null, targetVariables, indexVariables);

    /// <summary>Checks the arithmetic selector reached by a rejected local nameref declaration.</summary>
    internal static Task<ResolvedVariableReference?> ResolveRejectedNamerefSelectorAsync(
        VariableReference reference, ScopedEvaluation scope) =>
        ResolveReferenceAsync(reference, ReferenceUse.RejectedNamerefDeclaration, scope, ExpansionPurpose.ArithmeticSource);

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
        CompoundElement? compound,
        Variables targetVariables,
        Variables indexVariables) =>
        ResolveReferenceAsync(reference, use, scope, arithmeticPurpose, compound, targetVariables, indexVariables,
            null, ShellValue.Empty);

    private static async Task<ResolvedVariableReference?> ResolveReferenceAsync(
        VariableReference reference,
        ReferenceUse use,
        ScopedEvaluation scope,
        ExpansionPurpose arithmeticPurpose,
        CompoundElement? compound,
        Variables targetVariables,
        Variables indexVariables,
        ExpandedArraySubscript? preparedSubscript,
        ShellValue command)
    {
        if (!scope.CanExpand)
            return null;
        var convertedNameref = false;
        if (use is ReferenceUse.Read or ReferenceUse.BindingRead or ReferenceUse.DefaultRead or ReferenceUse.Length or ReferenceUse.Presence
            && reference.Kind != VariableReferenceKind.Name
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
            var parameterRead = use is ReferenceUse.Read or ReferenceUse.ShortRead or ReferenceUse.DefaultRead;
            if (parameterRead || use is ReferenceUse.BindingRead or ReferenceUse.Length)
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
                    await scope.Effects.ReportCircularNameReferenceAsync(reference.Name, scope).ConfigureAwait(false);
                    if (use == ReferenceUse.ArithmeticAssign && scope.CanExpand)
                        await scope.Effects.ReportCircularNameReferenceAsync(reference.Name, scope).ConfigureAwait(false);
                    if (!scope.Context.TryResolveCircularBinding(reference.Name, targetVariables, out binding))
                        return null;
                    targetVariables = binding.Owner;
                }
                else
                    return new ResolvedVariableReference.Scalar(BindingName.Empty);
            }
            if (binding.TryGetInvalidNameref(out var rawTarget) && use != ReferenceUse.BuiltinAssign)
            {
                if (parameterRead && !rawTarget.IsEmpty)
                    await scope.Effects.ReportParameterExpansionFailureAsync([rawTarget],
                        [ShellValue.FromText("invalid variable name for name reference")], scope, ExpansionFailureKind.ParameterTarget)
                        .ConfigureAwait(false);
                return new ResolvedVariableReference.Scalar(BindingName.Empty);
            }
            if (parameterRead && binding.IsArrayNameref && (use != ReferenceUse.ShortRead
                || binding.Owner.GetArrayMetadata(binding.Name) is { IsInitialized: true }))
            {
                await scope.Effects.ReportParameterExpansionFailureAsync([GetArrayNamerefDiagnosticSubject(binding)],
                    [ShellValue.FromText("invalid variable name for name reference")], scope, ExpansionFailureKind.ParameterTarget)
                    .ConfigureAwait(false);
                return null;
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
                    else if (use == ReferenceUse.DirectAssign)
                        await scope.Effects.ReportAssignmentTargetFailureAsync(reference.Name.ToValue(), "readonly variable", scope).ConfigureAwait(false);
                    else
                        await scope.Effects.ReportFatalExpansionAsync(reference.Name.ToValue(), ShellValue.FromText("readonly variable"),
                            use == ReferenceUse.Assign ? ExpansionFailureKind.Assignment : ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
                    return null;
                }
                convertedNameref = true;
            }
        }

        var builtinOutput = use == ReferenceUse.BuiltinAssign && !command.IsEmpty;
        var writesElement = use == ReferenceUse.DirectAssign || builtinOutput;
        var assignsDefault = use == ReferenceUse.Assign;
        // Compound preparation has already selected the binding.
        var name = reference.Name;
        var selectAfterIndex = false;
        var reachedArrayNameref = false;
        if (use != ReferenceUse.CompoundAssign)
        {
            var reached = targetVariables.TryResolveBinding(name, selectOwner: null, out var initial);
            var invalidNameref = reached && initial.TryGetInvalidNameref(out _);
            if (invalidNameref && use != ReferenceUse.DirectAssign)
                return new ResolvedVariableReference.Scalar(BindingName.Empty);
            reachedArrayNameref = reached && initial.IsArrayNameref;
            // Length lookup has no selector to expand when the array nameref has no referent.
            if (reachedArrayNameref && use == ReferenceUse.Length)
                return null;
            name = reached && initial.Reference.Kind == VariableReferenceKind.Name && !initial.IsUnsetNameref && !invalidNameref
                && !(reachedArrayNameref && (writesElement || assignsDefault))
                ? initial.Name : BindingName.Empty;
            selectAfterIndex = reached && name.IsEmpty
                && (writesElement || assignsDefault && reachedArrayNameref
                    || use is ReferenceUse.ArithmeticRead or ReferenceUse.ArithmeticAssign);
        }
        var associative = !reachedArrayNameref && use != ReferenceUse.RejectedNamerefDeclaration
            && targetVariables.AssociativeArrays.ContainsKey(name);
        // Prepared and staged selectors validate expanded bytes.
        var selector = preparedSubscript is { } preparedSelector
            ? VariableReference.ForElement(reference.Name, ShellSource.FromBytes(preparedSelector.Value.Bytes))
            : compound is { } staged
            ? VariableReference.ForElement(reference.Name, ShellSource.FromBytes(staged.Subscript.Bytes))
            : reference;
        if (selector.Subscript.IsEmpty || selector.IsAggregate && !associative)
        {
            if (!associative && compound is { } invalidItem)
            {
                await ReportRejectedCompoundElementAsync(invalidItem, aggregate: selector.IsAggregate).ConfigureAwait(false);
                return null;
            }
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
        var scalarLength = use == ReferenceUse.Length && targetVariables.Scalars.ContainsKey(name);
        if (use == ReferenceUse.Length && !scalarLength
            && targetVariables.GetArrayMetadata(name) is not { IsInitialized: true })
            return null;

        var purpose = associative ? ExpansionPurpose.Scalar : arithmeticPurpose;
        var syntax = use == ReferenceUse.Presence
            ? associative ? ExpansionWordSyntax.ExpandedSelector : ExpansionWordSyntax.ExpandedArithmeticSelector
            : GetOperandSyntax(purpose);
        // Indexed compound items have a word-expansion pass before replacement, then an
        // arithmetic-source pass against the array being built. Associative keys are already final.
        var expanded = preparedSubscript is { } prepared ? prepared.Value
            : associative && compound is { } stagedKey
            ? stagedKey.Subscript
            : await ExpandSubscriptAsync(selector.Subscript, scope, purpose,
                allowLeadingTilde: associative && use == ReferenceUse.DirectAssign, syntax).ConfigureAwait(false);
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
            // A literal index does not need another recursive arithmetic frame during promotion.
            var evaluated = scope.Arithmetic is { IsIntegerPromotion: true }
                && ArithmeticEvaluation.TryParseIntegerLiteral(operand.Value.Bytes.Span, scope.Effects.CancellationToken, out var literal)
                ? literal : await EvaluateArithmeticAsync(operand, scope, ArithmeticFailureMode.ArrayIndex).ConfigureAwait(false);
            if (evaluated is not { } index)
                return null;

            if (index < 0)
            {
                var maximum = indexVariables.GetArrayMetadata(name) is { Kind: ArrayKind.Indexed } array
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
            {
                // A reached scalar length still reads as scalar after selector effects.
                if (scalarLength && !targetVariables.Scalars.ContainsKey(name))
                    return null;
                if (selectAfterIndex && targetVariables.TryResolveBinding(reference.Name, selectOwner: null, out var binding))
                {
                    // Selector expansion may initialize the nameref. Keep the already validated index.
                    if (use == ReferenceUse.DirectAssign && binding.TryGetInvalidNameref(out var rawTarget))
                    {
                        if (!rawTarget.IsEmpty)
                        {
                            await scope.Effects.ReportInvalidNamerefElementTargetAsync(rawTarget, scope).ConfigureAwait(false);
                            return null;
                        }
                        targetVariables.RecreateDeclarationArray(reference.Name, ArrayKind.Indexed);
                        return new ResolvedVariableReference.Indexed(reference.Name, position) { ReadOwner = targetVariables };
                    }
                    var stillUnset = binding.IsUnsetNameref;
                    var arrayNameref = (writesElement || assignsDefault) && binding.IsArrayNameref;
                    if (!stillUnset && !arrayNameref && binding.Reference.Kind == VariableReferenceKind.Name
                        && !targetVariables.AssociativeArrays.ContainsKey(binding.Name))
                    {
                        return new ResolvedVariableReference.Indexed(binding.Name, position)
                        {
                            ReadOwner = binding.Owner,
                            ReadAsUnset = use == ReferenceUse.ArithmeticRead
                        };
                    }
                    if (writesElement || arrayNameref)
                    {
                        if (stillUnset || arrayNameref || binding.Reference.Kind != VariableReferenceKind.Name)
                        {
                            var spelling = arrayNameref ? GetArrayNamerefDiagnosticSubject(binding)
                                : stillUnset ? ShellValue.Empty : binding.Reference.ToValue(scope.Effects.Memory);
                            if (builtinOutput)
                                await scope.Effects.ReportInvalidAssignmentIdentifierAsync(command, spelling, scope).ConfigureAwait(false);
                            else if (assignsDefault)
                                await scope.Effects.ReportParameterDefaultFailureAsync(
                                    [ShellValue.FromText("`"), spelling, ShellValue.FromText("'")],
                                    [ShellValue.FromText("not a valid identifier")], scope).ConfigureAwait(false);
                            else
                                await scope.Effects.ReportInvalidNamerefElementTargetAsync(spelling, scope).ConfigureAwait(false);
                        }
                        return null;
                    }
                }
                return new ResolvedVariableReference.Indexed(name, position) { ReadOwner = targetVariables };
            }
            if (compound is { } item)
            {
                await ReportRejectedCompoundElementAsync(item, aggregate: false).ConfigureAwait(false);
                return null;
            }
        }

        await ReportBadSubscriptAsync().ConfigureAwait(false);
        return null;

        Task ReportRejectedCompoundElementAsync(CompoundElement item, bool aggregate)
        {
            var separator = item.Operator == AssignmentOperator.Append ? "]+=" : "]=";
            return scope.Effects.ReportCompoundIndexFailureAsync(
                [ShellValue.FromText("["), item.Subscript, ShellValue.FromText(separator), item.Value],
                aggregate, scope);
        }

        Task ReportEmptyArithmeticAssignmentAsync()
        {
            var referenceName = reference.Name.ToValue();
            ReadOnlySpan<ShellValue> subject = command.IsEmpty
                ? [ShellValue.FromText("`"), referenceName, ShellValue.FromText("[]'")]
                : [command, ShellValue.FromText(": `"), referenceName, ShellValue.FromText("[]'")];
            return scope.Effects.ReportCommandDiagnosticAsync(subject, [ShellValue.FromText("not a valid identifier")], scope);
        }

        Task ReportBadSubscriptAsync()
        {
            var referenceName = reference.Name.ToValue();
            if (use is ReferenceUse.Read or ReferenceUse.ShortRead or ReferenceUse.BindingRead or ReferenceUse.Presence or ReferenceUse.DefaultRead)
                return scope.Effects.ReportArrayReadFailureAsync([referenceName], scope, length: false);
            if (use == ReferenceUse.Length)
                return scope.Effects.ReportArrayReadFailureAsync([reference.Subscript.ToValue(), ShellValue.FromText("]")],
                    scope, length: true);

            var message = ShellValue.FromText("bad array subscript");
            if (use == ReferenceUse.Unset)
                return scope.Effects.ReportBuiltinDiagnosticAsync([ShellValue.FromText("unset")],
                    [ShellValue.FromText("["), reference.Subscript.ToValue(), ShellValue.FromText("]: "), message], scope.Context);
            if (use == ReferenceUse.ArithmeticRead && !selector.Subscript.IsEmpty && !selector.IsAggregate)
                return scope.Effects.ReportBuiltinDiagnosticAsync([referenceName], [message], scope.Context);

            var subscript = preparedSubscript is { } prepared
                ? prepared.Value
                : use == ReferenceUse.DirectAssign
                    ? scope.Effects.GetAssignmentDiagnosticSpelling(reference.Subscript, scope.ExpansionOrigin)
                    : reference.Subscript.ToValue();
            ReadOnlySpan<ShellValue> subject = [referenceName, ShellValue.FromText("["), subscript, ShellValue.FromText("]")];
            return use switch
            {
                ReferenceUse.ArithmeticAssign or ReferenceUse.ArithmeticRead
                    or ReferenceUse.DeclarationAssign or ReferenceUse.RejectedNamerefDeclaration or ReferenceUse.BuiltinAssign =>
                    scope.Effects.ReportBuiltinDiagnosticAsync(subject, [message], scope.Context),
                ReferenceUse.CompoundAssign =>
                    scope.Effects.ReportExpansionDiagnosticAsync(subject, [message]),
                ReferenceUse.Assign => scope.Effects.ReportParameterDefaultFailureAsync(subject, [message], scope),
                ReferenceUse.DirectAssign => scope.Effects.ReportAssignmentTargetFailureAsync(subject, [message], scope),
                _ => throw new InvalidOperationException("Unknown reference use."),
            };
        }

        static ResolvedVariableReference ResolveNameReference(BindingName name, Variables variables)
        {
            if (variables.IndexedArrays.ContainsKey(name))
                return new ResolvedVariableReference.Indexed(name, default) { ReadOwner = variables, IsWholeBindingRead = true };
            if (variables.AssociativeArrays.ContainsKey(name))
                return new ResolvedVariableReference.Associative(name, AssociativeArrayKey.Zero)
                    { ReadOwner = variables, IsWholeBindingRead = true };
            return new ResolvedVariableReference.Scalar(name) { ReadOwner = variables, IsWholeBindingRead = true };
        }
    }

    private static ShellValue GetArrayNamerefDiagnosticSubject(VariableBinding binding) =>
        binding.Owner.IndexedArrays.ContainsKey(binding.Name) ? IndexedArrayNamerefSpelling : binding.Name.ToValue();

    /// <summary>Warns at a reached read, then selects Bash's global fallback for a local cycle.</summary>
    private static async ValueTask<VariableBinding?> ResolveReadBindingAsync(
        VariableReference reference, ReferenceUse use, ScopedEvaluation scope, Variables variables)
    {
        if (variables.TryResolveBinding(reference.Name, selectOwner: null, out var binding))
            return binding;
        await scope.Effects.ReportCircularNameReferenceAsync(reference.Name, scope).ConfigureAwait(false);
        var found = scope.Context.TryResolveCircularBinding(reference.Name, variables, out binding);
        if (reference.Kind != VariableReferenceKind.Name && use != ReferenceUse.Length
            || reference.Kind == VariableReferenceKind.Name && use == ReferenceUse.Length && !found)
            await scope.Effects.ReportCircularNameReferenceAsync(reference.Name, scope).ConfigureAwait(false);
        return found ? binding : null;
    }

    private enum ReferenceUse
    {
        Read,
        ShortRead,
        BindingRead,
        DefaultRead,
        Length,
        Presence,
        Assign,
        DirectAssign,
        DeclarationAssign,
        RejectedNamerefDeclaration,
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

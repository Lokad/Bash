using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    internal enum DeclarationWrite { Ordinary, ActiveGlobalIndexed }

    /// <summary>A selected location; its typed index is not expanded again when read.</summary>
    internal abstract record ResolvedVariableReference(BindingName Name)
    {
        internal sealed record Scalar(BindingName VariableName) : ResolvedVariableReference(VariableName);
        internal sealed record Indexed(BindingName VariableName, IndexedArrayIndex Index) : ResolvedVariableReference(VariableName);
        internal sealed record Associative(BindingName VariableName, AssociativeArrayKey Key) : ResolvedVariableReference(VariableName);

        internal long ReadArithmetic(Variables variables) => ReadArithmetic(variables, CancellationToken.None);

        internal long ReadArithmetic(Variables variables, CancellationToken cancellationToken) => TryRead(variables, out var value)
            && ArithmeticEvaluation.TryParseDecimal(value.Bytes.Span, cancellationToken, out var number) ? number : 0;

        public bool TryRead(Variables variables, out ShellValue value)
        {
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

        public bool TryAssign(ScopedEvaluation scope, BindingName sourceName, ShellValue value, AssignmentOperator operation)
            => TryAssign(scope, sourceName, value, operation, DeclarationWrite.Ordinary);

        internal bool TryAssign(ScopedEvaluation scope, BindingName sourceName, ShellValue value,
            AssignmentOperator operation, DeclarationWrite declarationWrite)
        {
            if (operation == AssignmentOperator.Append && TryRead(scope.Variables, out var previous))
                value = AppendAssignmentValue(previous, value, scope);

            if (!TryAssignTo(scope.Variables, Name))
                return false;

            if (scope.GetSelectorWriteTarget(sourceName, Name) is { } retained)
            {
                if (this is Scalar)
                    retained.SetResolvedValuePreservingArray(Name, value);
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
                    ShellValue.FromText(Integers.FormatNumber(indexed.Index.Value)), value, ArrayAssignmentKind.Auto),
                Associative associative => variables.TryAssignArrayElement(name, associative.Key.Value, value, ArrayAssignmentKind.Auto),
                _ => throw new InvalidOperationException("Unknown variable reference."),
            };

            // Selection already resolved namerefs; local bindings can retain a literal name such as "a[1]".
            bool TryAssignTo(Variables variables, BindingName name) => !name.IsEmpty
                && (declarationWrite == DeclarationWrite.ActiveGlobalIndexed || !variables.IsReadonly(name)) && this switch
            {
                Scalar => variables.TrySetResolvedScalar(name, value, scope.Effects.Memory),
                Indexed indexed => variables.TrySetResolvedArrayElement(name, indexed.Index, value),
                Associative associative => variables.TrySetResolvedArrayElement(name, associative.Key, value),
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
            await scope.Effects.ReportExpansionDiagnosticAsync(ShellValue.FromText("unset"),
                ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append(name.ToValue().Bytes).Append(": cannot unset: readonly variable"u8).ToMemory())).ConfigureAwait(false);
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
            await scope.Effects.ReportExpansionDiagnosticAsync(ShellValue.FromText("unset"),
                AppendAssignmentValue(name.ToValue(), ShellValue.FromText(": not an array variable"), scope)).ConfigureAwait(false);
            return new(false, BindingName.Empty);
        }
    }

    /// <summary>Applies a direct assignment after its RHS has expanded in the active scope.</summary>
    internal static async Task<bool> TryAssignAsync(
        ShellSource source, ShellValue value, AssignmentOperator operation, ScopedEvaluation scope)
    {
        var opening = source.AsSpan().IndexOf((byte)'[');
        scope.Effects.Memory.Charge(checked(4L * (opening < 0 ? source.Length : opening)));
        if (!VariableReference.TryParse(source, out var reference))
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
        if (target.TryAssign(scope, reference.Name, value, operation))
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
                        if (!target.TryAssign(scope with { Variables = binding.Owner, ExpansionWriteTarget = null },
                                reference.Name, value, operation))
                            return new(NamerefAssignmentAction.Fail, value);
                        // A hidden array-element default returns the array's scalar value, not its assigned slot.
                        if (failure == ExpansionFailureKind.NamerefDefault
                            && target is ResolvedVariableReference.Indexed or ResolvedVariableReference.Associative)
                            binding.Owner.TryGetExpansionValue(target.Name, out value);
                    }
                    return new(NamerefAssignmentAction.Handled, value);
                }
                return await RejectAssignmentAsync(NamerefAssignmentFailure.IndirectUnsetTarget).ConfigureAwait(false);
        }
        return new(NamerefAssignmentAction.Fail, value);

        async Task<NamerefAssignmentResult> RejectAssignmentAsync(NamerefAssignmentFailure reason)
        {
            if (reason == NamerefAssignmentFailure.InvalidTarget)
                await scope.Effects.ReportFatalExpansionAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(value), ShellValue.FromText("not a valid identifier"), failure).ConfigureAwait(false);
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
        ResolveReferenceAsync(reference, use, scope, arithmeticPurpose, stagedSubscript, targetVariables, indexVariables, null);

    private static async Task<ResolvedVariableReference?> ResolveReferenceAsync(
        VariableReference reference,
        ReferenceUse use,
        ScopedEvaluation scope,
        ExpansionPurpose arithmeticPurpose,
        ShellValue? stagedSubscript,
        Variables targetVariables,
        Variables indexVariables,
        ExpandedArraySubscript? preparedSubscript)
    {
        if (!scope.CanExpand)
            return null;
        var convertedNameref = false;
        if (reference.Kind == VariableReferenceKind.Name)
        {
            var sourceName = reference.Name;
            if (!targetVariables.TryResolveBinding(reference.Name, selectOwner: null, out var binding))
            {
                if (use is ReferenceUse.ArithmeticRead or ReferenceUse.ArithmeticAssign)
                {
                    await scope.Effects.ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
                    if (use == ReferenceUse.ArithmeticAssign && scope.CanExpand)
                        await scope.Effects.ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
                    return null;
                }
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
            if (use == ReferenceUse.ArithmeticAssign && selector.Subscript.IsEmpty)
            {
                await scope.Effects.ReportExpansionDiagnosticAsync(SubscriptDiagnostic(includeName: true),
                    ShellValue.FromText("not a valid identifier")).ConfigureAwait(false);
                return null;
            }
            await ReportBadSubscriptAsync().ConfigureAwait(false);
            // Bash diagnoses an empty indexed read twice, before any attempted write.
            if (use == ReferenceUse.ArithmeticRead && selector.Subscript.IsEmpty && scope.CanExpand)
                await ReportBadSubscriptAsync().ConfigureAwait(false);
            return null;
        }

        // A declaration alone does not enable length-selector effects; even an empty assignment does.
        if (use == ReferenceUse.Length && !scope.Variables.Scalars.ContainsKey(name)
            && (!scope.Variables.IndexedArrays.TryGetValue(name, out var indexed) || !indexed.IsInitialized)
            && (!scope.Variables.AssociativeArrays.TryGetValue(name, out var map) || !map.IsInitialized))
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
                return new ResolvedVariableReference.Associative(name, key);
        }
        else
        {
            // Assignment index evaluation is shell-fatal, unlike arithmetic expansion in its source.
            var failure = use is ReferenceUse.DirectAssign or ReferenceUse.CompoundAssign
                ? ArithmeticFailureMode.Assignment : ArithmeticFailureMode.Fatal;
            var evaluated = await EvaluateArithmeticAsync(
                ArithmeticOperand.FromIndex(preparedSubscript ?? new ExpandedArraySubscript(expanded, ArithmeticBracketMode.References)),
                scope, failure).ConfigureAwait(false);
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
                return new ResolvedVariableReference.Indexed(name, position);
        }

        if (use != ReferenceUse.Length || !associative)
            await ReportBadSubscriptAsync().ConfigureAwait(false);
        return null;

        ShellValue SubscriptDiagnostic(bool includeName)
        {
            var diagnostic = ByteMemoryBuilder.CreateUnaccounted(0);
            if (includeName)
                diagnostic.Append(reference.Name.ToValue().Bytes).Append((byte)'[');
            diagnostic.Append(preparedSubscript is { } prepared ? prepared.Value.Bytes : reference.Subscript.Bytes).Append((byte)']');
            return ShellValue.FromBytes(diagnostic.ToMemory());
        }

        Task ReportBadSubscriptAsync() => use switch
        {
            ReferenceUse.Unset => scope.Effects.ReportExpansionDiagnosticAsync(
                ShellValue.FromText("unset"), ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append(SubscriptDiagnostic(includeName: true).Bytes).Append(": bad array subscript"u8).ToMemory())),
            ReferenceUse.Read or ReferenceUse.ArithmeticRead => scope.Effects.ReportExpansionDiagnosticAsync(
                reference.Name.ToValue(), ShellValue.FromText("bad array subscript")),
            ReferenceUse.CompoundAssign or ReferenceUse.DeclarationAssign or ReferenceUse.BuiltinAssign or ReferenceUse.ArithmeticAssign => scope.Effects.ReportExpansionDiagnosticAsync(
                SubscriptDiagnostic(includeName: true), ShellValue.FromText("bad array subscript")),
            ReferenceUse.Length => scope.Effects.ReportFatalExpansionAsync(SubscriptDiagnostic(includeName: false), ShellValue.FromText("bad array subscript"), ExpansionFailureKind.Arithmetic),
            ReferenceUse.Assign => scope.Effects.ReportFatalExpansionAsync(
                SubscriptDiagnostic(includeName: true), ShellValue.FromText("bad array subscript"), ExpansionFailureKind.Assignment),
            ReferenceUse.DirectAssign => scope.Effects.ReportFatalExpansionAsync(
                SubscriptDiagnostic(includeName: true), ShellValue.FromText("bad array subscript"), ExpansionFailureKind.AssignmentTarget),
            _ => throw new InvalidOperationException("Unknown reference use."),
        };

        static ResolvedVariableReference ResolveNameReference(BindingName name, Variables variables)
        {
            if (variables.IndexedArrays.ContainsKey(name))
                return new ResolvedVariableReference.Indexed(name, default);
            if (variables.AssociativeArrays.ContainsKey(name)
                && AssociativeArrayKey.TryCreate(ShellValue.FromText("0"), out var key))
                return new ResolvedVariableReference.Associative(name, key);
            return new ResolvedVariableReference.Scalar(name);
        }
    }

    private enum ReferenceUse
    {
        Read,
        Length,
        Assign,
        DirectAssign,
        DeclarationAssign,
        CompoundAssign,
        BuiltinAssign,
        Unset,
        ArithmeticRead,
        ArithmeticAssign,
    }

    private enum SpecialParameterKind { None, Status, Count, At, Star, Positional, BackgroundProcess }

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
        public ShellValue Value => Fields.Count == 0 ? ShellValue.Empty : Fields[0];
    }

}

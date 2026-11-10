using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    /// <summary>Expands direct selectors before numeric writes, retaining their source and literal result.</summary>
    internal static async Task<ArithmeticOperand?> PrepareArithmeticAsync(
        ShellSource source, ScopedEvaluation scope, ExpansionPurpose purpose)
    {
        if (source.Length == 0)
            return ArithmeticOperand.FromValue(ShellValue.Empty);
        var word = Tokenizer.ReadArithmeticWord(source, GetOperandSyntax(purpose));
        var bytes = ByteMemoryBuilder.CreateUnaccounted(0);
        var subscripts = new Dictionary<int, ArithmeticOperand.Subscript>();
        var pending = new List<WordPart>();
        var budget = scope.Effects.ExpansionBudget.Words;
        foreach (var part in word.Parts)
        {
            if (!scope.CanExpand)
                break;
            if (part.Kind != WordPartKind.ArithmeticSubscript)
            {
                pending.Add(part);
                continue;
            }

            await FlushAsync().ConfigureAwait(false);
            if (!scope.CanExpand)
                break;
            var selector = ((SourceWordPart)part).Source;
            var subscript = selector.Slice(1, selector.Length - 2);
            var associative = IsAssociativeTarget();
            var selectorPurpose = associative ? ExpansionPurpose.Scalar : purpose;
            var builder = await ExpandAsync(subscript.Length == 0 ? new Word([])
                : Tokenizer.ReadExpansionWord(subscript, GetOperandSyntax(selectorPurpose), ShellQuoteKind.None), selectorPurpose)
                .ConfigureAwait(false);
            if (!scope.CanExpand)
                break;
            var expanded = ExpandedWord.JoinWithSpaces(builder.Build(), budget).ToValue(budget);
            var brackets = !associative && builder.HasExpansion ? ArithmeticBracketMode.Literal : ArithmeticBracketMode.References;
            budget.Charge(1, expanded.Bytes.Length + 2L);
            subscripts.Add(bytes.Length, new ArithmeticOperand.Subscript(subscript, new ExpandedArraySubscript(expanded, brackets)));
            bytes.Append((byte)'[').Append(expanded.Bytes).Append((byte)']');
        }
        if (scope.CanExpand)
            await FlushAsync().ConfigureAwait(false);
        return !scope.CanExpand ? null
            : new ArithmeticOperand(ShellValue.FromBytes(bytes.ToMemory()), subscripts, ArithmeticBracketMode.References);

        async Task FlushAsync()
        {
            if (pending.Count == 0)
                return;
            var builder = await ExpandAsync(new Word(pending), purpose).ConfigureAwait(false);
            var value = ExpandedWord.JoinWithSpaces(builder.Build(), budget).ToValue(budget);
            budget.Charge(0, value.Bytes.Length);
            bytes.Append(value.Bytes);
            pending = [];
        }

        async Task<ExpandedWordBuilder> ExpandAsync(Word operand, ExpansionPurpose operandPurpose)
        {
            var builder = new ExpandedWordBuilder(scope.Variables, budget, operandPurpose, ImplicitEmptyPolicy.Preserve);
            await ExpandWordPartsAsync(operand, scope, builder, allowWordSplitting: false, allowLeadingTilde: false).ConfigureAwait(false);
            return builder;
        }

        bool IsAssociativeTarget()
        {
            var prefix = bytes.ToMemory();
            var start = prefix.Length;
            while (start > 0 && VariableNames.IsNamePart((char)prefix.Span[start - 1]))
                start--;
            return VariableReference.TryParse(ShellSource.FromBytes(prefix[start..]), budget, out var reference)
                && scope.Variables.AssociativeArrays.ContainsKey(scope.Variables.ResolveNamerefName(reference.Name));
        }
    }

    /// <summary>Evaluates through shared reference selection without recursive asynchronous callback stacks.</summary>
    internal static Task<long?> EvaluateArithmeticAsync(
        ArithmeticOperand expression, ScopedEvaluation scope, ArithmeticFailureMode failureMode) =>
        EvaluateArithmeticAsync(expression, scope, failureMode, null);

    internal static Task<long?> EvaluateArithmeticAsync(
        ArithmeticOperand expression, ScopedEvaluation scope, ArithmeticFailureMode failureMode,
        Func<VariableReference, ResolvedVariableReference, ShellValue, bool>? assignOutput)
    {
        var context = scope.Arithmetic ?? new ArithmeticContext(0);
        return context.Depth == 0 ? context.Work.RunAsync(EvaluateCoreAsync) : context.Work.Enqueue(EvaluateCoreAsync);

        async Task<long?> EvaluateCoreAsync()
        {
            if (context.Depth >= ShellSourceCursor.MaximumNestedExpansionDepth)
            {
                await ReportAsync(new ArithmeticError(ArithmeticErrorKind.NestingLimit, expression.Value)).ConfigureAwait(false);
                return null;
            }
            scope = scope with { Arithmetic = context with { Depth = context.Depth + 1 } };
            var referenceFailureReported = false;
            var result = await ArithmeticEvaluation.EvaluateAsync<ArithmeticTarget>(expression, BindAsync, WriteAsync).ConfigureAwait(false);
            if (!scope.CanExpand)
                return null;
            if (result.Error is not { } error)
                return result.Value;
            if (!referenceFailureReported)
                await ReportAsync(error).ConfigureAwait(false);
            return null;

            async ValueTask<ArithmeticBinding<ArithmeticTarget>> BindAsync(ArithmeticReference selected, ArithmeticReferenceUse use)
            {
                var target = await SelectAsync(selected, use).ConfigureAwait(false);
                if (!scope.CanExpand)
                {
                    referenceFailureReported = true;
                    return new ArithmeticBinding<ArithmeticTarget>.Failure(
                        new ArithmeticError(ArithmeticErrorKind.InvalidTarget, selected.Syntax.Name.ToValue()));
                }
                // Bad read subscripts diagnose and yield zero. A later write may select a newly created array.
                var value = use == ArithmeticReferenceUse.Read && target.Location != null ? target.Location.ReadArithmetic(scope.Variables) : 0;
                return new ArithmeticBinding<ArithmeticTarget>.Bound(target, value);
            }

            async ValueTask<ArithmeticError?> WriteAsync(ArithmeticReference selected, ArithmeticTarget binding, long value)
            {
                var reference = selected.Syntax;
                // Unlike a direct element operand, a whole-name nameref selects again for its write.
                var elementNameref = reference.Kind == VariableReferenceKind.Name
                    && scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out var resolved)
                    && resolved.Reference.Kind != VariableReferenceKind.Name;
                if (binding.Fallback == null && binding.Use == ArithmeticReferenceUse.Read && (binding.Location == null || elementNameref))
                    binding = await SelectAsync(selected, ArithmeticReferenceUse.Assign).ConfigureAwait(false);
                if (!scope.CanExpand)
                {
                    referenceFailureReported = true;
                    return new ArithmeticError(ArithmeticErrorKind.InvalidTarget, reference.Name.ToValue());
                }
                if (binding.Fallback is { } fallback)
                {
                    if (fallback is ArithmeticElementFallback.Circular circular)
                    {
                        var rebound = scope.Variables.TryResolveBinding(circular.Target.Name, selectOwner: null, out var current)
                            ? current : (VariableBinding?)null;
                        if (rebound == null && current.Name == circular.Target.Name)
                            await ReportCycleAsync(circular.Target.Name, binding.Use == ArithmeticReferenceUse.Read ? 3 : 1).ConfigureAwait(false);
                        if (scope.CanExpand)
                            return AssignCircular(circular.Target, rebound);
                    }
                    else if (fallback is ArithmeticElementFallback.Invalid invalid)
                        await scope.Effects.ReportExpansionDiagnosticAsync(invalid.Subject, ShellValue.FromText("not a valid identifier"))
                            .ConfigureAwait(false);
                    return null;
                }
                var target = binding.Location;
                // A diagnosed invalid write still returns its numeric value without changing storage.
                if (target == null)
                    return null;
                var formatted = ShellValue.FromText(Integers.FormatNumber(value));
                // Builtin output selects its local fallback before checking that destination's readonly state.
                if (assignOutput != null && assignOutput(reference, target, formatted))
                    return null;
                var readOnly = scope.Variables.IsReadonly(reference.Name) || scope.Variables.IsReadonly(target.Name);
                if (assignOutput == null && !readOnly && AssignExpansion(reference, target, formatted))
                    return null;
                var namerefFailure = scope.Variables.GetNamerefAssignmentFailure(
                    reference.Name, target.Name, formatted, scope.Effects.ExpansionBudget.Words);
                if (!readOnly && namerefFailure != NamerefAssignmentFailure.None)
                {
                    // A rejected nameref initializer diagnoses without discarding the arithmetic value.
                    await scope.Effects.ReportExpansionDiagnosticAsync(
                        namerefFailure == NamerefAssignmentFailure.InvalidTarget ? formatted : ShellValue.Empty,
                        ShellValue.FromText("not a valid identifier")).ConfigureAwait(false);
                    return null;
                }
                return new ArithmeticError(readOnly ? ArithmeticErrorKind.ReadonlyVariable : ArithmeticErrorKind.InvalidTarget,
                    reference.Name.ToValue());

                ArithmeticError? AssignCircular(ResolvedVariableReference.Indexed target, VariableBinding? resolved)
                {
                    var variables = scope.Variables;
                    var sourceName = target.Name;
                    // RHS/index writes may already have broken the cycle. Keep its index, not its old name lookup.
                    if (resolved is { } binding)
                        target = new ResolvedVariableReference.Indexed(binding.Name, target.Index);
                    var destination = resolved?.Owner ?? scope.Context.GetGlobalBindingOwner(target.Name, variables);
                    var forwarded = ReferenceEquals(destination, variables) ? scope.GetExpansionWriteTarget(sourceName) : null;
                    if (variables.IsReadonly(sourceName) || destination.IsReadonly(target.Name)
                        || forwarded?.IsReadonly(target.Name) == true)
                        return new ArithmeticError(ArithmeticErrorKind.ReadonlyVariable, target.Name.ToValue());

                    if (resolved == null)
                    {
                        // A cycle falls back to the original name globally, without changing a local alias.
                        if (!ReferenceEquals(destination, variables))
                        {
                            // Hidden nameref text survives at index zero; hidden scalar/array contents do not.
                            var previous = destination.TryGetNameref(target.Name, out var alias) ? alias : (ShellValue?)null;
                            destination.RecreateDeclarationArray(target.Name, ArrayKind.Indexed);
                            if (previous is { } scalar)
                                destination.SetResolvedValuePreservingArray(target.Name, scalar);
                        }
                        else if (!destination.TryDeclareResolvedArray(target.Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue))
                            return new ArithmeticError(ArithmeticErrorKind.InvalidTarget, target.Name.ToValue());
                        if (forwarded != null && !ReferenceEquals(forwarded, destination)
                            && !forwarded.TryDeclareResolvedArray(target.Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue))
                            return new ArithmeticError(ArithmeticErrorKind.InvalidTarget, target.Name.ToValue());
                    }

                    var writeScope = ReferenceEquals(destination, variables) ? scope
                        : scope with { Variables = destination, ExpansionWriteTarget = null, SelectorBindings = null };
                    return target.TryAssign(writeScope, sourceName, ShellValue.FromText(Integers.FormatNumber(value)), AssignmentOperator.Replace)
                        ? null : new ArithmeticError(ArithmeticErrorKind.InvalidTarget, target.Name.ToValue());
                }
            }

            async ValueTask<ArithmeticTarget> SelectAsync(ArithmeticReference selected, ArithmeticReferenceUse use)
            {
                var reference = selected.Syntax;
                var cyclic = false;
                if (reference.Kind != VariableReferenceKind.Name && scope.Variables.TryGetNameref(reference.Name, out _)
                    && !scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out var repeated))
                {
                    // Lookup precedes selector effects, which may break the cycle themselves.
                    cyclic = true;
                    if (repeated.Name == reference.Name)
                        await ReportCycleAsync(reference.Name, 2).ConfigureAwait(false);
                }
                var target = await ResolveReferenceAsync(reference,
                    use == ArithmeticReferenceUse.Read ? ReferenceUse.ArithmeticRead : ReferenceUse.ArithmeticAssign,
                    scope, ExpansionPurpose.ArithmeticSource, null, scope.Variables, scope.Variables, selected.ExpandedSubscript)
                    .ConfigureAwait(false);
                ArithmeticElementFallback? fallback = null;
                if (reference.Kind != VariableReferenceKind.Name && target is { Name.IsEmpty: true })
                {
                    // Retain the selected index even when a cyclic write must create its own array.
                    if (cyclic && target is ResolvedVariableReference.Indexed indexed)
                        fallback = new ArithmeticElementFallback.Circular(new ResolvedVariableReference.Indexed(reference.Name, indexed.Index));
                    else
                    {
                        scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out var binding);
                        fallback = new ArithmeticElementFallback.Invalid(binding.Reference.Kind == VariableReferenceKind.Name
                            ? ShellValue.Empty : binding.Reference.ToValue(scope.Effects.ExpansionBudget.Words));
                    }
                    target = null;
                }
                return new ArithmeticTarget(target, use, fallback);
            }

            async Task ReportCycleAsync(BindingName name, int count)
            {
                for (var index = 0; index < count && scope.CanExpand; index++)
                    await scope.Effects.ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
            }

            bool AssignExpansion(VariableReference reference, ResolvedVariableReference target, ShellValue value)
            {
                // Whole-name writes retain scalar-prefix ownership and preserve the caller's array.
                // Element namerefs use their already selected location, without replaying the selector.
                if (reference.Kind == VariableReferenceKind.Name
                    && scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out var binding)
                    && binding.Reference.Kind == VariableReferenceKind.Name)
                    return TrySetVariable(reference.Name, value, scope);
                return target.TryAssign(scope, reference.Name, value, AssignmentOperator.Replace);
            }
        }

        Task ReportAsync(ArithmeticError error) => failureMode switch
        {
            ArithmeticFailureMode.Diagnostic => scope.Effects.ReportExpansionDiagnosticAsync(error.Subject, ShellValue.FromText(error.Message)),
            _ => scope.Effects.ReportFatalExpansionAsync(error.Subject, ShellValue.FromText(error.Message),
                failureMode == ArithmeticFailureMode.Assignment ? ExpansionFailureKind.Assignment : ExpansionFailureKind.Arithmetic),
        };
    }

    // Missing read locations are selected again for writing unless selection retained a fallback.
    private readonly record struct ArithmeticTarget(
        ResolvedVariableReference? Location, ArithmeticReferenceUse Use, ArithmeticElementFallback? Fallback);

    private abstract record ArithmeticElementFallback
    {
        private ArithmeticElementFallback() { }

        internal sealed record Circular(ResolvedVariableReference.Indexed Target) : ArithmeticElementFallback;
        internal sealed record Invalid(ShellValue Subject) : ArithmeticElementFallback;
    }

    internal enum ArithmeticFailureMode { Diagnostic, Fatal, Assignment }
}

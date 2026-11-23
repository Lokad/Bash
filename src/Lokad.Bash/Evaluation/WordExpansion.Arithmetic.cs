using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    /// <summary>Expands direct selectors before numeric writes, retaining their source and literal result.</summary>
    internal static Task<ArithmeticOperand?> PrepareArithmeticAsync(
        ShellSource source, ScopedEvaluation scope, ExpansionPurpose purpose) =>
        PrepareArithmeticAsync(source, null, scope, purpose, null);

    internal static Task<ArithmeticOperand?> PrepareArithmeticAsync(
        ArithmeticSource syntax, ScopedEvaluation scope, ExpansionPurpose purpose) =>
        PrepareArithmeticAsync(syntax.Source, syntax, scope, purpose, null);

    private static async Task<ArithmeticOperand?> PrepareArithmeticAsync(
        ShellSource source, ArithmeticSource? syntax, ScopedEvaluation scope, ExpansionPurpose purpose,
        ShellScanContext? scanContext)
    {
        scope.Effects.CancellationToken.ThrowIfCancellationRequested();
        if (!scope.CanExpand)
            return null;
        if (syntax?.DirectOperand is { } direct)
            return direct;
        if (source.Length == 0)
            return ArithmeticOperand.FromValue(ShellValue.Empty);
        var budget = scope.Effects.Memory;
        // Bash expands the whole arithmetic word if any of these bytes require expansion.
        var expandSubscripts = source.AsSpan().IndexOfAny("$`~\u0001"u8) >= 0;
        scanContext ??= CreateExpansionScanContext(scope);
        var word = Tokenizer.ReadArithmeticWord(source, GetOperandSyntax(purpose), scanContext);
        using var bytes = new ByteMemoryBuilder(0, budget.Workspace);
        Dictionary<int, ArithmeticOperand.Subscript>? subscripts = null;
        var firstPart = 0;
        for (var partIndex = 0; partIndex < word.Parts.Count && scope.CanExpand; partIndex++)
        {
            if (word.Parts[partIndex] is not SourceWordPart { Kind: WordPartKind.ArithmeticSubscript } selectorPart)
                continue;

            if (firstPart != partIndex)
                AppendFields((await ExpandAsync(word, purpose, firstPart, partIndex).ConfigureAwait(false)).Build());
            if (!scope.CanExpand)
                break;
            firstPart = partIndex + 1;
            var selector = selectorPart.Source;
            var subscript = selector.Slice(1, selector.Length - 2);
            var selectorPurpose = expandSubscripts || IsAssociativeTarget() ? ExpansionPurpose.Scalar : purpose;
            var selectorWord = subscript.Length == 0 ? new Word([])
                : Tokenizer.ReadExpansionWord(subscript, GetOperandSyntax(selectorPurpose), ShellQuoteKind.None, scanContext);
            var builder = await ExpandAsync(selectorWord, selectorPurpose, 0, selectorWord.Parts.Count)
                .ConfigureAwait(false);
            if (!scope.CanExpand)
                break;
            var expanded = ExpandedWord.JoinValuesWithSpaces(builder.Build(), budget);
            budget.Charge(256);
            subscripts ??= new Dictionary<int, ArithmeticOperand.Subscript>();
            subscripts.Add(bytes.Length, new ArithmeticOperand.Subscript(subscript,
                new ExpandedArraySubscript(expanded, QuoteForArithmetic: expandSubscripts)));
            bytes.Append((byte)'[').Append(expanded.Bytes).Append((byte)']');
        }
        var tail = scope.CanExpand && firstPart != word.Parts.Count
            ? (await ExpandAsync(word, purpose, firstPart, word.Parts.Count).ConfigureAwait(false)).Build()
            : [];
        if (!scope.CanExpand)
            return null;
        ShellValue value;
        if (subscripts == null && tail is [{ Segments: [var segment] }])
        {
            // The expanded segment already retains its admitted backing.
            value = segment.Content;
            if (value.Bytes.Length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
        }
        else
        {
            AppendFields(tail);
            value = ShellValue.FromBytes(bytes.DetachSnapshot());
        }
        var lexical = syntax?.Syntax != null && value.Bytes.Span.SequenceEqual(source.AsSpan()) ? syntax.Syntax : null;
        return new ArithmeticOperand(value, subscripts, lexical);

        void AppendFields(IReadOnlyList<ExpandedWord> fields)
        {
            if (fields.Count == 0)
                return;

            var length = ExpandedWord.GetJoinedValueLength(fields);
            if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes || length > int.MaxValue - bytes.Length)
                throw new ByteMemoryBuilder.LimitExceededException();

            ExpandedWord.CopyJoinedValueTo(fields, bytes.AppendSpan((int)length));
        }

        async Task<ExpandedWordBuilder> ExpandAsync(Word operand, ExpansionPurpose operandPurpose, int start, int end)
        {
            var builder = new ExpandedWordBuilder(scope.Variables, budget, operandPurpose, ImplicitEmptyPolicy.Preserve, scope.Effects.CancellationToken);
            await ExpandWordPartsAsync(operand, scope, builder, allowWordSplitting: false, TildeExpansion.None, start, end)
                .ConfigureAwait(false);
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

    private static ShellValue QuoteExpandedArithmeticIndex(ShellValue value, ScopedEvaluation scope)
    {
        var bytes = value.Bytes.Span;
        var first = bytes.IndexOfAny("[]'~"u8);
        if (first < 0)
            return value;

        var cancellationToken = scope.Effects.CancellationToken;
        long length = bytes.Length;
        foreach (var b in bytes[first..])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NeedsEscape(b))
                length++;
        }
        if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        scope.Effects.Memory.Charge(checked(32L + length));
        var escaped = new byte[(int)length];
        bytes[..first].CopyTo(escaped);
        var position = first;
        foreach (var b in bytes[first..])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NeedsEscape(b))
                escaped[position++] = (byte)'\\';
            escaped[position++] = b;
        }
        return ShellValue.FromBytes(escaped);

        // These escapes survive the arithmetic quote-removal pass.
        static bool NeedsEscape(byte b) => b is (byte)'[' or (byte)']' or (byte)'\'' or (byte)'~';
    }

    /// <summary>Evaluates through shared reference selection without recursive asynchronous callback stacks.</summary>
    internal static Task<long?> EvaluateArithmeticAsync(
        ArithmeticOperand expression, ScopedEvaluation scope, ArithmeticFailureMode failureMode) =>
        EvaluateArithmeticAsync(expression, scope, failureMode, null, ShellValue.Empty);

    internal static Task<long?> EvaluateArithmeticAsync(
        ArithmeticOperand expression, ScopedEvaluation scope, ArithmeticFailureMode failureMode, ShellValue command) =>
        EvaluateArithmeticAsync(expression, scope, failureMode, null, command);

    internal static Task<long?> EvaluateArithmeticAsync(
        ArithmeticOperand expression, ScopedEvaluation scope, ArithmeticFailureMode failureMode,
        Func<VariableReference, ResolvedVariableReference, ShellValue, bool>? assignOutput, ShellValue command)
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
            var result = await ArithmeticEvaluation.EvaluateAsync<ArithmeticTarget>(expression, BindAsync, WriteAsync,
                scope.Effects.Memory.Workspace, scope.Effects.CancellationToken).ConfigureAwait(false);
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
                long value = 0;
                if (use == ArithmeticReferenceUse.Read && target.Location != null
                    && target.Location.TryRead(scope.Variables, out var stored) && !stored.IsEmpty)
                {
                    if (!ArithmeticEvaluation.TryParseIntegerLiteral(stored.Bytes.Span,
                            scope.Effects.CancellationToken, out value))
                    {
                        // Values are arithmetic expressions, not fresh shell source. Share the
                        // existing numeric pump so recursive reads never grow a callback stack.
                        scope.Effects.Memory.Charge(checked(256L + 4L * stored.Bytes.Length));
                        var nested = await EvaluateArithmeticAsync(ArithmeticOperand.FromValue(stored),
                            scope, failureMode, assignOutput, command).ConfigureAwait(false);
                        if (nested is not { } number)
                        {
                            referenceFailureReported = true;
                            return new ArithmeticBinding<ArithmeticTarget>.Failure(
                                new ArithmeticError(ArithmeticErrorKind.InvalidExpression, stored));
                        }
                        value = number;
                    }
                }
                return new ArithmeticBinding<ArithmeticTarget>.Bound(target, value);
            }

            async ValueTask<ArithmeticError?> WriteAsync(ArithmeticReference selected, ArithmeticTarget binding, long value)
            {
                var reference = selected.Syntax;
                // Unlike a direct element operand, a whole-name nameref selects again for its write.
                var resolved = default(VariableBinding);
                var hasBinding = reference.Kind != VariableReferenceKind.Name
                    || scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out resolved);
                var elementNameref = reference.Kind == VariableReferenceKind.Name && hasBinding
                    && resolved.Reference.Kind != VariableReferenceKind.Name;
                var circularNameref = reference.Kind == VariableReferenceKind.Name
                    && !hasBinding;
                if (binding.Fallback == null && binding.Use == ArithmeticReferenceUse.Read
                    && (binding.Location == null || elementNameref || circularNameref))
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
                var formatted = ShellValue.FromInteger(value);
                // Builtin output selects its local fallback before checking that destination's readonly state.
                if (assignOutput != null && assignOutput(reference, target, formatted))
                    return null;
                var readOnly = scope.Variables.IsReadonly(reference.Name) || (target.ReadOwner ?? scope.Variables).IsReadonly(target.Name);
                if (assignOutput == null && !readOnly && AssignExpansion(reference, target, formatted))
                    return null;
                var namerefFailure = scope.Variables.GetNamerefAssignmentFailure(
                    reference.Name, target.Name, formatted, scope.Effects.Memory);
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
                                destination.SetResolvedValuePreservingArray(target.Name, scalar, scope.Effects.Memory);
                        }
                        else if (!destination.TryDeclareResolvedArray(target.Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue))
                            return new ArithmeticError(ArithmeticErrorKind.InvalidTarget, target.Name.ToValue());
                        if (forwarded != null && !ReferenceEquals(forwarded, destination)
                            && !forwarded.TryDeclareResolvedArray(target.Name, ArrayKind.Indexed, ScalarArrayPromotion.DiscardValue))
                            return new ArithmeticError(ArithmeticErrorKind.InvalidTarget, target.Name.ToValue());
                    }

                    var writeScope = ReferenceEquals(destination, variables) ? scope
                        : scope with { Variables = destination, ExpansionWriteTarget = null, SelectorBindings = null };
                    return target.TryPublishValue(writeScope, sourceName, ShellValue.FromInteger(value))
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
                    scope, ExpansionPurpose.ArithmeticSource, null, scope.Variables, scope.Variables, selected.ExpandedSubscript, command)
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
                            ? ShellValue.Empty : binding.Reference.ToValue(scope.Effects.Memory));
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
                if (target.ReadOwner is { } owner && !ReferenceEquals(owner, scope.Variables))
                    return target.TryPublishValue(scope with { Variables = owner, ExpansionWriteTarget = null, SelectorBindings = null },
                        reference.Name, value);
                // Whole-name writes retain scalar-prefix ownership and preserve the caller's array.
                // Element namerefs use their already selected location, without replaying the selector.
                if (reference.Kind == VariableReferenceKind.Name
                    && scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out var binding)
                    && binding.Reference.Kind == VariableReferenceKind.Name)
                    return TrySetVariable(reference.Name, value, scope).HasValue;
                return target.TryPublishValue(scope, reference.Name, value);
            }
        }

        Task ReportAsync(ArithmeticError error)
        {
            var message = ShellValue.FromText(error.Message);
            var showCommand = !command.IsEmpty && (error.Token.HasValue || failureMode == ArithmeticFailureMode.IntegerAssignment);
            ReadOnlySpan<ShellValue> subject = showCommand
                ? [command, ShellValue.FromText(": "), error.Subject]
                : [error.Subject];
            ReadOnlySpan<ShellValue> detail = error.Token is { } token
                ? [message, ShellValue.FromText(" (error token is \""), token, ShellValue.FromText("\")")]
                : [message];
            if (failureMode == ArithmeticFailureMode.Diagnostic)
                return scope.Effects.ReportExpansionDiagnosticAsync(subject, detail);
            if (failureMode == ArithmeticFailureMode.IntegerAssignment)
                return scope.Effects.ReportFatalExpansionAsync(subject, detail, ExpansionFailureKind.IntegerAssignment);
            var failure = failureMode == ArithmeticFailureMode.ArrayIndex
                ? ExpansionFailureKind.ArrayIndex : ExpansionFailureKind.Arithmetic;
            return scope.Effects.ReportUnitExpansionFailureAsync(subject, detail, scope, failure);
        }
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

    internal enum ArithmeticFailureMode { Diagnostic, Expansion, ArrayIndex, IntegerAssignment }
}

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal enum CaseTransformation { Lowercase, Uppercase, Capitalize }

internal static partial class WordExpansion
{
    // Array sources keep the selected owner. Some operators consume it after applying operand effects.
    private abstract record ParameterSource
    {
        internal sealed record Indexed(IndexedArray Array) : ParameterSource;
        internal sealed record Associative(AssociativeArray Array) : ParameterSource;
        internal sealed record LiteralSlice(ParameterSource Source, long Offset, long? Length) : ParameterSource;
        // A direct computed slice resolves this binding at its reached bounds.
        internal sealed record DeferredSlice(VariableBinding Binding) : ParameterSource;
        internal sealed record Positional(IReadOnlyList<ShellValue> Arguments, ShellValue ArgumentZero) : ParameterSource
        {
            internal IEnumerable<ShellValue> ValuesFrom(long first, int count)
            {
                if (count == 0)
                    yield break;
                if (first == 0)
                {
                    yield return ArgumentZero;
                    first = 1;
                    count--;
                }

                // The positional window supports direct indexing after shift.
                var start = (int)(first - 1);
                var end = (int)Math.Min(start + (long)count, Arguments.Count);
                for (var index = start; index < end; index++)
                    yield return Arguments[index];
            }
        }
        internal sealed record Keys(IEnumerable<ShellValue> Values, int KeyCount) : ParameterSource;

        public int Count => this switch
        {
            Indexed indexed => indexed.Array.Count,
            Associative associative => associative.Array.Count,
            Positional positional => positional.Arguments.Count,
            Keys keys => keys.KeyCount,
            LiteralSlice slice => slice.Source.Count,
            _ => throw new InvalidOperationException("Unknown parameter source.")
        };

        public IEnumerable<ShellValue> Fields => this switch
        {
            Indexed indexed => indexed.Array.PresentValues,
            Associative associative => associative.Array.PresentValues,
            Positional positional => positional.Arguments,
            Keys keys => keys.Values,
            LiteralSlice slice => slice.Source.Fields,
            _ => throw new InvalidOperationException("Unknown parameter source.")
        };
    }

    private static async Task ExpandBracedParameterAsync(
        ShellSource content,
        ShellScanContext scanContext,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext,
        bool assignmentTildes,
        ShellSource segment,
        Word? originalWord,
        ParameterClosure closure)
    {
        var parsed = ParseBracedParameterExpression(content);
        if (parsed.Kind is BracedParameterOperatorKind.Invalid or BracedParameterOperatorKind.IncompleteSubscript)
        {
            if (parsed.Kind == BracedParameterOperatorKind.IncompleteSubscript && quoteContext == ExpansionQuoteContext.Unquoted)
                await ReportParameterWordSyntaxAsync(originalWord, segment, missingBrace: false, scope).ConfigureAwait(false);
            else
                await ReportBadSubstitutionAsync().ConfigureAwait(false);
            return;
        }
        var transform = parsed.Kind == BracedParameterOperatorKind.Transform && parsed.Word.Length == 1 ? parsed.Word[0] : (byte)0;
        var inspection = transform is (byte)'a' or (byte)'A' or (byte)'K' or (byte)'k';
        var removesPattern = parsed.Kind is BracedParameterOperatorKind.RemovePrefixShortest
            or BracedParameterOperatorKind.RemovePrefixLongest or BracedParameterOperatorKind.RemoveSuffixShortest
            or BracedParameterOperatorKind.RemoveSuffixLongest;
        if (transform == (byte)'P')
        {
            await ReportSubstitutionFailureAsync("unsupported parameter operator", ExpansionFailureKind.Parameter)
                .ConfigureAwait(false);
            return;
        }
        // Only unquoted source arithmetic carries its ANSI-C quote rules into indices and bounds.
        var arithmeticPurpose = output.Purpose == ExpansionPurpose.ArithmeticSource
            && quoteContext == ExpansionQuoteContext.Unquoted
                ? ExpansionPurpose.ArithmeticSource : ExpansionPurpose.Arithmetic;
        ShellSource? indirectName = null;
        ParameterValue? initialRemovalValue = null;
        var parameter = await ReadParameterAsync().ConfigureAwait(false);
        if (!scope.CanExpand)
            return;
        if (closure != ParameterClosure.Closed)
        {
            await ReportParameterWordSyntaxAsync(originalWord, segment, closure == ParameterClosure.MissingAfterOperator, scope,
                    quoteContext == ExpansionQuoteContext.Unquoted ? scanContext : null)
                .ConfigureAwait(false);
            return;
        }
        var positionalStar = parsed.Modifier == ParameterModifier.None && parameter.Kind == VariableReferenceKind.Star
            && parameter.RangeSource is ParameterSource.Positional;
        var starSplit = PositionalStarSplit.Default;
        if (positionalStar)
        {
            starSplit = parsed.Kind is BracedParameterOperatorKind.Substring or BracedParameterOperatorKind.Replace
                or BracedParameterOperatorKind.Transform or BracedParameterOperatorKind.UpperFirst
                or BracedParameterOperatorKind.UpperAll or BracedParameterOperatorKind.LowerFirst
                or BracedParameterOperatorKind.LowerAll
                ? PositionalStarSplit.WhenPresent : PositionalStarSplit.EvenWhenEmpty;
        }
        if (inspection)
        {
            ExpandInspectionTransform(transform, parameter, scope, output, allowWordSplitting, globQuoted, quoteContext, starSplit);
            return;
        }

        switch (parsed.Kind)
        {
            case BracedParameterOperatorKind.None:
            case BracedParameterOperatorKind.NamesAt:
            case BracedParameterOperatorKind.NamesStar:
                Emit(parameter.SelectedFields);
                return;
            case BracedParameterOperatorKind.Substring:
                if (parsed.Word.IsEmpty)
                {
                    await ReportSubstitutionFailureAsync("bad substitution", ExpansionFailureKind.Arithmetic).ConfigureAwait(false);
                    return;
                }
                var nameLength = parsed.Name.Length + (parsed.Modifier == ParameterModifier.Indirect ? 1 : 0);
                await ExpandSubstringAsync(parsed.Word, content[..nameLength].ToValue(), parameter, scope, output,
                        allowWordSplitting, globQuoted, quoteContext, arithmeticPurpose, starSplit)
                    .ConfigureAwait(false);
                return;
            case BracedParameterOperatorKind.Replace:
            case BracedParameterOperatorKind.UpperFirst:
            case BracedParameterOperatorKind.UpperAll:
            case BracedParameterOperatorKind.LowerFirst:
            case BracedParameterOperatorKind.LowerAll:
            case BracedParameterOperatorKind.Transform:
                await EmitValueOperatorAsync().ConfigureAwait(false);
                return;
            case BracedParameterOperatorKind.UseDefault:
                if (!IsAvailable())
                {
                    await AppendParameterWordAsync().ConfigureAwait(false);
                }
                else
                {
                    Emit(parameter.SelectedFields);
                }

                return;
            case BracedParameterOperatorKind.UseAlternate:
                if (IsAvailable())
                {
                    await AppendParameterWordAsync().ConfigureAwait(false);
                }
                else
                {
                    // An unavailable @ aggregate retains zero versus one empty field.
                    if (parameter.Kind == VariableReferenceKind.At)
                        Emit(parameter.SelectedFields);
                    else if (parameter.Kind == VariableReferenceKind.Star && parameter.IsSet
                             && quoteContext == ExpansionQuoteContext.DoubleQuoted)
                        output.AppendEmptyAggregateField();
                    else if (positionalStar && allowWordSplitting && output.Purpose == ExpansionPurpose.Normal)
                        Emit([]);
                    else
                        output.AppendExpansion(ShellValue.Empty, allowWordSplitting, globQuoted);
                }

                return;
            case BracedParameterOperatorKind.AssignDefault:
                if (!IsAvailable())
                {
                    var special = GetSpecialParameterKind(parsed.Name.AsSpan());
                    var assignmentPrefix = parsed.Modifier switch
                    {
                        ParameterModifier.None when special != SpecialParameterKind.None => "$",
                        ParameterModifier.Indirect when special is SpecialParameterKind.At or SpecialParameterKind.Star => "$!",
                        _ => null,
                    };
                    if (assignmentPrefix != null)
                    {
                        await ReportParameterTargetFailureAsync([ShellValue.FromText(assignmentPrefix), parsed.Name.ToValue()],
                            "cannot assign in this way").ConfigureAwait(false);
                        return;
                    }
                    var assignmentPurpose = output.Purpose switch
                    {
                        ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource => output.Purpose,
                        _ when quoteContext == ExpansionQuoteContext.HereDocument => ExpansionPurpose.Scalar,
                        _ => ExpansionPurpose.Assignment,
                    };
                    var operand = await ExpandOperatorWordAsync(
                            parsed.Word, scanContext, scope, quoteContext, assignmentPurpose,
                            ImplicitEmptyPolicy.Preserve, TildeExpansion.AssignDefault,
                            quoteContext == ExpansionQuoteContext.HereDocument ? output : null)
                        .ConfigureAwait(false);
                    if (!scope.CanExpand)
                        return;

                    var budget = scope.Effects.Memory;
                    var expanded = ExpandedWord.JoinValuesWithSpaces(operand?.Build() ?? [], budget);
                    // Indirect defaults can assign bare names only, after the RHS has expanded.
                    if (parsed.Modifier == ParameterModifier.Indirect
                        && parameter.Source is not { Kind: VariableReferenceKind.Name })
                    {
                        if (indirectName is { } invalidTarget)
                            await ReportParameterTargetFailureAsync([invalidTarget.ToValue()], "invalid variable name").ConfigureAwait(false);
                        else
                        {
                            // An absent argument or empty aggregate supplied no name.
                            await ReportParameterTargetFailureAsync([ShellValue.FromText("!"), parsed.Name.ToValue()],
                                "invalid indirect expansion").ConfigureAwait(false);
                        }
                        return;
                    }
                    var target = parameter.Target;
                    var elementNameref = parameter.Source is { Kind: VariableReferenceKind.Name } namedReference
                        && scope.Variables.TryResolveBinding(namedReference.Name, selectOwner: null, out var binding)
                        && binding.Reference.Kind != VariableReferenceKind.Name;
                    if (parameter.Source is { } reference && (reference.Kind != VariableReferenceKind.Name || elementNameref))
                    {
                        // A default write repeats selection after its RHS, for either array kind.
                        target = await ResolveReferenceAsync(reference, ReferenceUse.Assign, scope, arithmeticPurpose).ConfigureAwait(false);
                    }
                    if (!scope.CanExpand)
                        return;
                    var action = parameter.Source is { Kind: VariableReferenceKind.Name } assignmentSource
                        ? await PrepareNamerefAssignmentAsync(assignmentSource.Name, expanded, AssignmentOperator.Replace, scope, ExpansionFailureKind.NamerefDefault).ConfigureAwait(false)
                        : new NamerefAssignmentResult(NamerefAssignmentAction.Assign, expanded);
                    if (action.Action == NamerefAssignmentAction.Fail)
                        return;
                    ShellValue? assigned = action.Value;
                    if (action.Action == NamerefAssignmentAction.Assign)
                    {
                        if (!elementNameref && parameter.Source is { Kind: VariableReferenceKind.Name } named)
                        {
                            var resolved = scope.Variables.ResolveNamerefName(named.Name);
                            if (scope.Variables.IsIntegerBinding(resolved) && !scope.Variables.IsReadonly(resolved))
                                assigned = await EvaluateIntegerAssignmentAsync(expanded, null, scope, ShellValue.Empty).ConfigureAwait(false);
                            if (assigned is { } value && scope.CanExpand)
                                assigned = !named.Name.IsEmpty && !scope.Variables.IsReadonlyWriteTarget(named.Name, out var writableBinding)
                                    ? TrySetWritableVariable(named.Name, writableBinding, value, scope) : null;
                        }
                        else if (target != null && parameter.Source is { } source)
                        {
                            target = target.SelectPrefixElementWrite(source, scope);
                            var destination = target.ReadOwner ?? scope.Variables;
                            assigned = await target.AssignValueAsync(scope, destination, source.Name, expanded,
                                AssignmentOperator.Replace, ShellValue.Empty, DeclarationWrite.Ordinary).ConfigureAwait(false);
                            if (elementNameref && assigned.HasValue)
                            {
                                destination.TryGetExpansionValue(target.Name, out var arrayScalar);
                                assigned = arrayScalar;
                            }
                        }
                        else
                            assigned = null;
                    }
                    if (assigned is { } stored && scope.CanExpand)
                    {
                        output.AppendExpansion(stored, allowWordSplitting, globQuoted);
                    }
                    else if (scope.CanExpand)
                    {
                        var subject = parameter.Source is { Kind: VariableReferenceKind.Element } element
                            ? element.Name.ToValue() : target?.Name.ToValue() ?? parsed.Name.ToValue();
                        await scope.Effects.ReportParameterDefaultFailureAsync([subject], [ShellValue.FromText("readonly variable")], scope)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    Emit(parameter.SelectedFields);
                }

                return;
            case BracedParameterOperatorKind.ErrorIfUnset:
                if (!IsAvailable())
                {
                    ShellValue message;
                    if (parsed.Word.IsEmpty)
                        message = ShellValue.FromText(parsed.UsesColon ? "parameter null or not set" : "parameter not set");
                    else
                    {
                        var messageWord = await ExpandExpressionAsync(parsed.Word, scope, ExpansionPurpose.ErrorMessage, allowWordSplitting: true)
                            .ConfigureAwait(false);
                        message = messageWord.ToValue(scope.Effects.Memory);
                    }
                    if (scope.CanExpand)
                        await ReportRequiredParameterFailureAsync(message)
                            .ConfigureAwait(false);
                }
                else
                {
                    Emit(parameter.SelectedFields);
                }

                return;
            case BracedParameterOperatorKind.RemovePrefixShortest:
            case BracedParameterOperatorKind.RemovePrefixLongest:
            case BracedParameterOperatorKind.RemoveSuffixShortest:
            case BracedParameterOperatorKind.RemoveSuffixLongest:
            {
                if (!ReachesRemoval(initialRemovalValue ?? parameter))
                {
                    Emit(parameter.SelectedFields);
                    return;
                }

                var operand = await ExpandOperatorWordAsync(
                        parsed.Word, scanContext, scope, ExpansionQuoteContext.Unquoted, ExpansionPurpose.Scalar,
                        ImplicitEmptyPolicy.Preserve, TildeExpansion.Leading, enclosingWord: null)
                    .ConfigureAwait(false);
                if (!scope.CanExpand)
                    return;
                var budget = scope.Effects.Memory;
                var pattern = ShellPattern.FromWords(operand?.Build() ?? [], budget, scope.Effects.CancellationToken);
                EmitTransformedFields(RemoveField);
                return;

                ShellValue RemoveField(ShellValue value, MemoryCapacity scratch)
                {
                    var slice = ApplyPatternRemoval(value, pattern, parsed.Kind, scope.Effects.CancellationToken);
                    if (slice.Length == value.Bytes.Length)
                        return value;
                    if (slice.IsEmpty)
                        return ShellValue.Empty;
                    // Own the retained result independently of a larger input backing buffer.
                    scratch.Grow(MemoryCapacity.ArrayBackingBytes<byte>(slice.Length));
                    return ShellValue.FromBytes(slice.ToArray());
                }
            }
            default:
                throw new InvalidOperationException($"Unexpected braced parameter operator '{parsed.Kind}'.");
        }

        bool ReachesRemoval(ParameterValue value) =>
            (!parsed.Word.IsEmpty || parsed.Kind is BracedParameterOperatorKind.RemovePrefixLongest
                or BracedParameterOperatorKind.RemoveSuffixLongest)
            && value.IsSet && (value.Kind is VariableReferenceKind.At or VariableReferenceKind.Star || !value.Value.IsEmpty);

        bool IsAvailable()
        {
            if (!parameter.IsSet || !parsed.UsesColon)
                return parameter.IsSet;

            var count = parameter.FieldCount;
            if (count == 0)
                return false;
            if (count == 1 && parameter.Fields.Count == 1)
                return !parameter.Fields[0].IsEmpty;
            if (count > 1 && (parameter.Kind != VariableReferenceKind.Star || !globQuoted
                || quoteContext == ExpansionQuoteContext.HereDocument && parameter.RangeSource is ParameterSource.Positional
                || !FieldSeparators.FromVariables(scope.Variables).IsEmpty))
                return true;

            foreach (var field in parameter.SelectedFields)
            {
                scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                if (!field.IsEmpty)
                    return true;
            }
            return false;
        }

        Task ReportRequiredParameterFailureAsync(ShellValue message)
        {
            ReadOnlySpan<ShellValue> name = parsed.Modifier == ParameterModifier.Indirect
                ? [ShellValue.FromText("!"), parsed.Name.ToValue()] : [parsed.Name.ToValue()];
            return quoteContext == ExpansionQuoteContext.HereDocument
                ? scope.Effects.ReportHereDocumentParameterFailureAsync(name, [message], scope, ExpansionFailureKind.RequiredParameter)
                : scope.Effects.ReportParameterExpansionFailureAsync(name, [message], scope, ExpansionFailureKind.RequiredParameter);
        }

        async Task AppendParameterWordAsync()
        {
            if (quoteContext == ExpansionQuoteContext.HereDocument)
            {
                await ExpandHereDocumentOperandAsync(parsed.Word, scope, output)
                    .ConfigureAwait(false);
                return;
            }

            var purpose = output.Purpose switch
            {
                ExpansionPurpose.IndexedCompound => ExpansionPurpose.IndexedCompoundOperand,
                ExpansionPurpose.ParameterReplacement => ExpansionPurpose.Scalar,
                _ => output.Purpose,
            };
            var emptyPolicy = purpose switch
            {
                ExpansionPurpose.Scalar when output.EmptyPolicy == ImplicitEmptyPolicy.PreserveAggregate => output.EmptyPolicy,
                ExpansionPurpose.Scalar or ExpansionPurpose.ConditionalPattern or ExpansionPurpose.CasePattern
                    or ExpansionPurpose.Assignment or ExpansionPurpose.AssociativeCompound
                    or ExpansionPurpose.IndexedCompoundOperand or ExpansionPurpose.Arithmetic
                    or ExpansionPurpose.ArithmeticSource => ImplicitEmptyPolicy.Preserve,
                _ => ImplicitEmptyPolicy.Remove,
            };
            var operand = await ExpandOperatorWordAsync(
                    parsed.Word, scanContext, scope, quoteContext, purpose,
                    emptyPolicy, assignmentTildes ? TildeExpansion.Assignment : TildeExpansion.Leading,
                    enclosingWord: output)
                .ConfigureAwait(false);
            if (scope.CanExpand)
                output.AppendSelectedOperand(operand, allowWordSplitting, globQuoted, quoteContext);
        }

        BracedParameterExpression ParseBracedParameterExpression(ShellSource content)
        {
            var modifier = ParameterModifier.None;
            var start = 0;
            if (content.Length > 1 && content[0] is (byte)'#' or (byte)'!'
                && (VariableNames.IsNamePart((char)content[1]) || content.Length == 2 && IsSpecial(content[1])
                    || content[0] == (byte)'!' && content[1] is (byte)'@' or (byte)'*' or (byte)'#' or (byte)'?'))
            {
                modifier = content[0] == (byte)'#' ? ParameterModifier.Length : ParameterModifier.Indirect;
                start = 1;
            }
            var end = ReadPrimaryEnd(start);
            if (end < 0)
                return new(ShellSource.Empty, BracedParameterOperatorKind.IncompleteSubscript, ShellSource.Empty, false, modifier);
            if (end == start)
                return Invalid();
            if (modifier == ParameterModifier.Indirect
                && GetSpecialParameterKind(content.AsSpan(start, end - start)) == SpecialParameterKind.BackgroundProcess)
                return Invalid();
            if (end == content.Length)
                return Make(BracedParameterOperatorKind.None, end, false);
            if (modifier == ParameterModifier.Length)
                return Invalid();

            var current = content[end];
            if (modifier == ParameterModifier.Indirect && end == content.Length - 1
                && current is (byte)'@' or (byte)'*' && VariableNames.IsNameStart((char)content[start]))
                return Make(current == (byte)'@' ? BracedParameterOperatorKind.NamesAt : BracedParameterOperatorKind.NamesStar,
                    content.Length, false);

            var usesColon = current == (byte)':' && end + 1 < content.Length && content[end + 1] is (byte)'-' or (byte)'+' or (byte)'=' or (byte)'?';
            var operation = usesColon ? content[end + 1] : current;
            var wordStart = end + (usesColon ? 2 : 1);
            var kind = operation switch
            {
                (byte)'-' => BracedParameterOperatorKind.UseDefault,
                (byte)'+' => BracedParameterOperatorKind.UseAlternate,
                (byte)'=' => BracedParameterOperatorKind.AssignDefault,
                (byte)'?' => BracedParameterOperatorKind.ErrorIfUnset,
                (byte)':' => BracedParameterOperatorKind.Substring,
                (byte)'/' => BracedParameterOperatorKind.Replace,
                (byte)'^' => BracedParameterOperatorKind.UpperFirst,
                (byte)',' => BracedParameterOperatorKind.LowerFirst,
                (byte)'@' => BracedParameterOperatorKind.Transform,
                (byte)'#' => BracedParameterOperatorKind.RemovePrefixShortest,
                (byte)'%' => BracedParameterOperatorKind.RemoveSuffixShortest,
                _ => BracedParameterOperatorKind.Invalid,
            };
            if (kind == BracedParameterOperatorKind.Invalid)
                return Invalid();
            if (wordStart < content.Length && content[wordStart] == operation)
            {
                kind = operation switch
                {
                    (byte)'#' => BracedParameterOperatorKind.RemovePrefixLongest,
                    (byte)'%' => BracedParameterOperatorKind.RemoveSuffixLongest,
                    (byte)'^' => BracedParameterOperatorKind.UpperAll,
                    (byte)',' => BracedParameterOperatorKind.LowerAll,
                    _ => kind,
                };
                if (operation is (byte)'#' or (byte)'%' or (byte)'^' or (byte)',')
                    wordStart++;
            }
            return Make(kind, wordStart, usesColon);

            BracedParameterExpression Make(BracedParameterOperatorKind kind, int wordStart, bool usesColon) =>
                new(content.Slice(start, end - start), kind, content.Slice(wordStart), usesColon, modifier);

            BracedParameterExpression Invalid() =>
                new(ShellSource.Empty, BracedParameterOperatorKind.Invalid, ShellSource.Empty, false, ParameterModifier.None);

            int ReadPrimaryEnd(int position)
            {
                if (position >= content.Length)
                    return position;
                var first = content[position];
                if (VariableNames.IsNameStart((char)first))
                {
                    while (++position < content.Length && VariableNames.IsNamePart((char)content[position])) { }
                    if (position < content.Length && content[position] == (byte)'[')
                        return ArraySubscriptScanner.TryScan(new ShellScanContext { SyntaxMemory = scope.Effects.Memory.Workspace },
                            content, position, out var closing) ? closing + 1 : -1;
                    return position;
                }
                if (first is >= (byte)'0' and <= (byte)'9')
                {
                    while (++position < content.Length && content[position] is >= (byte)'0' and <= (byte)'9') { }
                    return position;
                }
                return IsSpecial(first) ? position + 1 : position;
            }

            static bool IsSpecial(byte value) => value is (byte)'@' or (byte)'*' or (byte)'#' or (byte)'?' or (byte)'-' or (byte)'$' or (byte)'!';
        }

        async Task<ParameterValue> ReadParameterAsync()
        {
            var budget = scope.Effects.Memory;
            if (parsed.Kind is BracedParameterOperatorKind.NamesAt or BracedParameterOperatorKind.NamesStar)
            {
                var prefix = BindingNameExpansion.FromValue(parsed.Name.ToValue(), budget);
                List<ShellValue>? names = null;
                HashSet<BindingName>? selected = null;
                var function = scope.Context.FunctionContext;
                while (function != null && function.Locals.NonEmptySavedBindings == null)
                    function = function.Enclosing;

                // The active store has unique names. Only saved bindings can repeat them.
                using var selectionMemory = function == null ? null : new MemoryCapacity(budget.Budget, 0);
                AddNames(scope.Variables.AssignedNames);
                for (; function != null; function = function.Enclosing)
                    if (function.Locals.NonEmptySavedBindings is { } saved)
                        AddNames(saved.AssignedNames);

                names?.Sort(ShellValue.ByteOrder);
                return new ParameterValue(parsed.Kind == BracedParameterOperatorKind.NamesAt
                    ? VariableReferenceKind.At : VariableReferenceKind.Star, names is null ? [] : names, names != null, null, null);

                void AddNames(IEnumerable<BindingName> assigned)
                {
                    foreach (var name in assigned)
                    {
                        scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                        if (!name.StartsWith(prefix, budget) || selected?.Contains(name) == true)
                            continue;
                        if (selectionMemory != null)
                        {
                            budget.Charge(selectionMemory, Variables.BindingNameSelectionBytes);
                            (selected ??= []).Add(name);
                        }
                        var matches = names ??= [];
                        budget.PrepareListCapacity(matches, 1);
                        if (name.NeedsValueEncoding)
                            budget.Charge(MemoryCapacity.ArrayBackingBytes<byte>(name.ByteLength));
                        matches.Add(name.ToValue());
                    }
                }
            }

            var name = parsed.Name;
            var syntax = ReadSyntax(name, GetSpecialParameterKind(name.AsSpan()));
            if (parsed.Modifier != ParameterModifier.Indirect
                || parsed.Kind == BracedParameterOperatorKind.None && syntax.Reference is { IsAggregate: true })
                return await ReadValueAsync(syntax, parsed.Modifier).ConfigureAwait(false);

            // A bare nameref initially supplies its referent's spelling. Value operators
            // can use that referent to select an array or inspect a scalar binding.
            if (syntax.Reference is { Kind: VariableReferenceKind.Name } binding
                && scope.Variables.HasNamerefAttribute(binding.Name))
            {
                var found = scope.Variables.TryResolveBinding(binding.Name, selectOwner: null,
                    BindingResolution.LastExpansionNameref, out var terminal);
                var referent = ShellValue.Empty;
                if (found && terminal.IsArrayNameref)
                {
                    if (terminal.Owner.IndexedArrays.ContainsKey(terminal.Name))
                        referent = IndexedArrayNamerefSpelling;
                }
                else if (!found || !terminal.Owner.TryGetNameref(terminal.Name, out referent) || referent.IsEmpty)
                {
                    await ReportParameterTargetFailureAsync([name.ToValue()], "invalid indirect expansion").ConfigureAwait(false);
                    return new ParameterValue(VariableReferenceKind.Name, [], false, null, null);
                }
                var initial = new ParameterValue(VariableReferenceKind.Name, [referent], true, null, null);
                if (!ReachesOperatorSelection(initial))
                    return initial;
                if (removesPattern)
                    initialRemovalValue = initial;
                return await SelectOperatorValueAsync(
                    ReadSyntax(ShellSource.FromBytes(referent.Bytes), GetSpecialParameterKind(referent.Bytes.Span)), initial, referent)
                    .ConfigureAwait(false);
            }

            var source = await ReadValueAsync(syntax, ParameterModifier.None).ConfigureAwait(false);
            if (!scope.CanExpand)
                return source;
            if (!source.IsSet)
            {
                if (source.Kind is VariableReferenceKind.At or VariableReferenceKind.Star)
                    return new ParameterValue(VariableReferenceKind.Name, [ShellValue.Empty], false, null, null);
                if (syntax.Special == SpecialParameterKind.Positional)
                    return source;
                // A selected element can be absent while its base variable still exists.
                if (syntax.Reference is { Kind: VariableReferenceKind.Element } && source.Target is { } location
                    && (location.ReadOwner ?? scope.Variables).HasBindingOrAttributes(location.Name))
                    return source;
                await ReportParameterTargetFailureAsync([name.ToValue()], "invalid indirect expansion").ConfigureAwait(false);
                return source;
            }

            var targetName = JoinIndirectFields(source);
            var targetSource = ShellSource.FromBytes(targetName.Bytes);
            indirectName = targetSource;
            var target = ParseBracedParameterExpression(targetSource);
            var special = GetSpecialParameterKind(targetName.Bytes.Span);
            if (target.Kind != BracedParameterOperatorKind.None || target.Modifier != ParameterModifier.None
                || special == SpecialParameterKind.None
                    && (targetName.IsEmpty || !VariableNames.IsNameStart((char)targetName.Bytes.Span[0])))
            {
                await ReportParameterTargetFailureAsync([targetName], "invalid variable name").ConfigureAwait(false);
                return new ParameterValue(VariableReferenceKind.Name, [], false, null, null);
            }

            var targetSyntax = ReadSyntax(targetSource, special);
            var value = await ReadValueAsync(targetSyntax, ParameterModifier.None).ConfigureAwait(false);
            if (!scope.CanExpand || !ReachesOperatorSelection(value))
                return value;

            if (removesPattern)
                initialRemovalValue = value;
            // A source selector can mutate the first target before changing its kind.
            var initialScalar = value.RangeSource != null && syntax.Reference is { Kind: VariableReferenceKind.Element }
                ? JoinIndirectFields(value) : value.Value;
            var nextSource = await ReadValueAsync(syntax, ParameterModifier.None).ConfigureAwait(false);
            if (!scope.CanExpand)
                return value;
            if (!nextSource.IsSet)
                return new ParameterValue(VariableReferenceKind.Name, [ShellValue.Empty],
                    parsed.Kind != BracedParameterOperatorKind.Transform, null, null);
            var selectedName = JoinIndirectFields(nextSource);
            var selectedSyntax = selectedName.Equals(targetName) ? targetSyntax
                : ReadSyntax(ShellSource.FromBytes(selectedName.Bytes), GetSpecialParameterKind(selectedName.Bytes.Span));
            return await SelectOperatorValueAsync(selectedSyntax, value, initialScalar).ConfigureAwait(false);

            bool ReachesOperatorSelection(ParameterValue initial) => parsed.Kind switch
            {
                BracedParameterOperatorKind.Transform => initial.IsSet || transform is (byte)'a' or (byte)'A',
                BracedParameterOperatorKind.Substring => initial.IsSet && !parsed.Word.IsEmpty,
                BracedParameterOperatorKind.Replace or BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.UpperAll
                    or BracedParameterOperatorKind.LowerFirst or BracedParameterOperatorKind.LowerAll => initial.IsSet,
                _ => removesPattern && ReachesRemoval(initial),
            };

            ShellValue JoinIndirectFields(ParameterValue source)
            {
                if (source.Kind is not (VariableReferenceKind.At or VariableReferenceKind.Star))
                    return source.Value;
                var joined = new ExpandedWordBuilder(scope.Variables, budget, ExpansionPurpose.Scalar, ImplicitEmptyPolicy.Preserve, scope.Effects.CancellationToken);
                var fields = source.SelectedFields;
                // Positional @ joins with spaces; array @ also uses spaces when IFS is empty.
                if (source.Kind == VariableReferenceKind.At
                    && (source.RangeSource is ParameterSource.Positional || FieldSeparators.FromVariables(scope.Variables).IsEmpty))
                    joined.AppendFields(fields, allowWordSplitting: false, globQuoted: true, ExpansionQuoteContext.DoubleQuoted);
                else
                    joined.AppendJoinedFields(fields, allowWordSplitting: false, globQuoted: true);
                return ExpandedWord.JoinValuesWithSpaces(joined.Build(), budget);
            }

            async Task<ParameterValue> SelectOperatorValueAsync(ParameterReadSyntax selected, ParameterValue initial, ShellValue initialScalar)
            {
                VariableBinding? selectedBinding = null;
                var useSelectedValue = selected.Special is SpecialParameterKind.At or SpecialParameterKind.Star
                    || selected.Reference is { Kind: VariableReferenceKind.Element, Subscript.IsEmpty: false };
                if (!useSelectedValue && selected.Reference is { Kind: not VariableReferenceKind.Element } reference)
                {
                    if (scope.Variables.TryResolveBinding(reference.Name, selectOwner: null, out var binding)
                        && binding.Reference.Kind == VariableReferenceKind.Name && binding.Owner.HasBinding(binding.Name))
                    {
                        selectedBinding = binding;
                        useSelectedValue = binding.Owner.IndexedArrays.ContainsKey(binding.Name)
                            || binding.Owner.AssociativeArrays.ContainsKey(binding.Name);
                    }
                }
                if (!useSelectedValue)
                    return new ParameterValue(VariableReferenceKind.Name, [initialScalar], initial.IsSet, null, null)
                        { InspectionBinding = selectedBinding };

                var value = await ReadValueAsync(selected, ParameterModifier.None).ConfigureAwait(false);
                var join = ParameterFieldJoin.Default;
                // The initial read determines whether the enclosing word recognizes
                // separate at fields. A newly selected at aggregate still uses its
                // operator's separators without introducing that word-level behavior.
                if (value.RangeSource is ParameterSource.Positional
                    && (removesPattern || parsed.Kind is BracedParameterOperatorKind.Transform or BracedParameterOperatorKind.Substring))
                {
                    var ifs = FieldSeparators.FromVariables(scope.Variables);
                    if (initial.Kind != VariableReferenceKind.At || !ifs.IsEmpty && !ifs.Bytes.Span.Contains((byte)' '))
                        join = ParameterFieldJoin.Space;
                }
                else if (value.Kind == VariableReferenceKind.At && initial.Kind != VariableReferenceKind.At)
                    join = ParameterFieldJoin.IfsOrSpace;
                // Pattern and arithmetic operands are reached according to the first read.
                return value with
                {
                    FieldJoin = join,
                    IsSet = parsed.Kind == BracedParameterOperatorKind.Transform ? value.IsSet : initial.IsSet
                };
            }

            ParameterReadSyntax ReadSyntax(ShellSource name, SpecialParameterKind special) =>
                new(name, special, special == SpecialParameterKind.None
                    && VariableReference.TryParse(name, budget, out var reference) ? reference : null);

            async Task<ParameterValue> ReadValueAsync(ParameterReadSyntax syntax, ParameterModifier modifier)
            {
                if (syntax.Special != SpecialParameterKind.None)
                {
                    var special = ReadSpecialParameter(syntax.Name.AsSpan(), syntax.Special, scope);
                    if (modifier != ParameterModifier.Length)
                        return special;
                    var length = special.Kind is VariableReferenceKind.At or VariableReferenceKind.Star
                        ? special.Fields.Count : CountParameterCharacters(special.Fields[0], scope);
                    return new ParameterValue(VariableReferenceKind.Name,
                        [ShellValue.FromInteger(length)], true, null, null);
                }
                if (syntax.Reference is not { } reference)
                {
                    await ReportSubstitutionFailureAsync("unsupported special parameter", ExpansionFailureKind.Parameter)
                        .ConfigureAwait(false);
                    return new ParameterValue(VariableReferenceKind.Name, [], false, null, null);
                }

                if (reference.Kind == VariableReferenceKind.Element && reference.Subscript.Length == 0)
                {
                    await ReportBadSubstitutionAsync().ConfigureAwait(false);
                    return new ParameterValue(reference.Kind, [], false, reference, null);
                }

                if (reference.IsAggregate)
                {
                    var selected = await ResolveReadBindingAsync(reference,
                        modifier == ParameterModifier.Length ? ReferenceUse.Length : ReferenceUse.Read,
                        scope, scope.Variables).ConfigureAwait(false);
                    var variables = selected?.Owner ?? scope.Variables;
                    var resolved = selected is { Reference.Kind: VariableReferenceKind.Name } binding
                        ? binding.Name : BindingName.Empty;
                    if (modifier == ParameterModifier.Length)
                    {
                        var length = variables.GetArrayMetadata(resolved)?.Count
                            ?? (variables.TryGetScalar(resolved, out _) ? 1 : 0);
                        return new ParameterValue(VariableReferenceKind.Name,
                            [ShellValue.FromInteger(length)], true, null, null);
                    }

                    // Operators that consume the array after expanding an operand keep the command-owned source.
                    var detachBorrowed = parsed.Kind is not (BracedParameterOperatorKind.None
                        or BracedParameterOperatorKind.UseDefault or BracedParameterOperatorKind.UseAlternate
                        or BracedParameterOperatorKind.AssignDefault or BracedParameterOperatorKind.ErrorIfUnset
                        or BracedParameterOperatorKind.Transform or BracedParameterOperatorKind.Substring);
                    if (detachBorrowed && parsed.Modifier == ParameterModifier.None
                        && (removesPattern || parsed.Kind is BracedParameterOperatorKind.Replace
                            or BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.UpperAll
                            or BracedParameterOperatorKind.LowerFirst or BracedParameterOperatorKind.LowerAll)
                        && variables.IsBorrowedBinding(resolved))
                    {
                        // Literal patterns and replacements cannot change the selected array before projection.
                        detachBorrowed = parsed.Word.AsSpan().IndexOfAny("$`~"u8) >= 0;
                    }
                    var (array, map) = variables.SelectExpansionArray(resolved, detachBorrowed);
                    ParameterSource.DeferredSlice? deferredSlice = null;
                    (long Offset, long? Length)? literalSliceBounds = null;
                    if (parsed.Kind == BracedParameterOperatorKind.Substring && (array?.Count ?? map?.Count) is > 0)
                    {
                        // An empty slice source skips its bounds. A nonempty source can stay
                        // borrowed when both reached bounds are already integer literals.
                        literalSliceBounds = ReadLiteralSliceBounds(parsed.Word, scope.Effects.CancellationToken);
                        if (literalSliceBounds == null)
                        {
                            // Indirection can evaluate more selectors before returning the final operand.
                            if (parsed.Modifier == ParameterModifier.Indirect)
                                (array, map) = variables.SelectExpansionArray(resolved, detachBorrowed: true);
                            else if (selected is { } sliceBinding && variables.IsBorrowedBinding(resolved))
                                deferredSlice = new ParameterSource.DeferredSlice(sliceBinding);
                        }
                    }
                    var scalar = ShellValue.Empty;
                    var count = array?.Count ?? map?.Count ?? (variables.TryGetScalar(resolved, out scalar) ? 1 : 0);

                    if (modifier == ParameterModifier.None)
                    {
                        ParameterSource? source = deferredSlice != null ? deferredSlice
                            : array != null ? new ParameterSource.Indexed(array)
                            : map != null ? new ParameterSource.Associative(map) : null;
                        if (source != null && literalSliceBounds is { } bounds)
                            source = new ParameterSource.LiteralSlice(source, bounds.Offset, bounds.Length);
                        return new ParameterValue(reference.Kind, source == null && count != 0 ? [scalar] : [], count != 0, reference, null)
                            { RangeSource = source, InspectionBinding = inspection ? selected : null };
                    }

                    if (count == 0)
                        return new ParameterValue(reference.Kind, [], false, reference, null);
                    var keys = array?.PresentKeys ?? map?.PresentKeys;
                    if (keys == null)
                        return new ParameterValue(reference.Kind, [ShellValue.FromInteger(0)], true, reference, null);

                    // Indirect aggregate keys are consumed synchronously. The word builder
                    // owns the emitted fields and admits any retained or copied backing.
                    return new ParameterValue(reference.Kind, [], true, reference, null)
                        { RangeSource = new ParameterSource.Keys(keys, count) };
                }

                var target = await ResolveReferenceAsync(reference,
                        modifier == ParameterModifier.Length ? ReferenceUse.Length
                            : parsed.Kind == BracedParameterOperatorKind.AssignDefault ? ReferenceUse.DefaultRead : ReferenceUse.Read,
                        scope, arithmeticPurpose)
                    .ConfigureAwait(false);
                var value = ShellValue.Empty;
                var isSet = target != null && target.TryRead(scope.Variables, out value);
                if (target is { Name.IsEmpty: true } && reference.Kind == VariableReferenceKind.Name
                    && parsed.Kind == BracedParameterOperatorKind.AssignDefault && modifier == ParameterModifier.None)
                {
                    // A failed local lookup can still see the original global nameref spelling.
                    var global = scope.Context.GetGlobalBindingOwner(reference.Name, scope.Variables);
                    isSet = !ReferenceEquals(global, scope.Variables) && global.TryGetNameref(reference.Name, out value);
                }
                return modifier == ParameterModifier.Length
                    ? new ParameterValue(VariableReferenceKind.Name,
                        [ShellValue.FromInteger(CountParameterCharacters(value, scope))], true, null, null)
                    : new ParameterValue(reference.Kind, [value], isSet, reference, target);

                // Special parameters are values, not assignable variable locations.
                static ParameterValue ReadSpecialParameter(ReadOnlySpan<byte> name, SpecialParameterKind kind, ScopedEvaluation scope)
                {
                    if (kind is SpecialParameterKind.At or SpecialParameterKind.Star)
                        return new ParameterValue(kind == SpecialParameterKind.At ? VariableReferenceKind.At : VariableReferenceKind.Star,
                            scope.Variables.PositionalParameters, scope.Variables.PositionalParameters.Count != 0, null, null)
                            { RangeSource = new ParameterSource.Positional(scope.Variables.PositionalParameters, scope.Variables.ArgumentZero) };

                    var value = ShellValue.Empty;
                    var isSet = true;
                    switch (kind)
                    {
                        case SpecialParameterKind.Status:
                            value = ShellValue.FromInteger(scope.Effects.LastExitCode ?? 0);
                            break;
                        case SpecialParameterKind.Count:
                            value = ShellValue.FromInteger(scope.Variables.PositionalParameters.Count);
                            break;
                        case SpecialParameterKind.Flags:
                            value = scope.Effects.ShellOptionFlags;
                            break;
                        case SpecialParameterKind.BackgroundProcess:
                            // Background execution is not supported, so no shell-owned job can set this parameter.
                            isSet = false;
                            break;
                        case SpecialParameterKind.Positional:
                            // Overflowing decimal positions are syntactically valid but have no argument.
                            isSet = Integers.TryParseNumber(name, out var position)
                                && position <= scope.Variables.PositionalParameters.Count;
                            if (isSet)
                                value = position == 0 ? scope.Variables.ArgumentZero : scope.Variables.PositionalParameters[(int)position - 1];
                            break;
                    }
                    return new ParameterValue(VariableReferenceKind.Name, [value], isSet, null, null);
                }
            }
        }

        Task ReportSubstitutionFailureAsync(string message, ExpansionFailureKind kind) =>
            scope.Effects.ReportFatalExpansionAsync(
                [ShellValue.FromText("${"), ShellValue.FromBytes(content.Bytes), ShellValue.FromText("}")],
                [ShellValue.FromText(message)], kind);

        Task ReportBadSubstitutionAsync()
        {
            if (quoteContext == ExpansionQuoteContext.HereDocument)
                return ReportParameterSyntaxAsync(segment.ToValue(), missingBrace: false, scope, hereDocument: true);
            ReadOnlySpan<ShellValue> subject = [ShellValue.FromText("${"), content.ToValue(), ShellValue.FromText("}")];
            ReadOnlySpan<ShellValue> message = [ShellValue.FromText("bad substitution")];
            return scope.Effects.ReportParameterExpansionFailureAsync(subject, message, scope, ExpansionFailureKind.WordSyntax);
        }

        Task ReportParameterTargetFailureAsync(ReadOnlySpan<ShellValue> subject, string message) =>
            scope.Effects.ReportParameterExpansionFailureAsync(subject, [ShellValue.FromText(message)], scope, ExpansionFailureKind.ParameterTarget);

        void Emit(IEnumerable<ShellValue> fields) => EmitFields(fields, projected: false);

        void EmitFields(IEnumerable<ShellValue> fields, bool projected)
        {
            if (parsed.Modifier == ParameterModifier.Length
                || parsed.Modifier == ParameterModifier.None && parsed.Kind == BracedParameterOperatorKind.None
                    && GetSpecialParameterKind(parsed.Name.AsSpan()) is SpecialParameterKind.Status or SpecialParameterKind.Count)
            {
                output.AppendIntegerExpansion(parameter.Value, allowWordSplitting, globQuoted);
                return;
            }
            var fieldJoin = parameter.FieldJoin;
            if (fieldJoin == ParameterFieldJoin.Default && output.Purpose == ExpansionPurpose.Assignment
                && quoteContext != ExpansionQuoteContext.HereDocument
                && parameter.Kind == VariableReferenceKind.At
                && (projected || parameter.RangeSource is ParameterSource.Keys && quoteContext == ExpansionQuoteContext.DoubleQuoted))
                fieldJoin = ParameterFieldJoin.IfsOrSpace;
            if (output.TryAppendSelectedFields(fieldJoin, fields, allowWordSplitting, globQuoted, quoteContext))
                return;
            if (output.Purpose == ExpansionPurpose.ConditionalPattern && quoteContext == ExpansionQuoteContext.Unquoted
                && parameter.Kind == VariableReferenceKind.Star && parameter.RangeSource is ParameterSource.Indexed or ParameterSource.Associative
                && parsed.Kind is BracedParameterOperatorKind.None or BracedParameterOperatorKind.Transform
                && FieldSeparators.FromVariables(scope.Variables).IsEmpty)
            {
                // These array joins retain literal pattern bytes when the join separator is empty.
                output.AppendSeparatedFields(fields, ShellValue.Empty, allowWordSplitting: false, globQuoted: true);
                return;
            }
            if (quoteContext == ExpansionQuoteContext.HereDocument && parameter.Kind == VariableReferenceKind.Star)
            {
                HereDocumentJoin? join = parsed.Kind switch
                {
                    BracedParameterOperatorKind.NamesStar => HereDocumentJoin.UnprotectedIfs,
                    BracedParameterOperatorKind.Replace or BracedParameterOperatorKind.UpperFirst
                        or BracedParameterOperatorKind.UpperAll or BracedParameterOperatorKind.LowerFirst
                        or BracedParameterOperatorKind.LowerAll => HereDocumentJoin.IfsBetweenProtectedValues,
                    BracedParameterOperatorKind.Transform => HereDocumentJoin.SpaceBetweenProtectedValues,
                    _ when removesPattern || parameter.RangeSource is ParameterSource.Keys => HereDocumentJoin.SpaceBetweenProtectedValues,
                    _ when parameter.RangeSource is ParameterSource.Positional => HereDocumentJoin.PositionalStar,
                    _ => null,
                };
                if (join is { } mode)
                {
                    output.AppendHereDocumentFields(fields, mode);
                    return;
                }
            }
            var source = (parameter.RangeSource, parsed.Kind) switch
            {
                _ when projected => AtFieldSource.Projected,
                (ParameterSource.Indexed or ParameterSource.Associative, _)
                    when parsed.Kind == BracedParameterOperatorKind.None || output.Purpose == ExpansionPurpose.CasePattern => AtFieldSource.Array,
                (ParameterSource.Positional, BracedParameterOperatorKind.None or BracedParameterOperatorKind.UseDefault
                    or BracedParameterOperatorKind.AssignDefault or BracedParameterOperatorKind.ErrorIfUnset) => AtFieldSource.Positional,
                _ => AtFieldSource.Other,
            };
            var preserveQuotedEmpty = parameter.Kind == VariableReferenceKind.Star
                ? !projected || parameter.RangeSource == null
                : projected && parameter.IsSet;
            AppendParameterFields(parameter.Kind, fields, output, allowWordSplitting, globQuoted, quoteContext, starSplit,
                source, preserveQuotedEmpty);
        }

        // Operand expansion may have changed which values the selected array holds.
        void EmitTransformedFields(Func<ShellValue, MemoryCapacity, ShellValue> project) =>
            EmitFields(ProjectParameterFields(parameter.SelectedFields, project, scope), projected: true);

        async Task EmitValueOperatorAsync()
        {
            if (!parameter.IsSet)
            {
                Emit(parameter.Fields);
                return;
            }

            var budget = scope.Effects.Memory;
            var cancellationToken = scope.Effects.CancellationToken;
            if (parsed.Kind == BracedParameterOperatorKind.Transform)
            {
                if (!"QEUuL"u8.Contains(transform))
                {
                    await ReportInvalidTransformAsync().ConfigureAwait(false);
                    return;
                }
                EmitTransformedFields((value, scratch) => transform switch
                {
                    (byte)'E' => ShellEscapeDecoder.DecodeAnsiCValue(value, scratch),
                    (byte)'Q' => QuoteParameterField(value, scratch),
                    _ => TransformCase(value, transform == (byte)'L' ? CaseTransformation.Lowercase : CaseTransformation.Uppercase,
                        transform == (byte)'u', matcher: null, scratch, cancellationToken)
                });
                return;
            }

            if (parsed.Kind == BracedParameterOperatorKind.Replace)
            {
                var mode = ParameterReplacementMode.First;
                var operand = parsed.Word;
                if (!operand.IsEmpty && operand[0] == (byte)'/')
                {
                    mode = ParameterReplacementMode.All;
                    operand = operand[1..];
                }
                var separator = FindReplacementSeparator(operand, mode);
                var pattern = await ExpandOperandAsync(separator < 0 ? operand : operand[..separator], ExpansionPurpose.Scalar)
                    .ConfigureAwait(false);
                if (!scope.CanExpand)
                    return;
                // Anchors can come from an expansion, but quoted prefixes and global replacements stay literal.
                if (mode == ParameterReplacementMode.First && !pattern.Value.IsEmpty
                    && pattern.Mask is { } patternMask && patternMask.AsSpan()[0] == GlobMask.Active
                    && pattern.Value.Bytes.Span[0] is (byte)'#' or (byte)'%')
                {
                    mode = pattern.Value.Bytes.Span[0] == (byte)'#' ? ParameterReplacementMode.Prefix : ParameterReplacementMode.Suffix;
                    pattern = new ParameterOperand(ShellValue.FromBytes(pattern.Value.Bytes[1..]),
                        new GlobMask(patternMask.Memory.IsEmpty ? ReadOnlyMemory<byte>.Empty : patternMask.Memory[1..]));
                }
                var canMatch = TryUnescapePattern(pattern, out pattern);
                var replacement = await ExpandOperandAsync(separator < 0 ? ShellSource.Empty : operand[(separator + 1)..],
                    ExpansionPurpose.ParameterReplacement).ConfigureAwait(false);
                if (!scope.CanExpand)
                    return;
                var matcher = canMatch ? ShellPattern.FromUnescaped(pattern.Value, pattern.Mask, budget, cancellationToken) : null;
                var replacementMask = replacement.Mask;
                var syntaxStart = replacementMask == null ? -1
                    : replacement.Value.Bytes.Span.IndexOfAny((byte)'&', (byte)'\\');
                var activity = replacementMask?.Memory ?? default;
                using var encoded = matcher != null && syntaxStart >= 0 && !activity.IsEmpty
                    ? new ByteMemoryBuilder(0, budget.Budget)
                    : null;
                ReadOnlyMemory<byte>? template = null;
                EmitTransformedFields((value, scratch) => matcher == null ? value : Replace(value, matcher, mode, scratch));
                return;

                int FindReplacementSeparator(ShellSource source, ParameterReplacementMode mode)
                {
                    // After the global marker, a leading slash belongs to the pattern.
                    var start = mode == ParameterReplacementMode.All && !source.IsEmpty && source[0] == (byte)'/' ? 1 : 0;
                    var cursor = new ShellSourceCursor(source, start,
                        new ShellScanContext { SyntaxMemory = scope.Effects.Memory.Workspace });
                    while (!cursor.IsAtEnd)
                    {
                        if (cursor.TryConsumeQuoteOrEscape()
                            || cursor.SkipNestedExpansion(BracedParameterRecognition.All) is ShellNestedExpansionScan.Complete)
                            continue;
                        if (!cursor.IsQuoted && cursor.Current == (byte)'/')
                            return cursor.Position;
                        cursor.Advance();
                    }
                    return -1;
                }

                ReadOnlyMemory<byte> EncodeReplacement()
                {
                    if (encoded == null)
                        return replacement.Value.Bytes;
                    var bytes = replacement.Value.Bytes.Span;
                    var mask = new GlobMaskSpan(activity.Span);
                    var copiedThrough = 0;
                    for (var index = syntaxStart; index < bytes.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Preserve quoted escapes in one stream: an earlier unquoted backslash can consume them.
                        if (bytes[index] is not ((byte)'&' or (byte)'\\') || mask[index] == GlobMask.Active)
                            continue;
                        encoded.Append(bytes[copiedThrough..index]).Append((byte)'\\');
                        copiedThrough = index;
                    }
                    return encoded.Length == 0
                        ? replacement.Value.Bytes
                        : encoded.Append(bytes[copiedThrough..]).ToMemory();
                }

                ShellValue Replace(ShellValue value, ShellPattern pattern, ParameterReplacementMode mode, MemoryCapacity scratch)
                {
                    var bytes = value.Bytes.Span;
                    if (pattern.IsEmpty && mode is ParameterReplacementMode.First or ParameterReplacementMode.All)
                        return value;
                    var byteCharacters = pattern.UsesByteCharacters(bytes);
                    var lastMalformed = -1;
                    if (mode == ParameterReplacementMode.All && byteCharacters && !pattern.RequiresByteCharacters)
                    {
                        // Later global matches can return to scalar matching after malformed
                        // input is consumed. Remember suffix validity without rescanning it.
                        for (var index = 0; index < bytes.Length;)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (Rune.DecodeFromUtf8(bytes[index..], out _, out var width) != OperationStatus.Done)
                            {
                                lastMalformed = index;
                                width = 1;
                            }
                            index += width;
                        }
                    }
                    var replaced = false;
                    using var builder = new ByteMemoryBuilder(0, scratch);
                    using var search = mode == ParameterReplacementMode.All ? pattern.CreateSearch(scratch) : default;
                    var position = 0;
                    var copiedThrough = 0;
                    do
                    {
                        int offset;
                        int length;
                        bool found;
                        if (mode == ParameterReplacementMode.Prefix)
                        {
                            offset = 0;
                            found = pattern.TryMatchPrefix(bytes[position..], shortest: false, byteCharacters, cancellationToken, out length);
                        }
                        else if (mode == ParameterReplacementMode.Suffix)
                        {
                            found = pattern.TryMatchSuffix(bytes[position..], shortest: false, byteCharacters, cancellationToken, out offset);
                            length = found ? bytes.Length - position - offset : 0;
                        }
                        else if (mode == ParameterReplacementMode.All)
                            found = search.TryFind(bytes[position..], byteCharacters, cancellationToken, out offset, out length);
                        else
                            found = pattern.TryFind(bytes[position..], byteCharacters, cancellationToken, out offset, out length);
                        if (!found)
                            break;
                        replaced = true;
                        position += offset;
                        Append(bytes[copiedThrough..position]);
                        AppendReplacement(bytes.Slice(position, length));
                        position += length;
                        copiedThrough = position;
                        if (mode != ParameterReplacementMode.All || position == bytes.Length)
                            break;
                        if (length == 0)
                            position += byteCharacters ? 1 : Utf8Text.FirstCharacterLength(bytes[position..]);
                        if (byteCharacters && !pattern.RequiresByteCharacters && position < bytes.Length)
                            byteCharacters = position <= lastMalformed || (bytes[position] & 0xc0) == 0x80;
                    } while (position < bytes.Length);
                    if (!replaced)
                        return value;
                    Append(bytes[copiedThrough..]);
                    return ShellValue.FromBytes(builder.DetachSnapshot());

                    void AppendReplacement(ReadOnlySpan<byte> match)
                    {
                        // Operand expansion is eager; template spelling is needed only after a match.
                        if (syntaxStart < 0)
                        {
                            Append(replacement.Value.Bytes.Span);
                            return;
                        }
                        var insert = (template ??= EncodeReplacement()).Span;
                        var literalStart = 0;
                        for (var index = syntaxStart; index < insert.Length; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (insert[index] == (byte)'&')
                            {
                                Append(insert[literalStart..index]);
                                Append(match);
                                literalStart = index + 1;
                            }
                            else if (insert[index] == (byte)'\\' && index + 1 < insert.Length
                                && insert[index + 1] is (byte)'&' or (byte)'\\')
                            {
                                Append(insert[literalStart..index]);
                                literalStart = ++index;
                            }
                        }
                        Append(insert[literalStart..]);
                    }

                    void Append(ReadOnlySpan<byte> content)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        builder.Append(content);
                    }
                }
            }

            var caseOperand = parsed.Word.IsEmpty ? null
                : await ExpandOperandWordAsync(parsed.Word, ExpansionPurpose.Scalar, ImplicitEmptyPolicy.PreserveAggregate)
                    .ConfigureAwait(false);
            if (!scope.CanExpand)
                return;
            if (parameter.RangeSource == null && parameter.Fields is [var field] && field.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EmitFields(parameter.Fields, projected: true);
                return;
            }
            // A missing field selects all characters. A present empty field matches none.
            var caseFields = caseOperand?.Build() ?? [];
            var casePattern = caseFields.Count == 0 ? null : JoinOperandFields(caseFields);
            var canMatchCase = casePattern == null || TryUnescapePattern(casePattern, out casePattern);
            var caseMatcher = canMatchCase && casePattern != null
                ? new CasePatternMatcher(casePattern, budget.Workspace) : null;
            var conversion = parsed.Kind is BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.UpperAll
                ? CaseTransformation.Uppercase : CaseTransformation.Lowercase;
            var firstOnly = parsed.Kind is BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.LowerFirst;
            EmitTransformedFields((value, scratch) =>
                canMatchCase ? TransformCase(value, conversion, firstOnly, caseMatcher, scratch, cancellationToken) : value);

            bool TryUnescapePattern(ParameterOperand operand, out ParameterOperand pattern)
            {
                if (operand.Mask == null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pattern = operand;
                    return true;
                }
                var success = operand.Mask.TryUnescape(operand.Value, budget, cancellationToken, out var value, out var mask);
                pattern = new ParameterOperand(value, mask);
                return success;
            }

            async Task<ParameterOperand> ExpandOperandAsync(ShellSource source, ExpansionPurpose purpose)
            {
                var operand = await ExpandOperandWordAsync(source, purpose, ImplicitEmptyPolicy.Preserve).ConfigureAwait(false);
                return JoinOperandFields(operand?.Build() ?? []);
            }

            Task<ExpandedWordBuilder?> ExpandOperandWordAsync(
                ShellSource source, ExpansionPurpose purpose, ImplicitEmptyPolicy emptyPolicy) =>
                ExpandOperatorWordAsync(source, scanContext, scope, ExpansionQuoteContext.Unquoted,
                    purpose, emptyPolicy, TildeExpansion.Leading, enclosingWord: null);

            ParameterOperand JoinOperandFields(IReadOnlyList<ExpandedWord> fields)
            {
                var (value, mask) = ExpandedWord.JoinPatternWithSpaces(fields, budget);
                return new ParameterOperand(value, mask);
            }
        }

        async Task ReportInvalidTransformAsync()
        {
            var hereDocument = quoteContext == ExpansionQuoteContext.HereDocument;
            var wholeWord = quoteContext == ExpansionQuoteContext.Unquoted && originalWord?.Parts.Count > 1 ? originalWord : null;
            if (hereDocument || wholeWord == null && segment.AsSpan().IndexOf("$("u8) < 0)
            {
                await scope.Effects.ReportInvalidParameterTransformAsync(segment.ToValue(), scope, hereDocument).ConfigureAwait(false);
                return;
            }
            var budget = scope.Effects.Memory.Budget;
            using var spelling = new ByteMemoryBuilder(0, budget);
            var cancellationToken = scope.Effects.CancellationToken;
            var written = wholeWord == null
                ? FunctionDefinitionFormatter.TryAppendSourceWithCaptures(segment, scanContext, spelling, budget, cancellationToken)
                : FunctionDefinitionFormatter.TryAppendWordSource(wholeWord, spelling, budget, cancellationToken, scanContext);
            if (!written)
                throw new ByteMemoryBuilder.LimitExceededException();
            await scope.Effects.ReportInvalidParameterTransformAsync(ShellValue.FromBytes(spelling.ToMemory()), scope, hereDocument: false)
                .ConfigureAwait(false);
        }

        static ReadOnlyMemory<byte> ApplyPatternRemoval(ShellValue value, ShellPattern pattern,
            BracedParameterOperatorKind kind, CancellationToken cancellationToken)
        {
            var shortest = kind is BracedParameterOperatorKind.RemovePrefixShortest or BracedParameterOperatorKind.RemoveSuffixShortest;
            if (kind is BracedParameterOperatorKind.RemovePrefixShortest or BracedParameterOperatorKind.RemovePrefixLongest)
                return pattern.TryMatchPrefix(value.Bytes.Span, shortest, cancellationToken, out var length)
                    ? value.Bytes[length..] : value.Bytes;
            return pattern.TryMatchSuffix(value.Bytes.Span, shortest, cancellationToken, out var start)
                ? value.Bytes[..start] : value.Bytes;
        }

        static (long Offset, long? Length)? ReadLiteralSliceBounds(ShellSource expression, CancellationToken cancellationToken)
        {
            var separator = expression.AsSpan().IndexOf((byte)':');
            var offset = separator < 0 ? expression : expression[..separator];
            if (!ArithmeticEvaluation.TryParseIntegerLiteral(offset.TrimBlanks().AsSpan(), cancellationToken, out var first))
                return null;
            if (separator < 0)
                return (first, null);
            return ArithmeticEvaluation.TryParseIntegerLiteral(expression[(separator + 1)..].TrimBlanks().AsSpan(),
                cancellationToken, out var length) ? (first, length) : null;
        }

        static async Task ExpandSubstringAsync(
            ShellSource expression, ShellValue name, ParameterValue parameter, ScopedEvaluation scope,
            ExpandedWordBuilder output, bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext,
            ExpansionPurpose arithmeticPurpose, PositionalStarSplit starSplit)
        {
            if (!parameter.IsSet && parameter.RangeSource is not ParameterSource.Positional)
            {
                Emit(parameter.Kind is VariableReferenceKind.At or VariableReferenceKind.Star ? parameter.Fields : [parameter.Value],
                    preserveQuotedEmpty: false);
                return;
            }

            var source = parameter.RangeSource;
            VariableBinding? borrowedBinding = null;
            if (source is ParameterSource.DeferredSlice deferred)
            {
                borrowedBinding = deferred.Binding;
                source = null;
            }
            var cancellationToken = scope.Effects.CancellationToken;
            var separator = -1;
            ShellScanContext? lengthScan = null;
            long start;
            long? length = null;
            if (source is ParameterSource.LiteralSlice bounds)
            {
                start = bounds.Offset;
                length = bounds.Length;
                source = bounds.Source;
            }
            else
            {
                var boundsScan = new ShellScanContext { SyntaxMemory = scope.Effects.Memory.Workspace };
                if (!TryFindSeparator(boundsScan, out separator))
                {
                    if (boundsScan.HasCommandSubstitutionLookahead)
                    {
                        using var spelling = new ByteMemoryBuilder(0, scope.Effects.Memory.Budget);
                        if (!FunctionDefinitionFormatter.TryAppendSourceWithCaptures(expression, boundsScan, spelling,
                                scope.Effects.Memory.Budget, scope.Effects.CancellationToken))
                            throw new ByteMemoryBuilder.LimitExceededException();
                        await ReportUnclosedOffsetAsync(ShellValue.FromBytes(spelling.ToMemory())).ConfigureAwait(false);
                    }
                    else
                        await ReportUnclosedOffsetAsync(expression.ToValue()).ConfigureAwait(false);
                    return;
                }

                var offsetSource = separator < 0 ? expression : expression[..separator];
                DetachForBound(offsetSource, cancellationToken, ref borrowedBinding, ref source);
                var offset = await ExpandArithmeticOperandAsync(offsetSource, scope, arithmeticPurpose, name, null)
                    .ConfigureAwait(false);
                if (offset is not { } selectedOffset)
                    return;
                start = selectedOffset;
            }

            if (source == null && borrowedBinding is { } selectedBinding)
                source = ReadSliceSource(selectedBinding, detachBorrowed: false);
            var value = source == null ? parameter.Value.Bytes : default;
            var scalarStart = 0;
            if (source == null)
            {
                if (FindScalarBoundary(value.Span, start) is not { } position)
                {
                    Emit(parameter.Kind is VariableReferenceKind.At or VariableReferenceKind.Star ? [] : [ShellValue.Empty],
                        preserveQuotedEmpty: false);
                    return;
                }
                scalarStart = position;
            }
            else
            {
                // Offset effects precede the negative-origin lookup; length effects precede enumeration.
                var maximum = source switch
                {
                    ParameterSource.Indexed indexed => indexed.Array.MaxIndex ?? -1,
                    ParameterSource.Associative associative => associative.Array.Count,
                    ParameterSource.Positional positional => (long)positional.Arguments.Count + 1,
                    _ => 0,
                };
                if (start < 0)
                    start = source switch
                    {
                        // Add the negative offset first to avoid overflow at long.MaxValue.
                        ParameterSource.Indexed or ParameterSource.Associative => maximum + start + 1,
                        _ => maximum + start,
                    };
                if (start < 0 || start > maximum)
                {
                    Emit([], preserveQuotedEmpty: false);
                    return;
                }
            }

            if (separator >= 0)
            {
                var lengthSource = expression[(separator + 1)..];
                DetachForBound(lengthSource, cancellationToken, ref borrowedBinding, ref source);
                lengthScan = CreateExpansionScanContext(scope);
                // A rejected bound renders the same parsed captures used during expansion.
                lengthScan.PreserveDelimiterLookahead = true;
                length = await ExpandArithmeticOperandAsync(lengthSource, scope, arithmeticPurpose, name, lengthScan)
                    .ConfigureAwait(false);
                if (length == null)
                    return;
            }

            if (source != null)
            {
                if (length < 0)
                {
                    await ReportInvalidLengthAsync().ConfigureAwait(false);
                    return;
                }
                var count = (int)Math.Min(length ?? int.MaxValue, int.MaxValue);
                switch (source)
                {
                    case ParameterSource.Indexed indexed:
                        if (IndexedArrayIndex.TryCreate(start, out var first))
                            Emit(indexed.Array.ValuesFrom(first, count), preserveQuotedEmpty: false);
                        return;
                    case ParameterSource.Associative associative:
                        // Keep the platform's unsigned byte order rather than native Bash hash traversal.
                        // Bash includes the selected entry even when the requested length is zero.
                        Emit(associative.Array.ValuesFrom((int)Math.Max(0, start - 1), Math.Max(1, count)), preserveQuotedEmpty: false);
                        return;
                    case ParameterSource.Positional positional:
                        Emit(positional.ValuesFrom(start, count), preserveQuotedEmpty: false);
                        return;
                }
            }

            var selected = value[scalarStart..];
            if (length is { } amount)
            {
                int selectedBytes;
                if (amount < 0)
                {
                    if (FindScalarBoundary(value.Span, amount) is not { } end || end < scalarStart)
                    {
                        await ReportInvalidLengthAsync().ConfigureAwait(false);
                        return;
                    }
                    selectedBytes = end - scalarStart;
                }
                else
                    selectedBytes = AdvanceParameterCharacters(selected.Span, (int)Math.Min(amount, selected.Length),
                        scope, out _);
                selected = selected[..selectedBytes];
            }
            Emit([ShellValue.FromBytes(selected)], preserveQuotedEmpty: true);

            static void DetachForBound(ShellSource operand, CancellationToken cancellationToken,
                ref VariableBinding? borrowed, ref ParameterSource? source)
            {
                if (borrowed is not { } binding || ArithmeticEvaluation.IsReferenceFree(operand.AsSpan(), cancellationToken))
                    return;
                source = ReadSliceSource(binding, detachBorrowed: true);
                borrowed = null;
            }

            static ParameterSource ReadSliceSource(VariableBinding binding, bool detachBorrowed)
            {
                var (indexed, associative) = binding.Owner.SelectExpansionArray(binding.Name, detachBorrowed);
                return indexed != null ? new ParameterSource.Indexed(indexed) : associative != null
                    ? new ParameterSource.Associative(associative)
                    : throw new InvalidOperationException("The borrowed slice no longer has array backing.");
            }

            int? FindScalarBoundary(ReadOnlySpan<byte> bytes, long offset)
            {
                if (offset < -bytes.Length || offset > bytes.Length)
                    return null;
                if (offset >= 0)
                {
                    var size = AdvanceParameterCharacters(bytes, (int)offset, scope, out var reached);
                    return reached == offset ? size : null;
                }

                var position = bytes.Length;
                for (var remaining = -(int)offset; remaining > 0; remaining--)
                {
                    if (position == 0)
                        return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    position -= Utf8Text.LastCharacterLength(bytes[..position]);
                }
                return position;
            }

            // Enumerate into the budgeted builder; never copy the full array or allocate its holes.
            void Emit(IEnumerable<ShellValue> fields, bool preserveQuotedEmpty)
            {
                if (output.TryAppendSelectedFields(parameter.FieldJoin, fields, allowWordSplitting, globQuoted, quoteContext))
                    return;
                if (quoteContext == ExpansionQuoteContext.HereDocument && parameter.Kind == VariableReferenceKind.Star)
                    output.AppendHereDocumentFields(fields, HereDocumentJoin.SpaceBetweenProtectedValues);
                else
                    AppendParameterFields(parameter.Kind, fields, output, allowWordSplitting, globQuoted, quoteContext,
                        starSplit, AtFieldSource.Projected, preserveQuotedEmpty);
            }

            async Task ReportInvalidLengthAsync()
            {
                var detail = ShellValue.FromText("substring expression < 0");
                if (scope.Effects.GetStdErrDescriptor().IsClosed)
                {
                    await scope.Effects.ReportFatalExpansionAsync(ShellValue.Empty, detail, ExpansionFailureKind.Arithmetic)
                        .ConfigureAwait(false);
                    return;
                }
                // The literal fast path accepted only numeric bounds, so its first colon is the delimiter.
                var delimiter = separator >= 0 ? separator : expression.AsSpan().IndexOf((byte)':');
                var spelling = expression[(delimiter + 1)..];
                if (lengthScan is { HasCommandSubstitutionLookahead: true })
                {
                    using var rendered = new ByteMemoryBuilder(0, scope.Effects.Memory.Budget);
                    if (!FunctionDefinitionFormatter.TryAppendSourceWithCaptures(spelling, lengthScan, rendered,
                            scope.Effects.Memory.Budget, cancellationToken))
                        throw new ByteMemoryBuilder.LimitExceededException();
                    await scope.Effects.ReportParameterFatalExpansionAsync(ShellValue.FromBytes(rendered.ToMemory()), detail, scope,
                            ExpansionFailureKind.Arithmetic).ConfigureAwait(false);
                    return;
                }
                await scope.Effects.ReportParameterFatalExpansionAsync(spelling.ToValue(), detail, scope, ExpansionFailureKind.Arithmetic)
                    .ConfigureAwait(false);
            }

            Task ReportUnclosedOffsetAsync(ShellValue spelling) => scope.Effects.ReportParameterExpansionFailureAsync(
                [ShellValue.FromText("bad substitution")], [ShellValue.FromText("no closing `)' in "), spelling],
                scope, ExpansionFailureKind.WordSyntax);

            bool TryFindSeparator(ShellScanContext boundsScan, out int position)
            {
                position = -1;
                var cursor = new ShellSourceCursor(expression, 0, boundsScan);
                var parentheses = 0;
                var groupStart = 0;
                var conditional = 0;
                while (!cursor.IsAtEnd)
                {
                    if (cursor.TryConsumeQuoteOrEscape())
                        continue;
                    var start = cursor.Position;
                    if (cursor.SkipNestedExpansion(BracedParameterRecognition.All) is ShellNestedExpansionScan.Complete)
                    {
                        if (parentheses == 0 && ArithmeticExpansionScanner.HasMissingCaptureCloser(expression, start,
                                cursor.Position, cursor.IsDoubleQuoted, boundsScan.SyntaxMemory))
                            return false;
                        continue;
                    }
                    if (!cursor.IsQuoted)
                    {
                        var current = cursor.Current;
                        if (current == '(')
                        {
                            if (parentheses == 0)
                                groupStart = cursor.Position + 1;
                            parentheses++;
                        }
                        else if (current == ')' && parentheses > 0)
                        {
                            parentheses--;
                            if (parentheses == 0 && ArithmeticExpansionScanner.HasMissingCloserInOperandQuotes(
                                    expression[groupStart..cursor.Position], skipLegacyCaptures: true, boundsScan.SyntaxMemory))
                                return false;
                        }
                        else if (parentheses == 0 && current == '?') conditional++;
                        else if (parentheses == 0 && current == ':' && conditional > 0)
                        {
                            conditional--;
                            cursor.Advance();
                            continue;
                        }
                        if (current == ':' && parentheses == 0)
                        {
                            position = cursor.Position;
                            return true;
                        }
                    }
                    cursor.Advance();
                }
                return parentheses == 0;
            }
        }
    }

    /// <summary>Counts UTF-8 characters, treating each malformed byte as one character.</summary>
    private static int CountParameterCharacters(ShellValue value, ScopedEvaluation scope)
    {
        var count = 0;
        for (var position = 0; position < value.Bytes.Length; count++)
        {
            scope.Effects.CancellationToken.ThrowIfCancellationRequested();
            position += Utf8Text.FirstCharacterLength(value.Bytes.Span[position..]);
        }
        return count;
    }

    private static int AdvanceParameterCharacters(ReadOnlySpan<byte> bytes, int limit, ScopedEvaluation scope,
        out int characters)
    {
        var cancellationToken = scope.Effects.CancellationToken;
        var position = 0;
        var count = 0;
        for (; count < limit && position < bytes.Length; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            position += Utf8Text.FirstCharacterLength(bytes[position..]);
        }
        characters = count;
        return position;
    }

    private enum BracedParameterOperatorKind
    {
        Invalid,
        IncompleteSubscript,
        None,
        UseDefault,
        UseAlternate,
        AssignDefault,
        ErrorIfUnset,
        RemoveSuffixShortest,
        RemoveSuffixLongest,
        RemovePrefixShortest,
        RemovePrefixLongest,
        NamesAt,
        NamesStar,
        Substring,
        Replace,
        UpperFirst,
        UpperAll,
        LowerFirst,
        LowerAll,
        Transform,
    }

    private enum ParameterModifier { None, Length, Indirect }
    private enum ParameterClosure { Closed, Missing, MissingAfterOperator }

    private enum ParameterReplacementMode { First, All, Prefix, Suffix }

    internal static ShellValue ChangeAssignmentCase(ShellValue value, CaseTransformation conversion, EvaluationMemory? memory) =>
        TransformCase(value, conversion, firstOnly: false, matcher: null, memory?.Workspace,
            memory?.CancellationToken ?? CancellationToken.None);

    private static ShellValue TransformCase(
        ShellValue value, CaseTransformation conversion, bool firstOnly, CasePatternMatcher? matcher,
        MemoryCapacity? scratch, CancellationToken cancellationToken)
    {
        var bytes = value.Bytes.Span;
        ByteMemoryBuilder? builder = null;
        Span<byte> encoded = stackalloc byte[4];
        var copiedThrough = 0;
        try
        {
            for (var position = 0; position < bytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var valid = Rune.DecodeFromUtf8(bytes[position..], out var rune, out var length) == OperationStatus.Done;
                if (!valid)
                    length = 1;
                if (valid && (matcher == null || matcher.Matches(rune, bytes.Slice(position, length), cancellationToken)))
                {
                    var upper = conversion == CaseTransformation.Uppercase
                        || conversion == CaseTransformation.Capitalize && position == 0;
                    var changed = upper ? Rune.ToUpperInvariant(rune) : Rune.ToLowerInvariant(rune);
                    if (changed != rune)
                    {
                        builder ??= scratch == null ? ByteMemoryBuilder.CreateUnaccounted(0) : new ByteMemoryBuilder(0, scratch);
                        builder.Append(bytes[copiedThrough..position]);
                        var written = changed.EncodeToUtf8(encoded);
                        builder.Append(encoded[..written]);
                        copiedThrough = position + length;
                    }
                }
                if (firstOnly)
                    break;
                position += length;
            }
            if (builder == null)
                return value;
            builder.Append(bytes[copiedThrough..]);
            return ShellValue.FromBytes(builder.DetachSnapshot());
        }
        finally
        {
            builder?.Dispose();
        }
    }

    private sealed record ParameterOperand(ShellValue Value, GlobMask? Mask);

    /// <summary>Reuses character matches across the fields of a single expanded pattern.</summary>
    private sealed class CasePatternMatcher(ParameterOperand operand, MemoryCapacity memory)
    {
        private const int EntryCount = 32;
        private readonly ParameterOperand _operand = operand;
        private readonly bool _bytePattern = operand.Mask != null && !System.Text.Unicode.Utf8.IsValid(operand.Value.Bytes.Span);
        private readonly MemoryCapacity _memory = memory;
        private Entries _entries;

        public bool Matches(Rune rune, ReadOnlySpan<byte> character, CancellationToken cancellationToken)
        {
            // Every candidate is a decoded scalar. Zero marks an unused slot and the low bit stores the result.
            var scalar = rune.Value;
            var key = (scalar + 1) << 1;
            ref var entry = ref _entries[(scalar ^ (scalar >> 5)) & (EntryCount - 1)];
            if ((entry & ~1) == key)
                return (entry & 1) != 0;

            var pattern = _operand.Value.Bytes.Span;
            var activity = _operand.Mask;
            var mask = activity == null ? default : activity.AsSpan();
            var matched = activity == null ? pattern.SequenceEqual(character)
                : TextGlobMatcher.MatchUtf8(pattern, in mask, character, requireEnd: true,
                    _bytePattern, _memory, cancellationToken, out _) == GlobMatchResult.Match;
            entry = key | (matched ? 1 : 0);
            return matched;
        }

        [InlineArray(EntryCount)]
        private struct Entries
        {
            private int _first;
        }
    }

    private readonly record struct ParameterReadSyntax(
        ShellSource Name, SpecialParameterKind Special, VariableReference? Reference);

    private readonly record struct BracedParameterExpression(
        ShellSource Name,
        BracedParameterOperatorKind Kind,
        ShellSource Word,
        bool UsesColon,
        ParameterModifier Modifier);

    private static void AppendParameterFields(
        VariableReferenceKind kind, IEnumerable<ShellValue> fields, ExpandedWordBuilder output,
        bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext, PositionalStarSplit starSplit,
        AtFieldSource source, bool preserveQuotedEmpty)
    {
        preserveQuotedEmpty &= quoteContext == ExpansionQuoteContext.DoubleQuoted;
        if (kind == VariableReferenceKind.At)
            output.AppendFields(fields, allowWordSplitting, globQuoted, quoteContext, source);
        else if (kind == VariableReferenceKind.Star)
            output.AppendJoinedFields(fields, allowWordSplitting, globQuoted, starSplit, preserveQuotedEmpty);
        else
            foreach (var field in fields)
                if (preserveQuotedEmpty && field.IsEmpty)
                    output.MarkCurrentWordForEmission();
                else
                    output.AppendExpansion(field, allowWordSplitting, globQuoted);
    }
}

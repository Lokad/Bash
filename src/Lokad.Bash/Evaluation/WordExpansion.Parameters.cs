using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    // Slices and value transforms retain live arrays through operand effects; scalar values stay selected.
    private abstract record ParameterSource
    {
        internal sealed record Indexed(IndexedArray Array) : ParameterSource;
        internal sealed record Associative(AssociativeArray Array) : ParameterSource;
        internal sealed record Positional(IReadOnlyList<ShellValue> Arguments, ShellValue ArgumentZero) : ParameterSource;

        public IEnumerable<ShellValue> Fields => this switch
        {
            Indexed indexed => indexed.Array.PresentValues,
            Associative associative => associative.Array.PresentValues,
            Positional positional => positional.Arguments,
            _ => throw new InvalidOperationException("Unknown parameter source.")
        };
    }

    private static async Task ExpandBracedParameterAsync(
        ShellSource content,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext)
    {
        var parsed = ParseBracedParameterExpression(content);
        if (parsed.Kind == BracedParameterOperatorKind.Invalid)
        {
            await ReportSubstitutionFailureAsync("bad substitution", ExpansionFailureKind.Parameter).ConfigureAwait(false);
            return;
        }
        if (parsed.Kind == BracedParameterOperatorKind.Transform && !"QEUuL"u8.Contains(parsed.Word[0]))
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
        var parameter = await ReadParameterAsync().ConfigureAwait(false);
        if (!scope.CanExpand)
            return;

        var separators = FieldSeparators.FromVariables(scope.Variables);
        var isNonEmpty = parameter.Kind == VariableReferenceKind.Star && globQuoted
            ? parameter.Fields.Any(field => !field.IsEmpty) || parameter.Fields.Count > 1 && !separators.IsEmpty
            : parameter.Fields.Count > 1 || parameter.Fields.Count == 1 && !parameter.Fields[0].IsEmpty;
        var isAvailable = parameter.IsSet && (!parsed.UsesColon || isNonEmpty);
        switch (parsed.Kind)
        {
            case BracedParameterOperatorKind.None:
            case BracedParameterOperatorKind.NamesAt:
            case BracedParameterOperatorKind.NamesStar:
                Emit(parameter.RangeSource?.Fields ?? parameter.Fields);
                return;
            case BracedParameterOperatorKind.Substring:
                if (parsed.Word.IsEmpty)
                {
                    await ReportSubstitutionFailureAsync("bad substitution", ExpansionFailureKind.Arithmetic).ConfigureAwait(false);
                    return;
                }
                await ExpandSubstringAsync(parsed, parameter, scope, output, allowWordSplitting, globQuoted, quoteContext, arithmeticPurpose)
                    .ConfigureAwait(false);
                return;
            case BracedParameterOperatorKind.Replace:
            case BracedParameterOperatorKind.UpperFirst:
            case BracedParameterOperatorKind.UpperAll:
            case BracedParameterOperatorKind.LowerFirst:
            case BracedParameterOperatorKind.LowerAll:
            case BracedParameterOperatorKind.Transform:
                var transformed = await ApplyValueOperatorAsync().ConfigureAwait(false);
                if (scope.CanExpand)
                    Emit(transformed);
                return;
            case BracedParameterOperatorKind.UseDefault:
                if (!isAvailable)
                {
                    await AppendParameterWordAsync().ConfigureAwait(false);
                }
                else
                {
                    Emit(parameter.Fields);
                }

                return;
            case BracedParameterOperatorKind.UseAlternate:
                if (isAvailable)
                {
                    await AppendParameterWordAsync().ConfigureAwait(false);
                }
                else
                {
                    // An unavailable @ aggregate retains zero versus one empty field.
                    if (parameter.Kind == VariableReferenceKind.At)
                        Emit(parameter.Fields);
                    else
                        output.AppendExpansion(ShellValue.Empty, allowWordSplitting, globQuoted);
                }

                return;
            case BracedParameterOperatorKind.AssignDefault:
                if (!isAvailable)
                {
                    // Positional @/* indirection remains non-assignable, even if it names a variable.
                    var special = GetSpecialParameterKind(parsed.Name.AsSpan());
                    if (parsed.Modifier == ParameterModifier.None && special != SpecialParameterKind.None
                        || parsed.Modifier == ParameterModifier.Indirect && special is SpecialParameterKind.At or SpecialParameterKind.Star)
                    {
                        var subject = ByteMemoryBuilder.CreateUnaccounted(0);
                        subject.Append((byte)'$');
                        if (parsed.Modifier == ParameterModifier.Indirect)
                            subject.Append((byte)'!');
                        subject.Append(parsed.Name.Bytes);
                        await ReportFatalAsync(ShellValue.FromBytes(subject.ToMemory()), "cannot assign in this way")
                            .ConfigureAwait(false);
                        return;
                    }
                    var words = await ExpandOperatorWordAsync(
                            parsed.Word, scope, quoteContext,
                            output.Purpose is ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource ? output.Purpose : ExpansionPurpose.Scalar,
                            ImplicitEmptyPolicy.Preserve)
                        .ConfigureAwait(false);
                    if (!scope.CanExpand)
                        return;

                    var budget = scope.Effects.Memory;
                    var expanded = ExpandedWord.JoinWithSpaces(words, budget).ToValue(budget);
                    // Indirect defaults can assign bare names only, after the RHS has expanded.
                    if (parsed.Modifier == ParameterModifier.Indirect
                        && parameter.Source is not { Kind: VariableReferenceKind.Name })
                    {
                        if (indirectName is { } invalidTarget)
                            await ReportFatalAsync(invalidTarget.ToValue(), "invalid variable name").ConfigureAwait(false);
                        else
                        {
                            // An empty aggregate has no indirect target, but its assignment RHS still expands.
                            await ReportFatalAsync(ParameterSubject(), "invalid indirect expansion")
                                .ConfigureAwait(false);
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
                    var assigned = action.Action == NamerefAssignmentAction.Handled || (!elementNameref && parameter.Source is { Kind: VariableReferenceKind.Name } named
                        ? TrySetVariable(named.Name, expanded, scope)
                        : target != null && parameter.Source is { } source
                            && target.TryAssign(scope, source.Name, expanded, AssignmentOperator.Replace));
                    if (assigned)
                    {
                        output.AppendExpansion(action.Value, allowWordSplitting, globQuoted);
                    }
                    else
                    {
                        await scope.Effects.ReportFatalExpansionAsync((target == null ? parsed.Name.ToValue() : target.Name.ToValue()), ShellValue.FromText("readonly variable"), ExpansionFailureKind.Assignment)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    Emit(parameter.Fields);
                }

                return;
            case BracedParameterOperatorKind.ErrorIfUnset:
                if (!isAvailable)
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
                        await scope.Effects.ReportFatalExpansionAsync(ParameterSubject(), message, ExpansionFailureKind.Parameter)
                            .ConfigureAwait(false);
                }
                else
                {
                    Emit(parameter.Fields);
                }

                return;
            case BracedParameterOperatorKind.RemovePrefixShortest:
            case BracedParameterOperatorKind.RemovePrefixLongest:
            case BracedParameterOperatorKind.RemoveSuffixShortest:
            case BracedParameterOperatorKind.RemoveSuffixLongest:
            {
                var words = await ExpandOperatorWordAsync(
                        parsed.Word, scope, ExpansionQuoteContext.Unquoted, ExpansionPurpose.Scalar, ImplicitEmptyPolicy.Preserve)
                    .ConfigureAwait(false);
                if (!scope.CanExpand)
                    return;
                var budget = scope.Effects.Memory;
                if (!GlobPattern.TryFromExpandedWord(ExpandedWord.JoinWithSpaces(words, budget), budget, out var pattern))
                {
                    await ReportSubstitutionFailureAsync("pattern must be valid UTF-8", ExpansionFailureKind.Parameter)
                        .ConfigureAwait(false);
                    return;
                }
                // Keep text-glob semantics and the selected field boundaries; no byte-oriented fallback.
                budget.Charge(checked(256L * (parameter.Fields.Count)));
                var removed = new List<ShellValue>(parameter.Fields.Count);
                foreach (var value in parameter.Fields)
                {
                    scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                    budget.Charge(checked(4L * value.Bytes.Length));
                    if (!value.TryGetUtf8Text(out var text))
                    {
                        await ReportSubstitutionFailureAsync("pattern removal value must be valid UTF-8", ExpansionFailureKind.Parameter)
                            .ConfigureAwait(false);
                        return;
                    }
                    var slice = ApplyPatternRemoval(text, pattern, parsed.Kind, scope.Effects.CancellationToken);
                    if (slice.Length == text.Length)
                    {
                        removed.Add(value);
                        continue;
                    }
                    // Materialize the slice and encode at most three bytes per UTF-16 unit.
                    budget.Charge(checked(4L * (4L * slice.Length)));
                    removed.Add(ShellValue.FromText(slice.ToString()));
                }
                Emit(removed);
                return;
            }
            default:
                throw new InvalidOperationException($"Unexpected braced parameter operator '{parsed.Kind}'.");
        }

        ShellValue ParameterSubject()
        {
            if (parsed.Modifier != ParameterModifier.Indirect)
                return parsed.Name.ToValue();
            var subject = ByteMemoryBuilder.CreateUnaccounted(0);
            subject.Append((byte)'!').Append(parsed.Name.Bytes);
            return ShellValue.FromBytes(subject.ToMemory());
        }

        async Task AppendParameterWordAsync()
        {
            var purpose = output.Purpose == ExpansionPurpose.IndexedCompound
                ? ExpansionPurpose.IndexedCompoundOperand : output.Purpose;
            var expanded = await ExpandOperatorWordAsync(
                    parsed.Word, scope, quoteContext, purpose,
                    purpose is ExpansionPurpose.Scalar or ExpansionPurpose.IndexedCompoundOperand
                        or ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource
                        ? ImplicitEmptyPolicy.Preserve : ImplicitEmptyPolicy.Remove)
                .ConfigureAwait(false);
            if (scope.CanExpand)
                output.AppendExpandedWords(expanded, allowWordSplitting, globQuoted);
        }

        static BracedParameterExpression ParseBracedParameterExpression(ShellSource content)
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
            if (kind == BracedParameterOperatorKind.Transform
                    && (content.Length - wordStart != 1 || !"QEPAaKkUuL"u8.Contains(content[wordStart])))
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
                        return ArraySubscriptScanner.TryScan(content, position, out var closing) ? closing + 1 : start;
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
                var names = scope.Variables.AssignedNames.Where(name => name.StartsWith(prefix, budget))
                    .Select(name =>
                    {
                        // Preflight distinct, sort and output entries before materializing them.
                        budget.Charge(checked(768L + 4L * name.ByteLength));
                        return name.ToValue();
                    })
                    .Distinct().Order(ShellValue.ByteOrder).ToArray();
                return new ParameterValue(parsed.Kind == BracedParameterOperatorKind.NamesAt
                    ? VariableReferenceKind.At : VariableReferenceKind.Star, names, names.Length != 0, null, null);
            }

            var name = parsed.Name;
            var modifier = parsed.Modifier;
            if (modifier == ParameterModifier.Indirect
                && (parsed.Kind != BracedParameterOperatorKind.None
                    || !VariableReference.TryParse(name, out var aggregate) || !aggregate.IsAggregate))
            {
                // Inspection follows the chain but never evaluates the terminal selector.
                if (VariableReference.TryParse(name, out var binding) && binding.Kind == VariableReferenceKind.Name
                    && scope.Variables.TryGetNameref(binding.Name, out _))
                {
                    if (!scope.Variables.TryResolveBinding(binding.Name, selectOwner: null, BindingResolution.LastNameref, out var terminal)
                        || !terminal.Owner.TryGetNameref(terminal.Name, out var referent) || referent.IsEmpty)
                    {
                        await ReportFatalAsync(name.ToValue(), "invalid indirect expansion").ConfigureAwait(false);
                        return new ParameterValue(VariableReferenceKind.Name, [], false, null, null);
                    }
                    return new ParameterValue(VariableReferenceKind.Name, [referent], true, null, null);
                }

                var source = await ReadValueAsync(name, ParameterModifier.None).ConfigureAwait(false);
                if (!scope.CanExpand)
                    return source;
                if (!source.IsSet)
                {
                    if (source.Kind is VariableReferenceKind.At or VariableReferenceKind.Star)
                        return new ParameterValue(VariableReferenceKind.Name, [ShellValue.Empty], false, null, null);
                    await ReportFatalAsync(name.ToValue(), "invalid indirect expansion").ConfigureAwait(false);
                    return source;
                }
                var joined = new ExpandedWordBuilder(scope.Variables, budget, ExpansionPurpose.Scalar, ImplicitEmptyPolicy.Preserve, scope.Effects.CancellationToken);
                var fields = source.RangeSource?.Fields ?? source.Fields;
                // Positional @ joins with spaces; array @ also uses spaces when IFS is empty.
                if (source.Kind == VariableReferenceKind.At
                    && (source.RangeSource is ParameterSource.Positional || FieldSeparators.FromVariables(scope.Variables).IsEmpty))
                    joined.AppendFields(fields, allowWordSplitting: false, globQuoted: true, ExpansionQuoteContext.DoubleQuoted);
                else
                    joined.AppendJoinedFields(fields, allowWordSplitting: false, globQuoted: true);
                name = ShellSource.FromBytes(ExpandedWord.JoinWithSpaces(joined.Build(), budget).ToValue(budget).Bytes);
                indirectName = name;
                var target = ParseBracedParameterExpression(name);
                if (target.Kind != BracedParameterOperatorKind.None || target.Modifier != ParameterModifier.None
                    || GetSpecialParameterKind(name.AsSpan()) == SpecialParameterKind.None
                        && (name.IsEmpty || !VariableNames.IsNameStart((char)name[0])))
                {
                    await ReportFatalAsync(name.ToValue(), "invalid variable name").ConfigureAwait(false);
                    return new ParameterValue(VariableReferenceKind.Name, [], false, null, null);
                }
                modifier = ParameterModifier.None;
            }
            var value = await ReadValueAsync(name, modifier).ConfigureAwait(false);
            if (!scope.CanExpand)
                return value;
            if (indirectName != null && value.IsSet && value.Source is { Kind: VariableReferenceKind.Element }
                && (parsed.Kind == BracedParameterOperatorKind.Substring && !parsed.Word.IsEmpty
                    || parsed.Kind is BracedParameterOperatorKind.RemovePrefixShortest or BracedParameterOperatorKind.RemovePrefixLongest
                        or BracedParameterOperatorKind.RemoveSuffixShortest or BracedParameterOperatorKind.RemoveSuffixLongest
                        or BracedParameterOperatorKind.Replace or BracedParameterOperatorKind.Transform
                        or BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.UpperAll
                        or BracedParameterOperatorKind.LowerFirst or BracedParameterOperatorKind.LowerAll))
            {
                // Reselect explicit indirect subscripts before operands, not selectors hidden behind namerefs.
                // Missing substring bounds fail without reselection.
                var selected = await ReadValueAsync(name, modifier).ConfigureAwait(false);
                // Operand effects use the first availability check; transforms must distinguish unset from empty.
                return parsed.Kind == BracedParameterOperatorKind.Transform ? selected : selected with { IsSet = value.IsSet };
            }
            return value;

            async Task<ParameterValue> ReadValueAsync(ShellSource name, ParameterModifier modifier)
            {
                if (ReadSpecialParameter(name.AsSpan(), scope) is { } special)
                {
                    if (modifier != ParameterModifier.Length)
                        return special;
                    var length = special.Kind is VariableReferenceKind.At or VariableReferenceKind.Star
                        ? special.Fields.Count : CountParameterCharacters(special.Fields[0], scope);
                    return new ParameterValue(VariableReferenceKind.Name,
                        [ShellValue.FromText(Integers.FormatNumber(length))], true, null, null);
                }
                budget.Charge(checked(4L * name.Length));
                if (!VariableReference.TryParse(name, out var reference))
                {
                    await ReportSubstitutionFailureAsync("unsupported special parameter", ExpansionFailureKind.Parameter)
                        .ConfigureAwait(false);
                    return new ParameterValue(VariableReferenceKind.Name, [], false, null, null);
                }

                if (reference.Kind == VariableReferenceKind.Element && reference.Subscript.Length == 0)
                {
                    await ReportSubstitutionFailureAsync("bad substitution", ExpansionFailureKind.Parameter).ConfigureAwait(false);
                    return new ParameterValue(reference.Kind, [], false, reference, null);
                }

                if (reference.IsAggregate)
                {
                    var resolved = scope.Variables.ResolveNamerefName(reference.Name);
                    IEnumerable<ShellValue> values;
                    int count;
                    if (scope.Variables.IndexedArrays.TryGetValue(resolved, out var array))
                    {
                        values = modifier == ParameterModifier.Indirect ? array.PresentKeys.Select(EncodeName) : array.PresentValues;
                        count = array.Count;
                    }
                    else if (scope.Variables.AssociativeArrays.TryGetValue(resolved, out var map))
                    {
                        values = modifier == ParameterModifier.Indirect ? map.PresentKeys : map.PresentValues;
                        count = map.Count;
                    }
                    else if (scope.Variables.TryGetScalar(resolved, out var scalar))
                    {
                        values = modifier == ParameterModifier.Indirect ? [ShellValue.FromText("0")] : [scalar];
                        count = 1;
                    }
                    else
                    {
                        values = [];
                        count = 0;
                    }

                    if (modifier == ParameterModifier.Length)
                        return new ParameterValue(VariableReferenceKind.Name,
                            [ShellValue.FromText(Integers.FormatNumber(count))], true, null, null);

                    if (parsed.Kind is BracedParameterOperatorKind.None or BracedParameterOperatorKind.Substring
                        or BracedParameterOperatorKind.Replace or BracedParameterOperatorKind.Transform
                        or BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.UpperAll
                        or BracedParameterOperatorKind.LowerFirst or BracedParameterOperatorKind.LowerAll
                        && modifier == ParameterModifier.None)
                    {
                        ParameterSource? source = scope.Variables.IndexedArrays.TryGetValue(resolved, out var indexedSource)
                            ? new ParameterSource.Indexed(indexedSource)
                            : scope.Variables.AssociativeArrays.TryGetValue(resolved, out var associativeSource)
                                ? new ParameterSource.Associative(associativeSource) : null;
                        return new ParameterValue(reference.Kind, source == null ? values.ToArray() : [], count != 0, reference, null)
                            { RangeSource = source };
                    }

                    // Operand expansion can mutate the source array; retain the selected values first.
                    budget.Charge(checked(256L * (count)));
                    var fields = values.ToArray();
                    return new ParameterValue(reference.Kind, fields, fields.Length != 0, reference, null);
                }

                var target = await ResolveReferenceAsync(reference,
                        modifier == ParameterModifier.Length ? ReferenceUse.Length : ReferenceUse.Read, scope, arithmeticPurpose)
                    .ConfigureAwait(false);
                var value = ShellValue.Empty;
                var isSet = target != null && target.TryRead(scope.Variables, out value);
                return modifier == ParameterModifier.Length
                    ? new ParameterValue(VariableReferenceKind.Name,
                        [ShellValue.FromText(Integers.FormatNumber(CountParameterCharacters(value, scope)))], true, null, null)
                    : new ParameterValue(reference.Kind, [value], isSet, reference, target);

                // Special parameters are values, not assignable variable locations.
                static ParameterValue? ReadSpecialParameter(ReadOnlySpan<byte> name, ScopedEvaluation scope)
                {
                    var kind = GetSpecialParameterKind(name);
                    if (kind == SpecialParameterKind.None)
                        return null;
                    if (kind is SpecialParameterKind.At or SpecialParameterKind.Star)
                        return new ParameterValue(kind == SpecialParameterKind.At ? VariableReferenceKind.At : VariableReferenceKind.Star,
                            scope.Variables.PositionalParameters, scope.Variables.PositionalParameters.Count != 0, null, null)
                            { RangeSource = new ParameterSource.Positional(scope.Variables.PositionalParameters, scope.Variables.ArgumentZero) };

                    var value = ShellValue.Empty;
                    var isSet = true;
                    switch (kind)
                    {
                        case SpecialParameterKind.Status:
                            value = ShellValue.FromText(Integers.FormatNumber(scope.Effects.LastExitCode ?? 0));
                            break;
                        case SpecialParameterKind.Count:
                            value = ShellValue.FromText(Integers.FormatNumber(scope.Variables.PositionalParameters.Count));
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

            ShellValue EncodeName(string name)
            {
                budget.Charge(checked(4L * (3L * name.Length)));
                return ShellValue.FromText(name);
            }
        }

        Task ReportSubstitutionFailureAsync(string message, ExpansionFailureKind kind)
        {
            var subject = ByteMemoryBuilder.CreateUnaccounted(0);
            subject.Append("${"u8).Append(content.Bytes).Append("}"u8);
            return scope.Effects.ReportFatalExpansionAsync(ShellValue.FromBytes(subject.ToMemory()), ShellValue.FromText(message), kind);
        }

        Task ReportFatalAsync(ShellValue subject, string message)
        {
            return scope.Effects.ReportFatalExpansionAsync(subject, ShellValue.FromText(message), ExpansionFailureKind.Parameter);
        }

        void Emit(IEnumerable<ShellValue> fields) =>
            AppendParameterFields(parameter.Kind, fields, output, allowWordSplitting, globQuoted, quoteContext);

        async Task<IReadOnlyList<ShellValue>> ApplyValueOperatorAsync()
        {
            if (!parameter.IsSet)
                return parameter.Fields;

            var budget = scope.Effects.Memory;
            var cancellationToken = scope.Effects.CancellationToken;
            var result = new List<ShellValue>();
            if (parsed.Kind == BracedParameterOperatorKind.Transform)
            {
                foreach (var value in ReadFields())
                {
                    switch (parsed.Word[0])
                    {
                        case (byte)'E':
                            budget.Charge(checked(4L * value.Bytes.Length));
                            result.Add(ShellEscapeDecoder.DecodeAnsiCValue(value.Bytes.Span));
                            break;
                        case (byte)'Q':
                            // Every input byte can become a four-byte escape; reserve before building it.
                            var maximumLength = 4L * value.Bytes.Length + 3;
                            budget.Charge(checked(4L * maximumLength));
                            var quoted = BoundedByteMemoryBuilder.CreateUnaccounted(0, (int)maximumLength);
                            if (!ShellArgumentEscaping.TryAppendQuoted(quoted, value.Bytes.Span, ShellQuoteStyle.SingleQuoted))
                                throw new InvalidOperationException("Parameter quote exceeded its reserved bound.");
                            result.Add(ShellValue.FromBytes(quoted.ToMemory()));
                            break;
                        default:
                            result.Add(ChangeCase(value, parsed.Word[0] != (byte)'L', parsed.Word[0] == (byte)'u', null));
                            break;
                    }
                }
                return result;
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
                var pattern = await ExpandOperandAsync(separator < 0 ? operand : operand[..separator]).ConfigureAwait(false);
                if (!scope.CanExpand)
                    return [];
                // Anchors can come from an expansion, but quoted prefixes and global replacements stay literal.
                if (mode == ParameterReplacementMode.First && !pattern.Value.IsEmpty
                    && pattern.Mask.AsSpan()[0] == GlobMask.Active && pattern.Value.Bytes.Span[0] is (byte)'#' or (byte)'%')
                {
                    mode = pattern.Value.Bytes.Span[0] == (byte)'#' ? ParameterReplacementMode.Prefix : ParameterReplacementMode.Suffix;
                    pattern = new ParameterOperand(ShellValue.FromBytes(pattern.Value.Bytes[1..]),
                        new GlobMask(pattern.Mask.Memory.IsEmpty ? ReadOnlyMemory<byte>.Empty : pattern.Mask.Memory[1..]));
                }
                var canMatch = TryUnescapePattern(pattern, out pattern);
                var replacement = await ExpandOperandAsync(separator < 0 ? ShellSource.Empty : operand[(separator + 1)..]).ConfigureAwait(false);
                if (!scope.CanExpand)
                    return [];
                var template = EncodeReplacement(replacement);
                foreach (var value in ReadFields())
                    result.Add(canMatch ? Replace(value, pattern, template, mode) : value);
                return result;

                static int FindReplacementSeparator(ShellSource source, ParameterReplacementMode mode)
                {
                    // After the global marker, a leading slash belongs to the pattern.
                    var start = mode == ParameterReplacementMode.All && !source.IsEmpty && source[0] == (byte)'/' ? 1 : 0;
                    var cursor = new ShellSourceCursor(source, start);
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

                ShellValue EncodeReplacement(ParameterOperand replacement)
                {
                    if (replacement.Mask.Memory.IsEmpty)
                        return replacement.Value;
                    var bytes = replacement.Value.Bytes.Span;
                    var mask = replacement.Mask.AsSpan();
                    budget.Charge(checked(4L * (2L * bytes.Length)));
                    var builder = ByteMemoryBuilder.CreateUnaccounted(0);
                    for (var index = 0; index < bytes.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Preserve quoted escapes in one stream: an earlier unquoted backslash can consume them.
                        if (mask[index] != GlobMask.Active && bytes[index] is (byte)'&' or (byte)'\\')
                            builder.Append((byte)'\\');
                        builder.Append(bytes[index]);
                    }
                    return ShellValue.FromBytes(builder.ToMemory());
                }

                ShellValue Replace(ShellValue value, ParameterOperand pattern, ShellValue replacement, ParameterReplacementMode mode)
                {
                    var bytes = value.Bytes.Span;
                    if (pattern.Value.IsEmpty && mode is ParameterReplacementMode.First or ParameterReplacementMode.All)
                        return value;
                    var builder = ByteMemoryBuilder.CreateUnaccounted(0);
                    var position = 0;
                    var copiedThrough = 0;
                    do
                    {
                        var length = Match(pattern, bytes[position..], mode == ParameterReplacementMode.Suffix);
                        if (length < 0)
                        {
                            if (mode == ParameterReplacementMode.Prefix || position == bytes.Length)
                                break;
                            position += Utf8Text.FirstCharacterLength(bytes[position..]);
                            continue;
                        }
                        Append(bytes[copiedThrough..position]);
                        var insert = replacement.Bytes.Span;
                        var literalStart = 0;
                        for (var index = 0; index < insert.Length; index++)
                        {
                            if (insert[index] == (byte)'&')
                            {
                                Append(insert[literalStart..index]);
                                Append(bytes.Slice(position, length));
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
                        position += length;
                        copiedThrough = position;
                        if (mode != ParameterReplacementMode.All || position == bytes.Length)
                            break;
                        if (length == 0)
                            position += Utf8Text.FirstCharacterLength(bytes[position..]);
                    } while (position < bytes.Length || mode == ParameterReplacementMode.Suffix && position == bytes.Length);
                    Append(bytes[copiedThrough..]);
                    return ShellValue.FromBytes(builder.ToMemory());

                    void Append(ReadOnlySpan<byte> content)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        budget.Charge(checked(4L * content.Length));
                        builder.Append(content);
                    }
                }
            }

            var casePattern = parsed.Word.IsEmpty ? null : await ExpandOperandAsync(parsed.Word).ConfigureAwait(false);
            if (!scope.CanExpand)
                return [];
            var canMatchCase = casePattern == null || TryUnescapePattern(casePattern, out casePattern);
            var upper = parsed.Kind is BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.UpperAll;
            var firstOnly = parsed.Kind is BracedParameterOperatorKind.UpperFirst or BracedParameterOperatorKind.LowerFirst;
            foreach (var value in ReadFields())
                result.Add(canMatchCase ? ChangeCase(value, upper, firstOnly, casePattern) : value);
            return result;

            bool TryUnescapePattern(ParameterOperand operand, out ParameterOperand pattern)
            {
                var success = operand.Mask.TryUnescape(operand.Value, budget, cancellationToken, out var value, out var mask);
                pattern = new ParameterOperand(value, mask);
                return success;
            }

            IEnumerable<ShellValue> ReadFields()
            {
                // Array operands can change values or add entries; enumerate and charge only after their effects.
                foreach (var value in parameter.RangeSource?.Fields ?? parameter.Fields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.Charge(256);
                    yield return value;
                }
            }

            async Task<ParameterOperand> ExpandOperandAsync(ShellSource source)
            {
                var words = await ExpandOperatorWordAsync(source, scope, ExpansionQuoteContext.Unquoted,
                    ExpansionPurpose.Scalar, ImplicitEmptyPolicy.Preserve).ConfigureAwait(false);
                var word = ExpandedWord.JoinWithSpaces(words, budget);
                var value = word.ToValue(budget);
                if (!word.Segments.Any(segment => segment.IsGlobQuoted && !segment.Content.IsEmpty))
                    return new ParameterOperand(value, new GlobMask(ReadOnlyMemory<byte>.Empty));
                budget.Charge(checked(4L * value.Bytes.Length));
                var mask = new byte[value.Bytes.Length];
                var position = 0;
                foreach (var segment in word.Segments)
                {
                    var length = segment.Content.Bytes.Length;
                    if (!segment.IsGlobQuoted)
                        mask.AsSpan(position, length).Fill(GlobMask.Active);
                    position += length;
                }
                return new ParameterOperand(value, new GlobMask(mask));
            }

            int Match(ParameterOperand pattern, ReadOnlySpan<byte> value, bool requireEnd)
            {
                var mask = pattern.Mask.AsSpan();
                var matched = TextGlobMatcher.MatchUtf8(pattern.Value.Bytes.Span, in mask, value,
                    requireEnd, cancellationToken, out var length);
                return matched == GlobMatchResult.Match ? length : -1;
            }

            ShellValue ChangeCase(ShellValue value, bool upper, bool firstOnly, ParameterOperand? pattern)
            {
                var bytes = value.Bytes.Span;
                // ASCII stays one byte; any other valid character starts with at least two bytes.
                // Reserve a twofold bound instead of locking the shared allowance for every character.
                budget.Charge(checked(4L * (2L * bytes.Length)));
                var builder = ByteMemoryBuilder.CreateUnaccounted(0);
                Span<byte> encoded = stackalloc byte[4];
                for (var position = 0; position < bytes.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var valid = Rune.DecodeFromUtf8(bytes[position..], out var rune, out var length) == OperationStatus.Done;
                    if (!valid)
                        length = 1;
                    var original = bytes.Slice(position, length);
                    if (valid && (!firstOnly || position == 0) && (pattern == null || Match(pattern, original, true) >= 0))
                    {
                        var changed = upper ? Rune.ToUpperInvariant(rune) : Rune.ToLowerInvariant(rune);
                        var written = changed.EncodeToUtf8(encoded);
                        builder.Append(encoded[..written]);
                    }
                    else
                    {
                        builder.Append(original);
                    }
                    position += length;
                }
                return ShellValue.FromBytes(builder.ToMemory());
            }
        }

        static ReadOnlySpan<char> ApplyPatternRemoval(string value, GlobPattern pattern, BracedParameterOperatorKind kind,
            CancellationToken cancellationToken)
        {
            var removePrefix = kind is BracedParameterOperatorKind.RemovePrefixShortest
                or BracedParameterOperatorKind.RemovePrefixLongest;
            var longest = kind is BracedParameterOperatorKind.RemovePrefixLongest
                or BracedParameterOperatorKind.RemoveSuffixLongest;
            for (var offset = 0; offset <= value.Length; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = longest ? value.Length - offset : offset;
                var candidate = removePrefix ? value.AsSpan(0, length) : value.AsSpan(value.Length - length);
                if (pattern.IsMatch(candidate, cancellationToken))
                    return removePrefix ? value.AsSpan(length) : value.AsSpan(0, value.Length - length);
            }

            return value.AsSpan();
        }

        static async Task ExpandSubstringAsync(
            BracedParameterExpression expression, ParameterValue parameter, ScopedEvaluation scope,
            ExpandedWordBuilder output, bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext,
            ExpansionPurpose arithmeticPurpose)
        {
            if (!parameter.IsSet && parameter.RangeSource is not ParameterSource.Positional)
            {
                Emit(parameter.Kind is VariableReferenceKind.At or VariableReferenceKind.Star ? parameter.Fields : [parameter.Value]);
                return;
            }

            var separator = FindSeparator(expression.Word);
            var offset = await ExpandArithmeticOperandAsync(
                separator < 0 ? expression.Word : expression.Word[..separator], scope, arithmeticPurpose).ConfigureAwait(false);
            if (offset is not { } start)
                return;

            var source = parameter.RangeSource;
            var characters = source == null ? CountParameterCharacters(parameter.Value, scope) : 0;
            // Offset effects precede the negative-origin lookup; length effects precede enumeration.
            var maximum = source switch
            {
                ParameterSource.Indexed indexed => indexed.Array.MaxIndex ?? -1,
                ParameterSource.Associative associative => associative.Array.Count,
                ParameterSource.Positional positional => (long)positional.Arguments.Count + 1,
                _ => characters,
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
                Emit(source == null ? [ShellValue.Empty] : []);
                return;
            }

            long? length = null;
            if (separator >= 0)
            {
                length = await ExpandArithmeticOperandAsync(expression.Word[(separator + 1)..], scope, arithmeticPurpose).ConfigureAwait(false);
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
                            Emit(indexed.Array.ValuesFrom(first, count));
                        return;
                    case ParameterSource.Associative associative:
                        // Keep the platform's unsigned byte order rather than native Bash hash traversal.
                        Emit(associative.Array.PresentValues.Skip((int)Math.Max(0, start - 1)).Take(count));
                        return;
                    case ParameterSource.Positional positional:
                        Emit((start == 0
                            ? Enumerable.Repeat(positional.ArgumentZero, 1).Concat(positional.Arguments)
                            : positional.Arguments.Skip((int)(start - 1))).Take(count));
                        return;
                }
            }

            var end = length is { } amount
                ? amount < 0 ? characters + amount : start + Math.Min(characters - start, amount)
                : characters;
            if (end < start)
            {
                await ReportInvalidLengthAsync().ConfigureAwait(false);
                return;
            }
            var bytes = parameter.Value.Bytes;
            var position = 0;
            var from = 0;
            for (var character = 0; character < end; character++)
            {
                scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                if (character == start)
                    from = position;
                position += Utf8Text.FirstCharacterLength(bytes.Span[position..]);
            }
            if (start == end)
                from = position;
            Emit([ShellValue.FromBytes(bytes.Slice(from, position - from))]);

            // Enumerate into the budgeted builder; never copy the full array or allocate its holes.
            void Emit(IEnumerable<ShellValue> fields) =>
                AppendParameterFields(parameter.Kind, fields, output, allowWordSplitting, globQuoted, quoteContext);

            Task ReportInvalidLengthAsync() => scope.Effects.ReportFatalExpansionAsync(
                ShellValue.FromText(Integers.FormatNumber(length.GetValueOrDefault())), ShellValue.FromText("substring expression < 0"),
                ExpansionFailureKind.Arithmetic);

            static int FindSeparator(ShellSource text)
            {
                var cursor = new ShellSourceCursor(text, 0);
                var parentheses = 0;
                var conditional = 0;
                while (!cursor.IsAtEnd)
                {
                    if (cursor.TryConsumeQuoteOrEscape()
                        || cursor.SkipNestedExpansion(BracedParameterRecognition.All) is ShellNestedExpansionScan.Complete)
                        continue;
                    if (!cursor.IsQuoted)
                    {
                        var current = cursor.Current;
                        if (current == '(') parentheses++;
                        else if (current == ')') parentheses--;
                        else if (parentheses == 0 && current == '?') conditional++;
                        else if (parentheses == 0 && current == ':' && conditional > 0)
                        {
                            conditional--;
                            cursor.Advance();
                            continue;
                        }
                        if (current == ':' && parentheses == 0)
                            return cursor.Position;
                    }
                    cursor.Advance();
                }
                return -1;
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

    private enum BracedParameterOperatorKind
    {
        Invalid,
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

    private enum ParameterReplacementMode { First, All, Prefix, Suffix }

    private sealed record ParameterOperand(ShellValue Value, GlobMask Mask);

    private readonly record struct BracedParameterExpression(
        ShellSource Name,
        BracedParameterOperatorKind Kind,
        ShellSource Word,
        bool UsesColon,
        ParameterModifier Modifier);

    private static void AppendParameterFields(
        VariableReferenceKind kind, IEnumerable<ShellValue> fields, ExpandedWordBuilder output,
        bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext)
    {
        if (kind == VariableReferenceKind.At)
            output.AppendFields(fields, allowWordSplitting, globQuoted, quoteContext);
        else if (kind == VariableReferenceKind.Star)
            output.AppendJoinedFields(fields, allowWordSplitting, globQuoted);
        else
            foreach (var field in fields)
                output.AppendExpansion(field, allowWordSplitting, globQuoted);
    }
}

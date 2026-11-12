using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Expands compound operands against the old binding, before replacement starts.</summary>
    private async Task<IReadOnlyList<ExpandedArrayItem>?> ExpandCompoundArrayItemsAsync(
        BindingName name, IReadOnlyList<CompoundArrayItem> items, ArrayKind kind, AssignmentOperator operation, AssignmentContext context, ScopedEvaluation scope)
    {
        var associative = kind == ArrayKind.Associative;
        var retainQuotes = context == AssignmentContext.QuotedIndexedDeclaration;
        var expanded = new List<ExpandedArrayItem>();
        // Direct assignments classify malformed bracket words before expansion; declaration operands retain parser classification.
        var bracketForm = items.Count > 0
            && (context == AssignmentContext.Declaration ? items[0].Element != null : items[0].Word.Parts is [SourceWordPart { Kind: WordPartKind.Unquoted } first, ..]
            && first.Source.AsSpan().StartsWith("["u8));
        if (associative && !bracketForm)
        {
            for (var index = 0; index < items.Count; index += 2)
            {
                var key = await ExpandValueAsync(items[index].Word).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return null;
                var value = index + 1 < items.Count
                    ? await ExpandValueAsync(items[index + 1].Word).ConfigureAwait(false)
                    : ShellValue.Empty;
                if (_state.HasPendingExpansionError)
                    return null;
                expanded.Add(new ExpandedArrayItem(
                    VariableReference.ForElement(name, ShellSource.FromBytes(key.Bytes)), key, value,
                    AssignmentOperator.Replace, PrefixWithElementZero: false));
            }
        }
        else
        {
            foreach (var item in items)
            {
                if (associative && item.Element == null)
                {
                    // Invalid words in explicit associative form are diagnosed without expanding them.
                    await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText("must use subscript when assigning associative array"))
                        .ConfigureAwait(false);
                    continue;
                }

                // Bash 5.2 brace-generated words lose their indexed-assignment classification.
                var words = associative || retainQuotes ? [item.Word] : WordExpansion.ExpandBraceWords(item.Word, scope);
                if (item.Element is { } element && words.Count == 1 && ReferenceEquals(words[0], item.Word))
                {
                    // Compound selectors remove quotes before arithmetic; direct selectors retain them.
                    var selector = element.Subscript.Length == 0
                        ? ShellValue.Empty
                        : await WordExpansion.ExpandSubscriptAsync(element.Subscript, scope,
                            associative || retainQuotes ? ExpansionPurpose.Scalar : ExpansionPurpose.IndexedCompound,
                            allowLeadingTilde: false).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return null;
                    var value = await ExpandValueAsync(element.Value).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return null;
                    if (retainQuotes)
                    {
                        selector = QuoteLiteral(selector);
                        value = QuoteLiteral(value);
                    }
                    expanded.Add(associative || retainQuotes
                        ? new ExpandedArrayItem(VariableReference.ForElement(name, element.Subscript), selector, value,
                            element.Operator, PrefixWithElementZero: false)
                        : ClassifyIndexedItem(element, selector, value));
                }
                else
                {
                    foreach (var word in words)
                    {
                        if (retainQuotes)
                        {
                            var value = await ExpandValueAsync(word).ConfigureAwait(false);
                            if (_state.HasPendingExpansionError)
                                return null;
                            expanded.Add(new ExpandedArrayItem(null, ShellValue.Empty, QuoteLiteral(value),
                                AssignmentOperator.Replace, PrefixWithElementZero: false));
                            continue;
                        }
                        var fields = await WordExpansion.ExpandWithoutBraceAsync(
                            word, scope, allowWordSplitting: true, ExpansionPurpose.Normal, allowLeadingTilde: true)
                            .ConfigureAwait(false);
                        if (_state.HasPendingExpansionError)
                            return null;
                        var values = await ExpandExpandedWordsAsync(fields).ConfigureAwait(false);
                        if (_state.HasPendingExpansionError)
                            return null;
                        foreach (var value in values)
                            expanded.Add(new ExpandedArrayItem(null, ShellValue.Empty, value,
                                AssignmentOperator.Replace, PrefixWithElementZero: false));
                    }
                }
            }
        }

        return expanded;

        Task<ShellValue> ExpandValueAsync(Word word) => WordExpansion.ExpandCompoundValueAsync(
            word, scope, retainQuotes ? ArrayKind.Associative : kind);

        ShellValue QuoteLiteral(ShellValue value)
        {
            var length = (long)value.Bytes.Length + 2 + 3L * value.Bytes.Span.Count((byte)'\'');
            if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            Memory.Charge(checked(4L * length));
            var quoted = BoundedByteMemoryBuilder.CreateUnaccounted((int)length, (int)length);
            if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(quoted, value.Bytes.Span))
                throw new InvalidOperationException("Precomputed literal quoting length was too small.");
            return ShellValue.FromBytes(quoted.ToMemory());
        }

        ExpandedArrayItem ClassifyIndexedItem(CompoundArrayElement element, ShellValue selector, ShellValue value)
        {
            // Ordinary arithmetic cannot change the item boundaries. Avoid copying its RHS.
            if (selector.Bytes.Span.IndexOfAny("[]'\"\\$`"u8) < 0)
                return new ExpandedArrayItem(
                    VariableReference.ForElement(name, element.Subscript), selector, value,
                    element.Operator, PrefixWithElementZero: false);

            // Expansion may expose an earlier closing bracket or turn the assignment into data.
            // Flatten once under the same allowance; never expand or split the resulting RHS again.
            var flattened = FlattenCompoundArrayItem(selector, value, element.Operator);
            var source = ShellSource.FromBytes(flattened.Bytes);
            if (ArraySubscriptScanner.TryScan(source, 0, out var closing))
            {
                var equals = closing + 1;
                var operation = AssignmentOperator.Replace;
                if (equals < source.Length && source[equals] == (byte)'+')
                {
                    operation = AssignmentOperator.Append;
                    equals++;
                }
                if (equals < source.Length && source[equals] == (byte)'=')
                {
                    var subscript = source.Slice(1, closing - 1);
                    return new ExpandedArrayItem(VariableReference.ForElement(name, subscript),
                        subscript.ToValue(), source.Slice(equals + 1).ToValue(), operation, PrefixWithElementZero: false);
                }
            }
            return new ExpandedArrayItem(null, ShellValue.Empty, flattened, AssignmentOperator.Replace,
                PrefixWithElementZero: operation == AssignmentOperator.Append);
        }
    }

    private ShellValue FlattenCompoundArrayItem(ShellValue selector, ShellValue value, AssignmentOperator operation)
    {
        Memory.Charge(checked(1024L));
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromText("["), isGlobQuoted: true),
            new ExpandedSegment(selector, isGlobQuoted: true),
            new ExpandedSegment(ShellValue.FromText(operation == AssignmentOperator.Append ? "]+=" : "]="), isGlobQuoted: true),
            new ExpandedSegment(value, isGlobQuoted: true)]);
        return word.ToValue(Memory);
    }

    /// <summary>Applies expanded items in order; selector effects observe earlier writes.</summary>
    private async Task<bool> ApplyCompoundArrayItemsAsync(
        BindingName name, AssignmentOperator operation, ArrayKind kind, IReadOnlyList<ExpandedArrayItem> expanded,
        ScopedEvaluation scope, Variables targetVariables, BindingName sourceName)
    {
        var associative = kind == ArrayKind.Associative;
        // Word expansion sees the previous binding. Arithmetic selection and writes see
        // the replacement as it is built; a later arithmetic failure does not roll it back.
        var append = operation == AssignmentOperator.Append;
        if (!append || !associative && !targetVariables.IndexedArrays.ContainsKey(name))
        {
            IReadOnlyList<ShellValue> initial = append && targetVariables.Scalars.TryGetValue(name, out var scalar)
                ? [scalar]
                : [];
            if (!targetVariables.TryAssignResolvedArray(name, initial, kind))
                return false;
        }

        if (append && associative && !targetVariables.TryDeclareResolvedArray(name, kind, ScalarArrayPromotion.RetainValue))
            return false;

        if (associative) targetVariables.AssociativeArrays[name].MarkInitialized();
        else targetVariables.IndexedArrays[name].MarkInitialized();

        var lastIndex = append && targetVariables.IndexedArrays.TryGetValue(name, out var indexed)
            ? indexed.MaxIndex ?? -1
            : -1;
        foreach (var item in expanded)
        {
            WordExpansion.ResolvedVariableReference? target;
            if (item.Target is { } reference)
            {
                target = await WordExpansion.ResolveCompoundElementAsync(
                    VariableReference.ForElement(name, reference.Subscript), item.ExpandedSubscript, scope, targetVariables).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return false;
                if (target == null)
                    continue;
            }
            else
            {
                // Sparse storage deliberately excludes Bash's signed-overflow positions.
                if (lastIndex == long.MaxValue || !IndexedArrayIndex.TryCreate(lastIndex + 1, out var position))
                {
                    await ReportFatalExpansionAsync(name.ToValue(), ShellValue.FromText("array index overflow"), ExpansionFailureKind.Assignment).ConfigureAwait(false);
                    return false;
                }
                target = new WordExpansion.ResolvedVariableReference.Indexed(name, position);
            }

            var value = item.Value;
            // Bash 5.2 prefixes reclassified += items with element zero, including earlier writes.
            if (item.PrefixWithElementZero && targetVariables.TryGetExpansionValue(name, out var previous))
                value = WordExpansion.AppendAssignmentValue(previous, value, scope);
            if (!target.TryAssign(scope with { Variables = targetVariables }, sourceName, value, item.Operator))
                return false;
            if (target is WordExpansion.ResolvedVariableReference.Indexed element)
                lastIndex = element.Index.Value;
        }
        return true;

    }
}

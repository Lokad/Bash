using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Expands and applies an initializer whose destination has already been selected.</summary>
    private async Task<bool> ExpandAndApplyCompoundArrayItemsAsync(
        VariableBinding target, ArrayKind kind, AssignmentOperator operation, IReadOnlyList<CompoundArrayItem> items,
        AssignmentContext context, ScopedEvaluation scope, ScopedEvaluation publicationScope, BindingName sourceName, bool initializeReadonly)
    {
        if (kind == ArrayKind.Associative)
            return await ApplyAssociativeInitializerAsync(target, operation, scope, publicationScope, sourceName,
                initializeReadonly, accept => VisitAssociativeInitializerAsync(target.Name, items, context, scope,
                    prepareDeclaration: false, accept)).ConfigureAwait(false);

        using var itemMemory = items.Count == 0 ? null : new MemoryCapacity(Memory.Budget, 0);
        IReadOnlyList<ExpandedArrayItem>? expanded = itemMemory is null ? []
            : await ExpandCompoundArrayItemsAsync(target.Name, items, kind, operation, context, scope, itemMemory)
                .ConfigureAwait(false);
        return expanded != null && await ApplyCompoundArrayItemsAsync(target.Name, operation, kind, expanded,
            scope, publicationScope, target.Owner, sourceName, initializeReadonly).ConfigureAwait(false);
    }

    /// <summary>Expands compound operands against the old binding, before replacement starts.</summary>
    private async Task<List<ExpandedArrayItem>?> ExpandCompoundArrayItemsAsync(
        BindingName name, IReadOnlyList<CompoundArrayItem> items, ArrayKind kind, AssignmentOperator operation,
        AssignmentContext context, ScopedEvaluation scope, MemoryCapacity itemMemory)
    {
        var retainQuotes = context == AssignmentContext.QuotedIndexedDeclaration;
        var expanded = new List<ExpandedArrayItem>();
        if (kind == ArrayKind.Associative)
        {
            if (!await VisitAssociativeInitializerAsync(name, items, context, scope, prepareDeclaration: true, StageItem)
                    .ConfigureAwait(false))
                return null;
        }
        else
        {
            if (!retainQuotes)
                foreach (var item in items)
                    if (!await WordExpansion.CheckBraceDelimitersAsync(item.Word, scope, protectSubscript: item.Element != null)
                            .ConfigureAwait(false))
                        return null;

            foreach (var item in items)
            {
                if (!retainQuotes && item.Element == null && scope.CanExpand && GetLiteralWithoutGlob(item.Word) is { } literal)
                {
                    CheckCancellation();
                    AddItem(new ExpandedArrayItem(null, ShellValue.Empty, literal.ToValue(Memory),
                        AssignmentOperator.Replace, PrefixWithElementZero: false));
                    continue;
                }

                // Bash 5.2 brace-generated words lose their indexed-assignment classification.
                var words = retainQuotes ? [item.Word] : WordExpansion.ExpandBraceWords(item.Word, scope);
                if (item.Element is { } element && words.Count == 1 && ReferenceEquals(words[0], item.Word))
                {
                    // Compound selectors remove quotes before arithmetic; direct selectors retain them.
                    var selector = element.Subscript.Length == 0
                        ? ShellValue.Empty
                        : await WordExpansion.ExpandSubscriptAsync(element.Subscript, scope,
                            retainQuotes ? ExpansionPurpose.Scalar : ExpansionPurpose.IndexedCompound,
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
                    AddItem(retainQuotes
                        ? new ExpandedArrayItem(element.Subscript, selector, value,
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
                            AddItem(new ExpandedArrayItem(null, ShellValue.Empty, QuoteLiteral(value),
                                AssignmentOperator.Replace, PrefixWithElementZero: false));
                            continue;
                        }
                        var fields = await WordExpansion.ExpandWithoutBraceAsync(
                            word, scope, allowWordSplitting: true, ExpansionPurpose.Normal, TildeExpansion.Leading)
                            .ConfigureAwait(false);
                        if (_state.HasPendingExpansionError)
                            return null;
                        if (!await AppendExpandedWordsAsync(fields, expanded, itemMemory,
                                static value => new ExpandedArrayItem(null, ShellValue.Empty, value,
                                    AssignmentOperator.Replace, PrefixWithElementZero: false)).ConfigureAwait(false)
                            || _state.HasPendingExpansionError)
                            return null;
                    }
                }
            }
        }

        itemMemory.ReleaseUnusedCapacity();
        return expanded;

        void AddItem(ExpandedArrayItem item)
        {
            Memory.PrepareListCapacity(itemMemory, expanded, 1);
            expanded.Add(item);
        }

        ValueTask<bool> StageItem(ExpandedArrayItem item)
        {
            AddItem(item);
            return ValueTask.FromResult(true);
        }

        Task<ShellValue> ExpandValueAsync(Word word) => WordExpansion.ExpandCompoundValueAsync(
            word, scope, retainQuotes ? ArrayKind.Associative : kind);

        ShellValue QuoteLiteral(ShellValue value)
        {
            var length = ShellArgumentEscaping.GetLiteralSingleQuotedLength(value.Bytes.Span);
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
                    element.Subscript, selector, value,
                    element.Operator, PrefixWithElementZero: false);

            // Expansion may expose an earlier closing bracket or turn the assignment into data.
            // Flatten once under the same allowance; never expand or split the resulting RHS again.
            var flattened = FlattenCompoundArrayItem(selector, value, element.Operator);
            var source = ShellSource.FromBytes(flattened.Bytes);
            if (ArraySubscriptScanner.TryScan(new ShellScanContext { SyntaxMemory = Memory.Workspace }, source, 0, out var closing))
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
                    return new ExpandedArrayItem(subscript,
                        subscript.ToValue(), source.Slice(equals + 1).ToValue(), operation, PrefixWithElementZero: false);
                }
            }
            return new ExpandedArrayItem(null, ShellValue.Empty, flattened, AssignmentOperator.Replace,
                PrefixWithElementZero: operation == AssignmentOperator.Append);
        }
    }

    /// <summary>Expands one associative key and its value in initializer order.</summary>
    private async Task<bool> VisitAssociativeInitializerAsync(
        BindingName name, IReadOnlyList<CompoundArrayItem> items, AssignmentContext context, ScopedEvaluation scope,
        bool prepareDeclaration, Func<ExpandedArrayItem, ValueTask<bool>> accept)
    {
        // Direct assignments also classify malformed bracket words as explicit items.
        var explicitItems = items.Count != 0 && (context == AssignmentContext.Declaration
            ? items[0].Element != null
            : items[0].Word.Parts is [SourceWordPart { Kind: WordPartKind.Unquoted } first, ..]
                && first.Source.AsSpan().StartsWith("["u8));
        for (var index = 0; index < items.Count; index += explicitItems ? 1 : 2)
        {
            CheckCancellation();
            var item = items[index];
            var element = explicitItems ? item.Element : null;
            if (explicitItems && element == null)
            {
                await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText("must use subscript when assigning associative array"))
                    .ConfigureAwait(false);
                continue;
            }
            var key = element is { } explicitElement
                ? explicitElement.Subscript.IsEmpty ? ShellValue.Empty
                    : await WordExpansion.ExpandSubscriptAsync(explicitElement.Subscript, scope, ExpansionPurpose.Scalar,
                        allowLeadingTilde: false).ConfigureAwait(false)
                : await ExpandValueAsync(item.Word).ConfigureAwait(false);
            if (_state.HasPendingExpansionError)
                return false;
            if (key.IsEmpty && !prepareDeclaration)
            {
                await ReportEmptyAssociativeKeyAsync(item.Word, scope).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return false;
                continue;
            }

            var valueWord = element?.Value ?? (index + 1 < items.Count ? items[index + 1].Word : null);
            var value = valueWord == null ? ShellValue.Empty : await ExpandValueAsync(valueWord).ConfigureAwait(false);
            if (_state.HasPendingExpansionError || !await accept(new ExpandedArrayItem(
                    element?.Subscript ?? ShellSource.FromBytes(key.Bytes), key, value,
                    element?.Operator ?? AssignmentOperator.Replace, PrefixWithElementZero: false)
                { IsAssociativePair = !explicitItems }).ConfigureAwait(false))
                return false;
        }
        return true;

        Task<ShellValue> ExpandValueAsync(Word word) => WordExpansion.ExpandCompoundValueAsync(word, scope, ArrayKind.Associative);

        async Task ReportEmptyAssociativeKeyAsync(Word word, ScopedEvaluation active)
        {
            using var spelling = new ByteMemoryBuilder(0, Memory.Budget);
            if (!FunctionDefinitionFormatter.TryAppendWordDisplay(word, spelling, Memory.Budget, _cancellationToken))
                throw new ByteMemoryBuilder.LimitExceededException();
            await ReportCompoundIndexFailureAsync([ShellValue.FromBytes(spelling.ToMemory())], aggregate: false, active)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> ValidateCompoundInitializerCapturesAsync(
        IReadOnlyList<CompoundArrayItem> items, ScopedEvaluation scope)
    {
        var needsValidation = false;
        foreach (var item in items)
        {
            CheckCancellation();
            if (item.Word.HasRejectedCaptureSpelling)
            {
                needsValidation = true;
                break;
            }
        }
        if (!needsValidation)
            return true;

        using var rendered = new ByteMemoryBuilder(0, Memory.Budget);
        AppendCompoundInitializerDisplay(items, rendered);
        using var workspace = new MemoryCapacity(Memory.Budget, 0);
        var source = ShellSource.FromBytes(rendered.ToMemory());
        if (Parser.ReadCompoundInitializerFailure(source, workspace, out var line) is not { } failure)
            return true;

        var status = ReaderSyntaxExitCode(scope);
        SetFatalExpansionError(new ExpansionFailure(ExpansionFailureKind.ReaderSyntax, status)
        {
            UseInheritedPipelineExitAction = UsesInheritedPipelineExitAction(scope)
        });
        _state.LastExitCode = status;
        if (failure.UnexpectedToken is { } token)
            await ReportUnexpectedSyntaxAsync(token, scope.Context.DiagnosticPath, SyntaxDiagnosticOrigin.ArrayAssignment,
                line, ReaderErrexitEnabled(scope) ? SyntaxDiagnosticDetail.WithoutSourceLine : SyntaxDiagnosticDetail.Complete)
                .ConfigureAwait(false);
        else
            await ReportSyntaxErrorAsync(failure.Error).ConfigureAwait(false);
        return false;
    }

    private void AppendCompoundInitializerDisplay(IReadOnlyList<CompoundArrayItem> items, ByteMemoryBuilder rendered)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (index != 0)
                rendered.Append((byte)' ');
            if (!FunctionDefinitionFormatter.TryAppendWordDisplay(items[index].Word, rendered, Memory.Budget, _cancellationToken))
                throw new ByteMemoryBuilder.LimitExceededException();
        }
    }

    private async ValueTask<ExpansionFailure?> ReportCompoundItemReadDiagnosticsAsync(
        IReadOnlyList<CompoundItemReadDiagnostic> items, ScopedEvaluation scope, int lineOffset)
    {
        var errexit = ReaderErrexitEnabled(scope);
        foreach (var item in items)
        {
            CheckCancellation();
            var fatal = errexit || item.CaptureFailure == SubstitutionSyntaxFailure.NestedHereDocumentSeparator;
            var reported = false;
            if (!item.DeclarationPrefix.IsEmpty)
            {
                using var workspace = new MemoryCapacity(Memory.Budget, 0);
                if (Tokenizer.TryReadExpansionWord(item.DeclarationPrefix, ExpansionWordSyntax.Parameter,
                        ShellQuoteKind.None, workspace, out var prefix, out _))
                    reported = await ReportWordAsync(prefix, item.PrefixEndLine).ConfigureAwait(false);
            }

            if (!reported || !fatal)
                reported = await ReportWordAsync(item.Item, item.EndLine).ConfigureAwait(false) || reported;

            if (fatal && reported)
            {
                var failure = new ExpansionFailure(ExpansionFailureKind.ReaderSyntax, ReaderSyntaxExitCode(scope));
                SetFatalExpansionError(failure);
                _state.LastExitCode = failure.ExitCode;
                return failure;
            }
        }
        return null;

        async ValueTask<bool> ReportWordAsync(Word word, int line)
        {
            using var spelling = new ByteMemoryBuilder(0, Memory.Budget);
            using var workspace = new MemoryCapacity(Memory.Budget, 0);
            if (!FunctionDefinitionFormatter.TryAppendWordDisplay(word, spelling, Memory.Budget, _cancellationToken))
                throw new ByteMemoryBuilder.LimitExceededException();
            var source = ShellSource.FromBytes(spelling.ToMemory());
            var token = Tokenizer.ReadUnexpectedWordToken(source, new ShellScanContext { SyntaxMemory = workspace });
            if (token == null)
                return false;
            await ReportUnexpectedSyntaxAsync(token, scope.Context.DiagnosticPath, SyntaxDiagnosticOrigin.CommandSubstitution,
                GetCaptureDiagnosticLine(token, source.Bytes, lineOffset + line),
                errexit ? SyntaxDiagnosticDetail.WithoutSourceLine : SyntaxDiagnosticDetail.Complete).ConfigureAwait(false);
            return true;
        }
    }

    private ShellValue FlattenCompoundArrayItem(ShellValue selector, ShellValue value, AssignmentOperator operation)
    {
        var separator = operation == AssignmentOperator.Append ? "]+="u8 : "]="u8;
        var length = 1L + selector.Bytes.Length + separator.Length + value.Bytes.Length;
        if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();
        Memory.Charge(checked(32L + 4L * length));
        return ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted((int)length)
            .Append((byte)'[').Append(selector.Bytes.Span).Append(separator).Append(value.Bytes.Span).ToMemory());
    }

    /// <summary>Applies expanded items in order; selector effects observe earlier writes.</summary>
    private async Task<bool> ApplyCompoundArrayItemsAsync(
        BindingName name, AssignmentOperator operation, ArrayKind kind, IReadOnlyList<ExpandedArrayItem> expanded,
        ScopedEvaluation scope, ScopedEvaluation publicationScope, Variables targetVariables, BindingName sourceName, bool initializeReadonly)
    {
        if (kind == ArrayKind.Associative)
        {
            var target = new VariableBinding(VariableReference.ForName(name), targetVariables);
            return await ApplyAssociativeInitializerAsync(target, operation, scope, publicationScope, sourceName,
                initializeReadonly, VisitPreparedItemsAsync).ConfigureAwait(false);
        }

        // Word expansion sees the previous binding. Arithmetic selection and writes see
        // the replacement as it is built; a later arithmetic failure does not roll it back.
        var append = operation == AssignmentOperator.Append;
        if (!append || !targetVariables.IndexedArrays.ContainsKey(name))
        {
            IReadOnlyList<ShellValue> initial = append && targetVariables.Scalars.TryGetValue(name, out var scalar)
                ? [scalar]
                : [];
            if (!targetVariables.TryAssignResolvedArray(name, initial, kind, initializeReadonly))
                return false;
        }

        targetVariables.IndexedArrays[name].MarkInitialized();

        var lastIndex = append && targetVariables.IndexedArrays.TryGetValue(name, out var indexed)
            ? indexed.MaxIndex ?? -1
            : -1;
        var write = initializeReadonly ? WordExpansion.DeclarationWrite.ReadonlyInitializer : WordExpansion.DeclarationWrite.Ordinary;
        foreach (var item in expanded)
        {
            WordExpansion.ResolvedVariableReference? target;
            if (item.Subscript is { } subscript)
            {
                target = await WordExpansion.ResolveCompoundElementAsync(
                    VariableReference.ForElement(name, subscript), item.ExpandedSubscript, item.Value, item.Operator,
                    scope, targetVariables).ConfigureAwait(false);
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
            if (await target.AssignValueAsync(publicationScope, targetVariables, sourceName, value, item.Operator, ShellValue.Empty, write, scope)
                    .ConfigureAwait(false) is null)
                return false;
            if (target is WordExpansion.ResolvedVariableReference.Indexed element)
                lastIndex = element.Index.Value;
        }
        return true;

        async Task<bool> VisitPreparedItemsAsync(Func<ExpandedArrayItem, ValueTask<bool>> accept)
        {
            foreach (var item in expanded)
            {
                CheckCancellation();
                if (!await accept(item).ConfigureAwait(false))
                    return false;
            }
            return true;
        }
    }

    /// <summary>Applies each associative element to the live append target or to a private replacement.</summary>
    private async Task<bool> ApplyAssociativeInitializerAsync(
        VariableBinding target, AssignmentOperator operation, ScopedEvaluation scope, ScopedEvaluation publicationScope, BindingName sourceName,
        bool initializeReadonly, Func<Func<ExpandedArrayItem, ValueTask<bool>>, Task<bool>> visit)
    {
        var name = target.Name;
        var owner = target.Owner;
        var write = initializeReadonly ? WordExpansion.DeclarationWrite.ReadonlyInitializer : WordExpansion.DeclarationWrite.Ordinary;
        AssociativeArray? replacement = null;
        if (operation == AssignmentOperator.Append)
        {
            if (!owner.TryDeclareResolvedArray(name, ArrayKind.Associative, ScalarArrayPromotion.RetainValue))
                return false;
            owner.AssociativeArrays[name].MarkInitialized();
        }
        else
            replacement = owner.CreateAssociativeReplacement(name, Memory);

        return await visit(AcceptAsync).ConfigureAwait(false)
            && (replacement == null || owner.TryAssignResolvedAssociativeArray(name, replacement, initializeReadonly));

        async ValueTask<bool> AcceptAsync(ExpandedArrayItem item)
        {
            if (!AssociativeArrayKey.TryCreate(item.ExpandedSubscript, out var key))
            {
                await ReportPreparedEmptyKeyAsync(item).ConfigureAwait(false);
                return !_state.HasPendingExpansionError;
            }

            if (replacement == null)
            {
                var location = new WordExpansion.ResolvedVariableReference.Associative(name, key);
                return await location.AssignValueAsync(publicationScope, owner, sourceName, item.Value, item.Operator, ShellValue.Empty, write, scope)
                    .ConfigureAwait(false) != null;
            }

            // An explicit += inside a replacement reads the live binding, even after a staged write to the same key.
            ShellValue? previous = item.Operator == AssignmentOperator.Append && owner.TryReadArrayElement(name, key, out var stored)
                ? stored : null;
            var attributes = owner.GetAssignmentAttributes(name);
            if (await WordExpansion.PrepareAssignmentValueAsync(item.Value, previous, attributes, scope, ShellValue.Empty)
                    .ConfigureAwait(false) is not { } prepared)
                return false;
            replacement.Set(key, global::Lokad.Bash.Evaluation.Variables.ConvertAssignmentCase(attributes, prepared, Memory), Memory);
            return true;
        }

        async Task ReportPreparedEmptyKeyAsync(ExpandedArrayItem item)
        {
            // Declaration preparation quotes the expanded operands before the binding phase.
            using var spelling = new ByteMemoryBuilder(0, Memory.Budget);
            if (item.IsAssociativePair)
                spelling.Append("''"u8);
            else
            {
                spelling.Append(item.Operator == AssignmentOperator.Append ? "['']+="u8 : "['']="u8);
                if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(spelling, item.Value.Bytes.Span))
                    throw new ByteMemoryBuilder.LimitExceededException();
            }
            await ReportCompoundIndexFailureAsync([ShellValue.FromBytes(spelling.ToMemory())], aggregate: false, scope)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reports the rejected expanded item before later array writes can run.</summary>
    internal Task ReportCompoundIndexFailureAsync(ReadOnlySpan<ShellValue> item, bool aggregate, ScopedEvaluation scope)
    {
        if (ReaderErrexitEnabled(scope))
        {
            var status = !aggregate && _state.LastExitCode is { } previous && previous != 0 ? previous : 1;
            SetFatalExpansionError(new ExpansionFailure(ExpansionFailureKind.CompoundArrayIndex, status));
            _state.LastExitCode = status;
            PublishExpansionPipelineStatus(status, scope);
        }
        else if (aggregate)
        {
            _state.LastExitCode = 1;
            PublishExpansionPipelineStatus(1, scope);
            // Assignment-only completion uses this status only after a capture has run.
            if (_state.LastCommandSubstitutionExitCode.HasValue)
                _state.LastCommandSubstitutionExitCode = 1;
        }
        var message = aggregate ? "cannot assign to non-numeric index" : "bad array subscript";
        return ReportCommandDiagnosticAsync(item, [ShellValue.FromText(message)], scope);
    }

    private readonly record struct ExpandedArrayItem(
        ShellSource? Subscript,
        ShellValue ExpandedSubscript,
        ShellValue Value,
        AssignmentOperator Operator,
        bool PrefixWithElementZero)
    {
        internal bool IsAssociativePair { get; init; }
    }
}

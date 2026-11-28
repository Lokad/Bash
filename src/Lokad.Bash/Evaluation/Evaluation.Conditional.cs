using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using static Lokad.Bash.Evaluation.ShellPredicates;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private static readonly ShellValue ConditionalCommandName = ShellValue.FromText("[[");
    private static readonly SearchValues<byte> RegexMetacharacters = CreateRegexMetacharacters();

    private static SearchValues<byte> CreateRegexMetacharacters()
    {
        Span<byte> characters = stackalloc byte[128];
        var count = 0;
        for (var index = 0; index < characters.Length; index++)
        {
            var value = (byte)index;
            if (RequiresRegexEscape(value))
                characters[count++] = value;
        }
        return SearchValues.Create(characters[..count]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RequiresRegexEscape(byte value) => value is (byte)'.' or (byte)'^' or (byte)'$' or (byte)'|'
        or (byte)'?' or (byte)'*' or (byte)'+' or (byte)'(' or (byte)')' or (byte)'[' or (byte)']' or (byte)'{'
        or (byte)'}' or (byte)'\\';

    /// <summary>Evaluates retained conditional structure, expanding only reached operands.</summary>
    private async Task<EvaluationResult> EvaluateConditionalCommandAsync(
        ConditionalCommand command, ScopedEvaluation scope, bool negated)
    {
        PreparePipelineStatusStorage(1);
        var status = 1;
        if (command.Condition is not (ConditionalExpression.Logical or ConditionalExpression.Negated))
        {
            CheckCancellation();
            status = await AwaitWordExpansionAsync(EvaluatePredicateAsync(command.Condition)).ConfigureAwait(false);
        }
        else
        {
            using var workMemory = new MemoryCapacity(Memory.Budget, 0);
            var work = new List<(ConditionalExpression Expression, bool Complete)>();
            Push(command.Condition, false);
            using var predicateMemory = Memory.BeginCommand(scope);
            while (work.Count > 0)
            {
                CheckCancellation();
                var frame = work[^1];
                work.RemoveAt(work.Count - 1);
                if (frame.Complete)
                {
                    if (_state.HasPendingExpansionError)
                        break;
                    switch (frame.Expression)
                    {
                        case ConditionalExpression.Negated: status = status == 0 ? 1 : 0; break;
                        case ConditionalExpression.Logical logical:
                            if (logical.And ? status == 0 : status != 0)
                                Push(logical.Right, false);
                            break;
                    }
                    continue;
                }
                switch (frame.Expression)
                {
                    case ConditionalExpression.Logical logical:
                        Push(logical, true);
                        Push(logical.Left, false);
                        break;
                    case ConditionalExpression.Negated not:
                        Push(not, true);
                        Push(not.Inner, false);
                        break;
                    default:
                        status = await AwaitWordExpansionAsync(EvaluatePredicateAsync(frame.Expression)).ConfigureAwait(false);
                        // All scratch users have settled. Retain the bindings for the next operand.
                        if (work.Count > 0)
                            predicateMemory.ResetWorkspace();
                        break;
                }
            }

            void Push(ConditionalExpression expression, bool complete)
            {
                Memory.PrepareListCapacity(workMemory, work, 1);
                work.Add((expression, complete));
            }
        }
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated).ConfigureAwait(false);

        Task<IReadOnlyList<ExpandedWord>> ExpandAsync(Word word, ExpansionPurpose purpose) =>
            WordExpansion.ExpandWithoutBraceAsync(word, scope with { SelectorParameterDiagnostics = true },
                allowWordSplitting: false, purpose, TildeExpansion.Leading);

        async Task<int> EvaluatePredicateAsync(ConditionalExpression expression)
        {
            switch (expression)
            {
                case ConditionalExpression.NonEmpty nonempty:
                    var expanded = await ExpandAsync(nonempty.Operand, ExpansionPurpose.Scalar).ConfigureAwait(false);
                    return ExpandedWord.IsJoinedValueEmpty(expanded) ? 1 : 0;
                case ConditionalExpression.Unary unary:
                    var fields = await ExpandAsync(unary.Operand, ExpansionPurpose.Scalar).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return 1;
                    var kind = unary.Predicate.Operator;
                    if (kind is UnaryPredicateKind.StringLengthZero or UnaryPredicateKind.StringLengthNonZero)
                        return ExpandedWord.IsJoinedValueEmpty(fields) == (kind == UnaryPredicateKind.StringLengthZero) ? 0 : 1;

                    var operand = ExpandedWord.JoinValuesWithSpaces(fields, Memory);
                    if (unary.Predicate.IsFilePredicate)
                        return await FileAsync(unary.Predicate.FileKind, operand, null).ConfigureAwait(false);
                    if (unary.Predicate.IsVariablePredicate)
                        return await WordExpansion.TestVariableAsync(kind, operand, scope).ConfigureAwait(false) ? 0 : 1;
                    return kind switch
                    {
                        UnaryPredicateKind.OptionEnabled => IsOptionEnabled(operand.Bytes.Span, _state.EnabledOptions) ? 0 : 1,
                        _ => throw new InvalidOperationException("Unexpected conditional unary predicate.")
                    };
                case ConditionalExpression.VariableElement element:
                    return await WordExpansion.TestVariableElementAsync(element.Name, element.Selector, scope)
                        .ConfigureAwait(false) ? 0 : 1;
                case ConditionalExpression.Binary binary:
                    var leftFields = await ExpandAsync(binary.Left, ExpansionPurpose.Scalar).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return 1;
                    var predicate = binary.Predicate;
                    var patternOperand = predicate.Operator is BinaryPredicateKind.RegexMatch
                        or BinaryPredicateKind.StringEqual or BinaryPredicateKind.StringNotEqual;
                    var rightFields = await ExpandAsync(binary.Right,
                        patternOperand ? ExpansionPurpose.ConditionalPattern : ExpansionPurpose.Scalar).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return 1;
                    var left = ExpandedWord.JoinValuesWithSpaces(leftFields, Memory);
                    if (predicate.Operator == BinaryPredicateKind.RegexMatch)
                        return await MatchRegexAsync(left, ExpandedWord.JoinWithSpaces(rightFields, Memory)).ConfigureAwait(false);
                    if (predicate.Operator is BinaryPredicateKind.StringEqual or BinaryPredicateKind.StringNotEqual)
                    {
                        var matched = ShellPattern.MatchesWords(rightFields, left.Bytes.Span, Memory, _cancellationToken,
                            IgnoreCaseInPatterns);
                        return matched == (predicate.Operator == BinaryPredicateKind.StringEqual) ? 0 : 1;
                    }
                    var right = ExpandedWord.JoinValuesWithSpaces(rightFields, Memory);
                    if (predicate.IsFilePredicate)
                        return await FileAsync(predicate.FileKind, left, right).ConfigureAwait(false);
                    if (predicate.IsNumeric)
                    {
                        // Both words expand before numeric evaluation changes bindings.
                        var l = await WordExpansion.EvaluateArithmeticAsync(ArithmeticOperand.FromValue(left), scope,
                            WordExpansion.ArithmeticFailureMode.Diagnostic, ConditionalCommandName).ConfigureAwait(false);
                        if (!l.HasValue)
                            return 1;
                        var r = await WordExpansion.EvaluateArithmeticAsync(ArithmeticOperand.FromValue(right), scope,
                            WordExpansion.ArithmeticFailureMode.Diagnostic, ConditionalCommandName).ConfigureAwait(false);
                        return !r.HasValue ? 1 : CompareNumbers(predicate.Operator, l.Value, r.Value) ? 0 : 1;
                    }
                    var comparison = ShellValue.ByteOrder.Compare(left, right);
                    return (predicate.Operator == BinaryPredicateKind.StringLess ? comparison < 0 : comparison > 0) ? 0 : 1;
                default: throw new InvalidOperationException("Unexpected conditional predicate node.");
            }
        }

        async Task<int> FileAsync(FilePredicateKind kind, ShellValue left, ShellValue? right)
        {
            UnresolvedFileTest test;
            if (kind.IsTerminal)
            {
                if (!Integers.TryParseBuiltinNumber(left.Bytes.Span, out var number)
                    || !UnresolvedFileTest.TryCreateTerminal(number, out test))
                    return 1;
            }
            else
            {
                Memory.Charge(checked(4L * (left.Bytes.Length + (long)(right?.Bytes.Length ?? 0))));
                if (!left.TryGetUtf8Text(out var path))
                    return await ErrorAsync("file operand is not valid UTF-8").ConfigureAwait(false);
                string? other = null;
                if (right.HasValue && !right.Value.TryGetUtf8Text(out other))
                    return await ErrorAsync("file operand is not valid UTF-8").ConfigureAwait(false);
                test = new(kind, path, other);
            }
            return await ExecuteFileTestsForEvaluationAsync([test], results => results[0]).ConfigureAwait(false);
        }

        async Task<int> MatchRegexAsync(ShellValue input, ExpandedWord patternWord)
        {
            var pattern = patternWord.ToValue(Memory);
            // Include the capture binding's fixed overhead in the initial admission.
            Memory.Charge(checked(512L + 16L * (input.Bytes.Length + (long)pattern.Bytes.Length)));
            if (!input.TryGetUtf8Text(out var text) || !System.Text.Unicode.Utf8.IsValid(pattern.Bytes.Span))
                return await ErrorAsync("regex operands must be valid UTF-8").ConfigureAwait(false);
            if (!TryPreparePattern(patternWord, pattern.Bytes, out var expression))
                return 2;
            Match match;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var options = RegexOptions.Singleline | RegexOptions.CultureInvariant;
                if (IgnoreCaseInPatterns)
                    options |= RegexOptions.IgnoreCase;
                match = Regex.Match(text, expression, options, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException) { return 2; }
            catch (RegexMatchTimeoutException) { return 2; }

            var owner = scope.Context.GetGlobalBindingOwner(global::Lokad.Bash.Evaluation.Variables.RegexCaptureName, scope.Variables);
            if (!match.Success)
            {
                owner.ClearRegexCaptures();
                return 1;
            }
            var groups = match.Groups;
            if (groups.Count >= Array.MaxLength)
                throw new ByteMemoryBuilder.LimitExceededException();
            var inputBytes = input.Bytes;
            // Valid UTF-8 has one byte per UTF-16 code unit only for ASCII.
            var ascii = inputBytes.Length == text.Length;
            const int MaximumCachedCaptureLengths = 128;
            // Bound stack scratch. Larger matches size new partial spans on demand.
            Span<int> partialLengths = stackalloc int[!ascii && groups.Count <= MaximumCachedCaptureLengths
                ? groups.Count : 0];
            Group? previousPartial = null;
            var previousByteLength = 0;
            var bytes = 0L;
            for (var index = 0; index < groups.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var group = groups[index];
                var length = group.Length;
                if (!ascii)
                {
                    // .NET matches UTF-16 code units. Captures splitting a scalar are unsupported.
                    if (!IsScalarBoundary(group.Index) || !IsScalarBoundary(group.Index + length))
                        return 2;
                    if (length == text.Length)
                        length = inputBytes.Length;
                    else if (length != 0)
                    {
                        if (!SameSpan(group, previousPartial))
                        {
                            previousByteLength = Encoding.UTF8.GetByteCount(group.ValueSpan);
                            previousPartial = group;
                        }
                        length = previousByteLength;
                        if (!partialLengths.IsEmpty)
                            partialLengths[index] = length;
                    }
                }
                bytes = checked(bytes + length);
            }
            Memory.Charge(checked(256L * groups.Count + 2L * bytes));
            _cancellationToken.ThrowIfCancellationRequested();
            var captures = new IndexedArray(isInitialized: true, capacity: groups.Count);
            previousPartial = null;
            var previousBytes = ReadOnlyMemory<byte>.Empty;
            for (var index = 0; index < groups.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var group = groups[index];
                var value = ShellValue.Empty;
                if (group.Length != 0)
                {
                    if (ascii || group.Length == text.Length)
                    {
                        var original = ascii ? inputBytes.Slice(group.Index, group.Length) : inputBytes;
                        value = ShellValue.FromBytes(original.ToArray());
                    }
                    else
                    {
                        // Equal consecutive partial spans share encoding work. Each result still owns its buffer.
                        var content = SameSpan(group, previousPartial) ? previousBytes.ToArray()
                            : CopyPartialCapture(group, partialLengths.IsEmpty
                                ? Encoding.UTF8.GetByteCount(group.ValueSpan) : partialLengths[index]);
                        value = ShellValue.FromBytes(content);
                        previousPartial = group;
                        previousBytes = content;
                    }
                }
                captures.Set(IndexedArrayIndex.FromUnsigned((uint)index), value);
            }
            _cancellationToken.ThrowIfCancellationRequested();
            owner.SetRegexCaptures(captures);
            return 0;

            bool IsScalarBoundary(int position) => position == 0 || position == text.Length
                || !char.IsHighSurrogate(text[position - 1]) || !char.IsLowSurrogate(text[position]);

            static bool SameSpan(Group group, Group? previous) =>
                previous != null && group.Index == previous.Index && group.Length == previous.Length;

            byte[] CopyPartialCapture(Group group, int length)
            {
                if (group.Index == 0)
                    return inputBytes[..length].ToArray();
                if (group.Index + group.Length == text.Length)
                    return inputBytes[^length..].ToArray();

                var content = new byte[length];
                Encoding.UTF8.GetBytes(group.ValueSpan, content);
                return content;
            }

            bool TryPreparePattern(ExpandedWord word, ReadOnlyMemory<byte> patternBytes, out string expression)
            {
                ByteMemoryBuilder? escaped = null;
                ByteMemoryBuilder? translated = null;
                expression = string.Empty;
                try
                {
                    var source = patternBytes.Span;
                    var segmentStart = 0;
                    var copyStart = 0;
                    foreach (var segment in word.Segments)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var end = segmentStart + segment.Content.Bytes.Length;
                        if (segment.IsGlobQuoted)
                        {
                            const int MaximumScanBytes = 4096;
                            var index = segmentStart;
                            while (end - index >= 32)
                            {
                                _cancellationToken.ThrowIfCancellationRequested();
                                var length = Math.Min(end - index, MaximumScanBytes);
                                var next = source.Slice(index, length).IndexOfAny(RegexMetacharacters);
                                if (next >= 0)
                                {
                                    index += next;
                                    break;
                                }
                                index += length;
                            }
                            for (; index < end; index++)
                            {
                                _cancellationToken.ThrowIfCancellationRequested();
                                if (!RequiresRegexEscape(source[index]))
                                    continue;
                                (escaped ??= new ByteMemoryBuilder(0, Memory.Budget))
                                    .Append(source[copyStart..index]).Append((byte)'\\');
                                copyStart = index;
                            }
                        }
                        segmentStart = end;
                    }
                    escaped?.Append(source[copyStart..]);

                    var rendered = escaped?.ToMemory() ?? patternBytes;
                    if (!TranslateClasses(rendered.Span, ref translated))
                        return false;

                    _cancellationToken.ThrowIfCancellationRequested();
                    expression = ShellValue.FromBytes(translated?.ToMemory() ?? rendered).ToUtf8String();
                    return true;
                }
                finally
                {
                    translated?.Dispose();
                    escaped?.Dispose();
                }
            }

            bool TranslateClasses(ReadOnlySpan<byte> source, ref ByteMemoryBuilder? output)
            {
                if (!source.Contains((byte)'['))
                    return true;

                var copyStart = 0;
                var bracket = false;
                var first = false;
                for (var index = 0; index < source.Length; index++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var ch = source[index];
                    if (ch == (byte)'\\' && index + 1 < source.Length)
                    {
                        index++;
                        if (bracket) first = false;
                        continue;
                    }
                    if (bracket)
                    {
                        if (ch == (byte)'[' && index + 1 < source.Length && source[index + 1] is (byte)':' or (byte)'=' or (byte)'.')
                        {
                            // Named C classes are supported. Collating/equivalence elements
                            // require a locale-aware POSIX matcher and remain explicit exclusions.
                            if (source[index + 1] != (byte)':')
                                return false;
                            var end = source[(index + 2)..].IndexOf(":]"u8);
                            if (end < 0)
                                return false;
                            var members = ShellCharacterClass.TryParse(source.Slice(index + 2, end), out var kind)
                                ? ShellCharacterClass.RegexMembers(kind) : default;
                            if (members.IsEmpty)
                                return false;
                            (output ??= new ByteMemoryBuilder(0, Memory.Budget)).Append(source[copyStart..index]).Append(members);
                            index += end + 3;
                            copyStart = index + 1;
                            first = false;
                            continue;
                        }
                        if (ch == (byte)']' && !first) bracket = false;
                        else if (!(first && ch == (byte)'^')) first = false;
                    }
                    else if (ch == (byte)'[')
                    {
                        bracket = true;
                        first = true;
                    }
                }
                output?.Append(source[copyStart..]);
                return true;
            }
        }

        async Task<int> ErrorAsync(string message)
        {
            await ReportExpansionDiagnosticAsync(ConditionalCommandName, ShellValue.FromText(message)).ConfigureAwait(false);
            return 2;
        }
    }
}

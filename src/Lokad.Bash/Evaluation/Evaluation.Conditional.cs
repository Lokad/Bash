using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using static Lokad.Bash.Evaluation.ShellPredicates;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Evaluates retained conditional structure, expanding only reached operands.</summary>
    private async Task<EvaluationResult> EvaluateConditionalCommandAsync(
        ConditionalCommand command, ScopedEvaluation scope, bool negated)
    {
        var work = new Stack<(ConditionalExpression Expression, bool Complete)>();
        Push(command.Condition, false);
        var status = 1;
        while (work.TryPop(out var frame))
        {
            _cancellationToken.ThrowIfCancellationRequested();
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
                    break;
            }
        }
        if (_state.FatalExpansion == null)
            scope.Variables.SetPipelineStatuses([status], Memory, _cancellationToken);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated).ConfigureAwait(false);

        void Push(ConditionalExpression expression, bool complete)
        {
            Memory.Charge(128);
            work.Push((expression, complete));
        }

        async Task<ExpandedWord> ExpandAsync(Word word) => await WordExpansion.ExpandUnsplitWordAsync(
            word, scope, ExpansionPurpose.Scalar, allowLeadingTilde: true).ConfigureAwait(false);

        async Task<int> EvaluatePredicateAsync(ConditionalExpression expression)
        {
            switch (expression)
            {
                case ConditionalExpression.NonEmpty nonempty:
                    var expanded = await ExpandAsync(nonempty.Operand).ConfigureAwait(false);
                    return expanded.ToValue(Memory).IsEmpty ? 1 : 0;
                case ConditionalExpression.Unary unary:
                    var operand = (await ExpandAsync(unary.Operand).ConfigureAwait(false)).ToValue(Memory);
                    if (_state.HasPendingExpansionError)
                        return 1;
                    if (unary.Predicate.IsFilePredicate)
                        return await FileAsync(unary.Predicate.FileKind, operand, null).ConfigureAwait(false);
                    return (unary.Predicate.Operator switch
                    {
                        UnaryPredicateKind.StringLengthZero => operand.IsEmpty,
                        UnaryPredicateKind.StringLengthNonZero => !operand.IsEmpty,
                        UnaryPredicateKind.OptionEnabled => IsOptionEnabled(operand.Bytes.Span, _state.ErrexitEnabled),
                        _ => throw new InvalidOperationException("Unexpected conditional unary predicate.")
                    }) ? 0 : 1;
                case ConditionalExpression.Binary binary:
                    var leftWord = await ExpandAsync(binary.Left).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return 1;
                    var rightWord = await ExpandAsync(binary.Right).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return 1;
                    var left = leftWord.ToValue(Memory);
                    var right = rightWord.ToValue(Memory);
                    var predicate = binary.Predicate;
                    if (predicate.IsFilePredicate)
                        return await FileAsync(predicate.FileKind, left, right).ConfigureAwait(false);
                    if (predicate.IsNumeric)
                    {
                        // Both words expand before numeric evaluation changes bindings.
                        var l = await WordExpansion.EvaluateArithmeticAsync(ArithmeticOperand.FromValue(left), scope,
                            WordExpansion.ArithmeticFailureMode.Diagnostic).ConfigureAwait(false);
                        if (!l.HasValue)
                            return 1;
                        var r = await WordExpansion.EvaluateArithmeticAsync(ArithmeticOperand.FromValue(right), scope,
                            WordExpansion.ArithmeticFailureMode.Diagnostic).ConfigureAwait(false);
                        return !r.HasValue ? 1 : CompareNumbers(predicate.Operator, l.Value, r.Value) ? 0 : 1;
                    }
                    if (predicate.Operator is BinaryPredicateKind.StringEqual or BinaryPredicateKind.StringNotEqual)
                    {
                        var matched = ShellPattern.FromWord(rightWord, Memory, _cancellationToken)
                            .IsMatch(left.Bytes.Span, _cancellationToken);
                        return matched == (predicate.Operator == BinaryPredicateKind.StringEqual) ? 0 : 1;
                    }
                    if (predicate.Operator == BinaryPredicateKind.RegexMatch)
                        return await MatchRegexAsync(left, rightWord).ConfigureAwait(false);
                    var comparison = ShellValue.ByteOrder.Compare(left, right);
                    return (predicate.Operator == BinaryPredicateKind.StringLess ? comparison < 0 : comparison > 0) ? 0 : 1;
                default: throw new InvalidOperationException("Unexpected conditional predicate node.");
            }
        }

        async Task<int> FileAsync(FilePredicateKind kind, ShellValue left, ShellValue? right)
        {
            Memory.Charge(checked(4L * (left.Bytes.Length + (long)(right?.Bytes.Length ?? 0))));
            if (!left.TryGetUtf8Text(out var path))
                return await ErrorAsync("file operand is not valid UTF-8").ConfigureAwait(false);
            string? other = null;
            if (right.HasValue && !right.Value.TryGetUtf8Text(out other))
                return await ErrorAsync("file operand is not valid UTF-8").ConfigureAwait(false);
            return await ExecuteFileTestsForEvaluationAsync([new(kind, path, other)], results => results[0]).ConfigureAwait(false);
        }

        async Task<int> MatchRegexAsync(ShellValue input, ExpandedWord patternWord)
        {
            var pattern = patternWord.ToValue(Memory);
            Memory.Charge(checked(16L * (input.Bytes.Length + (long)pattern.Bytes.Length)));
            if (!input.TryGetUtf8Text(out var text) || !System.Text.Unicode.Utf8.IsValid(pattern.Bytes.Span))
                return await ErrorAsync("regex operands must be valid UTF-8").ConfigureAwait(false);
            var activity = patternWord.GetGlobMask(Memory);
            using var quoted = new ByteMemoryBuilder(0, Memory.Budget);
            for (var index = 0; index < pattern.Bytes.Length; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var ch = pattern.Bytes.Span[index];
                if (activity.AsSpan()[index] != GlobMask.Active
                    && ch is (byte)'.' or (byte)'^' or (byte)'$' or (byte)'|' or (byte)'?' or (byte)'*'
                        or (byte)'+' or (byte)'(' or (byte)')' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'\\')
                    quoted.Append((byte)'\\');
                quoted.Append(ch);
            }
            using var encoded = new ByteMemoryBuilder(0, Memory.Budget);
            if (!TranslateClasses(quoted.ToMemory().Span, encoded))
                return 2;
            Match match;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                match = Regex.Match(text, ShellValue.FromBytes(encoded.ToMemory()).ToUtf8String(),
                    RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException) { return 2; }
            catch (RegexMatchTimeoutException) { return 2; }

            var owner = scope.Context.GetGlobalBindingOwner(global::Lokad.Bash.Evaluation.Variables.RegexCaptureName, scope.Variables);
            if (!match.Success)
            {
                Memory.Charge(512);
                owner.SetRegexCaptures([]);
                return 1;
            }
            if (match.Groups.Count >= Array.MaxLength)
                throw new ByteMemoryBuilder.LimitExceededException();
            var bytes = 0L;
            for (var index = 0; index < match.Groups.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var group = match.Groups[index];
                // .NET matches UTF-16 code units. Never encode an isolated half-surrogate
                // as shell data; such captures are outside the supported regex profile.
                if (!IsScalarBoundary(group.Index) || !IsScalarBoundary(group.Index + group.Length))
                    return 2;
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(group.ValueSpan));
            }
            Memory.Charge(checked(512L + 256L * match.Groups.Count + 2L * bytes));
            var captures = new ShellValue[match.Groups.Count];
            for (var index = 0; index < captures.Length; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var value = match.Groups[index].ValueSpan;
                var content = new byte[Encoding.UTF8.GetByteCount(value)];
                Encoding.UTF8.GetBytes(value, content);
                captures[index] = ShellValue.FromBytes(content);
            }
            owner.SetRegexCaptures(captures);
            return 0;

            bool IsScalarBoundary(int position) => position == 0 || position == text.Length
                || !char.IsHighSurrogate(text[position - 1]) || !char.IsLowSurrogate(text[position]);

            bool TranslateClasses(ReadOnlySpan<byte> source, ByteMemoryBuilder output)
            {
                var bracket = false;
                var first = false;
                for (var index = 0; index < source.Length; index++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var ch = source[index];
                    if (ch == (byte)'\\' && index + 1 < source.Length)
                    {
                        output.Append(source.Slice(index, 2));
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
                            var members = ClassMembers(source.Slice(index + 2, end));
                            if (members.IsEmpty)
                                return false;
                            output.Append(members);
                            index += end + 3;
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
                    output.Append(ch);
                }
                return true;
            }

            static ReadOnlySpan<byte> ClassMembers(ReadOnlySpan<byte> name)
            {
                if (name.SequenceEqual("alnum"u8)) return "A-Za-z0-9"u8;
                if (name.SequenceEqual("alpha"u8)) return "A-Za-z"u8;
                if (name.SequenceEqual("blank"u8)) return " \\t"u8;
                if (name.SequenceEqual("cntrl"u8)) return "\\x00-\\x1F\\x7F"u8;
                if (name.SequenceEqual("digit"u8)) return "0-9"u8;
                if (name.SequenceEqual("graph"u8)) return "\\x21-\\x7E"u8;
                if (name.SequenceEqual("lower"u8)) return "a-z"u8;
                if (name.SequenceEqual("print"u8)) return "\\x20-\\x7E"u8;
                if (name.SequenceEqual("punct"u8)) return "\\x21-\\x2F\\x3A-\\x40\\x5B-\\x60\\x7B-\\x7E"u8;
                if (name.SequenceEqual("space"u8)) return "\\x09-\\x0D\\x20"u8;
                if (name.SequenceEqual("upper"u8)) return "A-Z"u8;
                if (name.SequenceEqual("xdigit"u8)) return "A-Fa-f0-9"u8;
                return default;
            }
        }

        async Task<int> ErrorAsync(string message)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("[["), ShellValue.FromText(message)).ConfigureAwait(false);
            return 2;
        }
    }
}

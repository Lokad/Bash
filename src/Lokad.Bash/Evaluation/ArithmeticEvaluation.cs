using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Evaluates wrapping signed 64-bit arithmetic with immediate or staged assignments.
/// </summary>
public static partial class ArithmeticEvaluation
{
    /// <summary>
    /// Evaluates a byte-valued arithmetic expression and returns its integer result.
    /// </summary>
    /// <param name="expression">The expanded expression bytes.</param>
    /// <returns>The evaluated integer result, or 0 on invalid input.</returns>
    public static long Evaluate(ReadOnlySpan<byte> expression)
    {
        return Evaluate(ShellValue.FromBytes(expression.ToArray()), static _ => 0, static (_, _) => { });
    }

    /// <summary>
    /// Evaluates a text arithmetic expression and returns its integer result.
    /// </summary>
    /// <param name="expression">The arithmetic expression.</param>
    /// <returns>The evaluated integer result, or 0 on invalid input.</returns>
    public static long Evaluate(ReadOnlySpan<char> expression)
    {
        return Evaluate(expression, static _ => 0, static (_, _) => { });
    }

    /// <summary>
    /// Evaluates a text arithmetic expression with variable resolution.
    /// </summary>
    /// <param name="expression">The arithmetic expression.</param>
    /// <param name="resolveVariable">Resolves variable names to integer values.</param>
    /// <returns>The evaluated integer result, or 0 on invalid input.</returns>
    public static long Evaluate(
        ReadOnlySpan<char> expression,
        Func<string, long> resolveVariable)
    {
        return Evaluate(expression, resolveVariable, static (_, _) => { });
    }

    /// <summary>
    /// Evaluates a text arithmetic expression with variable resolution and assignment.
    /// </summary>
    /// <param name="expression">The arithmetic expression.</param>
    /// <param name="resolveVariable">Resolves variable names to integer values.</param>
    /// <param name="assignVariable">Receives assignments in order only after the whole expression succeeds.</param>
    /// <returns>The evaluated integer result, or 0 on invalid input.</returns>
    public static long Evaluate(
        ReadOnlySpan<char> expression,
        Func<string, long> resolveVariable,
        Action<string, long> assignVariable)
    {
        return Evaluate(ShellValue.FromText(expression.ToString()), resolveVariable, assignVariable);
    }

    private static long Evaluate(
        ShellValue expression,
        Func<string, long> resolveVariable,
        Action<string, long> assignVariable)
    {
        var assignments = new AssignmentLog();
        if (!TryEvaluate(expression, ResolveStagedVariable, StageAssignment, out var value, out _))
            return 0;

        assignments.Commit(assignVariable);
        return value;

        long ResolveStagedVariable(string name) =>
            assignments.TryResolve(name, out var staged) ? staged : resolveVariable(name);

        ArithmeticError? StageAssignment(string name, long assigned)
        {
            assignments.Add(name, assigned);
            return null;
        }
    }

    /// <summary>
    /// Evaluates with immediate assignments, stopping at the first error without undoing earlier writes.
    /// An empty expression succeeds with zero. Assignment callbacks return null on success; resolution
    /// must observe their writes. The staged Evaluate helper deliberately has a different commit contract.
    /// </summary>
    internal static bool TryEvaluate(
        ShellValue expression,
        Func<string, long> resolveVariable,
        Func<string, long, ArithmeticError?> assignVariable,
        out long value,
        [NotNullWhen(false)] out ArithmeticError? error)
    {
        var pending = EvaluateAsync<string>(ArithmeticOperand.FromValue(expression), Bind, Assign);
        // These adapters only return completed values; never block a synchronous caller.
        if (!pending.IsCompleted)
            throw new InvalidOperationException("Synchronous arithmetic callbacks must complete synchronously.");
        var result = pending.GetAwaiter().GetResult();
        value = result.Value;
        error = result.Error;
        return error == null;

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            // Text callbacks receive raw reference spelling, not an expanded shell location.
            var name = reference.Syntax.Name.CachedText ?? string.Empty;
            if (reference.Syntax.Kind != VariableReferenceKind.Name)
            {
                var target = reference.Syntax.ToValue(null);
                if (!target.TryGetUtf8Text(out name))
                    return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Failure(
                        new ArithmeticError(ArithmeticErrorKind.InvalidTarget, target)));
            }
            return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Bound(
                name, use == ArithmeticReferenceUse.Read ? resolveVariable(name) : 0));
        }

        ValueTask<ArithmeticError?> Assign(ArithmeticReference reference, string target, long assigned) =>
            ValueTask.FromResult(assignVariable(target, assigned));
    }

    /// <summary>
    /// Evaluates reached references through an awaitable binding contract. Assignment selects its target
    /// after the right-hand side; compound and increment operations retain the target selected for their read.
    /// Binding must defer readonly rejection until assignment, and assignments must be visible to later reads.
    /// Prepared selector values are literal; binding must not expand them again.
    /// </summary>
    internal static ValueTask<ArithmeticResult> EvaluateAsync<TTarget>(
        ArithmeticOperand expression,
        Func<ArithmeticReference, ArithmeticReferenceUse, ValueTask<ArithmeticBinding<TTarget>>> bindReference,
        Func<ArithmeticReference, TTarget, long, ValueTask<ArithmeticError?>> assignTarget) =>
        EvaluateAsync(expression, bindReference, assignTarget, CancellationToken.None);

    internal static ValueTask<ArithmeticResult> EvaluateAsync<TTarget>(
        ArithmeticOperand expression,
        Func<ArithmeticReference, ArithmeticReferenceUse, ValueTask<ArithmeticBinding<TTarget>>> bindReference,
        Func<ArithmeticReference, TTarget, long, ValueTask<ArithmeticError?>> assignTarget,
        CancellationToken cancellationToken) =>
        new Parser<TTarget>(expression, bindReference, assignTarget, cancellationToken).EvaluateAsync();

    /// <summary>Parses Bash integer constants modulo 2^64, without decoding byte-valued operands.</summary>
    internal static bool TryParseIntegerLiteral<T>(ReadOnlySpan<T> text, out long value) where T : IBinaryInteger<T> =>
        TryParseIntegerLiteral(text, CancellationToken.None, out value);

    internal static bool TryParseIntegerLiteral<T>(ReadOnlySpan<T> text, CancellationToken cancellationToken, out long value)
        where T : IBinaryInteger<T>
    {
        value = 0;
        cancellationToken.ThrowIfCancellationRequested();
        var negative = !text.IsEmpty && text[0] == T.CreateChecked('-');
        if (!text.IsEmpty && (negative || text[0] == T.CreateChecked('+')))
            text = text[1..];
        if (text.IsEmpty)
            return false;

        var radix = 10;
        var separator = text.IndexOf(T.CreateChecked('#'));
        if (separator >= 0)
        {
            radix = 0;
            if (separator == 0)
                return false;
            foreach (var character in text[..separator])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var digit = int.CreateChecked(character) - '0';
                if (digit is < 0 or > 9 || radix > 64)
                    return false;
                radix = radix * 10 + digit;
            }
            if (radix is < 2 or > 64)
                return false;
            text = text[(separator + 1)..];
        }
        else if (text.Length > 1 && text[0] == T.CreateChecked('0'))
        {
            if (text[1] == T.CreateChecked('x') || text[1] == T.CreateChecked('X'))
            {
                radix = 16;
                text = text[2..];
            }
            else
                radix = 8;
        }
        if (text.IsEmpty)
            return false;

        long number = 0;
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digit = int.CreateChecked(character) switch
            {
                >= '0' and <= '9' => int.CreateChecked(character) - '0',
                >= 'a' and <= 'z' => int.CreateChecked(character) - 'a' + 10,
                >= 'A' and <= 'Z' => int.CreateChecked(character) - 'A' + (radix <= 36 ? 10 : 36),
                '@' => 62,
                '_' => 63,
                _ => -1
            };
            if (digit < 0 || digit >= radix)
                return false;
            number = unchecked(number * radix + digit);
        }
        value = negative ? unchecked(-number) : number;
        return true;
    }

    private sealed class AssignmentLog
    {
        private readonly List<Assignment> _ordered = [];
        private readonly Dictionary<string, long> _latest = new(StringComparer.Ordinal);

        public bool TryResolve(string name, out long value)
        {
            return _latest.TryGetValue(name, out value);
        }

        public void Add(string name, long value)
        {
            _ordered.Add(new Assignment(name, value));
            _latest[name] = value;
        }

        public void Commit(Action<string, long> assignVariable)
        {
            foreach (var assignment in _ordered)
            {
                assignVariable(assignment.Name, assignment.Value);
            }
        }

        private readonly record struct Assignment(string Name, long Value);
    }

    private sealed class Parser<TTarget>
    {
        private readonly ArithmeticOperand _operand;
        private readonly ShellValue _expression;
        private ReadOnlySpan<byte> Span => _expression.Bytes.Span;
        private readonly Func<ArithmeticReference, ArithmeticReferenceUse, ValueTask<ArithmeticBinding<TTarget>>> _bindReference;
        private readonly Func<ArithmeticReference, TTarget, long, ValueTask<ArithmeticError?>> _assignTarget;
        private readonly CancellationToken _cancellationToken;
        private int _index;
        private int _suppressedEvaluation;
        private int _nestedParseDepth;
        private ParseResult _parsed;
        private ArithmeticBinding<TTarget>.Bound? _bound;
        // One serialized parser request is outstanding at a time; results use _parsed and _bound.
        private IEnumerable<ParseStep>? _nextProduction;
        private ArithmeticReference _requestedReference;
        private ArithmeticReferenceUse _requestedUse;
        private ArithmeticBinding<TTarget>.Bound? _writeBinding;
        private long _requestedValue;

        public Parser(
            ArithmeticOperand expression,
            Func<ArithmeticReference, ArithmeticReferenceUse, ValueTask<ArithmeticBinding<TTarget>>> bindReference,
            Func<ArithmeticReference, TTarget, long, ValueTask<ArithmeticError?>> assignTarget,
            CancellationToken cancellationToken)
        {
            _operand = expression;
            _expression = expression.Value;
            _bindReference = bindReference;
            _assignTarget = assignTarget;
            _cancellationToken = cancellationToken;
        }

        public async ValueTask<ArithmeticResult> EvaluateAsync()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_operand.Syntax?.LiteralValue is { } literal)
                return new ArithmeticResult(literal, null);
            SkipWhitespace();
            if (IsAtEnd)
                return new ArithmeticResult(0, null);

            // Grammar calls suspend here instead of growing the CLR stack at each precedence level.
            // Only binding and publication await effects; completed adapters stay synchronous.
            var frames = new Stack<IEnumerator<ParseStep>>();
            frames.Push(TryParseExpression().GetEnumerator());
            try
            {
                while (frames.TryPeek(out var frame))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (!frame.MoveNext())
                    {
                        frames.Pop().Dispose();
                        continue;
                    }

                    switch (frame.Current)
                    {
                        case ParseStep.Call:
                            if (_nextProduction is { } production)
                            {
                                _nextProduction = null;
                                frames.Push(production.GetEnumerator());
                            }
                            break;
                        case ParseStep.Bind:
                            var result = await _bindReference(_requestedReference, _requestedUse).ConfigureAwait(false);
                            _bound = result as ArithmeticBinding<TTarget>.Bound;
                            if (result is ArithmeticBinding<TTarget>.Failure failure)
                                Error = failure.Error;
                            break;
                        case ParseStep.Write when _writeBinding is { } binding:
                            _writeBinding = null;
                            Error = await _assignTarget(_requestedReference, binding.Target, _requestedValue).ConfigureAwait(false);
                            break;
                        default:
                            throw new InvalidOperationException("Missing arithmetic parser request.");
                    }
                }
            }
            finally
            {
                while (frames.TryPop(out var frame))
                    frame.Dispose();
            }

            if (_parsed.Success)
            {
                SkipWhitespace();
                if (IsAtEnd)
                    return new ArithmeticResult(_parsed.Value, null);
            }
            return new ArithmeticResult(0, Error ?? new ArithmeticError(ArithmeticErrorKind.InvalidExpression, _expression));
        }

        // The driver returns child results through _parsed and binding results through _bound.
        // A production must copy any result it needs to retain before yielding its next request.
        private enum ParseStep { Call, Bind, Write }

        private ParseStep Call(IEnumerable<ParseStep>? production)
        {
            // A literal operand or empty binary tail may have completed inline.
            _nextProduction = production;
            return ParseStep.Call;
        }

        private ParseStep Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            _requestedReference = reference;
            _requestedUse = use;
            return ParseStep.Bind;
        }

        private ParseStep Write(ArithmeticReference reference, ArithmeticBinding<TTarget>.Bound binding, long value)
        {
            _requestedReference = reference;
            _writeBinding = binding;
            _requestedValue = value;
            return ParseStep.Write;
        }

        private readonly record struct ParseResult(bool Success, long Value)
        {
            public bool TryGet(out long value)
            {
                value = Value;
                return Success;
            }
        }

        private ArithmeticError? Error { get; set; }

        private bool IsAtEnd => _index >= Span.Length;

        private bool IsSuppressed => _suppressedEvaluation != 0;

        private void SkipWhitespace()
        {
            static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n';

            if (_operand.Syntax is { } syntax && syntax.TrySkipBlanks(_index, out var end))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _index = end;
                return;
            }
            while (_index < Span.Length && IsWhitespace(Span[_index]))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _index++;
            }
        }

        private IEnumerable<ParseStep> TryParseExpression()
        {
            long value;
            yield return Call(TryParseAssignment());
            if (!_parsed.TryGet(out value))
                yield break;

            while (TryMatch(","u8))
            {
                yield return Call(TryParseAssignment());
                if (!_parsed.TryGet(out value))
                    yield break;
            }

            _parsed = new ParseResult(true, value);
            yield break;
        }

        private IEnumerable<ParseStep> TryParseAssignment()
        {
            var saved = _index;
            if (!TryParseReference(out var reference) || !TryMatchAssignmentOperator(out var op))
            {
                _index = saved;
                yield return Call(TryParseConditional());
                yield break;
            }

            ArithmeticBinding<TTarget>.Bound? binding = null;
            if (op != AssignmentOperator.Assign && !IsSuppressed)
            {
                yield return Bind(reference, ArithmeticReferenceUse.Read);
                binding = _bound;
                if (binding == null)
                {
                    _parsed = default;
                    yield break;
                }
            }

            yield return Call(TryParseNested(ParseProduction.Assignment));
            if (!_parsed.TryGet(out var rhs) || !IsAssignmentRhsComplete())
            {
                _parsed = default;
                yield break;
            }
            if (IsSuppressed)
            {
                _parsed = new ParseResult(true, 0);
                yield break;
            }

            if (op == AssignmentOperator.Assign)
            {
                yield return Bind(reference, ArithmeticReferenceUse.Assign);
                binding = _bound;
            }
            if (binding == null)
            {
                _parsed = default;
                yield break;
            }

            var value = rhs;
            if (op != AssignmentOperator.Assign)
            {
                var current = binding.Value;
                var applied = op switch
                {
                    AssignmentOperator.Add => TryApplyBinary(BinaryOperator.Add, current, rhs, out value),
                    AssignmentOperator.Subtract => TryApplyBinary(BinaryOperator.Subtract, current, rhs, out value),
                    AssignmentOperator.Multiply => TryApplyBinary(BinaryOperator.Multiply, current, rhs, out value),
                    AssignmentOperator.Divide => TryApplyBinary(BinaryOperator.Divide, current, rhs, out value),
                    AssignmentOperator.Remainder => TryApplyBinary(BinaryOperator.Remainder, current, rhs, out value),
                    AssignmentOperator.LeftShift => TryApplyBinary(BinaryOperator.LeftShift, current, rhs, out value),
                    AssignmentOperator.RightShift => TryApplyBinary(BinaryOperator.RightShift, current, rhs, out value),
                    AssignmentOperator.BitwiseAnd => TryApplyBinary(BinaryOperator.BitwiseAnd, current, rhs, out value),
                    AssignmentOperator.BitwiseXor => TryApplyBinary(BinaryOperator.BitwiseXor, current, rhs, out value),
                    AssignmentOperator.BitwiseOr => TryApplyBinary(BinaryOperator.BitwiseOr, current, rhs, out value),
                    _ => throw new NotSupportedException($"Unsupported assignment operator '{op}'.")
                };

                if (!applied)
                {
                    _parsed = default;
                    yield break;
                }
            }
            yield return Write(reference, binding, value);
            _parsed = Error == null ? new ParseResult(true, value) : default;

            bool IsAssignmentRhsComplete()
            {
                if (!IsNextTokenValid())
                    return false;
                // Inner writes have completed, but a remaining assignment operator has no valid target.
                var following = _index;
                if (TryMatchAssignmentOperator(out _))
                    return false;
                _index = following;
                return true;
            }
        }

        private IEnumerable<ParseStep> TryParseConditional()
        {
            long value;
            yield return Call(TryParseBinary(ArithmeticPrecedence.LogicalOr));
            if (!_parsed.TryGet(out var condition))
                yield break;

            if (!TryMatch("?"u8))
            {
                value = condition;
                _parsed = new ParseResult(true, value);
                yield break;
            }

            if (condition != 0 && !IsSuppressed)
            {
                yield return Call(TryParseNested(ParseProduction.Expression));
                if (!_parsed.TryGet(out value))
                    yield break;

                if (!TryMatch(":"u8))
                {
                    _parsed = default;
                    yield break;
                }

                yield return Call(TryParseSuppressed(ParseProduction.Conditional));
                _parsed = _parsed.Success ? new ParseResult(true, value) : default;
                yield break;
            }

            yield return Call(TryParseSuppressed(ParseProduction.Expression));
            if (!_parsed.Success)
                yield break;

            if (!TryMatch(":"u8))
            {
                _parsed = default;
                yield break;
            }

            yield return Call(TryParseNested(ParseProduction.Conditional));
            yield break;
        }

        // Precedence climbing creates continuations only for pending operators or asynchronous operands.
        private IEnumerable<ParseStep>? TryParseBinary(ArithmeticPrecedence minimum)
        {
            var initial = TryParseUnary();
            if (initial == null && (!_parsed.Success || !TryPeekBinary(this, out var token) || token.Precedence < minimum))
                return null;
            return Parse(this, minimum, initial);

            static IEnumerable<ParseStep> Parse(
                Parser<TTarget> parser, ArithmeticPrecedence minimum, IEnumerable<ParseStep>? initial)
            {
                if (initial != null)
                    yield return parser.Call(initial);
                if (!parser._parsed.TryGet(out var value))
                    yield break;

                while (TryPeekBinary(parser, out var token) && token.Precedence >= minimum)
                {
                    parser._index += token.Length;
                    var suppressed = parser.IsSuppressed
                        || token.Operator == BinaryOperator.LogicalAnd && value == 0
                        || token.Operator == BinaryOperator.LogicalOr && value != 0;
                    var nested = token.Operator == BinaryOperator.Power || suppressed
                        && token.Operator is BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr;
                    if (nested && parser._nestedParseDepth >= ShellSourceCursor.MaximumNestedExpansionDepth)
                    {
                        parser.Error = new ArithmeticError(ArithmeticErrorKind.NestingLimit, parser._expression);
                        parser._parsed = default;
                        yield break;
                    }

                    if (nested)
                        parser._nestedParseDepth++;
                    if (suppressed)
                        parser._suppressedEvaluation++;
                    try
                    {
                        yield return parser.Call(parser.TryParseBinary(token.Operator == BinaryOperator.Power
                            ? token.Precedence : token.Precedence + 1));
                    }
                    finally
                    {
                        if (suppressed)
                            parser._suppressedEvaluation--;
                        if (nested)
                            parser._nestedParseDepth--;
                    }
                    if (!parser._parsed.TryGet(out var rhs))
                        yield break;
                    if (!parser.TryApplyBinary(token.Operator, value, rhs, out value))
                    {
                        parser._parsed = default;
                        yield break;
                    }
                }
                parser._parsed = new ParseResult(true, value);
            }

            static bool TryPeekBinary(Parser<TTarget> parser, out BinaryToken binary)
            {
                var token = parser.PeekToken(out var kind);
                binary = parser._operand.Syntax is { } syntax && syntax.TryGetToken(parser._index, out var cached)
                    ? cached.Binary : GetBinaryToken(token, kind);
                return binary.Length != 0;
            }
        }

        private IEnumerable<ParseStep>? TryParseUnary()
        {
            var token = PeekToken(out var kind);
            if (kind == ArithmeticTokenKind.Number)
            {
                var valid = _operand.Syntax is { } syntax && syntax.TryGetToken(_index, out var cached)
                    ? ReadCachedNumber(cached, out var number) : TryParseIntegerLiteral(token, _cancellationToken, out number);
                _index += token.Length;
                _parsed = valid ? new ParseResult(true, IsSuppressed ? 0 : number) : default;
                return null;
            }
            return Parse(this);

            static IEnumerable<ParseStep> Parse(Parser<TTarget> parser)
            {
                var prefixStart = parser._index;
                if (parser.TryMatch("++"u8) || parser.TryMatch("--"u8))
                {
                    var op = parser.Span[prefixStart] == (byte)'+' ? BinaryOperator.Add : BinaryOperator.Subtract;
                    if (parser.TryParseReference(out var reference))
                    {
                        // Prefix mutation checks its following token before writing; postfix mutation does not.
                        if (!parser.IsNextTokenValid())
                        {
                            parser._parsed = default;
                            yield break;
                        }
                        if (parser.IsSuppressed)
                        {
                            parser._parsed = new ParseResult(true, 0);
                            yield break;
                        }
                        yield return parser.Bind(reference, ArithmeticReferenceUse.Read);
                        var binding = parser._bound;
                        if (binding == null || !parser.TryApplyBinary(op, binding.Value, 1, out var assigned))
                        {
                            parser._parsed = default;
                            yield break;
                        }
                        yield return parser.Write(reference, binding, assigned);
                        parser._parsed = parser.Error == null ? new ParseResult(true, assigned) : default;
                        yield break;
                    }

                    // Only a variable takes prefix mutation; ++1 is two unary pluses.
                    parser._index = prefixStart;
                }

                if (parser.TryMatchSingle((byte)'+'))
                {
                    yield return parser.Call(parser.TryParseNested(ParseProduction.Unary));
                    yield break;
                }

                if (parser.TryMatchSingle((byte)'-'))
                {
                    yield return parser.Call(parser.TryParseNested(ParseProduction.Unary));
                    if (!parser._parsed.TryGet(out var operand))
                        yield break;
                    parser._parsed = parser.TryApplyBinary(BinaryOperator.Subtract, 0, operand, out var value)
                        ? new ParseResult(true, value)
                        : default;
                    yield break;
                }

                if (parser.TryMatchSingle((byte)'!'))
                {
                    yield return parser.Call(parser.TryParseNested(ParseProduction.Unary));
                    if (!parser._parsed.TryGet(out var operand))
                        yield break;
                    parser._parsed = new ParseResult(true, !parser.IsSuppressed && operand == 0 ? 1 : 0);
                    yield break;
                }

                if (parser.TryMatchSingle((byte)'~'))
                {
                    yield return parser.Call(parser.TryParseNested(ParseProduction.Unary));
                    if (!parser._parsed.TryGet(out var operand))
                        yield break;
                    parser._parsed = new ParseResult(true, parser.IsSuppressed ? 0 : ~operand);
                    yield break;
                }

                parser.SkipWhitespace();
                if (parser._index >= parser.Span.Length)
                {
                    parser._parsed = default;
                    yield break;
                }

                if (parser.Span[parser._index] == (byte)'(')
                {
                    parser._index++;
                    yield return parser.Call(parser.TryParseNested(ParseProduction.Expression));
                    if (!parser._parsed.Success || !parser.TryMatch(")"u8))
                        parser._parsed = default;
                    yield break;
                }

                if (!parser.TryParseReference(out var identifier))
                {
                    parser._parsed = default;
                    yield break;
                }
                ArithmeticBinding<TTarget>.Bound? selected = null;
                if (!parser.IsSuppressed)
                {
                    yield return parser.Bind(identifier, ArithmeticReferenceUse.Read);
                    selected = parser._bound;
                    if (selected == null)
                    {
                        parser._parsed = default;
                        yield break;
                    }
                }
                var currentValue = selected?.Value ?? 0;
                if (parser.TryMatch("++"u8) || parser.TryMatch("--"u8))
                {
                    var op = parser.Span[parser._index - 1] == (byte)'+' ? BinaryOperator.Add : BinaryOperator.Subtract;
                    if (!parser.TryApplyBinary(op, currentValue, 1, out var assigned))
                    {
                        parser._parsed = default;
                        yield break;
                    }
                    if (selected != null)
                    {
                        yield return parser.Write(identifier, selected, assigned);
                        if (parser.Error != null)
                        {
                            parser._parsed = default;
                            yield break;
                        }
                    }
                }

                parser._parsed = new ParseResult(true, currentValue);
            }
        }

        private bool TryParseReference(out ArithmeticReference reference)
        {
            SkipWhitespace();
            if (_index >= Span.Length || !VariableNames.IsNameStart((char)Span[_index]))
            {
                reference = default;
                return false;
            }

            var cached = _operand.Syntax is { } syntax && syntax.TryGetReference(_index, out var retained)
                ? retained : ReadReferenceSyntax(ShellSource.FromBytes(_expression.Bytes), _index, PeekToken(out _).Length);
            if (cached.Bracket >= 0)
            {
                if (_operand.Brackets == ArithmeticBracketMode.Literal)
                {
                    reference = default;
                    Error = new ArithmeticError(ArithmeticErrorKind.InvalidExpression, _expression);
                    return false;
                }
                if (_operand.Subscripts.TryGetValue(cached.Bracket, out var prepared))
                {
                    reference = new ArithmeticReference(VariableReference.ForElement(cached.Syntax.Name, prepared.Source), prepared.Expansion);
                    _index = cached.Bracket + prepared.Expansion.Value.Bytes.Length + 2;
                    return true;
                }
            }
            _index = cached.End;
            if (cached.Failure is { } failure)
            {
                reference = default;
                Error = failure;
                return false;
            }
            reference = new ArithmeticReference(cached.Syntax, null);
            return true;
        }

        private IEnumerable<ParseStep> TryParseSuppressed(ParseProduction production)
        {
            _suppressedEvaluation++;
            try
            {
                yield return Call(TryParseNested(production));
            }
            finally
            {
                _suppressedEvaluation--;
            }
        }

        // Bound recursive grammar edges, including unselected arms, not flat operator chains.
        private IEnumerable<ParseStep> TryParseNested(ParseProduction production)
        {
            if (_nestedParseDepth >= ShellSourceCursor.MaximumNestedExpansionDepth)
            {
                Error = new ArithmeticError(ArithmeticErrorKind.NestingLimit, _expression);
                _parsed = default;
                yield break;
            }

            _nestedParseDepth++;
            try
            {
                yield return Call(production switch
                {
                    ParseProduction.Expression => TryParseExpression(),
                    ParseProduction.Assignment => TryParseAssignment(),
                    ParseProduction.Conditional => TryParseConditional(),
                    ParseProduction.Unary => TryParseUnary(),
                    _ => throw new NotSupportedException($"Unsupported parse production '{production}'.")
                });
            }
            finally
            {
                _nestedParseDepth--;
            }
        }

        private bool TryApplyBinary(BinaryOperator op, long left, long right, out long value)
        {
            if (IsSuppressed)
            {
                value = 0;
                return true;
            }

            if (op == BinaryOperator.Power)
            {
                if (right < 0)
                {
                    value = 0;
                    Error = new ArithmeticError(ArithmeticErrorKind.NegativeExponent, _expression);
                    return false;
                }

                value = 1;
                var factor = left;
                var remaining = right;
                while (remaining != 0)
                {
                    if ((remaining & 1) != 0)
                        value = unchecked(value * factor);

                    remaining >>= 1;
                    if (remaining != 0)
                        factor = unchecked(factor * factor);
                }

                return true;
            }

            if (op is BinaryOperator.Divide or BinaryOperator.Remainder && right == 0)
            {
                value = 0;
                Error = new ArithmeticError(ArithmeticErrorKind.DivisionByZero, _expression);
                return false;
            }

            // CLR division traps on MinValue / -1 even unchecked; Bash wraps.
            // Long shifts use the low six count bits, including for negative counts.
            value = op switch
            {
                BinaryOperator.LogicalOr => left != 0 || right != 0 ? 1 : 0,
                BinaryOperator.LogicalAnd => left != 0 && right != 0 ? 1 : 0,
                BinaryOperator.Equal => left == right ? 1 : 0,
                BinaryOperator.NotEqual => left != right ? 1 : 0,
                BinaryOperator.Less => left < right ? 1 : 0,
                BinaryOperator.LessOrEqual => left <= right ? 1 : 0,
                BinaryOperator.Greater => left > right ? 1 : 0,
                BinaryOperator.GreaterOrEqual => left >= right ? 1 : 0,
                BinaryOperator.Add => unchecked(left + right),
                BinaryOperator.Subtract => unchecked(left - right),
                BinaryOperator.Multiply => unchecked(left * right),
                BinaryOperator.Divide => left == long.MinValue && right == -1 ? long.MinValue : left / right,
                BinaryOperator.Remainder => right == -1 ? 0 : left % right,
                BinaryOperator.LeftShift => left << unchecked((int)right),
                BinaryOperator.RightShift => left >> unchecked((int)right),
                BinaryOperator.BitwiseAnd => left & right,
                BinaryOperator.BitwiseXor => left ^ right,
                BinaryOperator.BitwiseOr => left | right,
                _ => throw new NotSupportedException($"Unsupported binary operator '{op}'.")
            };
            return true;
        }

        private bool IsNextTokenValid()
        {
            var token = PeekToken(out var kind);
            return kind != ArithmeticTokenKind.Invalid
                && (kind != ArithmeticTokenKind.Number || (_operand.Syntax is { } syntax && syntax.TryGetToken(_index, out var cached)
                    ? cached.ValidNumber : TryParseIntegerLiteral(token, _cancellationToken, out _)));
        }

        // Prepared sources share the same token boundaries as dynamic operands.
        private ReadOnlySpan<byte> PeekToken(out ArithmeticTokenKind kind)
        {
            SkipWhitespace();
            var token = _operand.Syntax is { } syntax && syntax.TryGetToken(_index, out var cached)
                ? cached.Shape : ReadTokenShape(Span[_index..], _cancellationToken);
            kind = token.Kind;
            return Span.Slice(_index, token.Length);
        }

        private bool TryMatchAssignmentOperator(out AssignmentOperator op)
        {
            var token = PeekToken(out _);
            var assignment = _operand.Syntax is { } syntax && syntax.TryGetToken(_index, out var cached)
                ? cached.Assignment : GetAssignment(token);
            op = assignment.GetValueOrDefault();
            if (!assignment.HasValue)
                return false;
            _index += token.Length;
            return true;
        }

        private bool TryMatchSingle(byte token)
        {
            SkipWhitespace();
            if (IsAtEnd || Span[_index] != token)
                return false;

            var next = PeekToken(out var kind);
            // Outside variable mutation, adjacent signs remain separate binary/unary operators.
            if (kind != ArithmeticTokenKind.Operator
                || next.Length != 1 && !(token is (byte)'+' or (byte)'-' && next.Length == 2 && next[1] == token))
                return false;

            _index++;
            return true;
        }

        private bool TryMatch(ReadOnlySpan<byte> token)
        {
            SkipWhitespace();
            if (IsAtEnd || Span[_index] != token[0])
                return false;

            var next = PeekToken(out var kind);
            if (kind != ArithmeticTokenKind.Operator || !next.SequenceEqual(token))
                return false;

            _index += token.Length;
            return true;
        }

        private enum ParseProduction
        {
            Expression,
            Assignment,
            Unary,
            Conditional,
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
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
        AssignmentLog? assignments = null;
        if (!TryEvaluate(expression, ResolveStagedVariable, StageAssignment, out var value, out _))
            return 0;

        assignments?.Commit(assignVariable);
        return value;

        long ResolveStagedVariable(string name) =>
            assignments != null && assignments.TryResolve(name, out var staged) ? staged : resolveVariable(name);

        ArithmeticError? StageAssignment(string name, long assigned)
        {
            (assignments ??= new AssignmentLog()).Add(name, assigned);
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
        EvaluateAsync(expression, bindReference, assignTarget, null, CancellationToken.None);

    internal static ValueTask<ArithmeticResult> EvaluateAsync<TTarget>(
        ArithmeticOperand expression,
        Func<ArithmeticReference, ArithmeticReferenceUse, ValueTask<ArithmeticBinding<TTarget>>> bindReference,
        Func<ArithmeticReference, TTarget, long, ValueTask<ArithmeticError?>> assignTarget,
        MemoryCapacity? memory,
        CancellationToken cancellationToken) =>
        new Parser<TTarget>(expression, bindReference, assignTarget, memory, cancellationToken).EvaluateAsync();

    /// <summary>Parses Bash integer constants modulo 2^64, without decoding byte-valued operands.</summary>
    internal static bool TryParseIntegerLiteral<T>(ReadOnlySpan<T> text, out long value) where T : IBinaryInteger<T> =>
        TryParseIntegerLiteral(text, CancellationToken.None, out value);

    internal static bool TryParseIntegerLiteral<T>(ReadOnlySpan<T> text, CancellationToken cancellationToken, out long value)
        where T : IBinaryInteger<T> => ReadIntegerLiteral(text, cancellationToken, out value) == null;

    private static ArithmeticErrorKind? ReadIntegerLiteral<T>(ReadOnlySpan<T> text, CancellationToken cancellationToken, out long value)
        where T : IBinaryInteger<T>
    {
        value = 0;
        cancellationToken.ThrowIfCancellationRequested();
        var negative = !text.IsEmpty && text[0] == T.CreateChecked('-');
        if (!text.IsEmpty && (negative || text[0] == T.CreateChecked('+')))
            text = text[1..];
        if (text.IsEmpty)
            return ArithmeticErrorKind.InvalidNumber;

        var radix = 10;
        var separator = text.IndexOf(T.CreateChecked('#'));
        if (text.Length > 1 && text[0] == T.CreateChecked('0'))
        {
            if (text[1] == T.CreateChecked('x') || text[1] == T.CreateChecked('X'))
            {
                radix = 16;
                text = text[2..];
            }
            else
                radix = 8;
        }
        else if (separator >= 0)
        {
            radix = 0;
            if (separator == 0)
                return ArithmeticErrorKind.InvalidNumber;
            foreach (var character in text[..separator])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var digit = int.CreateChecked(character) - '0';
                if (digit is < 0 or > 9)
                    return ArithmeticErrorKind.DigitOutsideBase;
                if (radix > 64)
                    return ArithmeticErrorKind.InvalidArithmeticBase;
                radix = radix * 10 + digit;
            }
            if (radix is < 2 or > 64)
                return ArithmeticErrorKind.InvalidArithmeticBase;
            if (separator == text.Length - 1 || text[separator + 1] == T.CreateChecked('#'))
                return ArithmeticErrorKind.InvalidIntegerConstant;
            text = text[(separator + 1)..];
        }

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
            if (digit < 0)
                return ArithmeticErrorKind.InvalidNumber;
            if (digit >= radix)
                return ArithmeticErrorKind.DigitOutsideBase;
            number = unchecked(number * radix + digit);
        }
        value = negative ? unchecked(-number) : number;
        return null;
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
        private readonly MemoryCapacity? _memory;
        private int _index;
        private int _lastTokenStart;
        private int _suppressedEvaluation;
        private int _nestedParseDepth;
        private ((int Index, int LastTokenStart) Cursor, ArithmeticReference Reference)? _referenceLookahead;
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
            MemoryCapacity? memory,
            CancellationToken cancellationToken)
        {
            _operand = expression;
            _expression = expression.Value;
            _bindReference = bindReference;
            _assignTarget = assignTarget;
            _cancellationToken = cancellationToken;
            _memory = memory;
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
            using var frameMemory = _memory == null ? null : new MemoryCapacity(_memory.Budget, 0);
            var frames = new List<IEnumerator<ParseStep>>();
            var admittedFrames = 0;
            AdmitNextFrame();
            frames.Add(TryParseExpression().GetEnumerator());
            try
            {
                while (frames.Count != 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    // A production can construct its child while advancing. Prepay that slot
                    // before MoveNext and reuse it for subsequent calls at the same depth.
                    if (frames.Count == admittedFrames)
                        AdmitNextFrame();
                    var frame = frames[^1];
                    if (!frame.MoveNext())
                    {
                        DisposeTopFrame();
                        continue;
                    }

                    switch (frame.Current)
                    {
                        case ParseStep.Call:
                            if (_nextProduction is { } production)
                            {
                                _nextProduction = null;
                                frames.Add(production.GetEnumerator());
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
                while (frames.Count != 0)
                    DisposeTopFrame();
            }

            if (_parsed.Success)
            {
                SkipWhitespace();
                if (IsAtEnd)
                    return new ArithmeticResult(_parsed.Value, null);
                if (IsNextTokenValid())
                    Error ??= CreateError(ArithmeticErrorKind.UnexpectedToken, _index);
            }
            return new ArithmeticResult(0, Error ?? new ArithmeticError(ArithmeticErrorKind.InvalidExpression, _expression));

            void AdmitNextFrame()
            {
                if (frameMemory == null)
                    return;
                const int estimatedFrameBytes = 192;
                // Admit iterator state, including temporary child state retained by a production.
                frameMemory.Grow(estimatedFrameBytes);
                frameMemory.PrepareListCapacity(frames, 1);
                admittedFrames++;
            }

            void DisposeTopFrame()
            {
                var index = frames.Count - 1;
                var frame = frames[index];
                frames.RemoveAt(index);
                frame.Dispose();
            }
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

        private void ConsumeToken(int length)
        {
            _lastTokenStart = _index;
            _index += length;
        }

        private int CurrentTokenStart()
        {
            SkipWhitespace();
            return IsAtEnd ? _lastTokenStart : _index;
        }

        private ArithmeticError CreateError(ArithmeticErrorKind kind, int tokenStart) =>
            CreateError(kind, tokenStart, Span.Length);

        private ArithmeticError CreateError(ArithmeticErrorKind kind, int tokenStart, int end)
        {
            return new ArithmeticError(kind, ShellValue.FromBytes(_expression.Bytes[SkipBlanks(0)..end]))
            {
                Token = ShellValue.FromBytes(_expression.Bytes[tokenStart..end]),
            };
        }

        private int SkipBlanks(int start)
        {
            while (start < Span.Length && Span[start] is (byte)' ' or (byte)'\t')
            {
                _cancellationToken.ThrowIfCancellationRequested();
                start++;
            }
            return start;
        }

        private void SkipWhitespace()
        {
            if (_operand.Syntax is { } syntax && syntax.TrySkipBlanks(_index, out var end))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _index = end;
                return;
            }
            while (_index < Span.Length && IsArithmeticBlank(Span[_index]))
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
            var saved = (_index, _lastTokenStart);
            var hasReference = TryParseReference(out var reference);
            var afterReference = (_index, _lastTokenStart);
            if (!hasReference || !TryMatchAssignmentOperator(out var op))
            {
                if (Error != null)
                {
                    _parsed = default;
                    yield break;
                }
                if (hasReference)
                    _referenceLookahead = (afterReference, reference);
                (_index, _lastTokenStart) = saved;
                yield return Call(TryParseConditional());
                if (_parsed.Success && !IsAtEnd && TryMatchAssignmentOperator(out _))
                {
                    Error = CreateError(ArithmeticErrorKind.AssignmentToNonVariable, _lastTokenStart);
                    _parsed = default;
                }
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
            if (!_parsed.TryGet(out var rhs) || !IsNextTokenValid())
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
                    Error = CreateError(ArithmeticErrorKind.DivisionByZero, CurrentTokenStart());
                    _parsed = default;
                    yield break;
                }
            }
            yield return Write(reference, binding, value);
            _parsed = Error == null ? new ParseResult(true, value) : default;
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

            SkipWhitespace();
            if (IsAtEnd || Span[_index] == (byte)':')
            {
                Error = CreateError(ArithmeticErrorKind.ExpressionExpected, CurrentTokenStart());
                _parsed = default;
                yield break;
            }

            if (condition != 0 && !IsSuppressed)
            {
                yield return Call(TryParseNested(ParseProduction.Expression));
                if (!_parsed.TryGet(out value))
                    yield break;

                if (!TryReadConditionalSeparator())
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

            if (!TryReadConditionalSeparator())
            {
                _parsed = default;
                yield break;
            }

            yield return Call(TryParseNested(ParseProduction.Conditional));
            yield break;

            bool TryReadConditionalSeparator()
            {
                if (!TryMatch(":"u8))
                {
                    if (IsNextTokenValid())
                        Error = CreateError(ArithmeticErrorKind.MissingConditionalColon, CurrentTokenStart());
                    return false;
                }
                SkipWhitespace();
                if (!IsAtEnd)
                    return true;
                Error = CreateError(ArithmeticErrorKind.ExpressionExpected, _lastTokenStart);
                return false;
            }
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
                    parser.ConsumeToken(token.Length);
                    var rhsStart = parser._index;
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
                        if (parser.IsNextTokenValid())
                            parser.Error = token.Operator == BinaryOperator.Power
                                ? parser.CreateError(ArithmeticErrorKind.NegativeExponent, parser.CurrentTokenStart())
                                : parser.CreateError(ArithmeticErrorKind.DivisionByZero, parser.SkipBlanks(rhsStart));
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
                    ? cached.Binary
                    : GetBinaryToken(token, kind, parser.Span[(parser._index + token.Length)..], parser._cancellationToken);
                return binary.Length != 0;
            }
        }

        private IEnumerable<ParseStep>? TryParseUnary()
        {
            // The first operand after a rewind already has an identifier token.
            if (_referenceLookahead.HasValue)
                return Parse(this);

            var token = PeekToken(out var kind);
            if (kind == ArithmeticTokenKind.Number)
            {
                var valid = TryReadNumberToken(token, out var number);
                ConsumeToken(token.Length);
                _parsed = valid ? new ParseResult(true, IsSuppressed ? 0 : number) : default;
                return valid ? PrepareTrailingReference() : null;
            }
            return Parse(this);

            static IEnumerable<ParseStep> Parse(Parser<TTarget> parser)
            {
                var prefixStart = parser._index;
                var previousToken = parser._lastTokenStart;
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
                        var trailing = parser.PrepareTrailingReference();
                        if (parser.Error != null)
                            yield break;
                        if (parser.IsSuppressed)
                        {
                            parser.CompletePrefixMutation(0);
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
                        parser.CompletePrefixMutation(assigned);
                        if (parser._parsed.Success && trailing != null)
                            yield return parser.Call(trailing);
                        yield break;
                    }

                    if (parser.Error != null)
                    {
                        parser._parsed = default;
                        yield break;
                    }
                    // Only a variable takes prefix mutation; ++1 is two unary pluses.
                    parser._index = prefixStart;
                    parser._lastTokenStart = previousToken;
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
                    parser.Error ??= parser.CreateError(ArithmeticErrorKind.OperandExpected, parser._lastTokenStart);
                    parser._parsed = default;
                    yield break;
                }

                if (parser.Span[parser._index] == (byte)'(')
                {
                    parser.ConsumeToken(1);
                    yield return parser.Call(parser.TryParseNested(ParseProduction.Expression));
                    if (!parser._parsed.Success)
                        yield break;
                    if (parser.TryMatch(")"u8))
                    {
                        var trailing = parser.PrepareTrailingReference();
                        if (trailing != null)
                            yield return parser.Call(trailing);
                    }
                    else
                    {
                        if (parser.IsNextTokenValid())
                            parser.Error = parser.CreateError(ArithmeticErrorKind.MissingParenthesis, parser.CurrentTokenStart());
                        parser._parsed = default;
                    }
                    yield break;
                }

                if (!parser.TryParseReference(out var identifier))
                {
                    parser.Error ??= parser.CreateError(ArithmeticErrorKind.OperandExpected, parser.CurrentTokenStart());
                    parser._parsed = default;
                    yield break;
                }
                var postfix = parser.TryMatch("++"u8) || parser.TryMatch("--"u8);
                var trailingReference = postfix ? null : parser.PrepareTrailingReference();
                if (parser.Error != null)
                    yield break;
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
                parser._parsed = new ParseResult(true, currentValue);
                if (postfix)
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
                    trailingReference = parser.PrepareTrailingReference();
                }

                if (trailingReference != null)
                    yield return parser.Call(trailingReference);
            }
        }

        private void CompletePrefixMutation(long value)
        {
            if (Error == null)
            {
                // Prefix mutation commits before the adjacent second mutation is rejected.
                var following = Span[_index..];
                if (following.StartsWith("++"u8))
                    Error = CreateError(ArithmeticErrorKind.IncrementRequiresVariable, _index);
                else if (following.StartsWith("--"u8))
                    Error = CreateError(ArithmeticErrorKind.DecrementRequiresVariable, _index);
            }
            _parsed = Error == null ? new ParseResult(true, value) : default;
        }

        private IEnumerable<ParseStep>? PrepareTrailingReference()
        {
            SkipWhitespace();
            if (IsAtEnd || !VariableNames.IsNameStart((char)Span[_index]))
                return null;

            // A reference token checks the next token before its value is read. Consecutive
            // names extend that lexical lookahead without evaluating the later references.
            var cursor = (_index, _lastTokenStart);
            try
            {
                if (TryParseReference(out var first))
                {
                    var assignment = PeekToken(out _).SequenceEqual("="u8);
                    while (!IsAtEnd && VariableNames.IsNameStart((char)Span[_index]))
                    {
                        if (!TryParseReference(out _))
                            break;
                        SkipWhitespace();
                    }
                    if (Error == null && IsNextTokenValid())
                        return IsSuppressed || assignment ? null : Read(this, first);
                }
                _parsed = default;
                return null;
            }
            finally
            {
                (_index, _lastTokenStart) = cursor;
            }

            static IEnumerable<ParseStep> Read(Parser<TTarget> parser, ArithmeticReference reference)
            {
                yield return parser.Bind(reference, ArithmeticReferenceUse.Read);
                if (parser._bound == null)
                    parser._parsed = default;
            }
        }

        private bool TryParseReference(out ArithmeticReference reference)
        {
            SkipWhitespace();
            if (_referenceLookahead is { } lookahead)
            {
                // Assignment lookahead only parsed the reference. Binding still happens when reached.
                _referenceLookahead = null;
                if (_index == lookahead.Cursor.LastTokenStart)
                {
                    (_index, _lastTokenStart) = lookahead.Cursor;
                    reference = lookahead.Reference;
                    return true;
                }
            }
            if (_index >= Span.Length || !VariableNames.IsNameStart((char)Span[_index]))
            {
                reference = default;
                return false;
            }

            var cached = default(CachedReference);
            var hasCached = _operand.Syntax?.TryGetReference(_index, out cached) == true;
            var nameLength = hasCached ? 0 : PeekToken(out _).Length;
            var bracket = hasCached ? cached.Bracket : _index + nameLength;
            var source = ShellSource.FromBytes(_expression.Bytes);
            if (_operand.Subscripts.Count != 0 && bracket >= 0 && _operand.Subscripts.TryGetValue(bracket, out var prepared))
            {
                // Preparation recorded the bracket offset before inserting the expanded bytes.
                // Those bytes can include shell punctuation and do not define the selector boundaries.
                var name = hasCached ? cached.Syntax.Name : ReadReferenceName(source, _index, nameLength, _memory);
                reference = new ArithmeticReference(VariableReference.ForElement(name, prepared.Source), prepared.Expansion);
                ConsumeToken(bracket + prepared.Expansion.Value.Bytes.Length + 2 - _index);
                return true;
            }
            if (!hasCached)
                cached = ReadReferenceSyntax(source, _index, nameLength, _memory);
            ConsumeToken(cached.End - _index);
            if (cached.IsIncomplete)
            {
                reference = default;
                Error = CreateError(ArithmeticErrorKind.BadArraySubscript, _lastTokenStart);
                return false;
            }
            // A quoted selector diagnoses a following malformed token before reading the variable's value.
            if (_operand.IsExpandedIndex && !IsNextTokenValid())
            {
                reference = default;
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
            if (kind == ArithmeticTokenKind.Invalid)
            {
                var failure = Span[_lastTokenStart] == (byte)')'
                    ? ArithmeticErrorKind.OperandExpected : ArithmeticErrorKind.InvalidOperator;
                Error ??= CreateError(failure, _index);
                return false;
            }
            return kind != ArithmeticTokenKind.Number || TryReadNumberToken(token, out _);
        }

        private bool TryReadNumberToken(ReadOnlySpan<byte> token, out long number)
        {
            ArithmeticErrorKind? failure;
            if (_operand.Syntax is { } syntax && syntax.TryGetToken(_index, out var cached))
            {
                number = cached.Number;
                failure = cached.NumberError;
            }
            else
                failure = ReadIntegerLiteral(token, _cancellationToken, out number);
            if (failure is not { } kind)
                return true;
            // The lexical diagnostic occurs while the number is isolated from its following bytes.
            Error ??= CreateError(kind, _index, _index + token.Length);
            return false;
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
            ConsumeToken(token.Length);
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

            ConsumeToken(1);
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

            ConsumeToken(token.Length);
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

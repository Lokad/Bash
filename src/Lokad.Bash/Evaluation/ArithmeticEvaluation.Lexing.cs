using System;
using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public static partial class ArithmeticEvaluation
{
    internal enum ArithmeticTokenKind
    {
        End,
        Invalid,
        Number,
        Identifier,
        Operator
    }

    internal enum AssignmentOperator
    {
        Assign,
        Add,
        Subtract,
        Multiply,
        Divide,
        Remainder,
        LeftShift,
        RightShift,
        BitwiseAnd,
        BitwiseXor,
        BitwiseOr,
    }

    internal enum BinaryOperator
    {
        LogicalOr,
        LogicalAnd,
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        Power,
        LeftShift,
        RightShift,
        Add,
        Subtract,
        Multiply,
        Divide,
        Remainder,
        BitwiseAnd,
        BitwiseXor,
        BitwiseOr,
    }

    internal enum ArithmeticPrecedence
    {
        LogicalOr, LogicalAnd, BitwiseOr, BitwiseXor, BitwiseAnd, Equality,
        Relational, Shift, Additive, Multiplicative, Power
    }

    internal readonly record struct BinaryToken(BinaryOperator Operator, ArithmeticPrecedence Precedence, int Length);

    internal readonly record struct TokenShape(ArithmeticTokenKind Kind, int Length);
    internal readonly record struct CachedToken(TokenShape Shape, ArithmeticErrorKind? NumberError, long Number,
        BinaryToken Binary, AssignmentOperator? Assignment);
    internal readonly record struct CachedReference(VariableReference Syntax, int End, int Bracket, bool IsIncomplete);

    private static bool IsArithmeticBlank(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n';

    private static int SkipArithmeticBlanks(ReadOnlySpan<byte> bytes, int start, CancellationToken cancellationToken)
    {
        const int MaximumScanBytes = 4096;
        if (start >= bytes.Length || !IsArithmeticBlank(bytes[start]))
            return start;

        cancellationToken.ThrowIfCancellationRequested();
        start++;
        while (start < bytes.Length && IsArithmeticBlank(bytes[start]))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(bytes.Length - start, MaximumScanBytes);
            var next = bytes.Slice(start, length).IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\n');
            if (next >= 0)
                return start + next;
            start += length;
        }
        return start;
    }

    /// <summary>Checks that an operand has only numeric and operator tokens, with no shell expansions or bindings.</summary>
    internal static bool IsReferenceFree(ReadOnlySpan<byte> expression, CancellationToken cancellationToken)
    {
        while (!expression.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expression = expression[SkipArithmeticBlanks(expression, 0, cancellationToken)..];
            if (expression.IsEmpty)
                return true;
            var token = ReadTokenShape(expression, cancellationToken);
            if (token.Kind is not (ArithmeticTokenKind.Number or ArithmeticTokenKind.Operator))
                return false;
            expression = expression[token.Length..];
        }
        return true;
    }

    private static BinaryToken GetBinaryToken(ReadOnlySpan<byte> token, ArithmeticTokenKind kind,
        ReadOnlySpan<byte> following, CancellationToken cancellationToken)
    {
        if (kind != ArithmeticTokenKind.Operator)
            return default;

        if (token is [(byte)'+', (byte)'+'] or [(byte)'-', (byte)'-'])
        {
            // A doubled sign before a name stays a mutation token in infix position.
            var next = SkipArithmeticBlanks(following, 0, cancellationToken);
            if (next < following.Length && VariableNames.IsNameStart((char)following[next]))
                return default;
        }

        return token switch
        {
            [(byte)'|', (byte)'|'] => new(BinaryOperator.LogicalOr, ArithmeticPrecedence.LogicalOr, 2),
            [(byte)'&', (byte)'&'] => new(BinaryOperator.LogicalAnd, ArithmeticPrecedence.LogicalAnd, 2),
            [(byte)'|'] => new(BinaryOperator.BitwiseOr, ArithmeticPrecedence.BitwiseOr, 1),
            [(byte)'^'] => new(BinaryOperator.BitwiseXor, ArithmeticPrecedence.BitwiseXor, 1),
            [(byte)'&'] => new(BinaryOperator.BitwiseAnd, ArithmeticPrecedence.BitwiseAnd, 1),
            [(byte)'=', (byte)'='] => new(BinaryOperator.Equal, ArithmeticPrecedence.Equality, 2),
            [(byte)'!', (byte)'='] => new(BinaryOperator.NotEqual, ArithmeticPrecedence.Equality, 2),
            [(byte)'<', (byte)'='] => new(BinaryOperator.LessOrEqual, ArithmeticPrecedence.Relational, 2),
            [(byte)'>', (byte)'='] => new(BinaryOperator.GreaterOrEqual, ArithmeticPrecedence.Relational, 2),
            [(byte)'<'] => new(BinaryOperator.Less, ArithmeticPrecedence.Relational, 1),
            [(byte)'>'] => new(BinaryOperator.Greater, ArithmeticPrecedence.Relational, 1),
            [(byte)'<', (byte)'<'] => new(BinaryOperator.LeftShift, ArithmeticPrecedence.Shift, 2),
            [(byte)'>', (byte)'>'] => new(BinaryOperator.RightShift, ArithmeticPrecedence.Shift, 2),
            [(byte)'+'] or [(byte)'+', (byte)'+'] => new(BinaryOperator.Add, ArithmeticPrecedence.Additive, 1),
            [(byte)'-'] or [(byte)'-', (byte)'-'] => new(BinaryOperator.Subtract, ArithmeticPrecedence.Additive, 1),
            [(byte)'*'] => new(BinaryOperator.Multiply, ArithmeticPrecedence.Multiplicative, 1),
            [(byte)'/'] => new(BinaryOperator.Divide, ArithmeticPrecedence.Multiplicative, 1),
            [(byte)'%'] => new(BinaryOperator.Remainder, ArithmeticPrecedence.Multiplicative, 1),
            [(byte)'*', (byte)'*'] => new(BinaryOperator.Power, ArithmeticPrecedence.Power, 2),
            _ => default
        };
    }

    private static TokenShape ReadTokenShape(ReadOnlySpan<byte> remaining, CancellationToken cancellationToken)
    {
        if (remaining.IsEmpty) return new TokenShape(ArithmeticTokenKind.End, 0);
        ArithmeticTokenKind kind;
        var first = remaining[0];
        var length = 1;
        if (VariableNames.IsNamePart((char)first))
        {
            kind = VariableNames.IsNameStart((char)first) ? ArithmeticTokenKind.Identifier : ArithmeticTokenKind.Number;
            while (length < remaining.Length
                && (VariableNames.IsNamePart((char)remaining[length])
                    || kind == ArithmeticTokenKind.Number && remaining[length] is (byte)'#' or (byte)'@'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                length++;
            }
        }
        else if (first is (byte)'(' or (byte)')' or (byte)'?' or (byte)':' or (byte)',' or (byte)'~'
            or (byte)'+' or (byte)'-' or (byte)'*' or (byte)'/' or (byte)'%' or (byte)'<' or (byte)'>' or (byte)'&' or (byte)'|' or (byte)'^' or (byte)'=' or (byte)'!')
        {
            kind = ArithmeticTokenKind.Operator;
            if (remaining.Length > 1)
            {
                var next = remaining[1];
                if (first == next && first is (byte)'<' or (byte)'>')
                    length = remaining.Length > 2 && remaining[2] == (byte)'=' ? 3 : 2;
                else if (first == next && first is (byte)'+' or (byte)'-' or (byte)'*' or (byte)'&' or (byte)'|'
                    || next == (byte)'=' && first is (byte)'+' or (byte)'-' or (byte)'*' or (byte)'/' or (byte)'%' or (byte)'<' or (byte)'>' or (byte)'&' or (byte)'|' or (byte)'^' or (byte)'=' or (byte)'!')
                    length = 2;
            }
        }
        else
        {
            kind = ArithmeticTokenKind.Invalid;
        }
        return new TokenShape(kind, length);
    }

    private static AssignmentOperator? GetAssignment(ReadOnlySpan<byte> token) => token switch
    {
        [(byte)'='] => AssignmentOperator.Assign,
        [(byte)'+', (byte)'='] => AssignmentOperator.Add,
        [(byte)'-', (byte)'='] => AssignmentOperator.Subtract,
        [(byte)'*', (byte)'='] => AssignmentOperator.Multiply,
        [(byte)'/', (byte)'='] => AssignmentOperator.Divide,
        [(byte)'%', (byte)'='] => AssignmentOperator.Remainder,
        [(byte)'<', (byte)'<', (byte)'='] => AssignmentOperator.LeftShift,
        [(byte)'>', (byte)'>', (byte)'='] => AssignmentOperator.RightShift,
        [(byte)'&', (byte)'='] => AssignmentOperator.BitwiseAnd,
        [(byte)'^', (byte)'='] => AssignmentOperator.BitwiseXor,
        [(byte)'|', (byte)'='] => AssignmentOperator.BitwiseOr,
        _ => null
    };

    private static CachedReference ReadReferenceSyntax(ShellSource source, int start, int nameLength, MemoryCapacity? memory)
    {
        var name = ReadReferenceName(source, start, nameLength, memory);
        var reference = VariableReference.ForName(name);
        var end = start + nameLength;
        if (end == source.Length || source.AsSpan()[end] != (byte)'[')
            return new CachedReference(reference, end, -1, false);
        var bracket = end;
        if (!ArraySubscriptScanner.TryScan(new ShellScanContext { SyntaxMemory = memory }, source, bracket, out var closing))
            return new CachedReference(reference, end, bracket, true);
        reference = VariableReference.ForElement(name, source.Slice(bracket + 1, closing - bracket - 1));
        return new CachedReference(reference, closing + 1, bracket, false);
    }

    private static BindingName ReadReferenceName(ShellSource source, int start, int length, MemoryCapacity? memory)
    {
        memory?.Grow(checked(4L * length));
        return Utf8Text.Decode(source.Bytes.Slice(start, length));
    }

    /// <summary>Immutable lexical decisions owned by source syntax; no binding or execution state is retained.</summary>
    internal sealed class LexicalSyntax
    {
        private readonly Dictionary<int, CachedToken>? _tokens;
        private readonly Dictionary<int, CachedReference>? _references;
        private readonly Dictionary<int, int>? _blanks;
        public long? LiteralValue { get; }

        public LexicalSyntax(ShellSource source, MemoryCapacity? memory)
        {
            Dictionary<int, CachedToken>? tokens = null;
            Dictionary<int, CachedReference>? references = null;
            Dictionary<int, int>? blanks = null;
            var bytes = source.AsSpan();
            var previousEnd = 0;
            while (true)
            {
                var index = SkipArithmeticBlanks(bytes, previousEnd, CancellationToken.None);
                if (index == bytes.Length)
                {
                    if (tokens == null)
                    {
                        LiteralValue = 0;
                        return;
                    }
                    RememberBlanks(previousEnd, index);
                    break;
                }

                var shape = ReadTokenShape(bytes[index..], CancellationToken.None);
                var token = bytes.Slice(index, shape.Length);
                long value = 0;
                var numberError = shape.Kind == ArithmeticTokenKind.Number
                    ? ReadIntegerLiteral(token, CancellationToken.None, out value) : null;
                var end = index + shape.Length;
                var next = end;
                if (tokens == null && shape.Kind == ArithmeticTokenKind.Number && numberError == null)
                {
                    next = SkipArithmeticBlanks(bytes, end, CancellationToken.None);
                    if (next == bytes.Length)
                    {
                        LiteralValue = value;
                        return;
                    }
                }

                RememberBlanks(previousEnd, index);
                memory?.Grow(256);
                (tokens ??= new()).Add(index, new CachedToken(shape, numberError, value,
                    GetBinaryToken(token, shape.Kind, bytes[end..], CancellationToken.None), GetAssignment(token)));
                if (shape.Kind == ArithmeticTokenKind.Operator && token.Length == 2
                    && token[0] == token[1] && token[0] is (byte)'+' or (byte)'-')
                {
                    var tail = ReadTokenShape(bytes[(index + 1)..], CancellationToken.None);
                    var tailText = bytes.Slice(index + 1, tail.Length);
                    memory?.Grow(256);
                    tokens.Add(index + 1, new CachedToken(tail, null, 0,
                        GetBinaryToken(tailText, tail.Kind, bytes[(index + 1 + tail.Length)..], CancellationToken.None),
                        GetAssignment(tailText)));
                }
                RememberBlanks(end, next);
                if (shape.Kind == ArithmeticTokenKind.Identifier)
                {
                    memory?.Grow(256);
                    var reference = ReadReferenceSyntax(source, index, shape.Length, memory);
                    next = reference.End;
                    (references ??= new()).Add(index, reference);
                    // No execution can pass a malformed reference. Avoid rescanning its
                    // remainder for another incomplete selector at every identifier.
                    if (reference.IsIncomplete)
                        break;
                }
                previousEnd = next;
            }
            _tokens = tokens;
            _references = references;
            _blanks = blanks;

            void RememberBlanks(int start, int end)
            {
                if (start == end)
                    return;
                memory?.Grow(128); // Coarse dictionary capacity, including growth overlap.
                (blanks ??= new()).Add(start, end);
            }
        }

        public bool TryGetToken(int start, out CachedToken token)
        {
            token = default;
            return _tokens?.TryGetValue(start, out token) == true;
        }

        public bool TryGetReference(int start, out CachedReference reference)
        {
            reference = default;
            return _references?.TryGetValue(start, out reference) == true;
        }

        public bool TrySkipBlanks(int start, out int end)
        {
            end = default;
            return _blanks?.TryGetValue(start, out end) == true;
        }
    }
}

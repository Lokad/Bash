using System;
using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Helpers;
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
    internal readonly record struct CachedToken(TokenShape Shape, bool ValidNumber, long Number,
        BinaryToken Binary, AssignmentOperator? Assignment);
    internal readonly record struct CachedReference(VariableReference Syntax, int End, int Bracket, ArithmeticError? Failure);

    private static bool ReadCachedNumber(CachedToken token, out long number)
    {
        number = token.Number;
        return token.ValidNumber;
    }

    private static BinaryToken GetBinaryToken(ReadOnlySpan<byte> token, ArithmeticTokenKind kind) =>
        kind == ArithmeticTokenKind.Operator ? token switch
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
        } : default;

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

    private static CachedReference ReadReferenceSyntax(ShellSource source, int start, int nameLength)
    {
        var name = Utf8Text.Decode(source.Bytes.Slice(start, nameLength));
        var reference = VariableReference.ForName(name);
        var end = start + nameLength;
        if (end == source.Length || source.AsSpan()[end] != (byte)'[')
            return new CachedReference(reference, end, -1, null);
        var bracket = end;
        if (!ArraySubscriptScanner.TryScan(source, bracket, out var closing))
            return new CachedReference(reference, end, bracket,
                new ArithmeticError(ArithmeticErrorKind.BadArraySubscript, ShellValue.FromBytes(source.Bytes[start..])));
        reference = VariableReference.ForElement(name, source.Slice(bracket + 1, closing - bracket - 1));
        return new CachedReference(reference, closing + 1, bracket, null);
    }

    /// <summary>Immutable lexical decisions owned by source syntax; no binding or execution state is retained.</summary>
    internal sealed class LexicalSyntax
    {
        private readonly Dictionary<int, CachedToken> _tokens = new();
        private readonly Dictionary<int, CachedReference> _references = new();
        private readonly Dictionary<int, int> _blanks = new();
        public long? LiteralValue { get; }

        public LexicalSyntax(ShellSource source)
        {
            var bytes = source.AsSpan();
            var ordinaryTokens = 0;
            long? literal = 0;
            for (var index = 0; index < bytes.Length;)
            {
                if (bytes[index] is (byte)' ' or (byte)'\t' or (byte)'\n')
                {
                    var start = index++;
                    while (index < bytes.Length && bytes[index] is (byte)' ' or (byte)'\t' or (byte)'\n') index++;
                    _blanks.Add(start, index);
                    continue;
                }
                var shape = ReadTokenShape(bytes[index..], CancellationToken.None);
                var token = bytes.Slice(index, shape.Length);
                long value = 0;
                var valid = shape.Kind == ArithmeticTokenKind.Number && TryParseIntegerLiteral(token, out value);
                _tokens.Add(index, new CachedToken(shape, valid, value, GetBinaryToken(token, shape.Kind), GetAssignment(token)));
                ordinaryTokens++;
                literal = ordinaryTokens == 1 && valid ? value : null;
                if (shape.Kind == ArithmeticTokenKind.Operator && token.Length == 2
                    && token[0] == token[1] && token[0] is (byte)'+' or (byte)'-')
                {
                    var tail = ReadTokenShape(bytes[(index + 1)..], CancellationToken.None);
                    var tailText = bytes.Slice(index + 1, tail.Length);
                    _tokens.Add(index + 1, new CachedToken(tail, false, 0, GetBinaryToken(tailText, tail.Kind), GetAssignment(tailText)));
                }
                var end = index + shape.Length;
                if (shape.Kind == ArithmeticTokenKind.Identifier)
                {
                    var reference = ReadReferenceSyntax(source, index, shape.Length);
                    end = reference.End;
                    _references.Add(index, reference);
                    // No execution can pass a malformed reference. Avoid rescanning its
                    // remainder for another incomplete selector at every identifier.
                    if (reference.Failure != null)
                        break;
                }
                index = end;
            }
            LiteralValue = literal;
        }

        public bool TryGetToken(int start, out CachedToken token) => _tokens.TryGetValue(start, out token);
        public bool TryGetReference(int start, out CachedReference reference) => _references.TryGetValue(start, out reference);
        public bool TrySkipBlanks(int start, out int end) => _blanks.TryGetValue(start, out end);
    }
}

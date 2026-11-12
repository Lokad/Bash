using System;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Syntax;

/// <summary>Parses conditional operators without interpreting or expanding operand data.</summary>
internal sealed class ConditionalParser
{
    private readonly ShellSource _source;
    private int _position;
    private Token? _lookahead;
    private SyntaxError? _error;

    private ConditionalParser(ShellSource source) => _source = source;

    internal static bool TryParse(ShellSource source,
        [NotNullWhen(true)] out ConditionalExpression? expression,
        [NotNullWhen(false)] out SyntaxError? error)
    {
        var parser = new ConditionalParser(source);
        expression = parser.ParseOr(0);
        if (parser.Peek().Kind != TokenKind.End)
            parser.Fail("Unexpected conditional operator or operand.");
        error = parser._error;
        if (error != null || expression == null)
        {
            expression = null;
            error ??= new SyntaxError("Conditional operand expected.", parser._position);
            return false;
        }
        return true;
    }

    private ConditionalExpression? ParseOr(int depth)
    {
        var left = ParseAnd(depth);
        while (_error == null && Consume(TokenKind.Or))
        {
            var right = ParseAnd(depth);
            if (left == null || right == null)
                return null;
            left = new ConditionalExpression.Logical(false, left, right);
        }
        return left;
    }

    private ConditionalExpression? ParseAnd(int depth)
    {
        var left = ParseTerm(depth);
        while (_error == null && Consume(TokenKind.And))
        {
            var right = ParseTerm(depth);
            if (left == null || right == null)
                return null;
            left = new ConditionalExpression.Logical(true, left, right);
        }
        return left;
    }

    private ConditionalExpression? ParseTerm(int depth)
    {
        var negated = false;
        while (Peek().Source.AsSpan().SequenceEqual("!"u8))
        {
            Take();
            negated = !negated;
        }
        ConditionalExpression? expression;
        if (Consume(TokenKind.Open))
        {
            if (depth >= ShellSourceCursor.MaximumNestedExpansionDepth)
            {
                Fail("Conditional nesting exceeds the parsing limit.");
                return null;
            }
            expression = ParseOr(depth + 1);
            if (!Consume(TokenKind.Close))
                Fail("Closing conditional parenthesis expected.");
        }
        else
        {
            var token = Take();
            if (token.Kind != TokenKind.Word)
            {
                Fail("Conditional operand expected.");
                return null;
            }
            if (ShellPredicates.TryGetUnaryPredicate(token.Source.AsSpan(), out var unary))
            {
                var operand = ReadOperand(false);
                expression = operand == null ? null : new ConditionalExpression.Unary(unary, operand);
            }
            else
            {
                var left = ParseWord(token);
                if (ShellPredicates.TryGetBinaryPredicate(Peek().Source.AsSpan(), out var binary))
                {
                    Take();
                    var right = ReadOperand(binary.Operator == ShellPredicates.BinaryPredicateKind.RegexMatch);
                    expression = left == null || right == null ? null : new ConditionalExpression.Binary(binary, left, right);
                }
                else
                    expression = left == null ? null : new ConditionalExpression.NonEmpty(left);
            }
        }
        return negated && expression != null ? new ConditionalExpression.Negated(expression) : expression;
    }

    private Word? ReadOperand(bool regex)
    {
        // A regex operand has its own shell lexical context: | and balanced parentheses
        // belong to the regex word, while an unmatched ) closes a conditional group.
        var token = _lookahead == null ? Read(regex) : Take();
        if (token.Kind != TokenKind.Word)
        {
            Fail("Conditional operand expected.");
            return null;
        }
        return ParseWord(token);
    }

    private Word? ParseWord(Token token)
    {
        if (Tokenizer.TryReadExpansionWord(token.Source, ExpansionWordSyntax.Parameter, ShellQuoteKind.None,
                out var word, out var error))
            return word;
        _error ??= error;
        return null;
    }

    private Token Peek() => _lookahead ??= Read(false);
    private Token Take()
    {
        var token = Peek();
        _lookahead = null;
        return token;
    }
    private bool Consume(TokenKind kind)
    {
        if (Peek().Kind != kind)
            return false;
        Take();
        return true;
    }

    private Token Read(bool regex)
    {
        var span = _source.AsSpan();
        while (_position < span.Length && span[_position] is (byte)' ' or (byte)'\t' or (byte)'\n')
            _position++;
        if (_position == span.Length)
            return new(TokenKind.End, default);
        var start = _position;
        if (!regex)
        {
            var kind = span[start] switch
            {
                (byte)'(' => TokenKind.Open,
                (byte)')' => TokenKind.Close,
                (byte)'<' or (byte)'>' => TokenKind.Word,
                (byte)'&' when start + 1 < span.Length && span[start + 1] == (byte)'&' => TokenKind.And,
                (byte)'|' when start + 1 < span.Length && span[start + 1] == (byte)'|' => TokenKind.Or,
                (byte)'&' or (byte)'|' or (byte)';' => TokenKind.Invalid,
                _ => TokenKind.End
            };
            if (kind != TokenKind.End)
            {
                _position += kind is TokenKind.And or TokenKind.Or ? 2 : 1;
                return new(kind, _source.Slice(start, _position - start));
            }
        }
        var cursor = new ShellSourceCursor(_source, start);
        var parentheses = 0;
        var bracket = false;
        var firstBracketMember = false;
        byte classTerminator = 0;
        while (!cursor.IsAtEnd)
        {
            if (!cursor.IsQuoted)
            {
                var ch = cursor.Current;
                if (ch is (byte)' ' or (byte)'\t' or (byte)'\n')
                    break;
                if (regex)
                {
                    if (bracket)
                    {
                        if (classTerminator != 0)
                        {
                            if (ch == classTerminator && cursor.Position + 1 < span.Length
                                && span[cursor.Position + 1] == (byte)']')
                            {
                                cursor.Advance();
                                cursor.Advance();
                                classTerminator = 0;
                                firstBracketMember = false;
                                continue;
                            }
                        }
                        else if (ch == (byte)'[' && cursor.Position + 1 < span.Length
                            && span[cursor.Position + 1] is (byte)':' or (byte)'=' or (byte)'.')
                            classTerminator = span[cursor.Position + 1];
                        else if (ch == (byte)']' && !firstBracketMember)
                            bracket = false;
                        else if (!(firstBracketMember && ch == (byte)'^'))
                            firstBracketMember = false;
                    }
                    else if (ch == (byte)'[')
                    {
                        bracket = true;
                        firstBracketMember = true;
                    }
                    else if (ch == (byte)'(') parentheses++;
                    else if (ch == (byte)')')
                    {
                        if (parentheses == 0) break;
                        parentheses--;
                    }
                }
                else if (ch is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'&' or (byte)'|' or (byte)';')
                    break;
            }
            if (cursor.TryConsumeQuoteOrEscape())
                continue;
            switch (cursor.SkipNestedExpansion(BracedParameterRecognition.All))
            {
                case ShellNestedExpansionScan.Complete: continue;
                case ShellNestedExpansionScan.Incomplete or ShellNestedExpansionScan.Invalid:
                    Fail("Invalid expansion in conditional operand.");
                    return new(TokenKind.Invalid, default);
            }
            cursor.Advance();
        }
        if (cursor.IsQuoted || cursor.Position == start)
        {
            Fail("Invalid or unterminated conditional operand.");
            return new(TokenKind.Invalid, default);
        }
        _position = cursor.Position;
        return new(TokenKind.Word, _source.Slice(start, _position - start));
    }

    private void Fail(string message) => _error ??= new(message, _position);
    private enum TokenKind { End, Word, And, Or, Open, Close, Invalid }
    private sealed record Token(TokenKind Kind, ShellSource Source);
}

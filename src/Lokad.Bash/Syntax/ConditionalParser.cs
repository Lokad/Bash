using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Syntax;

/// <summary>Parses conditional operators without interpreting or expanding operand data.</summary>
internal sealed class ConditionalParser
{
    private static readonly Word EmptyRegexOperand = new([]);
    private readonly ShellSource _source;
    private readonly ShellScanContext _context;
    private readonly bool _command;
    private List<(int Start, int End)>? _ignoredRanges;
    private int _position;
    private Token? _lookahead;
    private SyntaxError? _error;

    private ConditionalParser(ShellSource source, int position, ShellScanContext context, bool command) =>
        (_source, _position, _context, _command) = (source, position, context, command);

    internal static bool TryParse(ShellSource source,
        [NotNullWhen(true)] out ConditionalExpression? expression,
        out ShellSource spelling,
        [NotNullWhen(false)] out SyntaxError? error)
    {
        var parser = new ConditionalParser(source, 0, new ShellScanContext(), command: false);
        spelling = default;
        if (!parser.TryParse(out expression, out error))
            return false;
        spelling = parser.GetSpelling(0, source.Length);
        return true;
    }

    /// <summary>The operand grammar owns closing-delimiter recognition as well as comments and regex words.</summary>
    internal static bool TryReadCommand(ShellSource source, int start, ShellScanContext context,
        [NotNullWhen(true)] out ConditionalCommand? command, out int end,
        [NotNullWhen(false)] out SyntaxError? error)
    {
        var parser = new ConditionalParser(source, start, context, command: true);
        command = null;
        end = start;
        if (!parser.TryParse(out var expression, out error))
            return false;
        var closing = parser.Peek();
        command = new ConditionalCommand(parser.GetSpelling(start, closing.Offset).TrimBlanks(), expression);
        end = parser._position;
        return true;
    }

    private bool TryParse([NotNullWhen(true)] out ConditionalExpression? expression,
        [NotNullWhen(false)] out SyntaxError? error)
    {
        expression = ParseOr(0);
        var ending = Peek();
        if (ending.Kind != (_command ? TokenKind.EndCommand : TokenKind.End))
            Fail(ending.Kind == TokenKind.End ? "Unterminated conditional command."
                : "Unexpected conditional operator or operand.", ending.Offset);
        error = _context.LimitError ?? _error;
        if (error != null || expression == null)
        {
            expression = null;
            error ??= new SyntaxError("Conditional operand expected.", _position);
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
        SkipNewlines();
        Span<byte> operation = stackalloc byte[3];
        var negated = false;
        while (ReadOperator(Peek(), operation) == 1 && operation[0] == (byte)'!')
        {
            Take();
            negated = !negated;
            SkipNewlines();
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
                Fail("Closing conditional parenthesis expected.", Peek().Offset);
        }
        else
        {
            var token = Take();
            if (token.Kind != TokenKind.Word)
            {
                Fail("Conditional operand expected.", token.Offset);
                return null;
            }
            if (ShellPredicates.TryGetUnaryPredicate(operation[..ReadOperator(token, operation)], out var unary))
            {
                if (unary.Operator == ShellPredicates.UnaryPredicateKind.VariableIsSet
                    && ParseVariableElement(Peek()) is { } element)
                {
                    Take();
                    expression = element;
                }
                else
                {
                    var operand = ReadOperand(false);
                    expression = operand == null ? null : new ConditionalExpression.Unary(unary, operand);
                }
            }
            else
            {
                var left = ParseWord(token);
                if (ShellPredicates.TryGetBinaryPredicate(operation[..ReadOperator(Peek(), operation)], out var binary))
                {
                    Take();
                    var right = ReadOperand(binary.Operator == ShellPredicates.BinaryPredicateKind.RegexMatch);
                    expression = left == null || right == null ? null : new ConditionalExpression.Binary(binary, left, right);
                }
                else
                {
                    if (Peek().Kind == TokenKind.Newline)
                        Fail("Conditional binary operator expected.", Peek().Offset);
                    expression = left == null ? null : new ConditionalExpression.NonEmpty(left);
                }
            }
        }
        SkipNewlines();
        return negated && expression != null ? new ConditionalExpression.Negated(expression) : expression;

        // Predicate tokens are at most three ASCII bytes; preserve physical operand slices.
        static int ReadOperator(Token token, Span<byte> output)
        {
            var source = token.Source.AsSpan();
            var count = 0;
            for (var index = 0; ; index++)
            {
                index = ShellSourceCursor.SkipLineContinuations(source, index);
                if (index == source.Length)
                    return count;
                if (count == output.Length)
                    return 0;
                output[count++] = source[index];
            }
        }
    }

    private Word? ReadOperand(bool regex)
    {
        // A regex operand has its own shell lexical context: | and balanced parentheses
        // belong to the regex word, while an unmatched ) closes a conditional group.
        var token = _lookahead == null ? Read(regex) : Take();
        if (token.Kind != TokenKind.Word)
        {
            Fail("Conditional operand expected.", token.Offset);
            return null;
        }
        return regex && token.Source.IsEmpty ? EmptyRegexOperand : ParseWord(token);
    }

    private ConditionalExpression.VariableElement? ParseVariableElement(Token token)
    {
        if (token.Kind != TokenKind.Word || !token.Source.AsSpan().Contains((byte)'['))
            return null;

        // A double-quoted whole operand still gives the bracket contents
        // their own expansion context. A single-quoted operand is data.
        var source = token.Source;
        var offset = token.Offset;
        var nameQuote = ShellQuoteKind.None;
        if (source.Length > 1 && source[0] == (byte)'"' && source[source.Length - 1] == (byte)'"')
        {
            source = source.Slice(1, source.Length - 2);
            offset++;
            nameQuote = ShellQuoteKind.Double;
        }
        var cursor = new ShellSourceCursor(source, 0, _context);
        while (!cursor.IsAtEnd)
        {
            if (!cursor.IsQuoted && cursor.Current == (byte)'[')
                break;
            if (cursor.TryConsumeQuoteOrEscape())
                continue;
            switch (cursor.SkipNestedExpansion(BracedParameterRecognition.All))
            {
                case ShellNestedExpansionScan.Complete: continue;
                case ShellNestedExpansionScan.Incomplete or ShellNestedExpansionScan.Invalid: return null;
            }
            cursor.Advance();
        }
        var opening = cursor.Position;
        if (cursor.IsAtEnd || opening == 0
            || !ArraySubscriptScanner.TryScan(_context, source, opening, out var closing)
            || closing != source.Length - 1)
            return null;

        var name = ParseWord(new Token(TokenKind.Word, source.Slice(0, opening), offset), nameQuote);
        var selector = ParseWord(new Token(TokenKind.Word, source.Slice(opening + 1, closing - opening - 1), offset + opening + 1));
        return name == null || selector == null ? null : new ConditionalExpression.VariableElement(name, selector);
    }

    private Word? ParseWord(Token token) => ParseWord(token, ShellQuoteKind.None);

    private Word? ParseWord(Token token, ShellQuoteKind quote)
    {
        if (Tokenizer.TryReadExpansionWord(token.Source, ExpansionWordSyntax.Parameter, quote, _context,
                out var word, out var error))
            return word;
        _error ??= new SyntaxError(error.Message, token.Offset + error.Offset);
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

    private void SkipNewlines()
    {
        while (Consume(TokenKind.Newline)) { }
    }

    private Token Read(bool regex)
    {
        var span = _source.AsSpan();
        while (_position < span.Length)
        {
            var next = ShellSourceCursor.SkipLineContinuations(span, _position);
            if (next != _position)
            {
                Ignore(_position, next);
                _position = next;
            }
            else if (span[_position] is (byte)' ' or (byte)'\t')
                _position++;
            else if (span[_position] == (byte)'#')
            {
                next = ShellSourceCursor.SkipComment(span, _position);
                Ignore(_position, next);
                _position = next;
            }
            else
                break;
        }
        if (_position == span.Length)
            return new(TokenKind.End, default, _position);
        var start = _position;
        if (span[start] == (byte)'\n')
        {
            _position++;
            return new(TokenKind.Newline, _source.Slice(start, 1), start);
        }
        if (!_context.TryReserveSyntax())
        {
            Fail("Script parsing limit exceeded (too many tokens or word parts).", start);
            _context.RecordLimitError(_error ?? throw new InvalidOperationException("A refused token requires an error."));
            return new(TokenKind.Invalid, default, start);
        }
        if (_command && ShellSourceCursor.TryMatchLogicalText(span, start, "]]", out var closing))
        {
            var after = ShellSourceCursor.SkipLineContinuations(span, closing);
            if (after == span.Length || span[after] is (byte)' ' or (byte)'\t' or (byte)'\n'
                or (byte)';' or (byte)'&' or (byte)'|' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>')
            {
                _position = closing;
                return new(TokenKind.EndCommand, _source.Slice(start, closing - start), start);
            }
        }
        if (!regex)
        {
            var logicalEnd = start;
            var kind = span[start] switch
            {
                (byte)'(' => TokenKind.Open,
                (byte)')' => TokenKind.Close,
                (byte)'<' or (byte)'>' => TokenKind.Order,
                (byte)'&' when ShellSourceCursor.TryMatchLogicalText(span, start, "&&", out logicalEnd) => TokenKind.And,
                (byte)'|' when ShellSourceCursor.TryMatchLogicalText(span, start, "||", out logicalEnd) => TokenKind.Or,
                (byte)'&' or (byte)'|' or (byte)';' => TokenKind.Invalid,
                _ => TokenKind.End
            };
            if (kind != TokenKind.End)
            {
                _position = kind is TokenKind.And or TokenKind.Or ? logicalEnd : start + 1;
                IgnoreContinuations(start, _position);
                return new(kind, _source.Slice(start, _position - start), start);
            }
        }
        var cursor = new ShellSourceCursor(_source, start, _context);
        var parentheses = 0;
        while (!cursor.IsAtEnd)
        {
            if (!cursor.IsQuoted)
            {
                var ch = cursor.Current;
                // Regex parentheses nest at the shell-word layer. Brackets are interpreted
                // later by the matcher. Outside a regex group, | remains part of its word.
                if (regex && ch == (byte)'(')
                    parentheses++;
                else if (ch == (byte)')' && parentheses > 0)
                    parentheses--;
                else if (parentheses == 0 && Tokenizer.IsWordDelimiter(ch) && (!regex || ch != (byte)'|'))
                    break;
            }
            var quotedLiteral = cursor.IsQuoted && !cursor.IsDoubleQuoted;
            var preceding = cursor.Position;
            if (cursor.TryConsumeQuoteOrEscape())
            {
                if (!quotedLiteral)
                    IgnoreContinuations(preceding, cursor.Position);
                continue;
            }
            switch (cursor.SkipNestedExpansion(BracedParameterRecognition.All))
            {
                case ShellNestedExpansionScan.Complete: continue;
                case ShellNestedExpansionScan.Incomplete or ShellNestedExpansionScan.Invalid:
                    Fail("Invalid expansion in conditional operand.", cursor.Position);
                    return new(TokenKind.Invalid, default, cursor.Position);
            }
            cursor.Advance();
        }
        _position = cursor.Position;
        if (cursor.IsQuoted || parentheses != 0 || (!regex && _position == start))
        {
            Fail("Invalid or unterminated conditional operand.", _position);
            return new(TokenKind.Invalid, default, _position);
        }

        // Regex-word reading can yield an empty operand before a shell metacharacter.
        // The next read uses conditional token rules and handles that character.
        var word = _source.Slice(start, _position - start);
        var boundary = _position < span.Length && span[_position] is (byte)'<' or (byte)'>'
            && IsDescriptorNumber(word.AsSpan());
        return new(boundary ? TokenKind.DescriptorNumber : TokenKind.Word, word, start);

        static bool IsDescriptorNumber(ReadOnlySpan<byte> source)
        {
            // The same adjacent numeric spelling denotes a descriptor in command syntax.
            // Recognize it without allocating a normalized copy for continued digits.
            if (source.IsEmpty)
                return false;
            uint value = 0;
            for (var index = 0; index < source.Length; index++)
            {
                index = ShellSourceCursor.SkipLineContinuations(source, index);
                if (index == source.Length)
                    break;
                var digit = (uint)(source[index] - (byte)'0');
                if (digit > 9 || value > (int.MaxValue - digit) / 10)
                    return false;
                value = value * 10 + digit;
            }
            return true;
        }
    }

    private void Fail(string message) => _error ??= new(message, _position);
    private void Fail(string message, int offset) => _error ??= new(message, offset);

    private void IgnoreContinuations(int start, int end)
    {
        var source = _source.AsSpan(0, end);
        for (var index = start; index + 1 < end; index++)
        {
            var next = ShellSourceCursor.SkipLineContinuations(source, index);
            if (next == index)
                continue;
            Ignore(index, next);
            index = next - 1;
        }
    }

    private void Ignore(int start, int end)
    {
        if (_ignoredRanges == null)
        {
            _context.SyntaxMemory?.Grow(128);
            _ignoredRanges = [];
        }
        // Admit geometric range-array capacity before recording normalization scratch.
        _context.SyntaxMemory?.Grow(32);
        _ignoredRanges.Add((start, end));
    }

    /// <summary>Copies only when this lexical layer removed comments/continuations; nested source stays borrowed.</summary>
    private ShellSource GetSpelling(int start, int end)
    {
        if (_ignoredRanges == null)
            return _source.Slice(start, end - start);
        var length = end - start;
        foreach (var range in _ignoredRanges)
            length -= range.End - range.Start;
        using var output = _context.CreateByteBuilder(length);
        foreach (var range in _ignoredRanges)
        {
            output.Append(_source.AsSpan(start, range.Start - start));
            start = range.End;
        }
        output.Append(_source.AsSpan(start, end - start));
        return ShellSource.FromBytes(output.ToMemory());
    }

    private enum TokenKind { End, Word, And, Or, Open, Close, Order, DescriptorNumber, EndCommand, Newline, Invalid }
    private sealed record Token(TokenKind Kind, ShellSource Source, int Offset);
}

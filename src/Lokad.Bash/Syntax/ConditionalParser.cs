using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Parses conditional operators without interpreting or expanding operand data.</summary>
internal sealed class ConditionalParser
{
    private static readonly Word EmptyRegexOperand = new([]);
    private readonly ShellSource _source;
    private readonly ShellScanContext _context;
    private readonly bool _command;
    private List<(int Start, int End)>? _ignoredRanges;
    private List<SpellingEdit>? _spellingEdits;
    private int _position;
    private Token? _lookahead;
    private ParserFailure? _failure;

    private ConditionalParser(ShellSource source, int position, ShellScanContext context, bool command) =>
        (_source, _position, _context, _command) = (source, position, context, command);

    internal static bool TryParse(ShellSource source,
        [NotNullWhen(true)] out ConditionalExpression? expression,
        out ShellSource spelling,
        [NotNullWhen(false)] out SyntaxError? error)
    {
        var parser = new ConditionalParser(source, 0, new ShellScanContext(), command: false);
        spelling = default;
        if (!parser.TryParse(out expression, out var failure))
        {
            error = failure.Error;
            return false;
        }
        error = null;
        spelling = parser.GetSpelling(0, source.Length, parser._spellingEdits);
        return true;
    }

    /// <summary>The operand grammar owns closing-delimiter recognition as well as comments and regex words.</summary>
    internal static bool TryReadCommand(ShellSource source, int start, ShellScanContext context,
        [NotNullWhen(true)] out ConditionalCommand? command, out int end,
        [NotNullWhen(false)] out ParserFailure? error)
    {
        var parser = new ConditionalParser(source, start, context, command: true);
        command = null;
        end = start;
        if (!parser.TryParse(out var expression, out error))
            return false;
        var closing = parser.Peek();
        var original = parser.GetSpelling(start, closing.Offset, edits: null).TrimBlanks();
        var spelling = parser._spellingEdits == null ? original
            : parser.GetSpelling(start, closing.Offset, parser._spellingEdits).TrimBlanks();
        command = new ConditionalCommand(original, expression, spelling, parser._spellingEdits != null);
        end = parser._position;
        return true;
    }

    private bool TryParse([NotNullWhen(true)] out ConditionalExpression? expression,
        [NotNullWhen(false)] out ParserFailure? error)
    {
        expression = ParseOr(0);
        var ending = Peek();
        if (ending.Kind != (_command ? TokenKind.EndCommand : TokenKind.End))
            Fail(ending.Kind == TokenKind.End ? "Unterminated conditional command."
                : "Unexpected conditional operator or operand.", ending.Offset);
        error = _context.LimitError is { } limit ? new ParserFailure(limit, ParserFailureKind.Limit) : _failure;
        if (error != null || expression == null)
        {
            expression = null;
            error ??= new ParserFailure(new SyntaxError("Conditional operand expected.", _position), ParserFailureKind.Grammar);
            return false;
        }
        return true;
    }

    private ConditionalExpression? ParseOr(int depth)
    {
        var left = ParseAnd(depth);
        while (_failure == null && Consume(TokenKind.Or))
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
        while (_failure == null && Consume(TokenKind.And))
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
            if (cursor.TrySkipReadAheadBodies())
                continue;
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

        var name = ParseWord(new Token(TokenKind.Word, source.Slice(0, opening), offset), nameQuote,
            recordSpelling: nameQuote == ShellQuoteKind.None);
        var selector = ParseWord(new Token(TokenKind.Word, source.Slice(opening + 1, closing - opening - 1), offset + opening + 1),
            ShellQuoteKind.None, recordSpelling: nameQuote == ShellQuoteKind.None);
        return name == null || selector == null ? null : new ConditionalExpression.VariableElement(name, selector);
    }

    private Word? ParseWord(Token token) => ParseWord(token, ShellQuoteKind.None, recordSpelling: true);

    private Word? ParseWord(Token token, ShellQuoteKind quote, bool recordSpelling)
    {
        if (Tokenizer.TryReadExpansionWord(token.Source, ExpansionWordSyntax.Parameter, quote, _context,
                recordSpelling ? new OperandSpelling(this, token.Offset) : default,
                out var word, out var error))
            return word;
        Fail(error.Message, token.Offset + error.Offset);
        return null;
    }

    internal readonly struct OperandSpelling(ConditionalParser? parser, int offset)
    {
        internal void RecordAnsiCLiteral(int start, int end, ShellValue value) =>
            Record(new SpellingEdit(offset + start, offset + end, value, null));

        internal void RecordAnsiCCapture(int start, int end, CommandSubstitutionSyntax capture) =>
            Record(new SpellingEdit(offset + start, offset + end, default, capture));

        private void Record(SpellingEdit edit)
        {
            if (parser == null)
                return;
            if (parser._spellingEdits == null)
            {
                parser._context.SyntaxMemory?.Grow(32);
                parser._spellingEdits = [];
            }
            parser._context.SyntaxMemory?.PrepareListCapacity(parser._spellingEdits, 1);
            parser._spellingEdits.Add(edit);
        }
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
            var afterBodies = _context.HereDocumentReadAhead?.SkipBodies(_source, _position) ?? _position;
            if (afterBodies != _position)
            {
                _position = afterBodies;
                continue;
            }
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
            _context.RecordLimitError(_failure?.Error ?? throw new InvalidOperationException("A refused token requires an error."));
            return new(TokenKind.Invalid, default, start);
        }
        if (_command && ShellSourceCursor.TryMatchLogicalText(_source, _context, start, "]]", out var closing))
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
                (byte)'&' when ShellSourceCursor.TryMatchLogicalText(_source, _context, start, "&&", out logicalEnd) => TokenKind.And,
                (byte)'|' when ShellSourceCursor.TryMatchLogicalText(_source, _context, start, "||", out logicalEnd) => TokenKind.Or,
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
            if (cursor.TrySkipReadAheadBodies())
                continue;
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
                case ShellNestedExpansionScan.Invalid { Failure: { } failure }:
                    _failure ??= failure.HereDocumentEndOfInput is { } missingCloser
                        ? failure with { HereDocumentEndOfInput = missingCloser with { InsideConditional = true } }
                        : failure;
                    return new(TokenKind.Invalid, default, failure.Error.Offset);
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

    private void Fail(string message) => Fail(message, _position);
    private void Fail(string message, int offset) =>
        _failure ??= new ParserFailure(new SyntaxError(message, offset), ParserFailureKind.Grammar);

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

    /// <summary>Normalizes this lexical layer while preserving the original operand bytes on the AST.</summary>
    private ShellSource GetSpelling(int start, int end, List<SpellingEdit>? edits)
    {
        if (_ignoredRanges == null && edits == null)
            return FunctionDefinitionFormatter.RenderReadAheadCaptures(_source.Slice(start, end - start), _context);
        var capacity = end - start;
        if (_ignoredRanges != null)
            foreach (var range in _ignoredRanges)
                capacity -= range.End - range.Start;
        using var output = _context.CreateByteBuilder(edits == null ? capacity : Math.Min(capacity, ByteMemoryBuilder.DefaultCapacity));
        var ignored = 0;
        var edited = 0;
        var ignoreCount = _ignoredRanges?.Count ?? 0;
        var editCount = edits?.Count ?? 0;
        while (ignored < ignoreCount || edited < editCount)
        {
            var nextIgnore = _ignoredRanges != null && ignored < ignoreCount ? _ignoredRanges[ignored] : (Start: int.MaxValue, End: int.MaxValue);
            if (edits != null && edited < editCount && edits[edited].Start <= nextIgnore.Start)
            {
                var edit = edits[edited++];
                AppendRange(start, edit.Start);
                if (!AppendEdit(edit))
                    throw new ByteMemoryBuilder.LimitExceededException();
                start = edit.End;
                continue;
            }
            ignored++;
            if (nextIgnore.Start < start)
                continue;
            AppendRange(start, nextIgnore.Start);
            start = nextIgnore.End;
        }
        AppendRange(start, end);
        return ShellSource.FromBytes(edits == null ? output.ToMemory() : output.ToCompactMemory(_context.SyntaxMemory));

        void AppendRange(int beginning, int finish) => output.Append(FunctionDefinitionFormatter.RenderReadAheadCaptures(
            _source.Slice(beginning, finish - beginning), _context).AsSpan());

        bool AppendEdit(SpellingEdit edit)
        {
            if (edit.Capture is { } capture)
                return FunctionDefinitionFormatter.TryAppendSubstitutionSource(capture, output, _context.SyntaxMemory?.Budget, CancellationToken.None);
            var bytes = edit.Value.Bytes.Span;
            return bytes.SequenceEqual("'"u8) ? output.TryAppend("\\'"u8)
                : ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, bytes);
        }
    }

    private readonly record struct SpellingEdit(int Start, int End, ShellValue Value, CommandSubstitutionSyntax? Capture);

    private enum TokenKind { End, Word, And, Or, Open, Close, Order, DescriptorNumber, EndCommand, Newline, Invalid }
    private sealed record Token(TokenKind Kind, ShellSource Source, int Offset);
}

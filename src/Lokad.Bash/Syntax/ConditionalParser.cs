using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using ShellTokenKind = Lokad.Bash.Syntax.TokenKind;

namespace Lokad.Bash.Syntax;

/// <summary>Parses conditional operators without interpreting or expanding operand data.</summary>
internal sealed class ConditionalParser : IDisposable
{
    private static readonly Word EmptyRegexOperand = new([]);
    private readonly ShellSource _source;
    private readonly ShellScanContext _context;
    private readonly bool _command;
    private readonly Tokenizer? _reader;
    private readonly int _opening;
    private List<(int Start, int End)>? _ignoredRanges;
    private List<SpellingEdit>? _spellingEdits;
    private List<SpacingEdit>? _spacingEdits;
    private MemoryCapacity? _editMemory;
    private int _firstTokenStart;
    private int _previousTokenEnd = -1;
    private int _position;
    private Token? _lookahead;
    private ParserFailure? _failure;
    private int[]? _unclosedGroupLines;
    private bool _wordContinuedAtEndOfInput;
    private bool _localReaderFailure;

    private ConditionalParser(ShellSource source, ShellScanContext context, Range? opening, Tokenizer? reader)
    {
        (_source, _context, _reader) = (source, context, reader);
        _command = opening.HasValue;
        _opening = opening?.Start.Value ?? 0;
        _position = opening?.End.Value ?? 0;
    }

    internal static bool TryParse(ShellSource source,
        [NotNullWhen(true)] out ConditionalExpression? expression,
        out SourceSpelling spelling,
        [NotNullWhen(false)] out SyntaxError? error)
    {
        using var parser = new ConditionalParser(source, new ShellScanContext(), null, null);
        spelling = default;
        if (!parser.TryParse(out expression, out var failure))
        {
            error = failure.Error;
            return false;
        }
        error = null;
        spelling = parser.GetSpelling(parser._firstTokenStart, parser._previousTokenEnd, display: true);
        return true;
    }

    /// <summary>The operand grammar owns closing-delimiter recognition as well as comments and regex words.</summary>
    internal static bool TryReadCommand(ShellSource source, Range opening, Tokenizer reader,
        [NotNullWhen(true)] out ConditionalCommand? command, out int end,
        [NotNullWhen(false)] out ParserFailure? error)
    {
        using var parser = new ConditionalParser(source, reader.ScanContext, opening, reader.TrackUnexpectedTokens ? reader : null);
        var start = opening.End.Value;
        command = null;
        end = start;
        if (!parser.TryParse(out var expression, out error))
            return false;
        var closing = parser.Peek();
        var original = parser.GetSpelling(start, closing.Offset, display: false).Source.TrimBlanks();
        var spelling = parser._spellingEdits == null && parser._spacingEdits == null
            ? new SourceSpelling(original, Captures: null)
            : parser.GetSpelling(parser._firstTokenStart, parser._previousTokenEnd, display: true);
        command = new ConditionalCommand(original, expression, spelling);
        end = parser._position;
        return true;
    }

    private bool TryParse([NotNullWhen(true)] out ConditionalExpression? expression,
        [NotNullWhen(false)] out ParserFailure? error)
    {
        expression = ParseOr(0);
        if (!HasReaderFailure)
        {
            var ending = Peek();
            if (ending.Kind != (_command ? TokenKind.EndCommand : TokenKind.End))
                FailReader(ending.Kind == TokenKind.End ? "Unterminated conditional command."
                        : "Unexpected conditional operator or operand.", ending,
                    ending.Kind == TokenKind.End || _failure?.ConditionalEndOfInput != null
                        || _failure?.UnclosedDelimiter?.Conditional != null
                        ? ConditionalReaderError.MissingCloser : ConditionalReaderError.Expression,
                    _opening);
        }
        error = _context.LimitError is { } limit ? new ParserFailure(limit, ParserFailureKind.Limit) : _failure;
        if (error != null || expression == null)
        {
            expression = null;
            error ??= new ParserFailure(new SyntaxError("Conditional operand expected.", _position), ParserFailureKind.Grammar);
            return false;
        }
        return true;
    }

    private bool HasReaderFailure => _failure?.UnexpectedToken != null
        || _failure?.ConditionalEndOfInput is { Diagnostic.Error: not ConditionalReaderError.None }
        || _failure?.UnclosedDelimiter?.Conditional is { Error: not ConditionalReaderError.None };

    private ConditionalExpression? ParseOr(int depth)
    {
        var left = ParseAnd(depth);
        while (_failure == null && Peek().Kind == TokenKind.Or)
        {
            RecordSpacing(Take());
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
        while (_failure == null && Peek().Kind == TokenKind.And)
        {
            RecordSpacing(Take());
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
        var termStart = Peek().Offset;
        Span<byte> operation = stackalloc byte[3];
        var negated = false;
        while (ReadOperator(Peek(), operation) == 1 && operation[0] == (byte)'!')
        {
            Take();
            negated = !negated;
            SkipNewlines();
        }
        ConditionalExpression? expression;
        if (Peek().Kind == TokenKind.Open)
        {
            var open = Take();
            RecordSpacing(open, termStart, negated, implicitPredicate: false);
            if (depth >= ShellSourceCursor.MaximumNestedExpansionDepth)
            {
                Fail("Conditional nesting exceeds the parsing limit.");
                return null;
            }
            expression = ParseOr(depth + 1);
            if (HasReaderFailure)
            {
                if (_localReaderFailure)
                    RecordUnclosedGroup(depth, GroupReaderPosition(open));
                return null;
            }
            if (Peek().Kind == TokenKind.Close)
                RecordSpacing(Take());
            else
                FailReader("Closing conditional parenthesis expected.", Peek(), ConditionalReaderError.GroupClose, GroupReaderPosition(open));
        }
        else
        {
            var token = Take();
            if (token.Kind != TokenKind.Word)
            {
                FailReader("Conditional operand expected.", token, ConditionalReaderError.Term, token.Offset);
                return null;
            }
            if (ShellPredicates.TryGetUnaryPredicate(operation[..ReadOperator(token, operation)], out var unary))
            {
                RecordSpacing(token, termStart, negated, implicitPredicate: false);
                if (unary.Operator == ShellPredicates.UnaryPredicateKind.VariableIsSet
                    && ParseVariableElement(Peek()) is { } element)
                {
                    RecordSpacing(Take());
                    expression = element;
                }
                else
                {
                    var operand = ReadOperand(false, ConditionalReaderError.UnaryOperand);
                    expression = operand == null ? null : new ConditionalExpression.Unary(unary, operand);
                }
            }
            else
            {
                var left = ParseWord(token);
                if (ShellPredicates.TryGetBinaryPredicate(operation[..ReadOperator(Peek(), operation)], out var binary))
                {
                    RecordSpacing(token, termStart, negated, implicitPredicate: false);
                    RecordSpacing(Take());
                    var right = ReadOperand(binary.Operator == ShellPredicates.BinaryPredicateKind.RegexMatch,
                        ConditionalReaderError.BinaryOperand);
                    expression = left == null || right == null ? null : new ConditionalExpression.Binary(binary, left, right);
                }
                else
                {
                    var next = Peek();
                    if (next.Kind == TokenKind.Newline || _reader != null
                        && next.Kind is not (TokenKind.EndCommand or TokenKind.And or TokenKind.Or or TokenKind.Close))
                        FailReader("Conditional binary operator expected.", next, ConditionalReaderError.BinaryOperator, next.Offset);
                    else
                        RecordSpacing(token, termStart, negated, implicitPredicate: true);
                    expression = left == null ? null : new ConditionalExpression.NonEmpty(left);
                }
            }
        }
        // Looking beyond a reached argument failure can collect bodies from an unread line.
        if (!HasReaderFailure)
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

    private Word? ReadOperand(bool regex, ConditionalReaderError error)
    {
        // A regex operand has its own shell lexical context: | and balanced parentheses
        // belong to the regex word, while an unmatched ) closes a conditional group.
        var token = _lookahead == null ? Read(regex) : Take();
        if (token.Kind != TokenKind.Word)
        {
            FailReader("Conditional operand expected.", token, error, token.Offset);
            return null;
        }
        RecordSpacing(token);
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
        if (cursor.IsAtEnd || opening == 0)
            return null;

        var quoted = nameQuote == ShellQuoteKind.Double;
        var selectorSource = source;
        var selectorOpening = opening;
        if (quoted && source.AsSpan()[opening..].IndexOf("\\\""u8) >= 0)
        {
            selectorSource = FunctionDefinitionFormatter.RenderReadAheadCaptures(source.Slice(opening), _context);
            // An enclosing double quote can supply escaped quotes to the selector grammar.
            _ = ArithmeticExpansionScanner.TryPrepareDoubleQuotedBracketOperand(_context, ref selectorSource);
            selectorOpening = 0;
        }
        if (!ArraySubscriptScanner.TryScan(_context, selectorSource, selectorOpening, recognizeAnsiC: !quoted, out var closing)
            || closing != selectorSource.Length - 1)
            return null;

        var name = ParseWord(new Token(TokenKind.Word, source.Slice(0, opening), offset), nameQuote,
            ExpansionWordSyntax.Parameter, recordSpelling: !quoted);
        var selector = ParseWord(new Token(TokenKind.Word, selectorSource.Slice(selectorOpening + 1, closing - selectorOpening - 1),
                offset + opening + 1),
            ShellQuoteKind.None, quoted ? ExpansionWordSyntax.ExpandedSelector : ExpansionWordSyntax.Parameter, recordSpelling: !quoted);
        return name == null || selector == null ? null : new ConditionalExpression.VariableElement(name, selector);
    }

    private Word? ParseWord(Token token) => ParseWord(token, ShellQuoteKind.None, ExpansionWordSyntax.Parameter, recordSpelling: true);

    private Word? ParseWord(Token token, ShellQuoteKind quote, ExpansionWordSyntax syntax, bool recordSpelling)
    {
        if (Tokenizer.TryReadExpansionWord(token.Source, syntax, quote, _context,
                recordSpelling ? new OperandSpelling(this, token.Offset) : default,
                out var word, out var error))
            return word;
        Fail(error.Message, token.Offset + error.Offset);
        return null;
    }

    internal readonly struct OperandSpelling(ConditionalParser? parser, int offset)
    {
        internal bool IsActive => parser != null;

        internal void RecordAnsiCLiteral(int start, int end, ShellValue value) =>
            Record(new SpellingEdit(offset + start, offset + end, new SourceSpelling(ShellSource.FromBytes(value.Bytes), null),
                null, SpellingEditKind.Literal));

        internal void RecordCapture(int start, int end, CommandSubstitutionSyntax capture) =>
            Record(new SpellingEdit(offset + start, offset + end, default, capture, SpellingEditKind.Capture));

        internal void RecordLocalePrefix(int start, int end) =>
            Record(new SpellingEdit(offset + start, offset + end, default, null, SpellingEditKind.Omit));

        internal void RecordParameter(int start, int end, SourceSpelling payload) =>
            Record(new SpellingEdit(offset + start, offset + end, payload, null, SpellingEditKind.Parameter));

        private void Record(SpellingEdit edit)
        {
            if (parser == null)
                return;
            parser.AddEdit(ref parser._spellingEdits, edit);
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
            var shellOperator = Tokenizer.ReadShellOperator(_source, _context, start);
            if (shellOperator.Kind != ShellTokenKind.None)
            {
                var operatorKind = shellOperator switch
                {
                    { ListOperator: ListOperator.And } => TokenKind.And,
                    { ListOperator: ListOperator.Or } => TokenKind.Or,
                    { RedirectionKind: RedirectionKind.Input or RedirectionKind.Output } => TokenKind.Order,
                    _ when Tokenizer.PeeksPastShellOperator(shellOperator) => TokenKind.InvalidWithLookahead,
                    _ => TokenKind.Invalid
                };
                _position = shellOperator.Source.End.Value;
                IgnoreContinuations(start, _position);
                return new(operatorKind, _source.Slice(start, _position - start), start);
            }
            var punctuation = span[start] switch
            {
                (byte)'(' => TokenKind.Open,
                (byte)')' => TokenKind.Close,
                (byte)'&' => TokenKind.InvalidWithLookahead,
                _ => TokenKind.End
            };
            if (punctuation != TokenKind.End)
            {
                _position++;
                return new(punctuation, _source.Slice(start, 1), start);
            }
        }
        var cursor = new ShellSourceCursor(_source, start, _context);
        var parentheses = 0;
        var quoteStart = 0;
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
            var wasQuoted = cursor.IsQuoted;
            var quotedLiteral = wasQuoted && !cursor.IsDoubleQuoted;
            var preceding = cursor.Position;
            if (cursor.TryConsumeQuoteOrEscape())
            {
                if (!quotedLiteral)
                    IgnoreContinuations(preceding, cursor.Position);
                if (!wasQuoted && cursor.IsQuoted)
                    quoteStart = cursor.Position - 1;
                _wordContinuedAtEndOfInput |= !wasQuoted && cursor.IsAtEnd
                    && span[preceding..].SequenceEqual("\\\n"u8);
                continue;
            }
            var nested = cursor.SkipNestedExpansion(BracedParameterRecognition.All);
            if (_reader != null && nested is ShellNestedExpansionScan.Incomplete { Kind: ShellNestedExpansionKind.Command }
                && span[preceding] == (byte)'`')
            {
                nested = new ShellNestedExpansionScan.Invalid(ShellNestedExpansionKind.Command,
                    new ParserFailure(new SyntaxError("Invalid expansion in conditional operand.", preceding), ParserFailureKind.Delimiter)
                    {
                        UnclosedDelimiter = new UnclosedDelimiter('`', preceding)
                    });
            }
            switch (nested)
            {
                case ShellNestedExpansionScan.Complete: continue;
                case ShellNestedExpansionScan.Invalid { Failure: { } failure }:
                    if (_failure == null)
                    {
                        if (failure.UnclosedDelimiter is { Conditional: null } delimiter)
                        {
                            failure = failure with { UnclosedDelimiter = delimiter with { Conditional = default(ConditionalReadDiagnostic) } };
                            _localReaderFailure = _reader != null;
                        }
                        _failure = failure;
                    }
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
            if (_failure == null && _reader != null && cursor.IsQuoted && cursor.IsAtEnd)
            {
                _localReaderFailure = true;
                _failure = new ParserFailure(new SyntaxError("Invalid or unterminated conditional operand.", _position),
                    ParserFailureKind.Grammar)
                {
                    ConditionalEndOfInput = new ConditionalEndOfInput(GetEndLine(), cursor.IsDoubleQuoted,
                        _reader.GetLine(quoteStart), default)
                };
            }
            else
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

    // The reader looks past an opening parenthesis through any continuations.
    private int GroupReaderPosition(Token opening) =>
        ShellSourceCursor.SkipLineContinuations(_source.AsSpan(), opening.Offset + opening.Source.Length);

    private int GetEndLine() => _reader == null ? 0
        : _reader.GetLine(_source.Length) + (_wordContinuedAtEndOfInput ? 1 : _reader.EndOfInputLineAdvance);

    private void RecordUnclosedGroup(int depth, int offset)
    {
        if (_reader == null || _failure is not { } failure)
            return;
        if (_unclosedGroupLines == null)
        {
            _context.SyntaxMemory?.Grow(24L + (depth + 1) * sizeof(int));
            _unclosedGroupLines = new int[depth + 1];
            if (failure.UnexpectedToken is { Conditional: { } conditional } token)
                _failure = failure with
                {
                    UnexpectedToken = token with { Conditional = conditional with { UnclosedGroupLines = _unclosedGroupLines } }
                };
            else if (failure.ConditionalEndOfInput is { } eof)
                _failure = failure with
                {
                    ConditionalEndOfInput = eof with { Diagnostic = eof.Diagnostic with { UnclosedGroupLines = _unclosedGroupLines } }
                };
            else if (failure.UnclosedDelimiter is { Conditional: { } diagnostic } delimiter)
                _failure = failure with
                {
                    UnclosedDelimiter = delimiter with { Conditional = diagnostic with { UnclosedGroupLines = _unclosedGroupLines } }
                };
        }
        _unclosedGroupLines[_unclosedGroupLines.Length - depth - 1] = _reader.GetLine(offset);
    }

    private void FailReader(string message, Token token, ConditionalReaderError error, int reasonPosition)
    {
        if (_localReaderFailure
            && _failure is { UnclosedDelimiter: { Conditional.Error: ConditionalReaderError.None } delimiter } failure
            && _reader != null)
        {
            var readLine = _reader.GetLine(delimiter.ReadPosition) + delimiter.LineAdvance;
            var delimiterReasonLine = delimiter.Delimiter == ')' ? readLine : GetEndLine();
            if (error is ConditionalReaderError.MissingCloser or ConditionalReaderError.GroupClose)
                delimiterReasonLine = _reader.GetLine(reasonPosition);
            _failure = failure with
            {
                UnclosedDelimiter = delimiter with { Conditional = new ConditionalReadDiagnostic(error, delimiterReasonLine) { ReadLine = readLine } }
            };
            return;
        }
        if (_localReaderFailure && _failure is { ConditionalEndOfInput: { } quote } && _reader != null)
        {
            var line = error is ConditionalReaderError.MissingCloser or ConditionalReaderError.GroupClose
                ? _reader.GetLine(reasonPosition) : quote.EndLine;
            _failure = _failure with { ConditionalEndOfInput = quote with { Diagnostic = new(error, line) } };
            return;
        }
        if (_failure != null)
            return;
        if (_reader == null)
        {
            Fail(message, token.Offset);
            return;
        }

        _localReaderFailure = true;
        if (token.Kind == TokenKind.End && (error is ConditionalReaderError.MissingCloser
                or ConditionalReaderError.GroupClose or ConditionalReaderError.Term || _source.AsSpan()[^1] == (byte)'\n'))
        {
            var endLine = GetEndLine();
            var line = error is ConditionalReaderError.MissingCloser or ConditionalReaderError.GroupClose
                ? _reader.GetLine(reasonPosition) : endLine;
            _failure = new ParserFailure(new SyntaxError(message, token.Offset), ParserFailureKind.Grammar)
            {
                ConditionalEndOfInput = new ConditionalEndOfInput(endLine, null, 0, new(error, line))
            };
            return;
        }

        var tokenEnd = token.Kind is TokenKind.Newline or TokenKind.End ? token.Offset : token.Offset + token.Source.Length;
        var peeksPastToken = token.Kind is TokenKind.InvalidWithLookahead or TokenKind.Open or TokenKind.Order
            || token.Kind == TokenKind.Close && !_reader.IsCommandSubstitution;
        var readPosition = peeksPastToken ? ShellSourceCursor.SkipLineContinuations(_source.AsSpan(), tokenEnd) : tokenEnd;
        var physicalLine = _source.GetPhysicalLine(readPosition, out var lineStart);
        var argument = token.Kind switch
        {
            TokenKind.Newline or TokenKind.End => UnexpectedSyntax.NewlineToken,
            TokenKind.Word or TokenKind.DescriptorNumber => default,
            // An empty term followed by the closing delimiter has only the near-token diagnostic.
            TokenKind.EndCommand when error == ConditionalReaderError.Term => default,
            _ => token.Source
        };
        if (error == ConditionalReaderError.Term && argument.IsEmpty)
            error = ConditionalReaderError.None;
        var reasonLine = _reader.GetLine(error is ConditionalReaderError.GroupClose or ConditionalReaderError.Expression
            ? reasonPosition : readPosition);
        _failure = new ParserFailure(new SyntaxError(message, token.Offset), ParserFailureKind.Grammar)
        {
            UnexpectedToken = new UnexpectedSyntax(FindNearToken(), physicalLine, readPosition)
            {
                Argument = argument,
                Conditional = new(error, reasonLine) { ReadLine = _reader.GetLine(readPosition) }
            }
        };

        ShellSource FindNearToken()
        {
            // Bash uses the physical reader line for this second diagnostic, including skipped comments.
            // The byte at the read position can be the separator immediately after the rejected argument.
            if (readPosition > tokenEnd && readPosition == _source.Length)
                return default;
            var nearEnd = Math.Min(physicalLine.Length, readPosition - lineStart + 1);
            var preceding = physicalLine.AsSpan(0, nearEnd).TrimEnd(" \t"u8);
            if (preceding.IsEmpty)
                return lineStart < _source.Length ? _source.Slice(lineStart, 1) : token.Source;
            var start = Math.Max(0, preceding.LastIndexOfAny(" \t;|&"u8));
            if (preceding[start] is (byte)' ' or (byte)'\t')
                start++;
            return physicalLine.Slice(start, preceding.Length - start);
        }
    }

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

    private void Ignore(int start, int end) => AddEdit(ref _ignoredRanges, (start, end));

    private void RecordSpacing(Token token) => RecordSpacing(token, token.Offset, negated: false, implicitPredicate: false);

    private void RecordSpacing(Token token, int termStart, bool negated, bool implicitPredicate)
    {
        if (_previousTokenEnd < 0)
            _firstTokenStart = termStart;
        var start = _previousTokenEnd < 0 ? termStart : _previousTokenEnd;
        var prefix = (_previousTokenEnd >= 0 ? TokenPrefix.Space : TokenPrefix.None)
            | (negated ? TokenPrefix.Negation : TokenPrefix.None)
            | (implicitPredicate ? TokenPrefix.NonEmpty : TokenPrefix.None);
        if (!_source.AsSpan(start, token.Offset - start).SequenceEqual(PrefixBytes(prefix)))
            AddEdit(ref _spacingEdits, new SpacingEdit(start, token.Offset, prefix));
        _previousTokenEnd = token.Offset + token.Source.Length;
    }

    private static ReadOnlySpan<byte> PrefixBytes(TokenPrefix prefix) => prefix switch
    {
        TokenPrefix.None => [],
        TokenPrefix.Space => " "u8,
        TokenPrefix.Negation => "! "u8,
        TokenPrefix.Space | TokenPrefix.Negation => " ! "u8,
        TokenPrefix.NonEmpty => "-n "u8,
        TokenPrefix.Space | TokenPrefix.NonEmpty => " -n "u8,
        TokenPrefix.Negation | TokenPrefix.NonEmpty => "! -n "u8,
        _ => " ! -n "u8
    };

    private void AddEdit<T>(ref List<T>? edits, T edit)
    {
        if (_context.SyntaxMemory is { } memory)
            _editMemory ??= new MemoryCapacity(memory.Budget, 0);
        if (edits == null)
        {
            _editMemory?.Grow(32);
            edits = [];
        }
        _editMemory?.PrepareListCapacity(edits, 1);
        edits.Add(edit);
    }

    /// <summary>Normalizes this lexical layer while preserving the original operand bytes on the AST.</summary>
    private SourceSpelling GetSpelling(int start, int end, bool display)
    {
        var edits = display ? _spellingEdits : null;
        var spacing = display ? _spacingEdits : null;
        if (_ignoredRanges == null && edits == null && spacing == null)
            return new SourceSpelling(FunctionDefinitionFormatter.RenderReadAheadCaptures(_source.Slice(start, end - start), _context),
                Captures: null);
        var capacity = end - start;
        if (!display && _ignoredRanges != null)
            foreach (var range in _ignoredRanges)
                capacity -= range.End - range.Start;
        using var output = _context.CreateByteBuilder(display ? Math.Min(capacity, ByteMemoryBuilder.DefaultCapacity) : capacity);
        var ignored = 0;
        var edited = 0;
        var spaced = 0;
        var ignoreCount = _ignoredRanges?.Count ?? 0;
        var editCount = edits?.Count ?? 0;
        var spacingCount = spacing?.Count ?? 0;
        List<SpellingCapture>? captures = null;
        while (ignored < ignoreCount || edited < editCount || spaced < spacingCount)
        {
            var nextIgnore = _ignoredRanges != null && ignored < ignoreCount ? _ignoredRanges[ignored] : (Start: int.MaxValue, End: int.MaxValue);
            var nextSpelling = edits != null && edited < editCount ? edits[edited].Start : int.MaxValue;
            if (spacing != null && spaced < spacingCount && spacing[spaced].Start <= Math.Min(nextIgnore.Start, nextSpelling))
            {
                var edit = spacing[spaced++];
                AppendRange(start, edit.Start);
                output.Append(PrefixBytes(edit.Prefix));
                start = edit.End;
                continue;
            }
            if (edits != null && edited < editCount && nextSpelling <= nextIgnore.Start)
            {
                var edit = edits[edited++];
                AppendRange(start, edit.Start);
                if (!AppendEdit(edit))
                    throw new ByteMemoryBuilder.LimitExceededException();
                start = edit.End;
                continue;
            }
            ignored++;
            if (nextIgnore.Start >= end)
                break;
            if (nextIgnore.Start < start)
                continue;
            AppendRange(start, nextIgnore.Start);
            start = nextIgnore.End;
        }
        AppendRange(start, end);
        return new SourceSpelling(
            ShellSource.FromBytes(display ? output.ToCompactMemory(_context.SyntaxMemory) : output.ToMemory()), captures);

        void AppendRange(int beginning, int finish)
        {
            if (beginning < finish)
                output.Append(FunctionDefinitionFormatter.RenderReadAheadCaptures(
                    _source.Slice(beginning, finish - beginning), _context).AsSpan());
        }

        bool AppendEdit(SpellingEdit edit)
        {
            if (edit.Capture is { } capture)
            {
                if (captures == null)
                {
                    _context.SyntaxMemory?.Grow(32);
                    captures = [];
                }
                _context.SyntaxMemory?.PrepareListCapacity(captures, 1);
                captures.Add(new SpellingCapture(output.Length, 0, capture));
                return true;
            }
            var bytes = edit.Spelling.Source.AsSpan();
            if (edit.Kind == SpellingEditKind.Parameter)
            {
                if (!output.TryAppend("${"u8))
                    return false;
                edit.Spelling.CopyCaptures(output.Length, ref captures, _context.SyntaxMemory);
                return output.TryAppend(bytes) && output.TryAppend((byte)'}');
            }
            if (edit.Kind == SpellingEditKind.Omit)
                return true;
            return bytes.SequenceEqual("'"u8) ? output.TryAppend("\\'"u8)
                : ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, bytes);
        }
    }

    public void Dispose() => _editMemory?.Dispose();

    [Flags]
    private enum TokenPrefix : byte { None, Space = 1, Negation = 2, NonEmpty = 4 }

    private readonly record struct SpacingEdit(int Start, int End, TokenPrefix Prefix);

    private enum SpellingEditKind { Literal, Capture, Omit, Parameter }

    private readonly record struct SpellingEdit(int Start, int End, SourceSpelling Spelling, CommandSubstitutionSyntax? Capture,
        SpellingEditKind Kind);

    private enum TokenKind { End, Word, And, Or, Open, Close, Order, DescriptorNumber, EndCommand, Newline, Invalid, InvalidWithLookahead }
    private sealed record Token(TokenKind Kind, ShellSource Source, int Offset);
}

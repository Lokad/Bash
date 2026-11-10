using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Breaks shell source bytes into tokens used by command parsing.
/// </summary>
internal sealed class Tokenizer
{
    /// <summary> Stores the script input to tokenize. </summary>
    private readonly ShellSource _input;

    /// <summary>Shares recursion bounds and lookahead parses across recursive tokenization.</summary>
    private readonly ShellScanContext _scanContext;

    private ArraySubscriptScanner.Lookahead? _arraySubscripts;

    internal ShellScanContext ScanContext => _scanContext;

    private SyntaxError? _error;

    /// <summary>The first failure; later reads stop without replacing its diagnostic.</summary>
    internal SyntaxError? Error => _error ?? _scanContext.LimitError;

    internal void RecordError(string message, int offset)
    {
        _error ??= _scanContext.LimitError ?? new SyntaxError(message, offset);
        if (_scanContext.ParsingLimitExceeded)
            _scanContext.RecordLimitError(_error);
    }

    internal void RecordError(SyntaxError error) => RecordError(error.Message, error.Offset);

    private bool TryReserveSyntax()
    {
        if (!_scanContext.TryReserveSyntax())
        {
            RecordError("Script parsing limit exceeded (too many tokens or word parts).", Position);
            return false;
        }
        return true;
    }

    /// <summary> Holds a stack of pushed-back tokens to be read before consuming new input. </summary>
    private readonly Stack<Token> _pushback = new();

    /// <summary>One lexical queue spans pipelines, lists and compound-command redirections.</summary>
    internal HereDocumentReader? HereDocuments { get; set; }

    /// <summary>Creates a tokenizer positioned at the beginning of <paramref name="input"/>.</summary>
    public Tokenizer(ShellSource input)
        : this(input, 0, new ShellScanContext())
    {
    }

    /// <summary>Creates a tokenizer at an exact source position.</summary>
    internal Tokenizer(ShellSource input, int position, ShellScanContext scanContext)
    {
        if (position < 0 || position > input.Length)
            throw new ArgumentOutOfRangeException(nameof(position));

        _input = input;
        _scanContext = scanContext;
        Position = position;
    }

    /// <summary>
    /// Gets the physical input position in bytes, which may run ahead of pushed-back
    /// tokens. Use each token's source range when locating a grammar boundary.
    /// </summary>
    public int Position { get; private set; }

    /// <summary>Gets whether parser lookahead has left a closing parenthesis unconsumed.</summary>
    internal bool HasPendingClosingParenthesis =>
        _pushback.TryPeek(out var token) && token.Kind == TokenKind.CloseParen;

    /// <summary>Tests token adjacency after removing only backslash-LF pairs.</summary>
    internal bool AreAdjacent(Token left, Token right) =>
        ShellSourceCursor.SkipLineContinuations(_input.AsSpan(), left.Source.End.Value) == right.Source.Start.Value;

    /// <summary>Tests the logical boundary immediately after a consumed token.</summary>
    internal bool IsAtWordBoundary() =>
        _pushback.Count == 0 && IsWordBoundary(_input.AsSpan(), Position);

    /// <summary>Checks function punctuation without advancing past a possible array-assignment word.</summary>
    internal bool IsFollowedByFunctionParentheses(WordToken token)
    {
        var source = _input.AsSpan();
        var opening = ShellSourceCursor.SkipTokenSpacing(source, token.Token.Source.End.Value);
        if (opening >= source.Length || source[opening] != (byte)'(')
            return false;
        var closing = ShellSourceCursor.SkipTokenSpacing(source, opening + 1);
        return closing < source.Length && source[closing] == (byte)')';
    }

    /// <summary>Retains the name token without expansion; backtick contents remain literal name characters.</summary>
    internal bool TryReadFunctionName(WordToken token, [NotNullWhen(true)] out FunctionName? name)
    {
        name = null;
        var text = ShellSourceCursor.RemoveLineContinuations(_input[token.Token.Source]);
        var syntax = FunctionNameSyntax.Plain;
        var cursor = new ShellSourceCursor(text, 0);
        while (!cursor.IsAtEnd)
        {
            if (cursor.Current is (byte)'$' or (byte)'\'' or (byte)'"' or (byte)'\\')
            {
                syntax |= FunctionNameSyntax.Invalid;
                break;
            }
            if (cursor.SkipNestedExpansion(BracedParameterRecognition.All) is not ShellNestedExpansionScan.Complete)
                cursor.Advance();
        }

        if (text.ToValue().TryGetUtf8Text(out var keyword) && Parser.IsSupportedKeyword(keyword.AsSpan())
            || AssignmentWord.TrySplitOperand(text, out var target, out _, out _) && VariableReference.TryParse(target, out _))
            syntax |= FunctionNameSyntax.KeywordOnly;
        var canonical = CanonicalName();
        if (Error != null)
            return false;
        name = new FunctionName(canonical, syntax);
        return true;

        ShellValue CanonicalName()
        {
            if (text.AsSpan().IndexOf("$'"u8) < 0)
                return text.ToValue();

            var output = BoundedByteMemoryBuilder.CreateUnaccounted(0, ByteMemoryBuilder.MaxMemoryBuilderBytes - 1);
            var nameCursor = new ShellSourceCursor(text, 0);
            var literalIndex = 0;
            while (!nameCursor.IsAtEnd)
            {
                var start = nameCursor.Position;
                bool appended;
                if (!nameCursor.IsQuoted && (nameCursor.Current == (byte)'\''
                    || text.AsSpan()[start..].StartsWith("$'"u8)))
                {
                    if (!nameCursor.TryReadQuotedLiteralSource(out _, out var kind))
                    {
                        RecordError("Unterminated single-quoted string.", nameCursor.Position);
                        return default;
                    }
                    while (literalIndex < token.Word.Parts.Count && token.Word.Parts[literalIndex].Kind != WordPartKind.SingleQuoted)
                        literalIndex++;
                    var literal = token.Word.Parts[literalIndex++].GetUnexpandedValue();
                    // Names keep lexical quoting; arithmetic canonicalization treats ordinary apostrophes as data.
                    appended = kind == ShellQuoteKind.AnsiC
                        ? ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, literal.Bytes.Span)
                        : output.TryAppend(text.AsSpan(start, nameCursor.Position - start));
                }
                else
                {
                    if (!nameCursor.TryConsumeQuoteOrEscape()
                        && nameCursor.SkipNestedExpansion(BracedParameterRecognition.All) is not ShellNestedExpansionScan.Complete)
                        nameCursor.Advance();
                    appended = output.TryAppend(text.AsSpan(start, nameCursor.Position - start));
                }
                if (!appended)
                {
                    RecordError("Function name exceeds the output limit.", token.Token.Source.Start.Value);
                    return default;
                }
            }
            return ShellValue.FromBytes(output.ToMemory());
        }
    }

    /// <summary>
    /// Reads an array assignment in assignment position, where subscript whitespace
    /// belongs to the target rather than separating ordinary command arguments.
    /// </summary>
    internal bool TryReadArrayAssignment(WordToken token, out AssignmentWord assignment)
    {
        assignment = default;
        if (token.Word.Parts is not [SourceWordPart { Kind: WordPartKind.Unquoted } first, ..]
            || !first.Source.AsSpan().Contains((byte)'['))
            return false;
        var source = _input.AsSpan();
        var position = ShellSourceCursor.SkipLineContinuations(source, token.Token.Source.Start.Value);
        if (position >= source.Length || !VariableNames.IsNameStart((char)source[position]))
            return false;

        var name = ByteMemoryBuilder.CreateUnaccounted(0);
        while (position < source.Length && VariableNames.IsNamePart((char)source[position]))
        {
            name.Append(source[position]);
            position = ShellSourceCursor.SkipLineContinuations(source, position + 1);
        }

        if (position >= source.Length || source[position] != (byte)'[')
            return false;
        _arraySubscripts ??= new ArraySubscriptScanner.Lookahead(_input, _scanContext);
        if (_arraySubscripts.Scan(position, out var closing) != ShellScanStatus.Complete
            || !TryReadSubscriptAssignment(position, closing, out var element))
            return false;

        name.Append(source.Slice(position, closing - position + 1));
        assignment = new AssignmentWord(ShellSource.FromBytes(name.ToMemory()), element.Value, element.Operator);
        return true;
    }

    /// <summary>Reads compound items, retaining whitespace inside an unquoted bracket prefix.</summary>
    internal CompoundArrayItem ReadCompoundArrayItem(WordToken token) =>
        TryReadCompoundArrayItem(token, out var item, out var error) ? item : throw error.ToException();

    internal bool TryReadCompoundArrayItem(WordToken token,
        [NotNullWhen(true)] out CompoundArrayItem? item, [NotNullWhen(false)] out SyntaxError? error)
    {
        var parsed = ReadItem(token);
        error = Error;
        item = error == null ? parsed : null;
        return error == null;

        CompoundArrayItem ReadItem(WordToken token)
        {
            var start = token.Token.Source.Start.Value;
            if (_input[start] != (byte)'[')
                return new CompoundArrayItem(token.Word, null);

            _arraySubscripts ??= new ArraySubscriptScanner.Lookahead(_input, _scanContext);
            if (_arraySubscripts.Scan(start, out var closing) != ShellScanStatus.Complete)
            {
                RecordError("Invalid bracketed array item.", start);
                return new CompoundArrayItem(new Word([]), null);
            }

            if (!TryReadSubscriptAssignment(start, closing, out var element))
            {
                if (token.Token.Source.End.Value > closing)
                    return new CompoundArrayItem(token.Word, null);

                // Brackets group source, not expansion results: normal field splitting still applies.
                Position = start;
                return new CompoundArrayItem(ReadWord(_input.AsSpan(), closing + 1,
                    ExpansionWordSyntax.Parameter, ShellQuoteKind.None), null);
            }

            var word = token.Word;
            if (token.Token.Source.End.Value != Position)
            {
                var child = new Tokenizer(_input.Slice(start, Position - start), 0, _scanContext);
                word = child.ReadWord(_input.AsSpan(start, Position - start), Position - start,
                    ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
                if (child.Error is { } error)
                    RecordError(error);
            }
            return new CompoundArrayItem(word, element);
        }
    }

    /// <summary>Reads = or += after a matched bracket pair; leaves Position unchanged if absent.</summary>
    private bool TryReadSubscriptAssignment(
        int opening,
        int closing,
        [NotNullWhen(true)] out CompoundArrayElement? element)
    {
        element = null;
        var source = _input.AsSpan();
        var equals = ShellSourceCursor.SkipLineContinuations(source, closing + 1);
        var operation = AssignmentOperator.Replace;
        if (equals < source.Length && source[equals] == (byte)'+')
        {
            operation = AssignmentOperator.Append;
            equals = ShellSourceCursor.SkipLineContinuations(source, equals + 1);
        }
        if (equals >= source.Length || source[equals] != (byte)'=')
            return false;

        Position = ShellSourceCursor.SkipLineContinuations(source, equals + 1);
        var value = Position >= source.Length || IsDelimiter(source[Position])
            ? new Word([])
            : ReadWord(source);
        element = new CompoundArrayElement(_input.Slice(opening + 1, closing - opening - 1), value, operation);
        return true;
    }

    /// <summary>
    /// Returns the next token without consuming it.
    /// </summary>
    public Token PeekToken() => TryPeekToken(out var token, out var error) ? token : throw error.ToException();

    internal bool TryPeekToken(out Token token, [NotNullWhen(false)] out SyntaxError? error)
    {
        token = PeekTokenCore();
        error = Error;
        return error == null;
    }

    /// <summary>Returns EOF after a recorded failure; the grammar checks Error before publishing syntax.</summary>
    internal Token PeekTokenCore()
    {
        if (Error != null)
            return Token.EndOfInput();
        if (_pushback.Count > 0)
        {
            return _pushback.Peek();
        }

        var token = NextTokenCore();
        _pushback.Push(token);
        return token;
    }

    /// <summary>
    /// Returns the next token from the input.
    /// </summary>
    public Token NextToken() => TryNextToken(out var token, out var error) ? token : throw error.ToException();

    internal bool TryNextToken(out Token token, [NotNullWhen(false)] out SyntaxError? error)
    {
        token = NextTokenCore();
        error = Error;
        return error == null;
    }

    /// <summary>Reads a token without throwing for source errors; EOF also terminates failed reads.</summary>
    internal Token NextTokenCore()
    {
        if (Error != null)
            return Token.EndOfInput();
        if (_pushback.Count > 0)
        {
            return _pushback.Pop();
        }

        var span = _input.AsSpan();
        var start = Position;
        while (true)
        {
            Position = ShellSourceCursor.SkipTokenSpacing(span, Position);
            start = Position;
            if (Position >= span.Length)
            {
                if (HereDocuments is { HasPending: true })
                    RecordError("Unterminated here-document.", Position);
                return Located(Token.EndOfInput());
            }

            var current = span[Position];

            if (current == '#')
            {
                SkipComment(span);
                continue;
            }

            if (!TryReserveSyntax())
                return Located(Token.EndOfInput());
            if (current == ';' && TryConsumeLogicalText(span, ";;"))
            {
                return Located(Token.CaseTerminator());
            }

            if (current == (byte)'\n')
            {
                Position++;
                if (HereDocuments is { HasPending: true })
                {
                    if (!HereDocuments.TryReadBodies(this))
                        return Located(Token.EndOfInput());
                    // Keep bodies in the preceding source segment for command-chain display.
                    return Token.Newline() with { Source = Position..Position };
                }
                return Located(Token.Newline());
            }

            if (current == (byte)';')
            {
                Position++;
                return Located(Token.Semicolon());
            }

            if (current == '(')
            {
                var status = ArithmeticExpansionScanner.ScanCommand(
                    _scanContext, _input, Position, out var arithmeticEnd, out var expression);
                if (status == ShellScanStatus.Complete)
                {
                    Position = arithmeticEnd + 1;
                    return Located(Token.ArithmeticCommand(expression));
                }

                if (status != ShellScanStatus.NotFound)
                {
                    RecordError(status == ShellScanStatus.Incomplete
                        ? "Unterminated arithmetic command."
                        : "Invalid arithmetic command.", Position);
                    return Located(Token.EndOfInput());
                }

                Position++;
                return Located(Token.OpenParen());
            }

            if (current == (byte)')')
            {
                Position++;
                return Located(Token.CloseParen());
            }

            if (current == (byte)'{' && IsBraceStandalone(span, Position))
            {
                Position++;
                return Located(Token.OpenBrace());
            }

            if (current == (byte)'}' && IsBraceStandalone(span, Position))
            {
                Position++;
                return Located(Token.CloseBrace());
            }

            var listOperator = TryReadListOperator(span);
            if (listOperator.Kind != TokenKind.None)
            {
                return Located(listOperator);
            }

            var pipelineOperator = TryReadPipelineOperator(span);
            if (pipelineOperator.Kind != TokenKind.None)
            {
                return Located(pipelineOperator);
            }

            var redirection = TryReadRedirection(span);
            if (Error != null)
                return Located(Token.EndOfInput());
            if (redirection.Kind != TokenKind.None)
            {
                return Located(redirection);
            }

            if (span[Position] == (byte)'&')
            {
                RecordError("Unsupported control operator.", Position);
                return Located(Token.EndOfInput());
            }

            return Located(Token.FromWord(ReadWord(span)));
        }

        Token Located(Token token) => (Error == null ? token : Token.EndOfInput()) with { Source = start..Position };
    }

    public void PushBack(Token token)
    {
        _pushback.Push(token);
    }

    /// <summary>
    /// Reads a here-document body from the current position until the delimiter line.
    /// </summary>
    /// <param name="delimiter">The delimiter bytes after quote removal.</param>
    /// <param name="stripTabs">Whether to strip leading tabs from lines and delimiter.</param>
    /// <param name="body">The raw contents when the delimiter is found.</param>
    /// <returns>Whether the delimiter was found; failure records the syntax error.</returns>
    internal bool TryReadHereDocument(
        ShellSource delimiter,
        bool stripTabs,
        out ShellSource body)
    {
        body = default;
        var content = ByteMemoryBuilder.CreateUnaccounted(0);
        var span = _input.AsSpan();

        while (Position < span.Length)
        {
            var lineStart = Position;
            while (Position < span.Length && span[Position] != (byte)'\n')
            {
                Position++;
            }

            var line = span.Slice(lineStart, Position - lineStart);
            var newlineLength = Position < span.Length ? 1 : 0;

            Position += newlineLength;

            var contentLine = line;
            if (stripTabs)
            {
                var tabCount = 0;
                while (tabCount < contentLine.Length && contentLine[tabCount] == (byte)'\t')
                {
                    tabCount++;
                }

                contentLine = contentLine[tabCount..];
            }

            if (contentLine.SequenceEqual(delimiter.AsSpan()))
            {
                body = ShellSource.FromBytes(content.ToMemory());
                return true;
            }

            content.Append(contentLine);
            if (newlineLength > 0)
            {
                content.Append((byte)'\n');
            }
        }

        RecordError("Unterminated here-document.", Position);
        return false;
    }

    /// <summary>
    /// Advances the position to the end of the current line comment.
    /// </summary>
    private void SkipComment(ReadOnlySpan<byte> span)
    {
        while (Position < span.Length && span[Position] != (byte)'\n')
        {
            Position++;
        }
    }

    /// <summary>
    /// Attempts to read a redirection operator at the current position.
    /// </summary>
    private Token TryReadRedirection(ReadOnlySpan<byte> span)
    {
        if (ShellSourceCursor.TryMatchLogicalText(span, Position, "<(", out _)
            || ShellSourceCursor.TryMatchLogicalText(span, Position, ">(", out _))
        {
            RecordError("process substitution is not supported", Position);
            return Token.None();
        }

        if (TryConsumeLogicalText(span, "&>>"))
        {
            return Token.Redirection(RedirectionKind.OutputAndErrorAppend);
        }

        if (TryConsumeLogicalText(span, "&>"))
        {
            return Token.Redirection(RedirectionKind.OutputAndError);
        }

        if (TryConsumeLogicalText(span, ">|"))
        {
            return Token.Redirection(RedirectionKind.ForceOutput);
        }

        if (TryConsumeLogicalText(span, ">&"))
        {
            return Token.Redirection(RedirectionKind.DuplicateOutput);
        }

        if (TryConsumeLogicalText(span, "<&"))
        {
            return Token.Redirection(RedirectionKind.DuplicateInput);
        }

        if (TryConsumeLogicalText(span, "<<-"))
        {
            return Token.Redirection(RedirectionKind.HereDocumentStripTabs);
        }

        if (TryConsumeLogicalText(span, "<<<"))
        {
            return Token.Redirection(RedirectionKind.HereString);
        }

        if (TryConsumeLogicalText(span, "<<"))
        {
            return Token.Redirection(RedirectionKind.HereDocument);
        }

        if (TryConsumeLogicalText(span, ">>"))
        {
            return Token.Redirection(RedirectionKind.OutputAppend);
        }

        if (TryConsumeLogicalText(span, "<>"))
        {
            return Token.Redirection(RedirectionKind.InputOutput);
        }

        if (span[Position] == (byte)'<')
        {
            Position++;
            return Token.Redirection(RedirectionKind.Input);
        }

        if (span[Position] == (byte)'>')
        {
            Position++;
            return Token.Redirection(RedirectionKind.Output);
        }

        return Token.None();
    }

    /// <summary>
    /// Attempts to read a list operator (&amp;&amp; or ||) at the current position.
    /// </summary>
    private Token TryReadListOperator(ReadOnlySpan<byte> span)
    {
        if (TryConsumeLogicalText(span, "&&"))
        {
            return Token.FromListOperator(ListOperator.And);
        }

        if (TryConsumeLogicalText(span, "||"))
        {
            return Token.FromListOperator(ListOperator.Or);
        }

        return Token.None();
    }

    /// <summary>
    /// Attempts to read a pipeline operator (| or |&amp;) at the current position.
    /// </summary>
    private Token TryReadPipelineOperator(ReadOnlySpan<byte> span)
    {
        if (TryConsumeLogicalText(span, "|&"))
        {
            return Token.FromPipelineOperator(PipelineOperator.PipeAndError);
        }

        if (span[Position] == (byte)'|')
        {
            Position++;
            return Token.FromPipelineOperator(PipelineOperator.Pipe);
        }

        return Token.None();
    }

    /// <summary>
    /// Reads through ]] after the parser consumes an unquoted [[ at a command boundary.
    /// </summary>
    internal bool TryReadConditionalExpression(Token opening, out ShellSource expression)
    {
        expression = default;
        var span = _input.AsSpan();
        var start = opening.Source.End.Value;
        var index = start;
        while (index < span.Length)
        {
            if (span[index] == (byte)']'
                && ShellSourceCursor.TryMatchLogicalText(span, index, "]]", out var end))
            {
                var source = _input.Slice(start, index - start);
                Position = end;
                expression = ShellSourceCursor.RemoveLineContinuations(source).TrimBlanks();
                return Error == null;
            }

            index++;
        }

        RecordError("Unterminated conditional command.", opening.Source.Start.Value);
        return false;
    }

    /// <summary>
    /// Consumes a matching logical token and retains its physical ending position.
    /// </summary>
    private bool TryConsumeLogicalText(ReadOnlySpan<byte> span, string text)
    {
        if (!ShellSourceCursor.TryMatchLogicalText(span, Position, text, out var end))
            return false;
        Position = end;
        return true;
    }

    /// <summary>
    /// Reads a word token, handling quotes and expansions within it.
    /// </summary>
    private Word ReadWord(ReadOnlySpan<byte> span)
    {
        return ReadWord(span, delimiterStart: 0, ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
    }

    /// <summary>Reads an expansion operand under its surrounding quote context, without adding source quotes.</summary>
    internal static Word ReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote) =>
        TryReadExpansionWord(content, syntax, outerQuote, out var word, out var error) ? word : throw error.ToException();

    internal static bool TryReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error)
    {
        var tokenizer = new Tokenizer(content);
        var parsed = tokenizer.ReadWord(content.AsSpan(), delimiterStart: content.Length, syntax, outerQuote);
        error = tokenizer.Error;
        word = error == null ? parsed : null;
        return error == null;
    }

    /// <summary>Retains direct selectors for the arithmetic consumer to prepare separately.</summary>
    internal static Word ReadArithmeticWord(ShellSource content, ExpansionWordSyntax syntax)
    {
        var tokenizer = new Tokenizer(content) { _retainArithmeticSubscripts = true };
        var word = tokenizer.ReadWord(content.AsSpan(), delimiterStart: content.Length, syntax, ShellQuoteKind.None);
        return tokenizer.Error is { } error ? throw error.ToException() : word;
    }

    private bool _retainArithmeticSubscripts;

    private bool TryReadArithmeticSubscript([NotNullWhen(true)] out WordPart? part)
    {
        part = null;
        if (!_retainArithmeticSubscripts || _input.AsSpan()[Position] != (byte)'[')
            return false;
        _arraySubscripts ??= new ArraySubscriptScanner.Lookahead(_input, _scanContext);
        if (_arraySubscripts.Scan(Position, out var closing) != ShellScanStatus.Complete)
            return false;
        if (!TryReserveSyntax())
            return false;
        part = WordPart.FromSource(WordPartKind.ArithmeticSubscript, _input.Slice(Position, closing - Position + 1));
        Position = closing + 1;
        return true;
    }

    /// <summary>Reads one word; delimiters before delimiterStart belong to a grouped prefix.</summary>
    private Word ReadWord(ReadOnlySpan<byte> span, int delimiterStart, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote)
    {
        var parts = new List<WordPart>();
        var unquoted = ByteMemoryBuilder.CreateUnaccounted(0);
        var quotedOperand = outerQuote == ShellQuoteKind.Double;
        var arithmetic = syntax != ExpansionWordSyntax.Parameter;

        void FlushUnquoted()
        {
            if (unquoted.Length == 0)
            {
                return;
            }

            if (!TryReserveSyntax())
                return;
            parts.Add(WordPart.FromSource(
                quotedOperand ? WordPartKind.DoubleQuoted : WordPartKind.Unquoted, ShellSource.FromBytes(unquoted.ToMemory())));
            unquoted = ByteMemoryBuilder.CreateUnaccounted(0);
        }

        while (Position < span.Length && Error == null)
        {
            var current = span[Position];
            if (TryReadArithmeticSubscript(out var subscript))
            {
                FlushUnquoted();
                parts.Add(subscript);
                continue;
            }
            if (TryReadBracedParameterExpansion(span, unquoted))
            {
                continue;
            }

            if (current == (byte)'$' && ArithmeticExpansionScanner.HasPrefix(_input, Position))
            {
                ReadArithmeticExpansion(unquoted);
                continue;
            }

            if (current == (byte)'$' && CommandSubstitutionScanner.HasPrefix(_input, Position))
            {
                FlushUnquoted();
                if (!TryReserveSyntax())
                    return new Word([]);
                if (!TryReadCommandSubstitution(out var content))
                    return new Word([]);
                parts.Add(new CommandSubstitutionWordPart(
                    quotedOperand ? WordPartKind.CommandSubstitutionQuoted : WordPartKind.CommandSubstitution, content));
                continue;
            }

            if (current == (byte)'`')
            {
                FlushUnquoted();
                if (!TryReserveSyntax())
                    return new Word([]);
                if (!TryReadCommandSubstitution(out var content))
                    return new Word([]);
                parts.Add(new CommandSubstitutionWordPart(
                    quotedOperand ? WordPartKind.CommandSubstitutionQuoted : WordPartKind.CommandSubstitution, content));
                continue;
            }

            if (Position >= delimiterStart && IsDelimiter(current))
            {
                break;
            }

            // Decoded arithmetic source must not reinterpret generated $' sequences.
            if (current == '\'' && !quotedOperand && !arithmetic
                || current == '$' && syntax != ExpansionWordSyntax.DecodedArithmetic
                && ShellSourceCursor.TryMatchLogicalText(span, Position, "$'", out _))
            {
                FlushUnquoted();
                if (!TryReserveSyntax())
                    return new Word([]);
                var content = ReadQuotedLiteral();
                if (Error != null)
                    return new Word([]);
                if (syntax == ExpansionWordSyntax.ArithmeticSource && !quotedOperand)
                {
                    // ((...)) and $((...)) retain the decoded quotes before expanding their contents.
                    var quoted = BoundedByteMemoryBuilder.CreateUnaccounted(0, ByteMemoryBuilder.MaxMemoryBuilderBytes - 1);
                    if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(quoted, content.Bytes.Span))
                    {
                        RecordError("Arithmetic quoted value exceeds the byte-buffer limit.", Position);
                        return new Word([]);
                    }
                    // Decoding another fragment must not reset the shared syntax/nesting allowance.
                    var decoded = ShellSource.FromBytes(quoted.ToMemory());
                    var tokenizer = new Tokenizer(decoded, 0, _scanContext) { _retainArithmeticSubscripts = _retainArithmeticSubscripts };
                    var operand = tokenizer.ReadWord(decoded.AsSpan(), delimiterStart: decoded.Length,
                        ExpansionWordSyntax.DecodedArithmetic, ShellQuoteKind.None);
                    if (tokenizer.Error is { } error)
                    {
                        RecordError(error);
                        return new Word([]);
                    }
                    parts.AddRange(operand.Parts);
                }
                else
                    parts.Add(WordPart.FromValue(WordPartKind.SingleQuoted, content));
                continue;
            }

            if (current == (byte)'"')
            {
                FlushUnquoted();
                Position++;
                var quotedParts = ReadDoubleQuotedParts(span, syntax);
                parts.AddRange(quotedParts);
                continue;
            }

            if (current == (byte)'\\')
            {
                // Arithmetic uses double-quote escape rules, but apostrophes remain operand data.
                if (arithmetic
                    && (Position + 1 >= span.Length || span[Position + 1] is not ((byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\' or (byte)'\n')))
                {
                    unquoted.Append(current);
                    Position++;
                    continue;
                }
                Position++;
                if (Position >= span.Length)
                {
                    RecordError("Unexpected end of input after escape.", Position);
                    return new Word([]);
                }

                var escaped = span[Position];
                if (escaped == (byte)'\n')
                {
                    Position++;
                    continue;
                }

                // A parameter operand also lets backslash protect its closing brace.
                if (quotedOperand && escaped is not ((byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\' or (byte)'}'))
                {
                    unquoted.Append((byte)'\\');
                    continue;
                }

                FlushUnquoted();
                // A supplementary source character must remain one escaped literal.
                var length = Utf8Text.FirstCharacterLength(span[Position..]);
                if (!TryReserveSyntax())
                    return new Word([]);
                parts.Add(WordPart.FromValue(WordPartKind.Escaped, _input.Slice(Position, length).ToValue()));
                Position += length;
                continue;
            }

            unquoted.Append(current);
            Position++;
        }

        if (Error != null)
            return new Word([]);
        FlushUnquoted();

        if (parts.Count == 0)
        {
            if (!quotedOperand)
            {
                RecordError("Expected a word.", Position);
                return new Word([]);
            }
            if (!TryReserveSyntax())
                return new Word([]);
            parts.Add(WordPart.FromText(WordPartKind.DoubleQuoted, string.Empty));
        }

        return new Word(parts);

        ShellValue ReadQuotedLiteral()
        {
            var cursor = new ShellSourceCursor(_input, Position, _scanContext);
            var complete = cursor.TryReadQuotedLiteral(out var value);
            Position = cursor.Position;
            if (!complete)
                RecordError("Unterminated single-quoted string.", Position);
            return value;
        }
    }

    /// <summary>
    /// Captures canonical arithmetic-expansion syntax while retaining physical cursor offsets.
    /// </summary>
    private void ReadArithmeticExpansion(ByteMemoryBuilder unquoted)
    {
        var start = Position;
        if (!ArithmeticExpansionScanner.TryScan(
                _scanContext,
                _input,
                start,
                out var endIndex,
                out var expression))
        {
            RecordError("Unterminated arithmetic expansion.", start);
            return;
        }

        Position = endIndex + 1;
        unquoted.Append("$(("u8);
        unquoted.Append(expression.Bytes);
        unquoted.Append("))"u8);
    }

    /// <summary>
    /// Reads a substitution, retaining the tree built by parenthesized delimiter parsing.
    /// </summary>
    private bool TryReadCommandSubstitution([NotNullWhen(true)] out CommandSubstitutionSyntax? substitution)
    {
        substitution = null;
        var start = Position;
        if (CommandSubstitutionScanner.ScanParsed(
                _scanContext,
                _input,
                start,
                out var endIndex,
                out var payload) != ShellScanStatus.Complete)
        {
            RecordError("Unterminated command substitution.", start);
            return false;
        }

        Position = endIndex + 1;
        substitution = payload ?? throw new InvalidOperationException("A complete substitution requires executable syntax.");
        return true;
    }

    /// <summary>
    /// Reads a braced parameter expansion as unexpanded segment bytes.
    /// </summary>
    private bool TryReadBracedParameterExpansion(ReadOnlySpan<byte> span, ByteMemoryBuilder content)
    {
        if (!BracedParameterScanner.HasPrefix(_input, Position))
        {
            return false;
        }

        var start = Position;
        var status = BracedParameterScanner.ScanShape(
            _scanContext,
            _input,
            start,
            out var endIndex,
            out var errorIndex,
            out _);
        if (status == BracedParameterScanStatus.Found)
        {
            _ = ShellSourceCursor.TryMatchLogicalText(span, start, "${", out var payloadStart);
            content.Append("${"u8);
            content.Append(ShellSourceCursor.RemoveLineContinuations(
                _input.Slice(payloadStart, endIndex - payloadStart)).Bytes);
            content.Append((byte)'}');
            Position = endIndex + 1;
            return true;
        }

        if (status == BracedParameterScanStatus.UnterminatedCommandSubstitution)
        {
            RecordError("Unterminated command substitution.", errorIndex);
            return false;
        }

        return false;
    }

    /// <summary>
    /// Reads double-quoted text, preserving escaped-character provenance.
    /// </summary>
    private List<WordPart> ReadDoubleQuotedParts(ReadOnlySpan<byte> span, ExpansionWordSyntax syntax)
    {
        var parts = new List<WordPart>();
        var content = ByteMemoryBuilder.CreateUnaccounted(0);

        void Flush(bool allowEmpty)
        {
            if (content.Length == 0)
            {
                if (allowEmpty && parts.Count == 0)
                {
                    if (!TryReserveSyntax())
                        return;
                    parts.Add(WordPart.FromText(WordPartKind.DoubleQuoted, string.Empty));
                }

                return;
            }

            if (!TryReserveSyntax())
                return;
            parts.Add(WordPart.FromSource(WordPartKind.DoubleQuoted, ShellSource.FromBytes(content.ToMemory())));
            content = ByteMemoryBuilder.CreateUnaccounted(0);
        }

        while (Position < span.Length && Error == null)
        {
            var current = span[Position];
            if (TryReadArithmeticSubscript(out var subscript))
            {
                Flush(allowEmpty: false);
                parts.Add(subscript);
                continue;
            }
            if (current == (byte)'"')
            {
                Position++;
                Flush(allowEmpty: true);
                return parts;
            }

            if (current == (byte)'\\')
            {
                if (Position + 1 >= span.Length)
                {
                    RecordError("Unexpected end of input after escape.", Position);
                    return parts;
                }

                var escaped = span[Position + 1];
                if (escaped == (byte)'\n')
                {
                    Position += 2;
                    continue;
                }

                if (escaped is (byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\')
                {
                    Flush(allowEmpty: false);
                    if (!TryReserveSyntax())
                        return parts;
                    parts.Add(WordPart.FromValue(WordPartKind.Escaped, _input.Slice(Position + 1, 1).ToValue()));
                    Position += 2;
                    continue;
                }

                content.Append((byte)'\\');
                Position++;
                continue;
            }

            if (TryReadBracedParameterExpansion(span, content))
            {
                continue;
            }

            if (current == (byte)'$' && ArithmeticExpansionScanner.HasPrefix(_input, Position))
            {
                ReadArithmeticExpansion(content);
                continue;
            }

            if (current == (byte)'$' && CommandSubstitutionScanner.HasPrefix(_input, Position))
            {
                Flush(allowEmpty: false);
                if (!TryReserveSyntax())
                    return parts;
                if (!TryReadCommandSubstitution(out var payload))
                    return parts;
                parts.Add(new CommandSubstitutionWordPart(WordPartKind.CommandSubstitutionQuoted, payload));
                continue;
            }

            if (current == (byte)'`')
            {
                Flush(allowEmpty: false);
                if (!TryReserveSyntax())
                    return parts;
                if (!TryReadCommandSubstitution(out var payload))
                    return parts;
                parts.Add(new CommandSubstitutionWordPart(WordPartKind.CommandSubstitutionQuoted, payload));
                continue;
            }

            content.Append(current);
            Position++;
        }

        if (Error != null)
            return parts;

        // Generated quotes undergo arithmetic quote removal, not source-syntax validation.
        if (syntax == ExpansionWordSyntax.DecodedArithmetic)
        {
            Flush(allowEmpty: true);
            return parts;
        }
        RecordError("Unterminated double-quoted string.", Position);
        return parts;
    }

    /// <summary>
    /// Returns true when an ASCII byte terminates a word; CR is ordinary data.
    /// </summary>
    private static bool IsDelimiter(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)';' or (byte)'&' or (byte)'|' or (byte)'<' or (byte)'>' or (byte)'(' or (byte)')';

    private static bool IsWordBoundary(ReadOnlySpan<byte> span, int position)
    {
        position = ShellSourceCursor.SkipLineContinuations(span, position);
        return position >= span.Length || IsDelimiter(span[position]);
    }

    /// <summary>
    /// Returns true when a brace should be treated as a standalone token.
    /// </summary>
    private static bool IsBraceStandalone(ReadOnlySpan<byte> span, int position) =>
        IsWordBoundary(span, position + 1);
}

/// <summary>Whether operand quotes and escapes follow parameter-word or arithmetic rules.</summary>
internal enum ExpansionWordSyntax
{
    Parameter,
    // Array selectors and substring bounds remove ANSI-C quotes.
    Arithmetic,
    // Arithmetic commands/expansions retain ANSI-C quotes after decoding their payload.
    ArithmeticSource,
    DecodedArithmetic,
}

/// <summary>Identifies the type of token returned by the tokenizer.</summary>
internal enum TokenKind
{
    None,
    Word,
    Redirection,
    ListOperator,
    PipelineOperator,
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    ArithmeticCommand,
    CaseTerminator,
    Newline,
    Semicolon,
    EndOfInput
}

/// <summary>
/// Represents a single token in the input stream.
/// </summary>
internal readonly struct Token
{
    private Token(
        TokenKind kind,
        Word? word,
        RedirectionKind? redirectionKind,
        ListOperator? listOperator,
        PipelineOperator? pipelineOperator,
        ShellSource commandExpression)
    {
        Kind = kind;
        Word = word;
        RedirectionKind = redirectionKind;
        ListOperator = listOperator;
        PipelineOperator = pipelineOperator;
        CommandExpression = commandExpression;
    }

    /// <summary>
    /// Gets the kind of token.
    /// </summary>
    public TokenKind Kind { get; }

    /// <summary>
    /// Gets the half-open byte range in the original source. Emitted tokens keep
    /// it through lookahead and pushback; synthetic heredoc separators have zero width.
    /// </summary>
    public Range Source { get; init; }

    /// <summary>
    /// Gets the word value for word tokens.
    /// </summary>
    private Word? Word { get; }

    /// <summary>
    /// Attempts to treat this token as a word token.
    /// </summary>
    /// <param name="wordToken">The non-nullable word token when successful.</param>
    /// <returns>True when the token is a word with a non-null word value.</returns>
    public bool TryGetWordToken([NotNullWhen(true)] out WordToken? wordToken)
    {
        if (Kind == TokenKind.Word && Word != null)
        {
            wordToken = new WordToken(this, Word);
            return true;
        }

        wordToken = null;
        return false;
    }

    /// <summary>
    /// Gets the redirection kind for redirection tokens.
    /// </summary>
    public RedirectionKind? RedirectionKind { get; }

    /// <summary>
    /// Gets the list operator for list operator tokens.
    /// </summary>
    public ListOperator? ListOperator { get; }

    /// <summary>
    /// Gets the pipeline operator for pipeline operator tokens.
    /// </summary>
    public PipelineOperator? PipelineOperator { get; }

    /// <summary>
    /// Gets the unexpanded source for arithmetic command tokens.
    /// </summary>
    public ShellSource CommandExpression { get; }

    public static Token None() =>
        new(TokenKind.None, null, null, null, null, ShellSource.Empty);

    public static Token EndOfInput() =>
        new(TokenKind.EndOfInput, null, null, null, null, ShellSource.Empty);

    public static Token Newline() =>
        new(TokenKind.Newline, null, null, null, null, ShellSource.Empty);

    public static Token Semicolon() =>
        new(TokenKind.Semicolon, null, null, null, null, ShellSource.Empty);

    public static Token FromWord(Word word) =>
        new(TokenKind.Word, word, null, null, null, ShellSource.Empty);

    public static Token Redirection(RedirectionKind kind) =>
        new(TokenKind.Redirection, null, kind, null, null, ShellSource.Empty);

    public static Token FromListOperator(ListOperator listOperator) =>
        new(TokenKind.ListOperator, null, null, listOperator, null, ShellSource.Empty);

    public static Token FromPipelineOperator(PipelineOperator pipelineOperator) =>
        new(TokenKind.PipelineOperator, null, null, null, pipelineOperator, ShellSource.Empty);

    public static Token OpenParen() =>
        new(TokenKind.OpenParen, null, null, null, null, ShellSource.Empty);

    public static Token CloseParen() =>
        new(TokenKind.CloseParen, null, null, null, null, ShellSource.Empty);

    public static Token OpenBrace() =>
        new(TokenKind.OpenBrace, null, null, null, null, ShellSource.Empty);

    public static Token CloseBrace() =>
        new(TokenKind.CloseBrace, null, null, null, null, ShellSource.Empty);

    public static Token ArithmeticCommand(ShellSource expression) =>
        new(TokenKind.ArithmeticCommand, null, null, null, null, expression);

    public static Token CaseTerminator() =>
        new(TokenKind.CaseTerminator, null, null, null, null, ShellSource.Empty);
}

/// <summary>
/// Represents a word token with a guaranteed non-null word value.
/// </summary>
internal sealed class WordToken(Token token, Word word)
{
    public Token Token { get; } = token;

    public Word Word { get; } = word;
}

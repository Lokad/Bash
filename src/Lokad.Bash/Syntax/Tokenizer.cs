using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

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
    private SourceLineCursor _lines;
    private int _nextOmittedSeparator;
    private HereDocumentReadAhead? _readingAhead;
    private bool _continuedAtEndOfInput;
    private bool _spacingContinuedAtEndOfInput;
    private int _lastPhysicalNewline;

    internal bool TrackCommandLocations { get; init; }
    internal bool TrackUnexpectedTokens { get; init; }
    internal bool AllowEofHereDocuments { get; init; }
    internal bool IsCommandSubstitution { get; init; }
    internal IReadOnlyList<Range>? OmittedSeparators { get; init; }
    internal IReadOnlyList<SourceLineAdjustment>? LineAdjustments { get; init; }
    internal CommandLocations? Locations { get; private set; }
    internal int ConsumedPosition { get; private set; }

    internal ShellScanContext ScanContext => _scanContext;

    internal void BeginCommandUnit()
    {
        _arraySubscripts = null;
        Locations = null;
        _scanContext.ClearCommandUnitLookahead();
    }

    internal int GetLine(int offset)
    {
        if (!TrackCommandLocations)
            return 0;
        var line = _lines.GetLine(_input, offset);
        var adjustment = 0;
        if (LineAdjustments is { } changes)
        {
            var lower = 0;
            var upper = changes.Count;
            while (lower < upper)
            {
                var middle = lower + (upper - lower) / 2;
                if (changes[middle].End <= offset)
                    lower = middle + 1;
                else
                    upper = middle;
            }
            if (lower != 0)
                adjustment = changes[lower - 1].Delta;
        }
        return line + adjustment + (_scanContext.HereDocumentReadAhead?.GetBufferedLineAdvance(_input, offset) ?? 0);
    }

    internal void RecordLocation(ICommand command, int startLine, int firstElementEndLine) =>
        RecordLocation(command, startLine, firstElementEndLine, 0, 0);

    internal void RecordLocation(ICommand command, int startLine, int firstElementEndLine, int wrapperOperandEndLine,
        int redirectionOperandLine)
    {
        if (!TrackCommandLocations)
            return;
        (Locations ??= new CommandLocations()).Add(command,
            new CommandLocation(startLine, firstElementEndLine, GetLine(ConsumedPosition))
            {
                WrapperOperandEndLine = wrapperOperandEndLine,
                RedirectionOperandLine = redirectionOperandLine
            }, _scanContext.SyntaxMemory);
    }

    internal void RecordReadDiagnostics(ICommand command, CommandReadDiagnostics diagnostics) =>
        (Locations ??= new CommandLocations()).SetReadDiagnostics(command, diagnostics, _scanContext.SyntaxMemory);

    internal int GetHereDocumentWarningLine(int position) => _readingAhead?.GetLine(position) ?? GetLine(position);

    /// <summary>Whether reaching EOF also completes a final logical line without a terminating LF.</summary>
    internal int EndOfInputLineAdvance
    {
        get
        {
            var source = _input.AsSpan();
            return source.IsEmpty || source[^1] != (byte)'\n' || _continuedAtEndOfInput ? 1 : 0;
        }
    }

    private bool SkipCollectedHereDocuments()
    {
        var next = _scanContext.HereDocumentReadAhead?.SkipBodies(_input, Position) ?? Position;
        if (next == Position)
            return false;
        Position = next;
        return true;
    }

    internal void ReadCaptureHereDocuments(HereDocumentReader pending)
    {
        var readAhead = _scanContext.BeginHereDocumentReadAhead();
        var afterCloser = Position;
        var start = readAhead.FindBodyStart(afterCloser);
        _scanContext.WarnPendingCaptureDocuments(pending.PendingCount);
        _readingAhead = readAhead;
        Position = start;
        try
        {
            pending.TryReadBodies(this);
            readAhead.Add(afterCloser, start, Position, _scanContext.SyntaxMemory);
            if (pending.Warnings is { } warnings)
                _scanContext.AppendCaptureWarnings(warnings);
        }
        finally
        {
            Position = afterCloser;
            _readingAhead = null;
        }
    }

    internal void ReadHereDocumentExpansionCaptureBodies(HereDocumentReader pending)
    {
        Position = HereDocumentReadAhead.FindNextLineStart(_input, Position);
        pending.TryReadBodies(this);
    }

    private ParserFailure? _failure;

    /// <summary>The first failure; later reads stop without replacing its diagnostic.</summary>
    internal ParserFailure? Failure => _failure ?? (_scanContext.LimitError is { } limit
        ? new ParserFailure(limit, ParserFailureKind.Limit) : null);
    internal SyntaxError? Error => _failure?.Error ?? _scanContext.LimitError;

    /// <summary>Allows the streaming parser to finish the grammar before a failed word.</summary>
    internal bool EndInputOnQuotedSubstitutionFailure { get; init; }
    internal ParserFailure? QuotedEndOfInputFailure { get; private set; }

    internal void RecordError(string message, int offset) => RecordError(new SyntaxError(message, offset));

    internal void RecordError(SyntaxError error) => RecordFailure(new ParserFailure(error, ParserFailureKind.Grammar));

    internal void RecordUnexpectedToken(string message, Token token, int errorOffset)
    {
        var error = new SyntaxError(message, errorOffset);
        if (!TrackUnexpectedTokens || token.Kind == TokenKind.EndOfInput)
        {
            RecordError(error);
            return;
        }
        RecordFailure(new ParserFailure(error, ParserFailureKind.Grammar)
        {
            UnexpectedToken = GetUnexpectedSyntax(token)
        });
    }

    internal UnexpectedSyntax GetUnexpectedSyntax(Token token)
    {
        var (start, end) = (token.Source.Start.Value, token.Source.End.Value);
        return new UnexpectedSyntax(_input.Slice(start, end - start), _input.GetPhysicalLine(end, out _), end);
    }

    internal void RecordMissingRedirectionOperand(Token token)
    {
        if (Error != null)
            return;
        UnexpectedSyntax? unexpected = null;
        if (TrackUnexpectedTokens)
        {
            if (token.Kind == TokenKind.Newline)
            {
                // Here-document collection can move the reader past the token's physical line.
                var read = Position > _lastPhysicalNewline && _input[Position - 1] == (byte)'\n' ? Position - 1 : Position;
                unexpected = new UnexpectedSyntax(UnexpectedSyntax.NewlineToken, _input.GetPhysicalLine(_lastPhysicalNewline, out _), read);
            }
            else if (token.Kind != TokenKind.EndOfInput)
                unexpected = GetUnexpectedSyntax(token);
            else if (!_spacingContinuedAtEndOfInput)
                unexpected = new UnexpectedSyntax(UnexpectedSyntax.NewlineToken, _input.GetPhysicalLine(Position, out _), Position);
        }
        RecordFailure(new ParserFailure(new SyntaxError("Expected a word after redirection operator.", Position),
            ParserFailureKind.RedirectionOperand) { UnexpectedToken = unexpected });
    }

    internal void RecordFailure(ParserFailure failure)
    {
        _failure ??= _scanContext.ParsingLimitExceeded
            ? new ParserFailure(_scanContext.LimitError ?? failure.Error, ParserFailureKind.Limit) : failure;
        if (_scanContext.ParsingLimitExceeded)
            _scanContext.RecordLimitError(_failure.Error);
    }

    internal void MarkCompoundArrayFailure()
    {
        if (_failure is { Kind: ParserFailureKind.Delimiter, CaptureEndOfInput: not null } missingCloser)
            _failure = missingCloser with { Kind = ParserFailureKind.ArraySubstitutionDelimiter };
        if ((_failure ?? QuotedEndOfInputFailure) is { Kind: ParserFailureKind.QuotedSubstitutionForHeader } failure)
        {
            _failure = failure with { Kind = ParserFailureKind.ArrayQuotedSubstitutionForHeader };
            QuotedEndOfInputFailure = null;
        }
    }

    private void RecordExpansionFailure(ParserFailure? failure, bool quoted, string missingDelimiter, int start) =>
        RecordFailure(failure?.InDoubleQuotes(quoted)
            ?? new ParserFailure(new SyntaxError(missingDelimiter, start), ParserFailureKind.Delimiter));

    private void RecordUnclosedQuote(char delimiter, int start, string message)
    {
        if (IsCommandSubstitution && AllowEofHereDocuments)
        {
            RecordFailure(new ParserFailure(new SyntaxError(message, Position), ParserFailureKind.Delimiter)
            {
                CaptureEndOfInput = new CaptureEndOfInput(delimiter, start)
            });
        }
        else
            RecordError(message, Position);
    }

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

    internal bool HasPendingHereDocumentRegion(Token boundary) => HereDocuments is { HasPending: true }
        || _scanContext.HereDocumentReadAhead?.GetBufferedLineAdvance(_input, boundary.Source.Start.Value) > 0;

    /// <summary>A parsed subshell whose final bare timer needs a separator before its closer.</summary>
    internal bool HasSubshellEndingInBareTime { get; set; }

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
        ConsumedPosition = position;
        _lines = new SourceLineCursor(position);
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
        var text = ShellSourceCursor.RemoveLineContinuations(_scanContext, _input[token.Token.Source]);
        var syntax = FunctionNameSyntax.Plain;
        var cursor = new ShellSourceCursor(text, 0, new ShellScanContext { SyntaxMemory = _scanContext.SyntaxMemory });
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

        if (Parser.IsSupportedKeyword(text.AsSpan())
            || AssignmentWord.TrySplitOperand(text, _scanContext.SyntaxMemory, out var target, out _, out _)
                && VariableReference.TryParseSource(target, _scanContext.SyntaxMemory, out _))
            syntax |= FunctionNameSyntax.KeywordOnly;
        var canonical = ReadNameSpelling(token, text, translateLocaleQuotes: false);
        if (Error != null)
            return false;
        name = new FunctionName(canonical, syntax);
        return true;
    }

    /// <summary>Retains a loop name as a word spelling without expanding it.</summary>
    internal ShellValue ReadForNameSpelling(WordToken token) =>
        ReadNameSpelling(token, ShellSourceCursor.RemoveLineContinuations(_scanContext, _input[token.Token.Source]),
            translateLocaleQuotes: true);

    private ShellValue ReadNameSpelling(WordToken token, ShellSource text, bool translateLocaleQuotes)
    {
        if (text.AsSpan().IndexOf("$'"u8) < 0 && (!translateLocaleQuotes || text.AsSpan().IndexOf("$\""u8) < 0))
            return text.ToValue();

        var output = _scanContext.CreateBoundedByteBuilder(0, ByteMemoryBuilder.MaxMemoryBuilderBytes - 1);
        var nameCursor = new ShellSourceCursor(text, 0, new ShellScanContext { SyntaxMemory = _scanContext.SyntaxMemory });
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
                if (translateLocaleQuotes && !nameCursor.IsQuoted && text.AsSpan()[start..].StartsWith("$\""u8))
                {
                    // The invariant locale retains the double-quoted word without its translation marker.
                    nameCursor.Advance();
                    start = nameCursor.Position;
                }
                if (!nameCursor.TryConsumeQuoteOrEscape()
                    && nameCursor.SkipNestedExpansion(BracedParameterRecognition.All) is not ShellNestedExpansionScan.Complete)
                    nameCursor.Advance();
                appended = output.TryAppend(text.AsSpan(start, nameCursor.Position - start));
            }
            if (!appended)
            {
                RecordError(translateLocaleQuotes ? "For loop name exceeds the output limit." : "Function name exceeds the output limit.",
                    token.Token.Source.Start.Value);
                return default;
            }
        }
        return ShellValue.FromBytes(output.ToMemory());
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

        var nameLength = 0;
        while (position < source.Length && VariableNames.IsNamePart((char)source[position]))
        {
            nameLength++;
            position = ShellSourceCursor.SkipLineContinuations(source, position + 1);
        }

        if (position >= source.Length || source[position] != (byte)'[')
            return false;
        _arraySubscripts ??= new ArraySubscriptScanner.Lookahead(_input, _scanContext);
        if (_arraySubscripts.Scan(position, out var closing) != ShellScanStatus.Complete
            || !TryReadSubscriptAssignment(token, position, closing, out var element, out _))
            return false;

        // The leading word part already has the logical name. Selector spelling still needs its
        // physical bytes, including quotes and continuations, for the eventual array kind.
        var selector = source.Slice(position, closing - position + 1);
        var targetLength = checked(nameLength + selector.Length);
        _scanContext.SyntaxMemory?.Grow(MemoryCapacity.ArrayBackingBytes<byte>(targetLength));
        var target = new byte[targetLength];
        first.Source.AsSpan(0, nameLength).CopyTo(target);
        selector.CopyTo(target.AsSpan(nameLength));
        assignment = new AssignmentWord(ShellSource.FromBytes(target), element.Value, element.Operator);
        return true;
    }

    /// <summary>Reads compound items, retaining whitespace inside an unquoted bracket prefix.</summary>
    internal CompoundArrayItem ReadCompoundArrayItem(WordToken token) =>
        TryReadCompoundArrayItem(token, declaration: false, out var item, out var error) ? item : throw error.ToException();

    internal bool TryReadCompoundArrayItem(WordToken token, bool declaration,
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
            if (_arraySubscripts.Scan(start, out var closing, out var quotedCapture) != ShellScanStatus.Complete)
            {
                RecordError("Invalid bracketed array item.", start);
                return new CompoundArrayItem(new Word([]), null);
            }

            if (!TryReadSubscriptAssignment(token, start, closing, out var element, out var assignmentPrefixEnd))
            {
                if (token.Token.Source.End.Value > closing)
                {
                    CollectDiagnostic(token.Word, token.Token.Source.End.Value, 0);
                    return new CompoundArrayItem(token.Word, null);
                }

                // Brackets group source, not expansion results: normal field splitting still applies.
                Position = start;
                var grouped = ReadWord(_input.AsSpan(), closing + 1, ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
                CollectDiagnostic(grouped, Position, 0);
                return new CompoundArrayItem(grouped, null);
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
            CollectDiagnostic(word, Position, declaration ? assignmentPrefixEnd : 0);
            return new CompoundArrayItem(word, element);

            void CollectDiagnostic(Word itemWord, int end, int prefixEnd)
            {
                if (quotedCapture == SubstitutionSyntaxFailure.None || !TrackCommandLocations || Error != null)
                    return;
                var prefix = prefixEnd == 0 ? default : _input.Slice(start, prefixEnd - start);
                var prefixLine = prefixEnd == 0 ? 0 : _scanContext.GetScriptDiagnosticLine(prefixEnd);
                _scanContext.RecordCompoundItemDiagnostic(start,
                    new CompoundItemReadDiagnostic(itemWord, _scanContext.GetScriptDiagnosticLine(end), prefix, prefixLine, quotedCapture));
            }
        }
    }

    /// <summary>Reads = or += after a matched bracket pair; leaves Position unchanged if absent.</summary>
    private bool TryReadSubscriptAssignment(
        WordToken token,
        int opening,
        int closing,
        [NotNullWhen(true)] out CompoundArrayElement? element,
        out int prefixEnd)
    {
        element = null;
        prefixEnd = 0;
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

        prefixEnd = equals + 1;
        Position = ShellSourceCursor.SkipLineContinuations(source, prefixEnd);
        Word value;
        if (Position >= source.Length || IsWordDelimiter(source[Position]))
            value = new Word([]);
        else
        {
            var prefix = source[token.Token.Source.Start.Value..Position];
            // Require an intact prefix in the first unquoted part. A dollar
            // expansion in the selector can contribute metadata before the RHS.
            if (Position <= token.Token.Source.End.Value
                && token.Word.Parts is [SourceWordPart { Kind: WordPartKind.Unquoted } leading, ..]
                && leading.Source.AsSpan().StartsWith(prefix) && !prefix.Contains((byte)'$'))
            {
                value = token.Word.AfterSourcePrefix(leading, prefix.Length, _scanContext.SyntaxMemory);
                Position = token.Token.Source.End.Value;
            }
            else
                value = ReadWord(source);
        }
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

        var token = ReadToken(forHeader: false, out _);
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
    internal Token NextTokenCore() => Consume(ReadToken(forHeader: false, out _));

    /// <summary>Reads the next token after a consumed for keyword, identifying arithmetic clause boundaries.</summary>
    internal Token ReadForHeader(out ArithmeticForSeparators separators) => Consume(ReadToken(forHeader: true, out separators));

    private Token Consume(Token token)
    {
        if (TrackCommandLocations && token.Kind != TokenKind.EndOfInput)
            ConsumedPosition = token.Source.End.Value;
        return token;
    }

    private Token ReadToken(bool forHeader, out ArithmeticForSeparators separators)
    {
        separators = default;
        if (Error != null)
            return Token.EndOfInput();
        if (_pushback.Count > 0)
        {
            return _pushback.Pop();
        }
        if (QuotedEndOfInputFailure != null)
            return Token.EndOfInput();

        var span = _input.AsSpan();
        var start = Position;
        while (true)
        {
            var beforeSpacing = Position;
            Position = ShellSourceCursor.SkipTokenSpacing(span, Position);
            if (SkipCollectedHereDocuments())
                continue;
            if (OmittedSeparators is { } edits && _nextOmittedSeparator < edits.Count
                && Position == edits[_nextOmittedSeparator].Start.Value)
            {
                var end = edits[_nextOmittedSeparator++].End.Value;
                if (!SkipOmittedSeparator(span, end))
                    return Located(Token.EndOfInput());
            }
            start = Position;
            if (Position >= span.Length)
            {
                _spacingContinuedAtEndOfInput |= Position > beforeSpacing && EndsInLineContinuation(span);
                if (HereDocuments is { HasPending: true } pending)
                {
                    if (AllowEofHereDocuments)
                    {
                        // A continued final header never reaches its here-document boundary.
                        if (!IsCommandSubstitution || !EndsInLineContinuation(span))
                            pending.TryReadBodies(this);
                    }
                    else
                        RecordError("Unterminated here-document.", Position);
                }
                return Located(Token.EndOfInput());
            }

            var current = span[Position];

            if (current == '#')
            {
                Position = ShellSourceCursor.SkipComment(span, Position);
                continue;
            }

            if (!TryReserveSyntax())
                return Located(Token.EndOfInput());
            if (current == (byte)'\n')
            {
                _lastPhysicalNewline = Position;
                Position++;
                var skippedBodies = SkipCollectedHereDocuments();
                if (HereDocuments is { HasPending: true })
                {
                    if (!HereDocuments.TryReadBodies(this))
                        return Located(Token.EndOfInput());
                    // Keep bodies in the preceding source segment for command-chain display.
                    return Token.Newline() with { Source = Position..Position };
                }
                if (skippedBodies)
                    return Token.Newline() with { Source = Position..Position };
                return Located(Token.Newline());
            }

            if (current == (byte)';')
            {
                if (TryConsumeLogicalText(";;&"))
                    return Located(Token.CaseTerminator(CaseContinuation.Retest));
                if (TryConsumeLogicalText(";&"))
                    return Located(Token.CaseTerminator(CaseContinuation.FallThrough));
                if (TryConsumeLogicalText(";;"))
                    return Located(Token.CaseTerminator(CaseContinuation.Stop));
                Position++;
                return Located(Token.Semicolon());
            }

            if (current == '(')
            {
                var status = forHeader
                    ? ArithmeticExpansionScanner.ScanForHeader(_scanContext, _input, Position,
                        out var arithmeticEnd, out var expression, out separators, out var error)
                    : ArithmeticExpansionScanner.ScanCommand(_scanContext, _input, Position,
                        out arithmeticEnd, out expression, out error);
                if (status == ShellScanStatus.Complete)
                {
                    Position = arithmeticEnd + 1;
                    return Located(Token.ArithmeticCommand(expression));
                }

                if (status != ShellScanStatus.NotFound)
                {
                    if (error != null)
                        RecordFailure(error);
                    else
                        RecordError(status == ShellScanStatus.Incomplete
                            ? "Unterminated arithmetic command." : "Invalid arithmetic command.", Position);
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

        Token Located(Token token)
        {
            if (Error != null)
            {
                token = Token.EndOfInput();
                if (EndInputOnQuotedSubstitutionFailure && !_scanContext.ParsingLimitExceeded
                    && _failure is { IsDirectQuotedSubstitutionFailure: true })
                {
                    // End the input at the failed word. The grammar can still finish a prefix.
                    QuotedEndOfInputFailure = _failure;
                    _failure = null;
                }
            }
            return token with { Source = start..Position };
        }
    }

    private static bool EndsInLineContinuation(ReadOnlySpan<byte> source)
    {
        if (source.Length < 2 || source[^1] != (byte)'\n')
            return false;
        var start = source.Length - 1;
        while (start > 0 && source[start - 1] == (byte)'\\')
            start--;
        return (source.Length - 1 - start) % 2 != 0;
    }

    private bool SkipOmittedSeparator(ReadOnlySpan<byte> source, int end)
    {
        if (HereDocuments is { HasPending: true } documents)
        {
            // The physical LF still supplies the redirection's body, even when it
            // occurs in the gap consumed by a separator that Bash did not print.
            var cursor = ShellSourceCursor.SkipTokenSpacing(source, Position + 1);
            if (cursor < end && source[cursor] == (byte)'#')
                cursor = ShellSourceCursor.SkipComment(source, cursor);
            if (cursor < end && source[cursor] == (byte)'\n')
            {
                Position = cursor + 1;
                if (!documents.TryReadBodies(this))
                    return false;
            }
        }
        Position = end;
        return true;
    }

    public void PushBack(Token token)
    {
        _pushback.Push(token);
    }

    /// <summary>Reads a brace token as a literal when the grammar expects a word.</summary>
    internal bool TryGetOperandWord(Token token, [NotNullWhen(true)] out WordToken? wordToken)
    {
        if (token.Kind is TokenKind.OpenBrace or TokenKind.CloseBrace)
        {
            if (!TryReserveSyntax())
            {
                wordToken = null;
                return false;
            }

            // Match the compact storage of an ordinary unquoted part.
            var literal = ShellSource.FromBytes(_input[token.Source].Bytes.ToArray());
            var word = new Word([WordPart.FromSource(WordPartKind.Unquoted, literal, _scanContext.SyntaxMemory)]);
            token = Token.FromWord(word) with { Source = token.Source };
        }

        return token.TryGetWordToken(out wordToken);
    }

    internal bool TryReadRedirectionWord(bool hereDocument, out Token token,
        [NotNullWhen(true)] out WordToken? word, out HereDocumentDelimiterSyntax? delimiter)
    {
        delimiter = null;
        if (!hereDocument)
        {
            token = NextTokenCore();
            return TryGetOperandWord(token, out word);
        }

        // The spelling pass needs the same nested boundaries as the token reader.
        var previous = _scanContext.PreserveDelimiterLookahead;
        _scanContext.PreserveDelimiterLookahead = true;
        try
        {
            token = NextTokenCore();
            if (!TryGetOperandWord(token, out word))
                return false;
            delimiter = HereDocumentDelimiterSyntax.Prepare(word.Word, _input[word.Token.Source], _scanContext);
            return true;
        }
        finally { _scanContext.PreserveDelimiterLookahead = previous; }
    }

    /// <summary>
    /// Reads a here-document body from the current position until the delimiter line.
    /// </summary>
    /// <param name="delimiter">The delimiter bytes after quote removal.</param>
    /// <param name="stripTabs">Whether to strip leading tabs from lines and delimiter.</param>
    /// <param name="joinContinuations">Whether the unquoted delimiter permits removing escaped LF before comparison.</param>
    /// <param name="body">The collected body with continuation/tab normalization, without value expansion.</param>
    /// <returns>Whether the delimiter was found. A script reader can use the collected EOF body.</returns>
    internal bool TryReadHereDocument(
        ShellSource delimiter,
        bool stripTabs,
        bool joinContinuations,
        out ShellSource body)
    {
        body = default;
        ByteMemoryBuilder? content = null;
        var bodyStart = Position;
        var span = _input.AsSpan();

        while (Position < span.Length)
        {
            var physicalLineStart = Position;
            var lineStart = content?.Length ?? 0;
            var runStart = physicalLineStart;
            var hasContinuedBytes = false;
            while (Position < span.Length && span[Position] != (byte)'\n')
            {
                if (joinContinuations && span[Position] == (byte)'\\' && Position + 1 < span.Length)
                {
                    if (span[Position + 1] == (byte)'\n')
                    {
                        content ??= StartContent();
                        hasContinuedBytes |= Position != runStart;
                        AppendRun(content, span[runStart..Position]);
                        Position += 2;
                        _continuedAtEndOfInput |= Position == span.Length;
                        runStart = Position;
                        continue;
                    }
                    // An escaped backslash protects a following LF from joining.
                    Position += 2;
                }
                else
                    Position++;
            }

            var run = span[runStart..Position];
            // A final continuation alone did not supply a logical body line.
            if (AllowEofHereDocuments && Position == span.Length && run.IsEmpty
                && runStart != physicalLineStart && !hasContinuedBytes)
                break;
            var line = run;
            if (content != null)
            {
                AppendRun(content, run);
                line = content.ToMemory().Span[lineStart..];
            }
            else if (stripTabs)
                line = run.TrimStart((byte)'\t');

            var newlineLength = Position < span.Length ? 1 : 0;
            Position += newlineLength;
            if (line.SequenceEqual(delimiter.AsSpan()))
            {
                if (content != null)
                {
                    content.Truncate(lineStart);
                    body = ShellSource.FromBytes(content.ToMemory());
                }
                else if (physicalLineStart != bodyStart)
                    body = _input.Slice(bodyStart, physicalLineStart - bodyStart);
                return true;
            }

            if (content == null && (line.Length != run.Length || newlineLength == 0 && AllowEofHereDocuments))
            {
                // A partial final body line receives LF. A matching delimiter returns above.
                content = StartContent();
                content.Append(line);
            }
            if (newlineLength > 0 || AllowEofHereDocuments)
                content?.Append((byte)'\n');

            ByteMemoryBuilder StartContent()
            {
                var builder = _scanContext.CreateByteBuilder(0);
                // Earlier unedited lines still live on the original source backing.
                builder.Append(_input.AsSpan(bodyStart, physicalLineStart - bodyStart));
                lineStart = builder.Length;
                return builder;
            }

            void AppendRun(ByteMemoryBuilder builder, ReadOnlySpan<byte> bytes)
            {
                // Strip only the logical line's leading tabs. A continued physical
                // line can begin with a tab that belongs inside the body or delimiter.
                builder.Append(stripTabs && builder.Length == lineStart ? bytes.TrimStart((byte)'\t') : bytes);
            }
        }

        if (AllowEofHereDocuments)
            body = content == null ? _input.Slice(bodyStart, Position - bodyStart) : ShellSource.FromBytes(content.ToMemory());
        else
            RecordError("Unterminated here-document.", Position);
        return false;
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

        if (TryConsumeLogicalText("&>>"))
        {
            return Token.Redirection(RedirectionKind.OutputAndErrorAppend);
        }

        if (TryConsumeLogicalText("&>"))
        {
            return Token.Redirection(RedirectionKind.OutputAndError);
        }

        if (TryConsumeLogicalText(">|"))
        {
            return Token.Redirection(RedirectionKind.ForceOutput);
        }

        if (TryConsumeLogicalText(">&"))
        {
            return Token.Redirection(RedirectionKind.DuplicateOutput);
        }

        if (TryConsumeLogicalText("<&"))
        {
            return Token.Redirection(RedirectionKind.DuplicateInput);
        }

        if (TryConsumeLogicalText("<<-"))
        {
            return Token.Redirection(RedirectionKind.HereDocumentStripTabs);
        }

        if (TryConsumeLogicalText("<<<"))
        {
            return Token.Redirection(RedirectionKind.HereString);
        }

        if (TryConsumeLogicalText("<<"))
        {
            return Token.Redirection(RedirectionKind.HereDocument);
        }

        if (TryConsumeLogicalText(">>"))
        {
            return Token.Redirection(RedirectionKind.OutputAppend);
        }

        if (TryConsumeLogicalText("<>"))
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
        if (TryConsumeLogicalText("&&"))
        {
            return Token.FromListOperator(ListOperator.And);
        }

        if (TryConsumeLogicalText("||"))
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
        if (TryConsumeLogicalText("|&"))
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
    /// Parses through the conditional grammar's closing ]] with the caller's scan context.
    /// </summary>
    internal bool TryReadConditionalCommand(Token opening, [NotNullWhen(true)] out ConditionalCommand? command)
    {
        if (!ConditionalParser.TryReadCommand(_input, opening.Source.End.Value, _scanContext, TrackUnexpectedTokens,
                out command, out var end, out var error))
        {
            RecordFailure(error);
            return false;
        }
        Position = end;
        if (TrackCommandLocations)
            ConsumedPosition = end;
        return true;
    }

    /// <summary>
    /// Consumes a matching logical token and retains its physical ending position.
    /// </summary>
    private bool TryConsumeLogicalText(string text)
    {
        if (!ShellSourceCursor.TryMatchLogicalText(_input, _scanContext, Position, text, out var end))
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
        ReadExpansionWord(content, syntax, outerQuote, memory: null);

    internal static Word ReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        MemoryCapacity? memory) =>
        ReadExpansionWord(content, syntax, outerQuote, new ShellScanContext { SyntaxMemory = memory });

    internal static Word ReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        ShellScanContext context) =>
        TryReadExpansionWord(content, syntax, outerQuote, context, out var word, out var error) ? word : throw error.ToException();

    internal static bool TryReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error) =>
        TryReadExpansionWord(content, syntax, outerQuote, memory: null, out word, out error);

    internal static bool TryReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        MemoryCapacity? memory, [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error)
        => TryReadExpansionWord(content, syntax, outerQuote, new ShellScanContext { SyntaxMemory = memory }, out word, out error);

    /// <summary>Consumes nested lookahead syntax without resetting its caller's structural allowance.</summary>
    internal static bool TryReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        ShellScanContext context, [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error)
        => TryReadExpansionWord(content, syntax, outerQuote, context, default, out word, out error);

    internal static bool TryReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        ShellScanContext context, ConditionalParser.OperandSpelling spelling,
        [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error)
    {
        var tokenizer = new Tokenizer(content, 0, context) { _isExpansionOperand = true };
        var parsed = tokenizer.ReadWord(content.AsSpan(), delimiterStart: content.Length, syntax, outerQuote, spelling);
        error = tokenizer.Error;
        word = error == null ? parsed : null;
        return error == null;
    }

    /// <summary>Retains direct selectors for the arithmetic consumer to prepare separately.</summary>
    internal static Word ReadArithmeticWord(ShellSource content, ExpansionWordSyntax syntax, ShellScanContext context)
    {
        var tokenizer = new Tokenizer(content, 0, context) { _retainArithmeticSubscripts = true, _isExpansionOperand = true };
        var word = tokenizer.ReadWord(content.AsSpan(), delimiterStart: content.Length, syntax, ShellQuoteKind.None);
        return tokenizer.Error is { } error ? throw error.ToException() : word;
    }

    internal static UnexpectedSyntax? ReadUnexpectedWordToken(ShellSource content, ShellScanContext context)
    {
        var tokenizer = new Tokenizer(content, 0, context) { TrackUnexpectedTokens = true };
        tokenizer.ReadWord(content.AsSpan(), content.Length, ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        return tokenizer.Failure?.UnexpectedToken;
    }

    private bool _retainArithmeticSubscripts;
    private bool _isExpansionOperand;

    /// <summary>Reads one already-delimited word after generated punctuation changes its quote context.</summary>
    internal static Word ReadBraceExpandedWord(ShellSource content, MemoryCapacity memory)
    {
        var tokenizer = new Tokenizer(content, 0,
            new ShellScanContext { SyntaxMemory = memory, LegacyOrigin = LegacySubstitutionOrigin.Temporary }) { _isExpansionOperand = true };
        var word = tokenizer.ReadWord(content.AsSpan(), content.Length, ExpansionWordSyntax.BraceExpanded, ShellQuoteKind.None);
        return tokenizer.Error is { } error
            ? new Word([new BraceExpansionFailureWordPart(ShellValue.FromText(error.Message))])
            : word;
    }

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
        part = WordPart.FromSource(WordPartKind.ArithmeticSubscript, _input.Slice(Position, closing - Position + 1), _scanContext.SyntaxMemory);
        Position = closing + 1;
        return true;
    }

    /// <summary>Reads one word; delimiters before delimiterStart belong to a grouped prefix.</summary>
    private Word ReadWord(ReadOnlySpan<byte> span, int delimiterStart, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote)
        => ReadWord(span, delimiterStart, syntax, outerQuote, default);

    private Word ReadWord(ReadOnlySpan<byte> span, int delimiterStart, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        ConditionalParser.OperandSpelling spelling)
    {
        var parts = new List<WordPart>();
        var unquoted = new WordPartBuffer(_input, _scanContext);
        var quotedOperand = outerQuote == ShellQuoteKind.Double;
        var braceExpanded = syntax == ExpansionWordSyntax.BraceExpanded;
        var arithmetic = syntax is not (ExpansionWordSyntax.Parameter or ExpansionWordSyntax.BraceExpanded
            or ExpansionWordSyntax.ExpandedSelector);
        var dollarQuotes = syntax is not (ExpansionWordSyntax.DecodedArithmetic or ExpansionWordSyntax.ExpandedSelector
            or ExpansionWordSyntax.ExpandedArithmeticSelector);
        var continuesQuote = false;
        var renderedLineAdjustment = 0;
        var subscriptFailure = ParameterSubscriptFailure.None;
        var expandedParameterQuoteSyntax = false;
        var quoteBounds = quotedOperand ? new QuotedWordBounds(Position, delimiterStart) : default;

        void AddPart(WordPart part)
        {
            if (quotedOperand)
            {
                part.DoubleQuote = continuesQuote ? DoubleQuoteBoundary.Continuation : DoubleQuoteBoundary.Start;
                continuesQuote = true;
            }
            parts.Add(part);
        }

        void FlushUnquoted()
        {
            if (unquoted.Length == 0)
            {
                return;
            }

            if (!TryReserveSyntax())
                return;
            var part = unquoted.TakeSource();
            AddPart(new SourceWordPart(
                quotedOperand ? WordPartKind.DoubleQuoted : WordPartKind.Unquoted, part.Source,
                _scanContext.SyntaxMemory)
            {
                IsBraceExpanded = braceExpanded,
                RenderedLineAdjustment = renderedLineAdjustment,
                ParameterSubscriptFailure = subscriptFailure,
                HasExpandedParameterQuoteSyntax = expandedParameterQuoteSyntax,
                SpellingCaptures = part.Captures
            });
            renderedLineAdjustment = 0;
            subscriptFailure = ParameterSubscriptFailure.None;
            expandedParameterQuoteSyntax = false;
        }

        while (Position < span.Length && Error == null)
        {
            if (SkipCollectedHereDocuments())
                continue;
            var current = span[Position];
            if (TryReadArithmeticSubscript(out var subscript))
            {
                FlushUnquoted();
                AddPart(subscript);
                continue;
            }
            if (TryReadBracedParameterExpansion(ref unquoted, quotedOperand, syntax, spelling, ref renderedLineAdjustment,
                    ref subscriptFailure, ref expandedParameterQuoteSyntax, ref quoteBounds))
            {
                continue;
            }

            if (current == (byte)'$' && ArithmeticExpansionScanner.HasPrefix(_input, Position)
                && TryReadArithmeticExpansion(ref unquoted, quotedOperand, ref renderedLineAdjustment))
                continue;

            if (current == (byte)'$' && CommandSubstitutionScanner.HasPrefix(_input, Position)
                || current == (byte)'`' && (!braceExpanded || Position + 1 < span.Length))
            {
                FlushUnquoted();
                if (ReadCommandSubstitutionPart(
                        quotedOperand ? WordPartKind.CommandSubstitutionQuoted : WordPartKind.CommandSubstitution,
                        syntax, spelling) is not { } substitution)
                    return new Word([]);
                AddPart(substitution);
                continue;
            }

            if (Position >= delimiterStart && IsWordDelimiter(current))
            {
                break;
            }

            // Decoded arithmetic source must not reinterpret generated $' sequences.
            if (current == '\'' && !quotedOperand && !arithmetic
                || current == '$' && dollarQuotes
                && ShellSourceCursor.TryMatchLogicalText(span, Position, "$'", out _))
            {
                FlushUnquoted();
                if (!TryReserveSyntax())
                    return new Word([]);
                var literalStart = Position;
                var content = ReadQuotedLiteral();
                if (Error != null)
                    return new Word([]);
                if (current == (byte)'$')
                    spelling.RecordAnsiCLiteral(literalStart, Position, content);
                if (syntax == ExpansionWordSyntax.ArithmeticSource && !quotedOperand)
                {
                    // ((...)) and $((...)) retain the decoded quotes before expanding their contents.
                    var quoted = _scanContext.CreateBoundedByteBuilder(0, ByteMemoryBuilder.MaxMemoryBuilderBytes - 1);
                    if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(quoted, content.Bytes.Span))
                    {
                        RecordError("Arithmetic quoted value exceeds the byte-buffer limit.", Position);
                        return new Word([]);
                    }
                    // Decoding another fragment must not reset the shared syntax/nesting allowance.
                    var decoded = ShellSource.FromBytes(quoted.ToMemory());
                    var tokenizer = new Tokenizer(decoded, 0, _scanContext)
                    {
                        _retainArithmeticSubscripts = _retainArithmeticSubscripts,
                        _isExpansionOperand = true
                    };
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
                    AddPart(WordPart.FromValue(WordPartKind.SingleQuoted, content));
                continue;
            }

            var quoteContentStart = Position + 1;
            // The invariant profile uses the original text of a locale-quoted word.
            if (current == (byte)'"' || current == (byte)'$' && dollarQuotes
                && ShellSourceCursor.TryMatchLogicalText(span, Position, "$\"", out quoteContentStart))
            {
                FlushUnquoted();
                if (current == (byte)'$')
                    spelling.RecordLocalePrefix(Position, quoteContentStart - 1);
                Position = quoteContentStart;
                var quotedParts = ReadDoubleQuotedParts(span, syntax, spelling);
                parts.AddRange(quotedParts);
                continuesQuote = false;
                continue;
            }

            if (current == (byte)'\\')
            {
                // Arithmetic uses double-quote escape rules, but apostrophes remain operand data.
                if (arithmetic
                    && (Position + 1 >= span.Length || span[Position + 1] is not ((byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\' or (byte)'\n')))
                {
                    unquoted.AppendSource(Position, 1);
                    Position++;
                    continue;
                }
                Position++;
                if (Position >= span.Length)
                {
                    if (braceExpanded)
                    {
                        unquoted.AppendSource(Position - 1, 1);
                        break;
                    }
                    RecordError("Unexpected end of input after escape.", Position);
                    return new Word([]);
                }

                var escaped = span[Position];
                if (escaped == (byte)'\n')
                {
                    Position++;
                    _continuedAtEndOfInput |= Position == span.Length;
                    continue;
                }

                // A parameter operand also lets backslash protect its closing brace.
                if (quotedOperand && escaped is not ((byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\' or (byte)'}'))
                {
                    unquoted.AppendSource(Position - 1, 1);
                    continue;
                }

                FlushUnquoted();
                // A supplementary source character must remain one escaped literal.
                var length = Utf8Text.FirstCharacterLength(span[Position..]);
                if (!TryReserveSyntax())
                    return new Word([]);
                AddPart(CopyEscapedLiteral(Position, length));
                Position += length;
                continue;
            }

            unquoted.AppendSource(Position, 1);
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
            AddPart(WordPart.FromText(WordPartKind.DoubleQuoted, string.Empty));
        }

        return new Word(parts);

        ShellValue ReadQuotedLiteral()
        {
            var start = Position;
            var cursor = new ShellSourceCursor(_input, Position, _scanContext);
            var complete = cursor.TryReadQuotedLiteral(out var value, allowUnterminated: braceExpanded);
            Position = cursor.Position;
            if (!complete)
                RecordUnclosedQuote('\'', start, "Unterminated single-quoted string.");
            return value;
        }
    }

    /// <summary>
    /// Captures canonical arithmetic-expansion syntax while retaining physical cursor offsets.
    /// </summary>
    private bool TryReadArithmeticExpansion(ref WordPartBuffer content, bool quoted, ref int renderedLineAdjustment)
    {
        var start = Position;
        if (_scanContext.TryGetCommandSubstitution(_input, start, out _))
            return false;
        var status = ArithmeticExpansionScanner.Scan(
            _scanContext, _input, start, out var endIndex, out var expression, out _, out var bracketed,
            out var adjustment, out var error, trackRenderedLines: true);
        if (status == ShellScanStatus.NotFound)
            return false;
        if (status != ShellScanStatus.Complete)
        {
            RecordExpansionFailure(error, quoted, "Unterminated arithmetic expansion.", start);
            return true;
        }

        Position = endIndex + 1;
        renderedLineAdjustment += adjustment;
        content.AppendExpansion(start, Position, bracketed ? "$["u8 : "$(("u8, expression, bracketed ? "]"u8 : "))"u8);
        return true;
    }

    /// <summary>
    /// Reads a substitution, retaining the tree built by parenthesized delimiter parsing.
    /// </summary>
    private WordPart? ReadCommandSubstitutionPart(WordPartKind kind, ExpansionWordSyntax syntax,
        ConditionalParser.OperandSpelling spelling)
    {
        if (!TryReserveSyntax())
            return null;
        var start = Position;
        var status = CommandSubstitutionScanner.ScanParsed(
                _scanContext,
                _input,
                start,
                out var endIndex,
                out var payload,
                out var error);
        if (status != ShellScanStatus.Complete)
        {
            if (syntax == ExpansionWordSyntax.BraceExpanded && _scanContext.LimitError == null)
            {
                Position = _input.Length;
                var delimiter = _input[start] == (byte)'`' ? (byte)'`' : (byte)')';
                var memory = _scanContext.SyntaxMemory ?? throw new InvalidOperationException("Generated words require an active memory owner.");
                return new BraceExpansionFailureWordPart(
                    BraceExpansionFailureWordPart.FormatMissingDelimiter(_input.Slice(start), delimiter, memory));
            }
            if (status == ShellScanStatus.Incomplete && IsCommandSubstitution && _input[start] == (byte)'`')
                RecordUnclosedQuote('`', start, "Unterminated command substitution.");
            else
                RecordExpansionFailure(error, kind == WordPartKind.CommandSubstitutionQuoted,
                    "Unterminated command substitution.", start);
            return null;
        }

        Position = endIndex + 1;
        var parsed = payload ?? throw new InvalidOperationException("A complete substitution requires executable syntax.");
        if (parsed is CommandSubstitutionSyntax.Parsed { CanRenderBody: true })
            spelling.RecordCapture(start, Position, parsed);
        return new CommandSubstitutionWordPart(kind, parsed);
    }

    /// <summary>
    /// Reads a braced parameter expansion as unexpanded segment bytes.
    /// </summary>
    private bool TryReadBracedParameterExpansion(ref WordPartBuffer content, bool quoted, ExpansionWordSyntax syntax,
        ConditionalParser.OperandSpelling spelling, ref int renderedLineAdjustment, ref ParameterSubscriptFailure subscriptFailure,
        ref bool expandedParameterQuoteSyntax, ref QuotedWordBounds quoteBounds)
    {
        if (!BracedParameterScanner.HasPrefix(_input, Position))
        {
            return false;
        }

        var start = Position;
        // Conditional spelling sees the authored word. Runtime operand readers see bytes from a later phase.
        var quotes = syntax == ExpansionWordSyntax.Parameter && (!_isExpansionOperand || spelling.IsActive)
            ? quoted ? BracedParameterScanner.LexicalQuotes.DoubleQuoted : BracedParameterScanner.LexicalQuotes.Unquoted
            : BracedParameterScanner.LexicalQuotes.Preserve;
        var status = BracedParameterScanner.ScanWithPayload(
            _scanContext,
            _input,
            start,
            out var endIndex,
            out var errorIndex,
            out var payload,
            out var adjustment,
            out var error,
            out var incompleteSubscript,
            trackRenderedLines: true, quotes, resolveLateSubscripts: false,
            out var hasSpellingEdits,
            out var expandedQuotes);
        if (status == BracedParameterScanStatus.Found)
        {
            if (incompleteSubscript is ParameterSubscriptFailure.Direct or ParameterSubscriptFailure.Nested
                && quoteBounds.IsActive)
            {
                if (quoteBounds.End == 0)
                    quoteBounds.End = ShellSourceCursor.FindDoubleQuoteEnd(_scanContext, _input, endIndex + 1);
                if (quoteBounds.End >= endIndex + 1)
                {
                    var source = _input.Slice(quoteBounds.Start, quoteBounds.End - quoteBounds.Start);
                    var failure = _scanContext.SubstitutionFailure;
                    if (BracedParameterScanner.ScanWithPayload(_scanContext, source, start - quoteBounds.Start,
                            out var relativeEnd, out _, out var complete, out var completeAdjustment, out _, out var completeness,
                            trackRenderedLines: true, quotes, resolveLateSubscripts: true,
                            out var completeEdits, out var completeQuotes) == BracedParameterScanStatus.Found
                        && completeness == ParameterSubscriptFailure.None && quoteBounds.Start + relativeEnd >= endIndex)
                    {
                        endIndex = quoteBounds.Start + relativeEnd;
                        payload = complete;
                        adjustment = completeAdjustment;
                        hasSpellingEdits = completeEdits;
                        expandedQuotes = completeQuotes;
                        incompleteSubscript = ParameterSubscriptFailure.None;
                    }
                    else
                        _scanContext.SubstitutionFailure = failure;
                }
            }
            content.AppendParameter(start, endIndex + 1, payload);
            if (hasSpellingEdits)
            {
                spelling.RecordParameter(start, endIndex + 1, payload);
                expandedParameterQuoteSyntax |= expandedQuotes;
            }
            renderedLineAdjustment += adjustment;
            if (quoted && subscriptFailure == ParameterSubscriptFailure.None
                && incompleteSubscript is ParameterSubscriptFailure.Direct or ParameterSubscriptFailure.Nested)
                subscriptFailure = incompleteSubscript;
            Position = endIndex + 1;
            return true;
        }

        if (error != null)
            RecordFailure(error.InDoubleQuotes(quoted));
        else if (status == BracedParameterScanStatus.UnterminatedCommandSubstitution)
        {
            RecordExpansionFailure(null, quoted, "Unterminated command substitution.", errorIndex);
        }
        else if (status == BracedParameterScanStatus.Unterminated && !_isExpansionOperand)
        {
            RecordExpansionFailure(null, quoted, quoted ? "Unterminated double-quoted string." : "Unterminated parameter expansion.", errorIndex);
        }

        return false;
    }

    /// <summary>
    /// Reads double-quoted text, preserving escaped-character provenance.
    /// </summary>
    private List<WordPart> ReadDoubleQuotedParts(ReadOnlySpan<byte> span, ExpansionWordSyntax syntax,
        ConditionalParser.OperandSpelling spelling)
    {
        var quoteStart = Position - 1;
        var quoteBounds = new QuotedWordBounds(Position, 0);
        var braceExpanded = syntax == ExpansionWordSyntax.BraceExpanded;
        var parts = new List<WordPart>();
        var renderedLineAdjustment = 0;
        var subscriptFailure = ParameterSubscriptFailure.None;
        var expandedParameterQuoteSyntax = false;
        var content = new WordPartBuffer(_input, _scanContext);

        void AddPart(WordPart part)
        {
            part.DoubleQuote = parts.Count == 0 ? DoubleQuoteBoundary.Start : DoubleQuoteBoundary.Continuation;
            parts.Add(part);
        }

        void Flush(bool allowEmpty)
        {
            if (content.Length == 0)
            {
                if (allowEmpty && parts.Count == 0)
                {
                    if (!TryReserveSyntax())
                        return;
                    AddPart(WordPart.FromText(WordPartKind.DoubleQuoted, string.Empty));
                }

                return;
            }

            if (!TryReserveSyntax())
                return;
            var part = content.TakeSource();
            AddPart(new SourceWordPart(WordPartKind.DoubleQuoted, part.Source, _scanContext.SyntaxMemory)
            {
                RenderedLineAdjustment = renderedLineAdjustment,
                ParameterSubscriptFailure = subscriptFailure,
                HasExpandedParameterQuoteSyntax = expandedParameterQuoteSyntax,
                SpellingCaptures = part.Captures
            });
            renderedLineAdjustment = 0;
            subscriptFailure = ParameterSubscriptFailure.None;
            expandedParameterQuoteSyntax = false;
        }

        while (Position < span.Length && Error == null)
        {
            if (SkipCollectedHereDocuments())
                continue;
            var current = span[Position];
            if (TryReadArithmeticSubscript(out var subscript))
            {
                Flush(allowEmpty: false);
                AddPart(subscript);
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
                    if (braceExpanded)
                    {
                        content.AppendSource(Position, 1);
                        Position++;
                        break;
                    }
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
                    AddPart(CopyEscapedLiteral(Position + 1, 1));
                    Position += 2;
                    continue;
                }

                content.AppendSource(Position, 1);
                Position++;
                continue;
            }

            if (TryReadBracedParameterExpansion(ref content, quoted: true, syntax, spelling, ref renderedLineAdjustment,
                    ref subscriptFailure, ref expandedParameterQuoteSyntax, ref quoteBounds))
            {
                continue;
            }

            if (current == (byte)'$' && ArithmeticExpansionScanner.HasPrefix(_input, Position)
                && TryReadArithmeticExpansion(ref content, quoted: true, ref renderedLineAdjustment))
                continue;

            if (current == (byte)'$' && CommandSubstitutionScanner.HasPrefix(_input, Position)
                || current == (byte)'`' && (!braceExpanded || Position + 1 < span.Length))
            {
                Flush(allowEmpty: false);
                if (ReadCommandSubstitutionPart(WordPartKind.CommandSubstitutionQuoted, syntax, spelling) is not { } substitution)
                    return parts;
                AddPart(substitution);
                continue;
            }

            content.AppendSource(Position, 1);
            Position++;
        }

        if (Error != null)
            return parts;

        // The complete source word was already validated before generating new quote syntax.
        if (syntax is ExpansionWordSyntax.DecodedArithmetic or ExpansionWordSyntax.BraceExpanded)
        {
            Flush(allowEmpty: true);
            return parts;
        }
        RecordUnclosedQuote('"', quoteStart, "Unterminated double-quoted string.");
        return parts;
    }

    private struct QuotedWordBounds(int start, int end)
    {
        internal bool IsActive { get; } = true;
        internal int Start { get; } = start;
        internal int End { get; set; } = end;
    }

    /// <summary>Tracks a contiguous source part until an edit requires independent assembly.</summary>
    private struct WordPartBuffer(ShellSource source, ShellScanContext context)
    {
        private ByteMemoryBuilder? _edited;
        private int _start;
        private int _length;
        private List<SpellingCapture>? _captures;

        public readonly int Length => _edited?.Length ?? _length;

        public void AppendSource(int start, int length)
        {
            if (_edited != null)
                _edited.Append(source.AsSpan(start, length));
            else if (_length == 0)
            {
                _start = start;
                _length = length;
            }
            else if (_start + _length == start)
                _length += length;
            else
                GetEdited().Append(source.AsSpan(start, length));
        }

        public void AppendExpansion(int start, int end, ReadOnlySpan<byte> opening, ShellSource payload, ReadOnlySpan<byte> closing)
        {
            // The scanner already recognized the logical delimiters. Their exact
            // widths and the physical payload slice identify an unedited spelling.
            if (end - start == opening.Length + payload.Length + closing.Length
                && source.Bytes.Slice(start + opening.Length, payload.Length).Equals(payload.Bytes))
            {
                AppendSource(start, end - start);
                return;
            }
            GetEdited().Append(opening).Append(payload.Bytes).Append(closing);
        }

        public void AppendExpansion(int start, int end, ReadOnlySpan<byte> opening, SourceSpelling payload, ReadOnlySpan<byte> closing)
        {
            payload.CopyCaptures(Length + opening.Length, ref _captures, context.SyntaxMemory);
            AppendExpansion(start, end, opening, payload.Source, closing);
        }

        public void AppendParameter(int start, int end, SourceSpelling payload) =>
            AppendExpansion(start, end, "${"u8, payload, "}"u8);

        public SourceSpelling TakeSource()
        {
            var memory = context.SyntaxMemory;
            ReadOnlyMemory<byte> bytes;
            if (_edited != null)
                bytes = _edited.ToCompactMemory(memory);
            else
            {
                // Values can outlive the source unit. Keep a compact owned part.
                memory?.Grow(MemoryCapacity.ArrayBackingBytes<byte>(_length));
                bytes = source.AsSpan(_start, _length).ToArray();
            }
            _edited = null;
            _length = 0;
            var part = new SourceSpelling(ShellSource.FromBytes(bytes), _captures);
            _captures = null;
            return part;
        }

        private ByteMemoryBuilder GetEdited()
        {
            if (_edited == null)
            {
                _edited = context.CreateByteBuilder(0);
                _edited.Append(source.AsSpan(_start, _length));
            }
            return _edited;
        }
    }

    // Like quoted literals, escaped characters can survive the dynamic source that produced them.
    // Copy at most one source character so retained values do not pin the complete input buffer.
    private WordPart CopyEscapedLiteral(int offset, int length) =>
        WordPart.FromValue(WordPartKind.Escaped, ShellValue.FromBytes(_input.Bytes.Slice(offset, length).ToArray()));

    /// <summary>
    /// Returns true when an ASCII byte terminates a word; CR is ordinary data.
    /// </summary>
    internal static bool IsWordDelimiter(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)';' or (byte)'&' or (byte)'|' or (byte)'<' or (byte)'>' or (byte)'(' or (byte)')';

    private static bool IsWordBoundary(ReadOnlySpan<byte> span, int position)
    {
        position = ShellSourceCursor.SkipLineContinuations(span, position);
        return position >= span.Length || IsWordDelimiter(span[position]);
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
    BraceExpanded,
    // Array selectors and substring bounds remove ANSI-C quotes.
    Arithmetic,
    // Arithmetic commands/expansions retain ANSI-C quotes after decoding their payload.
    ArithmeticSource,
    DecodedArithmetic,
    // Expansion of a variable-test selector after the original word was read.
    ExpandedSelector,
    ExpandedArithmeticSelector,
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

    internal CaseContinuation CaseContinuation { get; private init; }

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

    public static Token CaseTerminator(CaseContinuation continuation) =>
        new Token(TokenKind.CaseTerminator, null, null, null, null, ShellSource.Empty) { CaseContinuation = continuation };
}

/// <summary>
/// Represents a word token with a guaranteed non-null word value.
/// </summary>
internal sealed class WordToken(Token token, Word word)
{
    public Token Token { get; } = token;

    public Word Word { get; } = word;
}

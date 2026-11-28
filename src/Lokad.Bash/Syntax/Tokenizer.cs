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
    private List<RedirectedCommand>? _compoundHereDocuments;
    private bool _continuedAtEndOfInput;
    private bool _spacingContinuedAtEndOfInput;
    private bool _wordEndsWithUnframedBackslash;
    private bool _redirectionLookaheadAtEndOfInput;
    private bool _emptyPipelinePrefixContinuedAtEndOfInput;
    private int _wordLookaheadEnd = -1;
    private int _lastPhysicalNewline;
    private int _functionBraceLine;
    private int _arithmeticFailureStart = -1;

    internal bool TrackCommandLocations { get; init; }
    internal bool TrackUnexpectedTokens { get; init; }
    internal bool AllowEofHereDocuments { get; init; }
    internal bool IsCommandSubstitution { get; init; }
    internal IReadOnlyList<Range>? OmittedSeparators { get; init; }
    internal IReadOnlyList<SourceLineAdjustment>? LineAdjustments { get; init; }
    internal CommandLocations? Locations { get; private set; }
    internal int ConsumedPosition { get; private set; }
    private int FunctionBraceLine => !IsCommandSubstitution && OmittedSeparators == null && _scanContext.CanReadAhead(_input)
        ? _scanContext.FunctionBraceLine : _functionBraceLine;

    internal ShellScanContext ScanContext => _scanContext;

    internal void BeginCommandUnit()
    {
        _arraySubscripts = null;
        Locations = null;
        _functionBraceLine = 0;
        _scanContext.FunctionBraceLine = 0;
        _scanContext.ClearCommandUnitLookahead();
    }

    internal int ArrayRecoveryStart(int offset)
    {
        var next = HereDocumentReadAhead.FindNextLineStart(_input, offset);
        return _scanContext.HereDocumentReadAhead?.SkipBodies(_input, next) ?? next;
    }

    // Discard the failed unit's tokens and pending documents while keeping absolute line coordinates.
    internal void ResumeCommandUnits(int position)
    {
        Position = ConsumedPosition = position;
        _lastPhysicalNewline = position - 1;
        _pushback.Clear();
        HereDocuments = null;
        _failure = null;
        QuotedEndOfInputFailure = null;
        _readingAhead = null;
        _wordLookaheadEnd = -1;
        _arithmeticFailureStart = -1;
        _arithmeticFallbackAtFileEnd = false;
        _continuedAtEndOfInput = false;
        _spacingContinuedAtEndOfInput = false;
        _wordEndsWithUnframedBackslash = false;
        _redirectionLookaheadAtEndOfInput = false;
        _emptyPipelinePrefixContinuedAtEndOfInput = false;
    }

    internal void RecordFunctionBrace(int offset)
    {
        _functionBraceLine = GetLine(offset);
        // Delimiter parsing reads physical function braces in the enclosing source. Layout
        // replay has its own coordinates and leaves that physical read boundary alone.
        if (OmittedSeparators == null && LineAdjustments == null && _scanContext.CanReadAhead(_input))
            _scanContext.FunctionBraceLine = IsCommandSubstitution ? _scanContext.GetScriptDiagnosticLine(offset) : _functionBraceLine;
    }

    internal int TakeFunctionBraceLine()
    {
        var line = FunctionBraceLine;
        _functionBraceLine = 0;
        _scanContext.FunctionBraceLine = 0;
        return line;
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

    internal void RecordLocation(ICommand command, int startLine, int commandLine) =>
        RecordLocation(command, startLine, commandLine, 0, 0, 0);

    internal void RecordLocation(ICommand command, int startLine, int commandLine, int redirectionDiagnosticLine,
        int redirectionOpenLine, int lastWordLine)
    {
        if (!TrackCommandLocations)
            return;
        (Locations ??= new CommandLocations()).Add(command,
            new CommandLocation(startLine, commandLine, GetLine(ConsumedPosition))
            {
                RedirectionDiagnosticLine = redirectionDiagnosticLine,
                RedirectionOpenLine = redirectionOpenLine,
                FunctionBraceLine = command is FunctionDefinition ? FunctionBraceLine : 0,
                LastWordLine = lastWordLine
            }, _scanContext.SyntaxMemory);
    }

    internal void RecordReadDiagnostics(ICommand command, CommandReadDiagnostics diagnostics) =>
        (Locations ??= new CommandLocations()).SetReadDiagnostics(command, diagnostics, _scanContext.SyntaxMemory);

    internal int CompletedUnitReadLine => GetLine(Math.Max(0, Position - 1));

    internal void RecordUnitReadLine(ICommand command)
    {
        if (Locations is not { } locations)
            return;
        var anchor = CommandLocations.UnitLocationAnchor(command);
        if (locations.TryGet(anchor, out var location) && location.UnitReadLine == 0)
            locations.Update(anchor, location with { UnitReadLine = CompletedUnitReadLine });
    }

    internal void RecordRedirectedCommandLocation(RedirectedCommand command, int startLine)
    {
        if (!TrackCommandLocations)
            return;
        var openLine = 0;
        if (command.Body is ArithmeticCommand or ConditionalCommand or SubshellCommand or ArithmeticForCommand
            && Locations?.TryGet(command.Body, out var body) == true)
            openLine = command.Body is ArithmeticForCommand ? body.CommandLine : body.EndLine;
        RecordLocation(command, startLine, startLine, 0, openLine, 0);
        if (command.Body is SubshellCommand
            || !command.Redirections.Any(redirection => redirection.Kind is RedirectionKind.HereDocument or RedirectionKind.HereDocumentStripTabs))
            return;
        if (_compoundHereDocuments == null)
        {
            _scanContext.SyntaxMemory?.Grow(32);
            _compoundHereDocuments = [];
        }
        _scanContext.SyntaxMemory?.PrepareListCapacity(_compoundHereDocuments, 1);
        _compoundHereDocuments.Add(command);
    }

    internal void CompleteCompoundHereDocumentLocations()
    {
        if (_compoundHereDocuments == null || Locations == null)
            return;
        // Position follows the last byte consumed while reading the command unit.
        var line = CompletedUnitReadLine;
        foreach (var command in _compoundHereDocuments)
        {
            if (Locations.TryGet(command, out var location))
                Locations.Update(command, location with { CompoundHereDocumentLine = line });
        }
        _compoundHereDocuments = null;
    }

    internal int GetHereDocumentWarningLine(int position) => _readingAhead?.GetLine(position) ?? GetLine(position);

    internal void RecordCaptureDocumentRead(int start)
    {
        if (IsCommandSubstitution)
            _scanContext.RecordEchoedCaptureDocument(_input, start, Position);
    }

    internal int GetScriptReadOffset() =>
        _scanContext.ScriptInput.TryGetViewOffset(_input, out var offset) ? offset + Position : 0;

    internal int HereDocumentHeaderLineAdvance
    {
        get
        {
            if (_readingAhead != null || Position != _input.Length)
                return 0;
            if (_wordEndsWithUnframedBackslash)
                return 2;
            return _continuedAtEndOfInput || _spacingContinuedAtEndOfInput ? 1 : 0;
        }
    }

    internal int GetRedirectionLookaheadLine(Token token)
    {
        var end = token.Source.End.Value;
        if (PeeksPastShellOperator(token))
            end = ShellSourceCursor.SkipLineContinuations(_input.AsSpan(), end);
        return GetLine(end);
    }

    internal int GetCommandWordLine(int offset, bool readEndOfInput = false)
    {
        var line = GetLine(offset);
        if (offset != _input.Length)
            return line;
        // Reading a terminal backslash in a word reaches the continued line.
        // Looking past that word for a function opener reaches EOF as well.
        if (_wordEndsWithUnframedBackslash)
            return line + (readEndOfInput ? EndOfInputLineAdvance : 1);
        if (_continuedAtEndOfInput)
        {
            // A buffered final line has a terminating LF in the reader even if
            // its physical bytes ended at the here-document delimiter.
            var finalLine = _input.AsSpan()[^1] == (byte)'\n' ? 0 : 1;
            return line + finalLine + (readEndOfInput ? 1 : 0);
        }
        if (_spacingContinuedAtEndOfInput && _input.AsSpan()[^1] == (byte)'\\')
            return line + 1;
        return line;
    }

    internal int GetCommandTerminatorLine(Token token)
    {
        if (token.Kind != TokenKind.Newline)
            return GetCommandWordLine(token.Source.End.Value, readEndOfInput: token.Kind == TokenKind.EndOfInput);
        // The zero-width newline after read-ahead uses the original header's reader line.
        var offset = token.Source.Start.Equals(token.Source.End) ? _lastPhysicalNewline : token.Source.Start.Value;
        return GetLine(offset);
    }

    internal void RecordEmptyPipelinePrefixEndOfInput() =>
        _emptyPipelinePrefixContinuedAtEndOfInput = _continuedAtEndOfInput || _spacingContinuedAtEndOfInput;

    /// <summary>Extra diagnostic lines reached past the last physical input line.</summary>
    internal int EndOfInputLineAdvance
    {
        get
        {
            var prefixAdvance = _emptyPipelinePrefixContinuedAtEndOfInput ? 1 : 0;
            // Reading the end of a word reaches the continued line and then EOF.
            if (_wordEndsWithUnframedBackslash)
                return 2 + prefixAdvance;
            var source = _input.AsSpan();
            var advance = source.IsEmpty || source[^1] != (byte)'\n' || _continuedAtEndOfInput ? 1 : 0;
            if (_arithmeticFallbackAtFileEnd)
                advance++;
            if (_redirectionLookaheadAtEndOfInput)
                advance++;
            return advance + prefixAdvance;
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
        _scanContext.WarnPendingCaptureDocuments(pending.PendingCount, _scanContext.GetScriptDiagnosticLine(afterCloser), start);
        _readingAhead = readAhead;
        Position = start;
        try
        {
            pending.TryReadBodies(this, out var lineFeeds);
            readAhead.Add(afterCloser, start, Position, lineFeeds, _scanContext.SyntaxMemory);
            if (pending.Warnings is { } warnings)
                _scanContext.AppendCaptureWarnings(warnings);
        }
        finally
        {
            Position = afterCloser;
            _readingAhead = null;
        }
    }

    /// <summary>Collects the inner bodies and reports whether only trailing LFs follow the header.</summary>
    internal bool ReadHereDocumentExpansionCaptureBodies(HereDocumentReader pending, out Range read)
    {
        Position = HereDocumentReadAhead.FindNextLineStart(_input, Position);
        var bodyStart = Position;
        pending.TryReadBodies(this);
        read = bodyStart..Position;
        return _input.AsSpan(bodyStart, Position - bodyStart).TrimEnd((byte)'\n').IsEmpty;
    }

    private ParserFailure? _failure;
    private bool _arithmeticFallbackAtFileEnd;

    /// <summary>The first failure; later reads stop without replacing its diagnostic.</summary>
    internal ParserFailure? Failure => _failure ?? (_scanContext.LimitError is { } limit
        ? new ParserFailure(limit, ParserFailureKind.Limit) : null);
    internal SyntaxError? Error => _failure?.Error ?? _scanContext.LimitError;

    /// <summary>Allows the streaming parser to finish the grammar before a failed word.</summary>
    internal bool EndInputOnQuotedSubstitutionFailure { get; init; }
    internal ParserFailure? QuotedEndOfInputFailure { get; private set; }

    internal void RecordError(string message, int offset) => RecordError(new SyntaxError(message, offset));

    internal void RecordError(SyntaxError error) => RecordFailure(new ParserFailure(error, ParserFailureKind.Grammar));

    internal void RecordUnexpectedToken(string message)
    {
        if (Error != null)
            return;
        var token = PeekTokenCore();
        RecordUnexpectedToken(message, token, Position);
    }

    internal void RecordUnexpectedToken(string message, Token token, int errorOffset)
        => RecordUnexpectedToken(message, token, errorOffset, allowsImplicitNewline: false);

    /// <summary>Records a failure where the grammar has not skipped the final line break.</summary>
    internal void RecordUnexpectedTokenBeforeNewline(string message, Token token) =>
        RecordUnexpectedToken(message, token, Position, allowsImplicitNewline: true);

    /// <summary>Reports the unexpected token read while checking for a function header's closing parenthesis.</summary>
    internal void RecordUnexpectedFunctionHeaderToken(string message)
    {
        // Keep parsed substitutions available for the diagnostic's source spelling.
        var retainLookahead = _scanContext.RetainCaptureLookahead;
        _scanContext.RetainCaptureLookahead = true;
        try
        {
            var token = NextTokenCore();
            if (token.Kind != TokenKind.ArithmeticCommand)
            {
                RecordUnexpectedTokenBeforeNewline(message, token);
                return;
            }

            var expression = ReadArithmeticDiagnosticExpression(token);
            var end = token.Source.End.Value;
            if (expression.AsSpan().IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0)
            {
                using var quoted = _scanContext.CreateByteBuilder(0);
                if (!ShellArgumentEscaping.TryAppendArithmeticDiagnosticToken(quoted, expression.AsSpan()))
                    throw new ByteMemoryBuilder.LimitExceededException();
                expression = ShellSource.FromBytes(quoted.ToMemory());
            }
            RecordFailure(new ParserFailure(new SyntaxError(message, Position), ParserFailureKind.Grammar)
            {
                UnexpectedToken = new UnexpectedSyntax(expression, _input.GetPhysicalLine(end, out _), end)
            });
        }
        finally { _scanContext.RetainCaptureLookahead = retainLookahead; }
    }

    internal void RecordArithmeticForHeaderFailure(Token token, string message, bool hasExtraSeparator)
    {
        var start = token.Source.Start.Value;
        RecordFailure(new ParserFailure(new SyntaxError(message, start), ParserFailureKind.ArithmeticForHeader)
        {
            ArithmeticForHeader = TrackUnexpectedTokens
                ? new ArithmeticForHeaderError(ReadArithmeticDiagnosticExpression(token),
                    SecondArithmeticOpener(start).Source.Start.Value, ConsumedPosition, hasExtraSeparator)
                : null
        });
    }

    internal (UnclosedDelimiter Delimiter, int Line)? ReadQuotedRecoveryDelimiter(int bodyEnd)
    {
        // The reader discards tokens after the loop body through the next unquoted newline.
        // A quote that closed the original word can now open a new one.
        using var memory = _scanContext.SyntaxMemory is { } syntax ? new MemoryCapacity(syntax.Budget, 0) : null;
        var context = new ShellScanContext
        {
            LegacyOrigin = _scanContext.LegacyOrigin,
            ReadsCommandString = _scanContext.ReadsCommandString,
            ScriptInput = _input,
            SyntaxMemory = memory
        };
        var suffix = new Tokenizer(_input, bodyEnd, context)
        {
            AllowEofHereDocuments = AllowEofHereDocuments,
            TrackCommandLocations = true,
            TrackUnexpectedTokens = true
        };
        while (suffix.NextTokenCore().Kind is not (TokenKind.Newline or TokenKind.EndOfInput)) { }
        return suffix.Failure?.UnclosedDelimiter is { } delimiter
            ? (delimiter, GetLine(delimiter.ReadPosition) + delimiter.LineAdvance)
            : null;
    }

    private ShellSource ReadArithmeticDiagnosticExpression(Token token)
    {
        var expression = token.CommandExpression;
        if (expression.AsSpan().IndexOfAny((byte)'$', (byte)'`') < 0)
            return expression;

        var start = SecondArithmeticOpener(token.Source.Start.Value).Source.End.Value;
        var spelling = ShellSourceCursor.ReadAdjacentCaptureSpelling(_scanContext, _input, start, token.Source.End.Value - 2,
            _scanContext.SyntaxMemory, prepareReplay: false, out _, out _, out _);
        if (spelling.Captures == null)
            return spelling.Source;

        using var rendered = _scanContext.CreateByteBuilder(0);
        if (!FunctionDefinitionFormatter.TryAppendDiagnosticSpelling(spelling, rendered, _scanContext.SyntaxMemory?.Budget))
            throw new ByteMemoryBuilder.LimitExceededException();
        return ShellSource.FromBytes(rendered.ToMemory());
    }

    /// <summary>Selects the local opener diagnostic when a failed arithmetic scan occurred in a word position.</summary>
    internal bool RejectArithmeticCommandInWordPosition() => RejectArithmeticCommandInWordPosition(ParserFailureKind.Grammar);

    private bool RejectArithmeticCommandInWordPosition(ParserFailureKind kind)
    {
        if (!TrackUnexpectedTokens || _arithmeticFailureStart < 0
            || (_failure ?? QuotedEndOfInputFailure) is not { Kind: not ParserFailureKind.Limit } failure)
            return false;

        _failure = new ParserFailure(failure.Error, kind)
        {
            UnexpectedToken = GetUnexpectedSyntax(SecondArithmeticOpener(_arithmeticFailureStart))
        };
        _arithmeticFailureStart = -1;
        QuotedEndOfInputFailure = null;
        return true;
    }

    private Token SecondArithmeticOpener(int start)
    {
        var opener = ShellSourceCursor.SkipLineContinuations(_input.AsSpan(), start + 1);
        return Token.OpenParen() with { Source = opener..(opener + 1) };
    }

    private void RecordUnexpectedToken(string message, Token token, int errorOffset, bool allowsImplicitNewline)
    {
        if (Error != null)
            return;
        var atEnd = token.Kind == TokenKind.EndOfInput;
        var reachedEnd = atEnd && Position == _input.Length && QuotedEndOfInputFailure == null;
        var implicitNewline = allowsImplicitNewline && reachedEnd && !_spacingContinuedAtEndOfInput && !_continuedAtEndOfInput
            && !_input.IsEmpty && _input[Position - 1] != (byte)'\n';
        UnexpectedSyntax? unexpected = null;
        if (TrackUnexpectedTokens)
        {
            if (!atEnd)
                unexpected = GetUnexpectedSyntax(token);
            else if (implicitNewline)
                unexpected = GetSyntheticNewlineSyntax();
        }
        RecordFailure(new ParserFailure(new SyntaxError(message, errorOffset), ParserFailureKind.Grammar)
        {
            UnexpectedEndOfInput = reachedEnd && !implicitNewline,
            UnexpectedToken = unexpected
        });
    }

    internal UnexpectedSyntax GetUnexpectedSyntax(Token token)
    {
        if (token.Kind == TokenKind.ArithmeticCommand)
            token = SecondArithmeticOpener(token.Source.Start.Value);
        if (token.Kind == TokenKind.Newline)
        {
            // Here-document collection can move the reader past the token's physical line.
            var read = Position > _lastPhysicalNewline && _input[Position - 1] == (byte)'\n' ? Position - 1 : Position;
            return new UnexpectedSyntax(UnexpectedSyntax.NewlineToken, _input.GetPhysicalLine(_lastPhysicalNewline, out _), read);
        }
        var (start, end) = (token.Source.Start.Value, token.Source.End.Value);
        return new UnexpectedSyntax(_input.Slice(start, end - start), _input.GetPhysicalLine(end, out _), end);
    }

    private UnexpectedSyntax GetSyntheticNewlineSyntax() =>
        new(UnexpectedSyntax.NewlineToken, _input.GetPhysicalLine(Position, out _), Position);

    internal void RecordMissingRedirectionOperand(Token redirection, Token operand)
    {
        RejectArithmeticCommandInWordPosition();
        if (Error != null)
            return;
        var continuedToEnd = operand.Kind == TokenKind.EndOfInput && Position == _input.Length && _spacingContinuedAtEndOfInput;
        _redirectionLookaheadAtEndOfInput = continuedToEnd && WordLookaheadReachedEnd();
        UnexpectedSyntax? unexpected = null;
        if (TrackUnexpectedTokens)
        {
            if (operand.Kind != TokenKind.EndOfInput)
                unexpected = GetUnexpectedSyntax(operand);
            else if (!_spacingContinuedAtEndOfInput)
                unexpected = GetSyntheticNewlineSyntax();
        }
        RecordFailure(new ParserFailure(new SyntaxError("Expected a word after redirection operator.", Position),
            ParserFailureKind.RedirectionOperand)
        {
            UnexpectedToken = unexpected,
            UnexpectedEndOfInput = continuedToEnd
        });

        bool WordLookaheadReachedEnd()
        {
            if (redirection.RedirectionKind is not (RedirectionKind.Input or RedirectionKind.Output) || _wordLookaheadEnd < 0)
                return false;
            var source = _input.AsSpan();
            if (ShellSourceCursor.SkipLineContinuations(source, _wordLookaheadEnd) != redirection.Source.Start.Value)
                return false;
            // Adjacent word and operator lookahead both reach the continued EOF.
            var end = ShellSourceCursor.SkipLineContinuations(source, redirection.Source.End.Value);
            return end == source.Length || end == source.Length - 1 && source[end] == (byte)'\\';
        }
    }

    internal void RecordWordLookahead() => _wordLookaheadEnd = Position;

    internal void RecordDescriptorCloseOperand() => _wordLookaheadEnd = -1;

    internal void RecordFailure(ParserFailure failure)
    {
        _failure ??= _scanContext.ParsingLimitExceeded
            ? new ParserFailure(_scanContext.LimitError ?? failure.Error, ParserFailureKind.Limit) : failure;
        if (_scanContext.ParsingLimitExceeded)
            _scanContext.RecordLimitError(_failure.Error);
    }

    internal void MarkCompoundArrayFailure()
    {
        RejectArithmeticCommandInWordPosition(ParserFailureKind.ArrayDelimiter);
        if (_failure is { Kind: ParserFailureKind.Delimiter, UnclosedDelimiter: not null } missingCloser)
            _failure = missingCloser with { Kind = ParserFailureKind.ArrayDelimiter };
        if ((_failure ?? QuotedEndOfInputFailure) is { Kind: ParserFailureKind.QuotedSubstitutionForHeader } failure)
        {
            _failure = failure with { Kind = ParserFailureKind.ArrayQuotedSubstitutionForHeader };
            QuotedEndOfInputFailure = null;
        }
    }

    /// <summary>Recognizes an array opener and keeps adjacent incomplete arithmetic in the initializer grammar.</summary>
    internal bool HasArrayAssignmentOpener(Token assignment)
    {
        var opener = ShellSourceCursor.SkipLineContinuations(_input.AsSpan(), assignment.Source.End.Value);
        // An adjacent parenthesis starts the initializer. Leave any following parenthesis
        // to the item reader instead of looking ahead for the end of an arithmetic command.
        var arrayOpener = TracksScriptDiagnostics && opener < _input.Length && _input[opener] == (byte)'(';
        if (PeekTokenCore(arrayOpener ? ArrayTokenRole.Opener : ArrayTokenRole.None).Kind == TokenKind.OpenParen)
            return true;
        if (_arithmeticFailureStart >= 0
            && ShellSourceCursor.SkipLineContinuations(_input.AsSpan(), assignment.Source.End.Value) == _arithmeticFailureStart)
            MarkCompoundArrayFailure();
        return false;
    }

    internal void RecordUnexpectedArrayToken(Token token)
    {
        const string message = "Unexpected token in array assignment.";
        if (!TracksScriptDiagnostics)
        {
            RecordError(message, Position);
            return;
        }
        RecordFailure(new ParserFailure(new SyntaxError(message, Position), ParserFailureKind.ArrayDelimiter)
        {
            UnexpectedToken = GetUnexpectedSyntax(token)
        });
    }

    private void RecordExpansionFailure(ParserFailure? failure, bool quoted, string missingDelimiter, int start) =>
        RecordFailure(failure?.InDoubleQuotes(quoted)
            ?? new ParserFailure(new SyntaxError(missingDelimiter, start), ParserFailureKind.Delimiter));

    // Keep the public parser's diagnostic stable while the script reader retains the matching delimiter.
    private void RecordUnterminatedExpansion(ParserFailure? failure, string message, int offset)
    {
        var error = new SyntaxError(message, offset);
        RecordFailure((TracksScriptDiagnostics || _scanContext.RetainCaptureEofDelimiter) && failure != null
            ? failure with { Error = error } : new ParserFailure(error, ParserFailureKind.Delimiter));
    }

    private bool TracksScriptDiagnostics => !IsCommandSubstitution && TrackCommandLocations && TrackUnexpectedTokens;

    private void RecordUnclosedDelimiter(char delimiter, int start, string message, int offset)
    {
        var inCapture = IsCommandSubstitution && AllowEofHereDocuments;
        if (inCapture || TracksScriptDiagnostics || _scanContext.RetainCaptureEofDelimiter)
        {
            RecordFailure(new ParserFailure(new SyntaxError(message, offset), ParserFailureKind.Delimiter)
            {
                UnclosedDelimiter = new UnclosedDelimiter(delimiter, start)
                {
                    AtOpeningDelimiter = true,
                    PayloadLine = _scanContext.LegacyOrigin == LegacySubstitutionOrigin.HereDocument ? GetLine(start) : 0
                }
            });
        }
        else
            RecordError(message, offset);
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

    /// <summary>A parsed subshell whose final bare pipeline prefix needs a separator before its closer.</summary>
    internal bool HasSubshellEndingInBarePipelinePrefix { get; set; }

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
    internal bool IsFollowedByFunctionParentheses(Token token)
    {
        var source = _input.AsSpan();
        var opening = ShellSourceCursor.SkipTokenSpacing(source, token.Source.End.Value);
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
    /// Reads an array assignment. Plain assignments can group delimiters inside
    /// the subscript. Declaration operands are limited to the word already read.
    /// </summary>
    internal bool TryReadArrayAssignment(WordToken token, bool groupSubscript, out AssignmentWord assignment)
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
        int closing;
        if (groupSubscript)
        {
            _arraySubscripts ??= new ArraySubscriptScanner.Lookahead(_input, _scanContext);
            var status = _arraySubscripts.ScanAssignment(position, out closing, out var failure);
            if (status != ShellScanStatus.Complete)
            {
                if (failure != null)
                {
                    // Nested delimiter failures keep their reader location.
                    if (failure.UnclosedDelimiter is { } delimiter)
                        failure = failure with { UnclosedDelimiter = delimiter with { IncludeScriptLine = true } };
                    RecordFailure(failure);
                }
                else if (status == ShellScanStatus.Incomplete)
                    RecordUnclosedDelimiter(']', position, "Unterminated array subscript.", _input.Length);
                return false;
            }
        }
        else if (!ArraySubscriptScanner.TryScanWord(_scanContext, _input, position, token.Token.Source.End.Value, out closing))
            return false;

        if (!TryReadSubscriptAssignment(token, position, closing, out var element, out _))
            return false;
        RecordWordLookahead();
        if (TrackCommandLocations)
            ConsumedPosition = Position;

        // The leading word part has the logical name. The selector retains quoting for
        // the eventual array kind, with any read-ahead here-documents included.
        var selector = element.Subscript.AsSpan();
        var targetLength = checked(nameLength + selector.Length + 2);
        _scanContext.SyntaxMemory?.Grow(MemoryCapacity.ArrayBackingBytes<byte>(targetLength));
        var target = new byte[targetLength];
        first.Source.AsSpan(0, nameLength).CopyTo(target);
        target[nameLength] = (byte)'[';
        selector.CopyTo(target.AsSpan(nameLength + 1));
        target[^1] = (byte)']';
        assignment = new AssignmentWord(ShellSource.FromBytes(target), element.Value, element.Operator);
        return true;
    }

    /// <summary>Reads compound items, retaining whitespace inside an unquoted bracket prefix.</summary>
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
        var operation = AssignmentOperator.Replace;
        if (!ShellSourceCursor.TryMatchLogicalText(_input, _scanContext, closing + 1, "=", out prefixEnd))
        {
            if (!ShellSourceCursor.TryMatchLogicalText(_input, _scanContext, closing + 1, "+=", out prefixEnd))
                return false;
            operation = AssignmentOperator.Append;
        }

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
        var selector = FunctionDefinitionFormatter.RenderReadAheadCaptures(
            _input.Slice(opening + 1, closing - opening - 1), _scanContext);
        element = new CompoundArrayElement(selector, value, operation);
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
    internal Token PeekTokenCore() => PeekTokenCore(ArrayTokenRole.None);

    private Token PeekTokenCore(ArrayTokenRole arrayRole)
    {
        if (Error != null)
            return Token.EndOfInput();
        if (_pushback.Count > 0)
        {
            return _pushback.Peek();
        }

        var token = ReadToken(forHeader: false, arrayRole, out _);
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
    internal Token NextTokenCore() => Consume(ReadToken(forHeader: false, ArrayTokenRole.None, out _));

    internal Token NextCompoundArrayToken() =>
        Consume(ReadToken(forHeader: false, TracksScriptDiagnostics ? ArrayTokenRole.Item : ArrayTokenRole.None, out _));

    /// <summary>Reads the next token after a consumed for keyword, identifying arithmetic clause boundaries.</summary>
    internal Token ReadForHeader(out ArithmeticForSeparators separators) =>
        Consume(ReadToken(forHeader: true, ArrayTokenRole.None, out separators));

    private Token Consume(Token token)
    {
        if (TrackCommandLocations && token.Kind != TokenKind.EndOfInput)
            ConsumedPosition = token.Source.End.Value;
        return token;
    }

    private enum ArrayTokenRole { None, Opener, Item }

    private Token ReadToken(bool forHeader, ArrayTokenRole arrayRole, out ArithmeticForSeparators separators)
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
            if (Position == span.Length - 1 && span[Position] == (byte)'\\'
                && _scanContext.GetFinalBackslash(_input) == FinalBackslash.Continuation)
            {
                Position++;
                _spacingContinuedAtEndOfInput = true;
            }
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
                        if (!IsCommandSubstitution || !EndsInLineContinuation(span)
                            && !_spacingContinuedAtEndOfInput && !_continuedAtEndOfInput)
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

            if (current == '(')
            {
                if (arrayRole != ArrayTokenRole.None)
                {
                    if (arrayRole == ArrayTokenRole.Item)
                    {
                        // The adjacent arithmetic spelling reports its second opener here.
                        var second = ShellSourceCursor.SkipLineContinuations(span, Position + 1);
                        if (second < span.Length && span[second] == (byte)'(')
                            start = Position = second;
                    }
                    Position++;
                    return Located(Token.OpenParen());
                }
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
                    _arithmeticFailureStart = Position;
                    if (error != null)
                    {
                        if (error.Kind == ParserFailureKind.ContinuedArithmeticCloser)
                            error = ReadContinuedArithmeticCloser(error.Error);
                        if (error is { Kind: ParserFailureKind.IncompleteArithmeticCommand, UnclosedDelimiter: { } delimiter }
                            && _scanContext.LegacyOrigin == LegacySubstitutionOrigin.HereDocument)
                            error = error with { UnclosedDelimiter = delimiter with { PayloadLine = GetLine(delimiter.ReadPosition) } };
                        RecordFailure(error);
                    }
                    else
                        RecordError(status == ShellScanStatus.Incomplete
                            ? "Unterminated arithmetic command." : "Invalid arithmetic command.", Position);
                    return Located(Token.EndOfInput());
                }
                _arithmeticFallbackAtFileEnd |= arithmeticEnd > Position;

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

            if (current is (byte)'<' or (byte)'>'
                && ShellSourceCursor.TryMatchLogicalText(span, Position, current == (byte)'<' ? "<(" : ">(", out _))
            {
                RecordError("process substitution is not supported", Position);
                return Located(Token.EndOfInput());
            }

            var shellOperator = ReadShellOperator(_input, _scanContext, Position);
            if (shellOperator.Kind != TokenKind.None)
            {
                Position = shellOperator.Source.End.Value;
                return Located(shellOperator);
            }

            if (span[Position] == (byte)'&')
            {
                if (arrayRole == ArrayTokenRole.Item)
                    RecordUnexpectedArrayToken(new Token { Source = Position..(Position + 1) });
                else
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
            if (token.Kind is TokenKind.Word or TokenKind.OpenBrace or TokenKind.CloseBrace)
                RecordWordLookahead();
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
    /// <param name="continuedFinalLine">Whether EOF completed a nonempty line ending in a continuation.</param>
    /// <param name="lineFeeds">Physical LFs consumed, including continuations and any delimiter line.</param>
    /// <returns>Whether the delimiter was found. A script reader can use the collected EOF body.</returns>
    internal bool TryReadHereDocument(
        ShellSource delimiter,
        bool stripTabs,
        bool joinContinuations,
        out ShellSource body,
        out bool continuedFinalLine,
        out int lineFeeds)
    {
        body = default;
        continuedFinalLine = false;
        lineFeeds = 0;
        ByteMemoryBuilder? content = null;
        var bodyStart = Position;
        var span = _input.AsSpan();

        while (Position < span.Length)
        {
            var physicalLineStart = Position;
            var lineStart = content?.Length ?? 0;
            var runStart = physicalLineStart;
            var hasContinuedBytes = false;
            var escapedStringEof = false;
            while (Position < span.Length && span[Position] != (byte)'\n')
            {
                if (joinContinuations && span[Position] == (byte)'\\')
                {
                    if (Position + 1 == span.Length)
                    {
                        escapedStringEof = AllowEofHereDocuments && _scanContext.ReadsCommandString;
                        Position++;
                        break;
                    }
                    if (span[Position + 1] == (byte)'\n')
                    {
                        _scanContext.RecordHereDocumentSplice(_input, Position);
                        content ??= StartContent();
                        hasContinuedBytes |= Position != runStart;
                        AppendRun(content, span[runStart..Position]);
                        Position += 2;
                        lineFeeds++;
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
            if (escapedStringEof)
                content ??= StartContent();
            if (content != null)
            {
                AppendRun(content, run);
                // Bash's string reader supplies the byte from pushing EOF back after the escape.
                if (escapedStringEof)
                {
                    content.Append((byte)0xff);
                    _scanContext.RecordHereDocumentEscapedEof(_input);
                }
                line = content.ToMemory().Span[lineStart..];
            }
            else if (stripTabs)
                line = run.TrimStart((byte)'\t');

            var newlineLength = Position < span.Length ? 1 : 0;
            Position += newlineLength;
            lineFeeds += newlineLength;
            continuedFinalLine = newlineLength == 0 && run.IsEmpty && runStart != physicalLineStart;
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

    /// <summary>Reads one shell metacharacter token with its physical range.</summary>
    internal static Token ReadShellOperator(ShellSource source, ShellScanContext context, int start)
    {
        var end = start;
        var token = source[start] switch
        {
            (byte)';' when Match(";;&") => Token.CaseTerminator(CaseContinuation.Retest),
            (byte)';' when Match(";&") => Token.CaseTerminator(CaseContinuation.FallThrough),
            (byte)';' when Match(";;") => Token.CaseTerminator(CaseContinuation.Stop),
            (byte)';' => Single(Token.Semicolon()),
            (byte)'&' when Match("&&") => Token.FromListOperator(ListOperator.And),
            (byte)'&' when Match("&>>") => Token.Redirection(RedirectionKind.OutputAndErrorAppend),
            (byte)'&' when Match("&>") => Token.Redirection(RedirectionKind.OutputAndError),
            (byte)'|' when Match("||") => Token.FromListOperator(ListOperator.Or),
            (byte)'|' when Match("|&") => Token.FromPipelineOperator(PipelineOperator.PipeAndError),
            (byte)'|' => Single(Token.FromPipelineOperator(PipelineOperator.Pipe)),
            (byte)'<' when Match("<&") => Token.Redirection(RedirectionKind.DuplicateInput),
            (byte)'<' when Match("<<-") => Token.Redirection(RedirectionKind.HereDocumentStripTabs),
            (byte)'<' when Match("<<<") => Token.Redirection(RedirectionKind.HereString),
            (byte)'<' when Match("<<") => Token.Redirection(RedirectionKind.HereDocument),
            (byte)'<' when Match("<>") => Token.Redirection(RedirectionKind.InputOutput),
            (byte)'<' => Single(Token.Redirection(RedirectionKind.Input)),
            (byte)'>' when Match(">|") => Token.Redirection(RedirectionKind.ForceOutput),
            (byte)'>' when Match(">&") => Token.Redirection(RedirectionKind.DuplicateOutput),
            (byte)'>' when Match(">>") => Token.Redirection(RedirectionKind.OutputAppend),
            (byte)'>' => Single(Token.Redirection(RedirectionKind.Output)),
            _ => Token.None()
        };
        return token with { Source = start..end };

        bool Match(string spelling) => ShellSourceCursor.TryMatchLogicalText(source, context, start, spelling, out end);

        Token Single(Token matched)
        {
            end = start + 1;
            return matched;
        }
    }

    /// <summary>Identifies operators whose reader checks for a longer spelling.</summary>
    internal static bool PeeksPastShellOperator(Token token) => token is
        { Kind: TokenKind.Semicolon }
        or { Kind: TokenKind.CaseTerminator, CaseContinuation: CaseContinuation.Stop }
        or { PipelineOperator: PipelineOperator.Pipe }
        or { RedirectionKind: RedirectionKind.Input or RedirectionKind.Output
            or RedirectionKind.HereDocument or RedirectionKind.OutputAndError };

    /// <summary>
    /// Parses through the conditional grammar's closing ]] with the caller's scan context.
    /// </summary>
    internal bool TryReadConditionalCommand(Token opening, [NotNullWhen(true)] out ConditionalCommand? command)
    {
        if (!ConditionalParser.TryReadCommand(_input, opening.Source, this,
                out command, out var end, out var error))
        {
            RecordFailure(error);
            return false;
        }
        Position = end;
        RecordWordLookahead();
        if (TrackCommandLocations)
            ConsumedPosition = end;
        return true;
    }

    /// <summary>
    /// Reads a word token, handling quotes and expansions within it.
    /// </summary>
    private Word ReadWord(ReadOnlySpan<byte> span)
    {
        return ReadWord(span, delimiterStart: 0, ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
    }

    private ParserFailure ReadContinuedArithmeticCloser(SyntaxError error)
    {
        var start = error.Offset;
        var source = _input.AsSpan();
        var wordStart = ShellSourceCursor.SkipLineContinuations(source, start);
        var readPosition = wordStart;
        var terminalBackslash = start == source.Length - 1;
        ShellSource token = default;
        if (terminalBackslash)
        {
            // The file reader consumes its implicit LF. A command string quotes the final backslash.
            readPosition = source.Length;
            if (_scanContext.ReadsCommandString)
                token = ShellSource.FromText(@"\\");
        }
        else if (wordStart < source.Length && !IsWordDelimiter(source[wordStart]))
        {
            var wordReader = new Tokenizer(_input, wordStart, _scanContext);
            wordReader.ReadWord(source);
            if (wordReader.Failure is { } wordFailure)
                return wordFailure;
            readPosition = wordReader.Position;
            token = ShellSourceCursor.RemoveLineContinuations(_scanContext,
                _input.Slice(wordStart, readPosition - wordStart));
        }

        var captureContinuation = IsCommandSubstitution && wordStart > start;
        if (captureContinuation || token.AsSpan().IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0)
        {
            using var quoted = _scanContext.CreateByteBuilder(0);
            if (captureContinuation)
            {
                using var spelling = _scanContext.CreateByteBuilder(0);
                spelling.Append(source.Slice(start, 2)).Append(token.Bytes);
                if (!ShellArgumentEscaping.TryAppendArithmeticDiagnosticToken(quoted, spelling.ToMemory().Span))
                    throw new ByteMemoryBuilder.LimitExceededException();
            }
            else if (!ShellArgumentEscaping.TryAppendArithmeticDiagnosticToken(quoted, token.AsSpan()))
                throw new ByteMemoryBuilder.LimitExceededException();
            token = ShellSource.FromBytes(quoted.ToMemory());
        }

        var line = terminalBackslash ? default : _input.GetPhysicalLine(readPosition, out _);
        return new ParserFailure(error with { Offset = Position }, ParserFailureKind.Grammar)
        {
            UnexpectedToken = new UnexpectedSyntax(token, line, readPosition) { LineAdvance = terminalBackslash ? 1 : 0 }
        };
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
        MemoryCapacity? memory, [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error)
        => TryReadExpansionWord(content, syntax, outerQuote, new ShellScanContext { SyntaxMemory = memory }, out word, out error);

    /// <summary>Consumes nested lookahead syntax without resetting its caller's structural allowance.</summary>
    internal static bool TryReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        ShellScanContext context, [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error)
        => TryReadExpansionWord(content, syntax, outerQuote, context, default, FinalBackslash.Invalid, out word, out error);

    internal static bool TryReadExpansionWord(ShellSource content, ExpansionWordSyntax syntax, ShellQuoteKind outerQuote,
        ShellScanContext context, ConditionalParser.OperandSpelling spelling, FinalBackslash finalBackslash,
        [NotNullWhen(true)] out Word? word, [NotNullWhen(false)] out SyntaxError? error)
    {
        var tokenizer = new Tokenizer(content, 0, context) { _isExpansionOperand = true, _finalBackslash = finalBackslash };
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
    private FinalBackslash _finalBackslash;

    /// <summary>Reads one already-delimited word after generated punctuation changes its quote context.</summary>
    internal static Word ReadGeneratedWord(ShellSource content, MemoryCapacity memory)
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
        var endsAtDecodedNul = false;
        var quoteBounds = quotedOperand ? new QuotedWordBounds(Position, delimiterStart) : default;

        void AddPart(WordPart part)
        {
            if (endsAtDecodedNul)
                return;
            if (quotedOperand)
            {
                part.DoubleQuote = continuesQuote ? DoubleQuoteBoundary.Continuation : DoubleQuoteBoundary.Start;
                continuesQuote = true;
            }
            parts.Add(part);
            endsAtDecodedNul = part is SourceWordPart { EndsAtDecodedNul: true };
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
                EndsAtDecodedNul = part.EndsAtDecodedNul,
                SpellingCaptures = part.Captures
            });
            renderedLineAdjustment = 0;
            subscriptFailure = ParameterSubscriptFailure.None;
            expandedParameterQuoteSyntax = false;
        }

        var continuationEnd = -1;
        while (Position < span.Length && Error == null)
        {
            var position = Position;
            if (SkipCollectedHereDocuments())
            {
                // A body read can leave the continued command word at physical EOF.
                _continuedAtEndOfInput |= position == continuationEnd && Position == span.Length;
                continue;
            }
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

            UnclosedDelimiter? arithmeticOpening = null;
            if (current == (byte)'$' && ArithmeticExpansionScanner.HasPrefix(_input, Position)
                && TryReadArithmeticExpansion(ref unquoted, quotedOperand, ref renderedLineAdjustment, out arithmeticOpening))
                continue;

            if (current == (byte)'$' && CommandSubstitutionScanner.HasPrefix(_input, Position)
                || current == (byte)'`' && (!braceExpanded || Position + 1 < span.Length))
            {
                FlushUnquoted();
                if (ReadCommandSubstitutionPart(
                        quotedOperand ? WordPartKind.CommandSubstitutionQuoted : WordPartKind.CommandSubstitution,
                        syntax, spelling, arithmeticOpening) is not { } substitution)
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
                    if (!endsAtDecodedNul)
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
                if (!endsAtDecodedNul)
                {
                    parts.AddRange(quotedParts);
                    endsAtDecodedNul = quotedParts is [.., SourceWordPart { EndsAtDecodedNul: true }];
                }
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
                    var finalBackslash = _isExpansionOperand ? _finalBackslash : _scanContext.GetFinalBackslash(_input);
                    if (!quotedOperand && finalBackslash != FinalBackslash.Invalid)
                    {
                        _wordEndsWithUnframedBackslash = true;
                        if (finalBackslash == FinalBackslash.Continuation)
                            _continuedAtEndOfInput = true;
                        else
                        {
                            FlushUnquoted();
                            if (!TryReserveSyntax())
                                return new Word([]);
                            AddPart(CreateEscapedLiteral(Position - 1, 1));
                        }
                        break;
                    }
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
                    continuationEnd = Position;
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
                AddPart(CreateEscapedLiteral(Position, length));
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
            {
                var opening = _input[start] == (byte)'$' ? ShellSourceCursor.SkipLineContinuations(_input.AsSpan(), start + 1) : start;
                RecordUnclosedDelimiter('\'', opening, "Unterminated single-quoted string.", Position);
            }
            return value;
        }
    }

    /// <summary>
    /// Captures canonical arithmetic-expansion syntax while retaining physical cursor offsets.
    /// </summary>
    private bool TryReadArithmeticExpansion(ref WordPartBuffer content, bool quoted, ref int renderedLineAdjustment,
        out UnclosedDelimiter? arithmeticOpening)
    {
        arithmeticOpening = null;
        var start = Position;
        if (_scanContext.TryGetCommandSubstitution(_input, start, out _))
            return false;
        var status = ArithmeticExpansionScanner.Scan(
            _scanContext, _input, start, out var endIndex, out var expression, out _, out var bracketed,
            out var adjustment, out var error, trackRenderedLines: true, trackIncompleteDelimiter: TracksScriptDiagnostics);
        if (status == ShellScanStatus.NotFound)
        {
            arithmeticOpening = error?.UnclosedDelimiter;
            return false;
        }
        if (status != ShellScanStatus.Complete)
        {
            if (status == ShellScanStatus.Incomplete)
                RecordUnterminatedExpansion(error, "Unterminated arithmetic expansion.", start);
            else
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
        ConditionalParser.OperandSpelling spelling, UnclosedDelimiter? arithmeticOpening)
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
        if (_scanContext.LimitError == null
            && ArithmeticExpansionScanner.TrySelectCommentedAdjacentSubshell(_input, start,
                status == ShellScanStatus.Complete ? endIndex : null,
                kind == WordPartKind.CommandSubstitutionQuoted, _scanContext.SyntaxMemory,
                out var selectedEnd, out var selected))
        {
            Position = selectedEnd + 1;
            return new CommandSubstitutionWordPart(kind, selected);
        }
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
            if (status == ShellScanStatus.Incomplete && _input[start] == (byte)'`'
                && (IsCommandSubstitution || TracksScriptDiagnostics))
                RecordUnclosedDelimiter('`', start, "Unterminated command substitution.", Position);
            else
                RecordExpansionFailure(CommandSubstitutionScanner.KeepArithmeticOpeningAtEof(status, error, arithmeticOpening),
                    kind == WordPartKind.CommandSubstitutionQuoted,
                    "Unterminated command substitution.", start);
            return null;
        }

        Position = endIndex + 1;
        var parsed = payload ?? throw new InvalidOperationException("A complete substitution requires executable syntax.");
        if (parsed is CommandSubstitutionSyntax.Parsed { CanRenderBody: true } capture)
            spelling.RecordCapture(start, Position, capture);
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
            content.AppendExpansion(start, endIndex + 1, "${"u8, payload, "}"u8);
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

        if (status == BracedParameterScanStatus.UnterminatedCommandSubstitution)
            RecordUnterminatedExpansion(error, "Unterminated command substitution.", errorIndex);
        else if (status == BracedParameterScanStatus.Unterminated && !_isExpansionOperand)
            RecordUnterminatedExpansion(error, quoted ? "Unterminated double-quoted string." : "Unterminated parameter expansion.", errorIndex);
        else if (status != BracedParameterScanStatus.Unterminated && error != null)
            RecordFailure(error.InDoubleQuotes(quoted));

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
        var endsAtDecodedNul = false;
        var content = new WordPartBuffer(_input, _scanContext);

        void AddPart(WordPart part)
        {
            if (endsAtDecodedNul)
                return;
            part.DoubleQuote = parts.Count == 0 ? DoubleQuoteBoundary.Start : DoubleQuoteBoundary.Continuation;
            parts.Add(part);
            endsAtDecodedNul = part is SourceWordPart { EndsAtDecodedNul: true };
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
                EndsAtDecodedNul = part.EndsAtDecodedNul,
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
                    RecordUnclosedDelimiter('"', quoteStart, "Unexpected end of input after escape.", Position);
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
                    AddPart(CreateEscapedLiteral(Position + 1, 1));
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

            UnclosedDelimiter? arithmeticOpening = null;
            if (current == (byte)'$' && ArithmeticExpansionScanner.HasPrefix(_input, Position)
                && TryReadArithmeticExpansion(ref content, quoted: true, ref renderedLineAdjustment, out arithmeticOpening))
                continue;

            if (current == (byte)'$' && CommandSubstitutionScanner.HasPrefix(_input, Position)
                || current == (byte)'`' && (!braceExpanded || Position + 1 < span.Length))
            {
                Flush(allowEmpty: false);
                if (ReadCommandSubstitutionPart(WordPartKind.CommandSubstitutionQuoted, syntax, spelling, arithmeticOpening) is not { } substitution)
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
        RecordUnclosedDelimiter('"', quoteStart, "Unterminated double-quoted string.", Position);
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
        private bool _endsAtDecodedNul;

        public readonly int Length => _edited?.Length ?? _length;

        public void AppendSource(int start, int length)
        {
            if (_endsAtDecodedNul)
                return;
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

        public void AppendExpansion(int start, int end, ReadOnlySpan<byte> opening, SourceSpelling payload, ReadOnlySpan<byte> closing)
        {
            if (_endsAtDecodedNul)
                return;
            payload.CopyCaptures(Length + opening.Length, ref _captures, context.SyntaxMemory);
            if (payload.EndsAtDecodedNul)
            {
                GetEdited().Append(opening).Append(payload.Source.Bytes);
                _endsAtDecodedNul = true;
                return;
            }
            // The scanner already recognized the logical delimiters. Their exact
            // widths and the physical payload slice identify an unedited spelling.
            if (end - start == opening.Length + payload.Source.Length + closing.Length
                && source.Bytes.Slice(start + opening.Length, payload.Source.Length).Equals(payload.Source.Bytes))
            {
                AppendSource(start, end - start);
                return;
            }
            GetEdited().Append(opening).Append(payload.Source.Bytes).Append(closing);
        }

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
            var part = new SourceSpelling(ShellSource.FromBytes(bytes), _captures) { EndsAtDecodedNul = _endsAtDecodedNul };
            _captures = null;
            _endsAtDecodedNul = false;
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
    // Keep the value independent of the input by borrowing an immutable byte or copying one scalar.
    private WordPart CreateEscapedLiteral(int offset, int length)
    {
        var bytes = _input.Bytes.Slice(offset, length);
        var value = length == 1 ? ShellValue.FromByte(bytes.Span[0]) : ShellValue.FromBytes(bytes.ToArray());
        return WordPart.FromValue(WordPartKind.Escaped, value);
    }

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
    internal Word? Word { get; }

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

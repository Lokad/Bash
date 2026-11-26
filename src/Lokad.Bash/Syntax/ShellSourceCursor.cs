using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>Traverses shell source while applying shared quote, escape, and nested-expansion rules.</summary>
internal sealed class ShellSourceCursor
{
    /// <summary>Bounds recursive scanner composition before it can threaten the CLR stack.</summary>
    internal const int MaximumNestedExpansionDepth = 256;

    /// <summary>The immutable complete source being scanned.</summary>
    private readonly ShellSource _text;

    /// <summary>The recursion budget shared by mutually recursive nested scanners.</summary>
    private readonly ShellScanContext _context;

    /// <summary>The currently open shell quote, if any.</summary>
    private ShellQuoteKind _quote;
    private int _quoteStart;

    /// <summary>Creates a cursor at one validated source position with no open quote.</summary>
    public ShellSourceCursor(ShellSource text, int position)
        : this(text, position, new ShellScanContext())
    {
    }

    internal ShellSourceCursor(ShellSource text, int position, ShellScanContext context)
    {
        if (position < 0 || position > text.Length)
            throw new System.ArgumentOutOfRangeException(nameof(position));

        _text = text;
        _context = context;
        Position = position;
    }

    /// <summary>Gets the next source position to inspect.</summary>
    public int Position { get; private set; }

    /// <summary>Gets whether no source byte remains.</summary>
    public bool IsAtEnd => Position >= _text.Length;

    /// <summary>Gets the current byte.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown at end of input.</exception>
    public byte Current => !IsAtEnd
        ? _text[Position]
        : throw new System.InvalidOperationException("The shell source cursor is at end of input.");

    /// <summary>Gets whether scanning is inside single, double or ANSI-C quotes.</summary>
    public bool IsQuoted => _quote != ShellQuoteKind.None;

    /// <summary>Gets whether scanning is specifically inside double quotes.</summary>
    public bool IsDoubleQuoted => _quote == ShellQuoteKind.Double;

    /// <summary>Returns the open quote's closing character and physical opening position.</summary>
    internal UnclosedDelimiter? UnclosedQuote => !IsQuoted ? null
        : new UnclosedDelimiter(IsDoubleQuoted ? '"' : '\'', _quoteStart) { AtOpeningDelimiter = true };

    /// <summary>Advances one byte, never moving beyond the end position.</summary>
    public void Advance()
    {
        if (!IsAtEnd)
            Position++;
    }

    internal bool TrySkipReadAheadBodies()
    {
        var next = _context.HereDocumentReadAhead?.SkipBodies(_text, Position) ?? Position;
        if (next == Position)
            return false;
        Position = next;
        return true;
    }

    /// <summary>Skips consecutive backslash-LF pairs, retaining physical source offsets.</summary>
    public static int SkipLineContinuations(ReadOnlySpan<byte> source, int position)
    {
        while (position < source.Length - 1 && source[position] == (byte)'\\' && source[position + 1] == (byte)'\n')
            position += 2;
        return position;
    }

    internal bool TrySkipLineContinuations()
    {
        if (_quote is ShellQuoteKind.Single or ShellQuoteKind.AnsiC)
            return false;
        var next = SkipLineContinuations(_text.AsSpan(), Position);
        if (next == Position)
            return false;
        Position = next;
        return true;
    }

    /// <summary>Skips token spaces, tabs and backslash-LF pairs, but no physical newline or CR.</summary>
    public static int SkipTokenSpacing(ReadOnlySpan<byte> source, int position)
    {
        while (true)
        {
            position = SkipLineContinuations(source, position);
            if (position >= source.Length || source[position] is not ((byte)' ' or (byte)'\t'))
                return position;
            position++;
        }
    }

    /// <summary>Skips an already selected line comment; only LF ends it, including after backslash.</summary>
    internal static int SkipComment(ReadOnlySpan<byte> source, int position)
    {
        var length = source[position..].IndexOf((byte)'\n');
        return length < 0 ? source.Length : position + length;
    }

    internal bool TrySkipArithmeticExpansionComment()
    {
        if (IsQuoted || Current != (byte)'#' || Position == 0
            || _text[Position - 1] is not ((byte)' ' or (byte)'\t' or (byte)'\n'))
            return false;
        Position = SkipComment(_text.AsSpan(), Position);
        return true;
    }

    /// <summary>Reads one ordinary or ANSI-C literal, retaining its physical span even after decoded NUL.</summary>
    internal bool TryReadQuotedLiteral(out ShellValue value) =>
        TryReadQuotedLiteral(out value, allowUnterminated: false);

    internal bool TryReadQuotedLiteral(out ShellValue value, bool allowUnterminated)
    {
        value = default;
        if (!TryReadQuotedLiteralSource(out var payload, out var kind, allowUnterminated))
            return false;
        // Literal arguments may survive source execution (for example, set -- 'value').
        // Keep their storage independent of the complete input buffer.
        if (kind == ShellQuoteKind.AnsiC)
            value = ShellEscapeDecoder.DecodeAnsiCValue(payload.AsSpan(), _context.SyntaxMemory);
        else
        {
            _context.SyntaxMemory?.Grow(checked(64L + payload.Length));
            value = ShellValue.FromBytes(payload.Bytes.ToArray());
        }
        return true;
    }

    /// <summary>Advances over one literal without decoding again when the token already owns its value.</summary>
    internal bool TryReadQuotedLiteralSource(out ShellSource payload, out ShellQuoteKind kind) =>
        TryReadQuotedLiteralSource(out payload, out kind, allowUnterminated: false);

    private bool TryReadQuotedLiteralSource(out ShellSource payload, out ShellQuoteKind kind, bool allowUnterminated)
    {
        payload = default;
        kind = Current == (byte)'$' ? ShellQuoteKind.AnsiC : ShellQuoteKind.Single;
        _ = TryConsumeQuoteOrEscape();
        var start = Position;
        while (!IsAtEnd && IsQuoted)
            if (!TrySkipReadAheadBodies())
                _ = TryConsumeQuoteOrEscape();
        if (IsQuoted && !allowUnterminated)
            return false;

        payload = _text.Slice(start, Position - start - (IsQuoted ? 0 : 1));
        payload = _context.HereDocumentReadAhead?.CopyWithoutBodies(payload, _context) ?? payload;
        return true;
    }

    /// <summary>
    /// Matches an unquoted ASCII token across backslash-LF pairs. Failure leaves the returned
    /// offset at the start; success returns the physical position after its final character.
    /// </summary>
    public static bool TryMatchLogicalText(
        ReadOnlySpan<byte> source,
        int startIndex,
        ReadOnlySpan<char> expected,
        out int endIndex) => TryMatchLogicalText(source, default, null, startIndex, expected, out endIndex);

    internal static bool TryMatchLogicalText(ShellSource source, ShellScanContext context, int startIndex,
        ReadOnlySpan<char> expected, out int endIndex) =>
        TryMatchLogicalText(source.AsSpan(), source, context.HereDocumentReadAhead, startIndex, expected, out endIndex);

    private static bool TryMatchLogicalText(ReadOnlySpan<byte> bytes, ShellSource source, HereDocumentReadAhead? readAhead,
        int startIndex, ReadOnlySpan<char> expected, out int endIndex)
    {
        var position = startIndex;
        foreach (var character in expected)
        {
            while (true)
            {
                position = SkipLineContinuations(bytes, position);
                var next = readAhead?.SkipBodies(source, position) ?? position;
                if (next == position)
                    break;
                position = next;
            }
            if (position >= bytes.Length || bytes[position] != character)
            {
                endIndex = startIndex;
                return false;
            }
            position++;
        }
        endIndex = position;
        return true;
    }

    /// <summary>
    /// Removes this lexical layer's continuations using the cursor's quote/escape rules.
    /// Nested expansions retain their source for their own parser, including heredoc data.
    /// </summary>
    public static ShellSource RemoveLineContinuations(ShellSource source) =>
        RemoveLineContinuations(source, syntaxMemory: null, LegacySubstitutionOrigin.Temporary);

    internal static ShellSource RemoveLineContinuations(
        ShellSource source, MemoryCapacity? syntaxMemory, LegacySubstitutionOrigin origin)
    {
        if (!HasLineContinuationPair(source))
            return source;
        var context = new ShellScanContext { SyntaxMemory = syntaxMemory, LegacyOrigin = origin };
        return ScanLexicalLayer(context, source, normalize: true, SeparatorScan.None, out _);
    }

    /// <summary>Normalizes a scanned payload without rebuilding its nested lookahead syntax.</summary>
    internal static ShellSource RemoveLineContinuations(ShellScanContext context, ShellSource source) =>
        ScanLexicalLayer(context, source, normalize: HasLineContinuationPair(source), SeparatorScan.None, out _);

    internal static ShellSource PrepareExpansionSource(ShellScanContext context, ShellSource source, ref int renderedLineAdjustment)
    {
        var expanded = FunctionDefinitionFormatter.RenderReadAheadCaptures(source, context);
        if (!expanded.Bytes.Equals(source.Bytes))
            renderedLineAdjustment += source.AsSpan().Count((byte)'\n') - expanded.AsSpan().Count((byte)'\n');
        return RemoveLineContinuations(context, expanded);
    }

    /// <summary>Preserves the matched-pair spelling of an adjacent capture, including its final connector.</summary>
    internal static SourceSpelling ReadAdjacentCaptureSpelling(ShellScanContext context, ShellSource input,
        int start, int end, MemoryCapacity? workspace, bool prepareReplay, out ShellSource projectedInput,
        out IReadOnlyList<SourceLineAdjustment>? lineAdjustments, out int renderedLines)
    {
        var source = input.Slice(start, end - start);
        var cursor = new ShellSourceCursor(source, 0, context);
        ByteMemoryBuilder? output = null;
        List<SpellingCapture>? captures = null;
        List<SourceLineAdjustment>? changes = null;
        var written = 0;
        var delta = 0;
        var previousFailure = context.SubstitutionFailure;
        try
        {
            while (!cursor.IsAtEnd)
            {
                var first = cursor.Position;
                if (cursor.TrySkipLineContinuations())
                {
                    AppendBefore(first);
                    RecordLineChange(first, cursor.Position, replacementLines: 0);
                    written = cursor.Position;
                    continue;
                }

                if (!cursor.IsQuoted && cursor.Current == (byte)'$')
                {
                    if (TryMatchLogicalText(source.AsSpan(), first, "$'", out _))
                    {
                        if (!cursor.TryReadQuotedLiteralSource(out var value, out _))
                            break;
                        var decoded = ShellEscapeDecoder.DecodeAnsiCValue(value.AsSpan(), workspace);
                        if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(AppendBefore(first), decoded.Bytes.Span))
                            throw new ByteMemoryBuilder.LimitExceededException();
                        RecordLineChange(first, cursor.Position, decoded.Bytes.Span.Count((byte)'\n'));
                        written = cursor.Position;
                        continue;
                    }
                    if (TryMatchLogicalText(source.AsSpan(), first, "$\"", out var afterQuote))
                    {
                        AppendBefore(first);
                        // Retain the quote and let the cursor apply the ordinary double-quote rules.
                        cursor.Position = afterQuote - 1;
                        RecordLineChange(first, cursor.Position, replacementLines: 0);
                        written = cursor.Position;
                        continue;
                    }
                }

                if (cursor.TryConsumeQuoteOrEscape())
                    continue;
                if (!cursor.IsQuoted || cursor.IsDoubleQuoted)
                {
                    if (TryMatchLogicalText(source.AsSpan(), first, "${", out var payloadStart)
                        && BracedParameterScanner.ScanWithPayload(context, source, first, out var close, out _,
                            out var payload, out _, out _, out _, trackRenderedLines: false,
                            cursor.IsDoubleQuoted ? BracedParameterScanner.LexicalQuotes.DoubleQuoted
                                : BracedParameterScanner.LexicalQuotes.Unquoted,
                            resolveLateSubscripts: false, out _, out _) == BracedParameterScanStatus.Found)
                    {
                        cursor.Position = close + 1;
                        ProjectNested(first, payloadStart, close, cursor.Position, "${"u8, "}"u8, payload, CountLines(payload));
                        continue;
                    }

                    if (cursor.SkipNestedExpansion(BracedParameterRecognition.All) is ShellNestedExpansionScan.Complete expansion)
                    {
                        if (expansion.Kind == ShellNestedExpansionKind.Command
                            && context.TryGetCommandSubstitution(source, first, out var cached))
                        {
                            if (cached.Substitution is CommandSubstitutionSyntax.Parsed capture
                                && (capture.CanRenderBody || !prepareReplay && (capture.HasDiagnosticSpelling || capture.AdjacentSpelling != null)))
                            {
                                if (captures == null)
                                {
                                    context.SyntaxMemory?.Grow(32);
                                    captures = [];
                                }
                                context.SyntaxMemory?.PrepareListCapacity(captures, 1);
                                captures.Add(new SpellingCapture(ProjectOffset(first), cursor.Position - first, capture));
                                if (capture.Script.RenderedLineBreaks is { } lines)
                                    RecordLineChange(first, cursor.Position, lines);
                            }
                            else if (cached.Substitution is CommandSubstitutionSyntax.Legacy { RemovedLineContinuations: > 0 })
                            {
                                var body = RemoveLineContinuations(context, source.Slice(first + 1, cursor.Position - first - 2));
                                ProjectNested(first, first + 1, cursor.Position - 1, cursor.Position, "`"u8, "`"u8,
                                    new SourceSpelling(body, null), body.AsSpan().Count((byte)'\n'));
                            }
                        }
                        else if (expansion.Kind == ShellNestedExpansionKind.Arithmetic)
                        {
                            var bracketed = TryMatchLogicalText(source.AsSpan(), first, "$[", out var operandStart);
                            var operandEnd = cursor.Position - 1;
                            if (!bracketed)
                            {
                                _ = TryMatchLogicalText(source.AsSpan(), first, "$((", out operandStart);
                                // An expansion's final double closer can cross a line continuation.
                                while (operandEnd >= operandStart + 2
                                    && source[operandEnd - 2] == (byte)'\\' && source[operandEnd - 1] == (byte)'\n')
                                    operandEnd -= 2;
                                operandEnd--;
                            }
                            var body = source.Slice(operandStart, operandEnd - operandStart);
                            var bytes = body.AsSpan();
                            SourceSpelling operand;
                            int lines;
                            if (expansion.ContainsCommandSubstitution || bytes.IndexOf("\\\n"u8) >= 0
                                || bytes.IndexOf("$'"u8) >= 0 || bytes.IndexOf("$\""u8) >= 0)
                                operand = ReadAdjacentCaptureSpelling(context, source, operandStart, operandEnd, workspace,
                                    prepareReplay: false, out _, out _, out lines);
                            else
                            {
                                operand = new SourceSpelling(body, null);
                                lines = bytes.Count((byte)'\n');
                            }
                            ProjectNested(first, operandStart, operandEnd, cursor.Position,
                                bracketed ? "$["u8 : "$(("u8, bracketed ? "]"u8 : "))"u8, operand, lines);
                        }
                        continue;
                    }
                }
                cursor.Advance();
            }

            lineAdjustments = changes;
            renderedLines = source.AsSpan().Count((byte)'\n') + delta;
            projectedInput = ShellSource.Empty;
            if (output == null)
                return new SourceSpelling(source, captures);
            output.Append(source.AsSpan()[written..]);
            var length = output.Length;
            if (prepareReplay)
                output.Append((byte)')');
            var normalized = ShellSource.FromBytes(output.ToCompactMemory(context.SyntaxMemory));
            if (prepareReplay)
                projectedInput = normalized;
            return new SourceSpelling(normalized.Slice(0, length), captures);
        }
        finally
        {
            output?.Dispose();
            context.SubstitutionFailure = previousFailure;
        }

        ByteMemoryBuilder AppendBefore(int position)
        {
            output ??= workspace == null ? ByteMemoryBuilder.CreateUnaccounted(0) : new ByteMemoryBuilder(0, workspace);
            output.Append(source.AsSpan(written, position - written));
            return output;
        }

        int ProjectOffset(int position) => output == null ? position : output.Length + position - written;

        void ProjectNested(int first, int bodyStart, int bodyEnd, int after, ReadOnlySpan<byte> opening,
            ReadOnlySpan<byte> closing, SourceSpelling spelling, int lines)
        {
            if (bodyStart == first + opening.Length && after == bodyEnd + closing.Length
                && source.Slice(bodyStart, bodyEnd - bodyStart).Bytes.Equals(spelling.Source.Bytes))
                spelling.CopyCaptures(ProjectOffset(bodyStart), ref captures, context.SyntaxMemory);
            else
            {
                var target = AppendBefore(first).Append(opening);
                spelling.CopyCaptures(target.Length, ref captures, context.SyntaxMemory);
                target.Append(spelling.Source.AsSpan()).Append(closing);
                written = after;
            }
            RecordLineChange(first, after, lines);
        }

        static int CountLines(SourceSpelling spelling)
        {
            var lines = spelling.Source.AsSpan().Count((byte)'\n');
            if (spelling.Captures is { } nested)
                foreach (var capture in nested)
                    if (capture.Syntax is CommandSubstitutionSyntax.Parsed { Script.RenderedLineBreaks: { } rendered })
                        lines += rendered - spelling.Source.AsSpan(capture.Offset, capture.Length).Count((byte)'\n');
            return lines;
        }

        void RecordLineChange(int first, int after, int replacementLines)
        {
            var difference = replacementLines - source.AsSpan(first, after - first).Count((byte)'\n');
            if (difference == 0)
                return;
            delta += difference;
            if (!prepareReplay)
                return;
            if (changes == null)
            {
                workspace?.Grow(32);
                changes = [];
            }
            workspace?.PrepareListCapacity(changes, 1);
            changes.Add(new SourceLineAdjustment(start + after, delta));
        }
    }

    /// <summary>Finds separators while normalizing a loop header on its original source backing.</summary>
    internal static ShellSource ReadArithmeticForHeader(ShellScanContext context, ShellSource source,
        out ArithmeticForSeparators separators) =>
        ScanLexicalLayer(context, source, normalize: HasLineContinuationPair(source), SeparatorScan.Validate, out separators);

    /// <summary>Finds clause boundaries in the spelling supplied to a caller-created AST.</summary>
    internal static ArithmeticForSeparators FindArithmeticForSeparators(ShellSource source)
    {
        ScanLexicalLayer(new ShellScanContext(), source, normalize: false, SeparatorScan.FirstTwo, out var separators);
        return separators;
    }

    private static bool HasLineContinuationPair(ShellSource source) => source.AsSpan().IndexOf("\\\n"u8) >= 0;

    private static ShellSource ScanLexicalLayer(ShellScanContext context, ShellSource source,
        bool normalize, SeparatorScan separatorScan, out ArithmeticForSeparators separators)
    {
        separators = default;
        var findSeparators = separatorScan != SeparatorScan.None && source.AsSpan().Contains((byte)';');
        if (!normalize && !findSeparators)
            return source;

        var result = normalize ? context.CreateByteBuilder(source.Length) : null;
        var cursor = new ShellSourceCursor(source, 0, context);
        while (!cursor.IsAtEnd)
        {
            var start = cursor.Position;
            if (normalize && cursor.TrySkipLineContinuations())
                continue;

            var precedingQuote = cursor._quote;
            if (!cursor.TryConsumeQuoteOrEscape())
            {
                if (cursor.SkipNestedExpansion(BracedParameterRecognition.All) is not ShellNestedExpansionScan.Complete)
                    cursor.Advance();
                else if (findSeparators && separatorScan == SeparatorScan.Validate
                    && ArithmeticExpansionScanner.HasMissingCaptureCloser(source, start, cursor.Position,
                        cursor.IsDoubleQuoted, context.SyntaxMemory))
                {
                    // An incomplete capture consumes the remaining clause spelling.
                    if (result == null)
                        break;
                    findSeparators = false;
                }
            }
            if (findSeparators && !separators.HasExtra && !cursor.IsQuoted && source[start] == (byte)';')
            {
                var offset = result?.Length ?? start;
                separators = separators.First == null ? separators with { First = offset }
                    : separators.Second == null ? separators with { Second = offset }
                    : separators with { HasExtra = true };
            }
            if (result == null)
            {
                if (separators.HasExtra || (separatorScan == SeparatorScan.FirstTwo && separators.Second.HasValue))
                    break;
                continue;
            }
            if (precedingQuote == ShellQuoteKind.None && cursor._quote == ShellQuoteKind.AnsiC)
                result.Append("$'"u8);
            else
                result.Append(source.AsSpan(start, cursor.Position - start));
        }
        return result == null ? source : ShellSource.FromBytes(result.ToMemory());
    }

    private enum SeparatorScan { None, FirstTwo, Validate }

    /// <summary>
    /// Consumes quote syntax, quoted content, or one backslash escape and updates quote
    /// state. In double quotes, backslash escapes only dollar, backtick, double quote,
    /// backslash, and newline; before any other character the backslash alone is consumed.
    /// ANSI-C quotes protect escaped apostrophes and suppress all nested expansions.
    /// </summary>
    /// <returns>True when at least the current character was consumed.</returns>
    public bool TryConsumeQuoteOrEscape() => TryConsumeQuoteOrEscape(recognizeAnsiC: true);

    internal bool TryConsumeQuoteOrEscape(bool recognizeAnsiC)
    {
        var current = Current;
        if (_quote == ShellQuoteKind.AnsiC)
        {
            if (current == (byte)'\'')
                _quote = ShellQuoteKind.None;
            Position += current == (byte)'\\' && Position + 1 < _text.Length ? 2 : 1;
            return true;
        }

        if (_quote == ShellQuoteKind.Single)
        {
            if (current == (byte)'\'')
                _quote = ShellQuoteKind.None;

            Position++;
            return true;
        }

        if (recognizeAnsiC && _quote == ShellQuoteKind.None && current == (byte)'$'
            && TryMatchLogicalText(_text.AsSpan(), Position, "$'", out var literalStart))
        {
            _quote = ShellQuoteKind.AnsiC;
            _quoteStart = literalStart - 1;
            Position = literalStart;
            return true;
        }

        if (current == (byte)'\\')
        {
            if (_quote != ShellQuoteKind.Double)
            {
                Position += Position + 1 < _text.Length ? 2 : 1;
                return true;
            }

            if (Position + 1 < _text.Length && IsDoubleQuoteEscape(_text[Position + 1]))
            {
                Position += 2;
                return true;
            }

            Position++;
            return true;
        }

        if (current == (byte)'"')
        {
            _quoteStart = Position;
            _quote = _quote == ShellQuoteKind.Double
                ? ShellQuoteKind.None
                : ShellQuoteKind.Double;
            Position++;
            return true;
        }

        if (_quote == ShellQuoteKind.None && current == (byte)'\'')
        {
            _quote = ShellQuoteKind.Single;
            _quoteStart = Position;
            Position++;
            return true;
        }

        return false;
    }

    internal static bool IsDoubleQuoteEscape(byte value) =>
        value is (byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\' or (byte)'\n';

    /// <summary>Bounds late selector lookahead within adjacent double-quoted segments of one word.</summary>
    internal static int FindDoubleQuoteEnd(ShellScanContext context, ShellSource source, int position)
    {
        var cursor = new ShellSourceCursor(source, position, context)
            { _quote = ShellQuoteKind.Double, _quoteStart = position - 1 };
        var failure = context.SubstitutionFailure;
        try
        {
            while (!cursor.IsAtEnd)
            {
                if (cursor.TrySkipReadAheadBodies())
                    continue;
                if (cursor.Current == (byte)'"')
                {
                    var next = SkipLineContinuations(source.AsSpan(), cursor.Position + 1);
                    if (next >= source.Length || source[next] != (byte)'"')
                        return cursor.Position;
                    cursor.Position = next + 1;
                    continue;
                }
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;
                switch (cursor.SkipNestedExpansion(BracedParameterRecognition.All))
                {
                    case ShellNestedExpansionScan.Complete:
                        continue;
                    case ShellNestedExpansionScan.Incomplete or ShellNestedExpansionScan.Invalid:
                        return -1;
                    default:
                        cursor.Advance();
                        break;
                }
            }
            return -1;
        }
        finally { context.SubstitutionFailure = failure; }
    }

    /// <summary>
    /// Attempts to consume one nested arithmetic, parameter, or command substitution.
    /// A <see cref="ShellNestedExpansionScan.Complete"/> result advances past the closing
    /// delimiter; every other result leaves the cursor at the original position.
    /// Arithmetic commands leave unquoted parameter operands to their own delimiter rules;
    /// those operands are validated only when execution reaches the command.
    /// </summary>
    public ShellNestedExpansionScan SkipNestedExpansion(BracedParameterRecognition parameters, bool trackRenderedLines = false) =>
        SkipNestedExpansion(parameters, trackRenderedLines, resolveLateSubscripts: false);

    internal ShellNestedExpansionScan SkipNestedExpansion(BracedParameterRecognition parameters, bool trackRenderedLines,
        bool resolveLateSubscripts)
    {
        var edits = new BracedParameterScanner.ParameterSpellingEdits();
        return SkipNestedExpansion(parameters, trackRenderedLines, resolveLateSubscripts,
            BracedParameterScanner.LexicalQuotes.Preserve, ref edits);
    }

    internal ShellNestedExpansionScan SkipNestedExpansion(BracedParameterRecognition parameters, bool trackRenderedLines,
        bool resolveLateSubscripts, BracedParameterScanner.LexicalQuotes quotes, ref BracedParameterScanner.ParameterSpellingEdits edits)
    {
        if (_quote is ShellQuoteKind.Single or ShellQuoteKind.AnsiC)
            return ShellNestedExpansionScan.NotFound.Instance;

        var start = Position;
        UnclosedDelimiter? arithmeticOpening = null;
        if (ArithmeticExpansionScanner.HasPrefix(_text, start) && !_context.TryGetCommandSubstitution(_text, start, out _))
        {
            var status = _context.ReadArithmeticExpansionComments && TryMatchLogicalText(_text.AsSpan(), start, "$((", out _)
                ? ArithmeticExpansionScanner.ScanReachedParentheses(_context, _text, start, out var endIndex,
                    out var containsCommandSubstitution, out var renderedLineAdjustment, out var error)
                : ArithmeticExpansionScanner.ScanShape(_context, _text, start, out endIndex,
                    out containsCommandSubstitution, out renderedLineAdjustment, out error, trackRenderedLines);
            if (status == ShellScanStatus.Complete)
            {
                Position = endIndex + 1;
                return new ShellNestedExpansionScan.Complete(
                    ShellNestedExpansionKind.Arithmetic,
                    containsCommandSubstitution,
                    renderedLineAdjustment);
            }
            if (status != ShellScanStatus.NotFound)
                return ToFailure(status, ShellNestedExpansionKind.Arithmetic, error);
            arithmeticOpening = error?.UnclosedDelimiter;
        }

        if ((parameters == BracedParameterRecognition.All || IsDoubleQuoted)
            && BracedParameterScanner.HasPrefix(_text, start))
        {
            var nestedQuotes = quotes == BracedParameterScanner.LexicalQuotes.Unquoted && IsDoubleQuoted
                ? BracedParameterScanner.LexicalQuotes.DoubleQuoted : quotes;
            var status = BracedParameterScanner.ScanShape(
                _context,
                _text,
                start,
                out var endIndex,
                out _,
                out var containsCommandSubstitution,
                out var renderedLineAdjustment,
                out var error,
                out var subscriptFailure,
                out var hasLateSubscript,
                trackRenderedLines, resolveLateSubscripts, nestedQuotes, ref edits);
            if (status != BracedParameterScanStatus.Found)
            {
                return status is BracedParameterScanStatus.Unterminated
                    or BracedParameterScanStatus.UnterminatedCommandSubstitution
                    or BracedParameterScanStatus.MissingBraceAfterSelector
                    ? new ShellNestedExpansionScan.Incomplete(ShellNestedExpansionKind.Parameter)
                        { ParameterSubscriptFailure = subscriptFailure, Failure = error }
                    : new ShellNestedExpansionScan.Invalid(ShellNestedExpansionKind.Parameter, error?.InDoubleQuotes(IsDoubleQuoted));
            }

            Position = endIndex + 1;
            return new ShellNestedExpansionScan.Complete(
                ShellNestedExpansionKind.Parameter,
                containsCommandSubstitution,
                renderedLineAdjustment) { ParameterSubscriptFailure = subscriptFailure, HasLateSubscript = hasLateSubscript };
        }

        if (CommandSubstitutionScanner.HasPrefix(_text, start))
        {
            var status = CommandSubstitutionScanner.ScanParsedForLaterReuse(
                _context,
                _text,
                start,
                out var endIndex,
                out var substitution,
                out var error);
            if (status != ShellScanStatus.Complete)
                return ToFailure(status, ShellNestedExpansionKind.Command,
                    CommandSubstitutionScanner.KeepArithmeticOpeningAtEof(status, error, arithmeticOpening));

            Position = endIndex + 1;
            var renderedLineAdjustment = trackRenderedLines ? substitution switch
            {
                CommandSubstitutionSyntax.Parsed { Script.RenderedLineBreaks: { } breaks } =>
                    breaks - _text.AsSpan(start, Position - start).Count((byte)'\n'),
                CommandSubstitutionSyntax.Legacy legacy => -legacy.RemovedLineContinuations,
                _ => 0
            } : 0;
            return new ShellNestedExpansionScan.Complete(
                ShellNestedExpansionKind.Command,
                ContainsCommandSubstitution: true,
                renderedLineAdjustment);
        }

        return ShellNestedExpansionScan.NotFound.Instance;

        ShellNestedExpansionScan ToFailure(
            ShellScanStatus status,
            ShellNestedExpansionKind kind,
            ParserFailure? failure) =>
            status == ShellScanStatus.Incomplete && (kind != ShellNestedExpansionKind.Command || failure?.UnclosedDelimiter == null)
                ? new ShellNestedExpansionScan.Incomplete(kind)
                {
                    Failure = failure ?? (_text[start] == (byte)'`'
                        ? new ParserFailure(new SyntaxError("Unterminated command substitution.", _text.Length), ParserFailureKind.Delimiter)
                        {
                            UnclosedDelimiter = new UnclosedDelimiter('`', start) { AtOpeningDelimiter = true }
                        }
                        : null)
                }
                : new ShellNestedExpansionScan.Invalid(kind, failure?.InDoubleQuotes(IsDoubleQuoted));
    }

}

/// <summary>Lengths before the first two unquoted separators in an arithmetic loop's source.</summary>
internal readonly record struct ArithmeticForSeparators(int? First, int? Second, bool HasExtra);

/// <summary>Controls whether a delimiter scan consumes unquoted braced parameter operands.</summary>
internal enum BracedParameterRecognition
{
    All,
    DoubleQuotedOnly,
}

/// <summary>How the reader presents a final unpaired, unquoted backslash.</summary>
internal enum FinalBackslash { Invalid, Continuation, Literal }

/// <summary>Owns recursion bounds and temporary lookahead parses for one source traversal.</summary>
internal sealed class ShellScanContext
{
    // A token accounts for its command/word and associated collections. Word parts
    // are charged separately because one word can contain many quoted fragments.
    // Nested delimiter parsing and cached array brackets share this allowance.
    internal const int MaximumSyntaxUnits = 65536;
    internal const int MaximumCommandDepth = 64;
    private int _remainingSyntaxUnits = MaximumSyntaxUnits;
    private int _commandDepth;
    private int _nestedExpansionDepth;
    private ScanDepth _lookaheadPeak;
    private Dictionary<ReadOnlyMemory<byte>, CachedParse>? _commandSubstitutions;
    private ShellSource _lateSubscriptSource;
    private int _lastClosingBracket = -1;
    private ArraySubscriptScanner.Lookahead? _lateSubscripts;

    /// <summary>One reached command unit's coarse syntax and payload allowance; null denotes caller-owned parsing.</summary>
    internal MemoryCapacity? SyntaxMemory { get; set; }
    internal LegacySubstitutionOrigin LegacyOrigin { get; set; }
    internal bool PreserveDelimiterLookahead { get; set; }
    internal bool RetainCaptureLookahead { get; set; }
    internal bool ReadArithmeticExpansionComments { get; set; }
    internal bool MissingReachedArithmeticParenthesis { get; set; }
    internal SubstitutionSyntaxFailure SubstitutionFailure { get; set; }
    internal int RejectedCaptureReads { get; set; }
    internal SubstitutionSyntaxFailure LastRejectedCaptureRead { get; set; }
    internal ShellSource ScriptInput { get; set; }
    internal int FunctionBraceLine { get; set; }
    internal HereDocumentReadAhead? HereDocumentReadAhead { get; private set; }
    private List<HereDocumentWarning>? _captureWarnings;
    private SourceLineCursor _scriptDiagnosticLines;
    private (HashSet<int> Starts, List<CompoundItemReadDiagnostic> Items)? _compoundItemDiagnostics;

    internal ByteMemoryBuilder CreateByteBuilder(int capacity) => SyntaxMemory is { } memory
        ? new ByteMemoryBuilder(capacity, memory) : ByteMemoryBuilder.CreateUnaccounted(capacity);

    internal BoundedByteMemoryBuilder CreateBoundedByteBuilder(int capacity, int maximumLength) => SyntaxMemory is { } memory
        ? new BoundedByteMemoryBuilder(capacity, maximumLength, memory)
        : BoundedByteMemoryBuilder.CreateUnaccounted(capacity, maximumLength);

    // No completed unit can need a later lookup of its consumed source positions.
    internal void ClearCommandUnitLookahead()
    {
        _commandSubstitutions = null;
        HereDocumentReadAhead = null;
    }

    internal bool CanReadAhead(ShellSource source) => !ScriptInput.IsEmpty && source.Bytes.Equals(ScriptInput.Bytes);

    internal bool ReadsCommandString { get; init; }
    internal bool ReadQuotedRecovery { get; set; } = true;
    internal bool ReadArrayRecovery { get; set; }

    internal FinalBackslash GetFinalBackslash(ShellSource source)
    {
        if (!CanReadAhead(source))
            return FinalBackslash.Invalid;
        return ReadsCommandString ? FinalBackslash.Literal : FinalBackslash.Continuation;
    }

    // Capture tokenizers count relative to their bodies. Reader diagnostics use the enclosing script.
    internal int GetScriptDiagnosticLine(int offset) => _scriptDiagnosticLines.GetLine(ScriptInput, offset)
        + (HereDocumentReadAhead?.GetBufferedLineAdvance(ScriptInput, offset) ?? 0);

    internal void RecordCompoundItemDiagnostic(int start, CompoundItemReadDiagnostic diagnostic)
    {
        if (_compoundItemDiagnostics is not { } pending)
        {
            SyntaxMemory?.Grow(160);
            pending = ([], []);
            _compoundItemDiagnostics = pending;
        }
        if (pending.Starts.Contains(start))
            return;
        // Covers the entry and growth of the temporary set until the command unit completes.
        SyntaxMemory?.Grow(48);
        pending.Starts.Add(start);
        SyntaxMemory?.PrepareListCapacity(pending.Items, 1);
        pending.Items.Add(diagnostic);
    }

    internal IReadOnlyList<CompoundItemReadDiagnostic>? TakeCompoundItemDiagnostics()
    {
        var diagnostics = _compoundItemDiagnostics?.Items;
        _compoundItemDiagnostics = null;
        return diagnostics;
    }

    internal HereDocumentReadAhead BeginHereDocumentReadAhead()
    {
        if (HereDocumentReadAhead == null)
        {
            SyntaxMemory?.Grow(128);
            HereDocumentReadAhead = new HereDocumentReadAhead(ScriptInput);
        }
        return HereDocumentReadAhead;
    }

    internal void WarnPendingCaptureDocuments(int count, int readerLine)
    {
        if (_captureWarnings == null)
        {
            SyntaxMemory?.Grow(32);
            _captureWarnings = [];
        }
        SyntaxMemory?.PrepareListCapacity(_captureWarnings, 1);
        _captureWarnings.Add(new HereDocumentWarning { PendingCaptureDocuments = count, ReaderLine = readerLine });
    }

    internal IReadOnlyList<HereDocumentWarning>? TakeHereDocumentWarnings(IReadOnlyList<HereDocumentWarning>? trailing)
    {
        if (_captureWarnings == null)
            return trailing;
        if (trailing != null)
            AppendCaptureWarnings(trailing);
        var warnings = _captureWarnings;
        _captureWarnings = null;
        return warnings;
    }

    internal void AppendCaptureWarnings(IReadOnlyList<HereDocumentWarning> warnings)
    {
        var pending = _captureWarnings ?? throw new InvalidOperationException("A capture warning must precede body warnings.");
        SyntaxMemory?.PrepareListCapacity(pending, warnings.Count);
        pending.AddRange(warnings);
    }

    internal bool HasCommandSubstitutionLookahead => _commandSubstitutions is { Count: > 0 };

    internal bool TryScanLateSubscript(ShellSource source, int opening, int earlyBrace, out int closing)
    {
        if (!source.Bytes.Equals(_lateSubscriptSource.Bytes))
        {
            _lateSubscriptSource = source;
            _lastClosingBracket = source.AsSpan().LastIndexOf((byte)']');
            _lateSubscripts = null;
        }

        closing = -1;
        if (_lastClosingBracket <= earlyBrace)
            return false;
        _lateSubscripts ??= new ArraySubscriptScanner.Lookahead(source, this);
        if (_lateSubscripts.Scan(opening, out var end) != ShellScanStatus.Complete || end <= earlyBrace)
            return false;
        closing = end;
        return true;
    }

    internal bool TryTakeCommandSubstitution(ShellSource source, int startIndex, out CachedParse parsed)
        => TryFindCommandSubstitution(source, startIndex, take: true, out parsed);

    internal bool TryGetCommandSubstitution(ShellSource source, int startIndex, out CachedParse parsed)
        => TryFindCommandSubstitution(source, startIndex, take: false, out parsed);

    private bool TryFindCommandSubstitution(ShellSource source, int startIndex, bool take, out CachedParse parsed)
    {
        if (_commandSubstitutions != null && (uint)startIndex < (uint)source.Length)
        {
            var key = source.Bytes.Slice(startIndex, 0);
            if (_commandSubstitutions.TryGetValue(key, out parsed) && parsed.Length <= source.Length - startIndex
                && _nestedExpansionDepth + parsed.RequiredDepth.Expansion <= ShellSourceCursor.MaximumNestedExpansionDepth
                && _commandDepth + parsed.RequiredDepth.Command <= MaximumCommandDepth)
            {
                RecordLookaheadDepth(_nestedExpansionDepth + parsed.RequiredDepth.Expansion, _commandDepth + parsed.RequiredDepth.Command);
                // The source spelling may be published after its operand has consumed the parse.
                if (take && parsed.Substitution is not CommandSubstitutionSyntax.Parsed { Script.HasReadAheadHereDocuments: true })
                    _commandSubstitutions.Remove(key);
                return true;
            }
        }
        parsed = default;
        return false;
    }

    internal void StoreCommandSubstitution(
        ShellSource source, int startIndex, int length, CommandSubstitutionSyntax substitution)
    {
        // A zero-length slice identifies a physical byte across source views.
        var required = new ScanDepth(_lookaheadPeak.Expansion - _nestedExpansionDepth, _lookaheadPeak.Command - _commandDepth);
        (_commandSubstitutions ??= [])[source.Bytes.Slice(startIndex, 0)] = new CachedParse(length, substitution, required);
    }

    internal readonly record struct CachedParse(int Length, CommandSubstitutionSyntax Substitution, ScanDepth RequiredDepth);

    internal readonly record struct ScanDepth(int Expansion, int Command);

    // Track the actual depths a tree needs, including any cached descendants.
    // An arithmetic lookahead can prepare a child at a different command depth.
    internal ScanDepth BeginLookahead()
    {
        var previous = _lookaheadPeak;
        _lookaheadPeak = new ScanDepth(_nestedExpansionDepth, _commandDepth);
        return previous;
    }

    internal void EndLookahead(ScanDepth previous) => RecordLookaheadDepth(previous.Expansion, previous.Command);

    private void RecordLookaheadDepth(int expansion, int command) => _lookaheadPeak =
        new ScanDepth(Math.Max(_lookaheadPeak.Expansion, expansion), Math.Max(_lookaheadPeak.Command, command));

    internal bool ParsingLimitExceeded { get; private set; }

    /// <summary>Preserves hard parsing-limit failures across nested delimiter lookahead.</summary>
    internal SyntaxError? LimitError { get; private set; }

    internal void RecordLimitError(SyntaxError error) => LimitError ??= error;

    internal bool TryReserveSyntax()
    {
        if (_remainingSyntaxUnits == 0)
        {
            ParsingLimitExceeded = true;
            return false;
        }
        SyntaxMemory?.Grow(256); // Tokens/parts, command nodes, collections and prepared navigation.
        _remainingSyntaxUnits--;
        return true;
    }

    internal bool TryEnterCommand()
    {
        if (_commandDepth == MaximumCommandDepth)
        {
            ParsingLimitExceeded = true;
            return false;
        }
        _commandDepth++;
        RecordLookaheadDepth(_nestedExpansionDepth, _commandDepth);
        return true;
    }

    internal void ExitCommand() => _commandDepth--;

    /// <summary>Reserves one recursive scan level when the bounded budget permits it.</summary>
    public bool TryEnter()
    {
        if (_nestedExpansionDepth >= ShellSourceCursor.MaximumNestedExpansionDepth
            || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return false;

        _nestedExpansionDepth++;
        RecordLookaheadDepth(_nestedExpansionDepth, _commandDepth);
        return true;
    }

    /// <summary>Returns one previously reserved recursive scan level.</summary>
    public void Exit() => _nestedExpansionDepth--;
}

/// <summary>Identifies the shell quote currently controlling delimiter recognition.</summary>
internal enum ShellQuoteKind
{
    /// <summary>No quote is open.</summary>
    None,

    /// <summary>A single quote is open; every character except the closing quote is literal.</summary>
    Single,

    /// <summary>An ANSI-C quote is open; backslash protects the next character from closing it.</summary>
    AnsiC,

    /// <summary>A double quote is open; selected dollar, backtick, and backslash processing remains active.</summary>
    Double,
}

/// <summary>Identifies the nested expansion whose delimiter scan was attempted.</summary>
internal enum ShellNestedExpansionKind
{
    /// <summary>A <c>${...}</c> parameter expansion.</summary>
    Parameter,

    /// <summary>A parenthesized or bracketed arithmetic expansion.</summary>
    Arithmetic,

    /// <summary>A <c>$(...)</c> command substitution.</summary>
    Command,
}

/// <summary>Represents every outcome of scanning one nested shell expansion.</summary>
internal abstract record ShellNestedExpansionScan
{
    private ShellNestedExpansionScan()
    {
    }

    /// <summary>The current source position does not begin a supported nested expansion.</summary>
    internal sealed record NotFound : ShellNestedExpansionScan
    {
        private NotFound()
        {
        }

        /// <summary>Gets the allocation-free result shared by all absent-prefix scans.</summary>
        internal static NotFound Instance { get; } = new();
    }

    /// <summary>A complete nested expansion was consumed.</summary>
    internal sealed record Complete(
        ShellNestedExpansionKind Kind,
        bool ContainsCommandSubstitution,
        int RenderedLineAdjustment = 0) : ShellNestedExpansionScan
    {
        internal ParameterSubscriptFailure ParameterSubscriptFailure { get; init; }
        internal bool HasLateSubscript { get; init; }
    }

    /// <summary>A nested prefix was present but input ended before it closed.</summary>
    internal sealed record Incomplete(ShellNestedExpansionKind Kind) : ShellNestedExpansionScan
    {
        internal ParameterSubscriptFailure ParameterSubscriptFailure { get; init; }
        internal ParserFailure? Failure { get; init; }
    }

    /// <summary>A nested prefix was present but contained malformed nested syntax.</summary>
    internal sealed record Invalid(ShellNestedExpansionKind Kind, ParserFailure? Failure) : ShellNestedExpansionScan;
}

/// <summary>Reports the four semantic outcomes shared by delimiter scanners.</summary>
internal enum ShellScanStatus
{
    /// <summary>The requested position does not begin the scanner's syntax form.</summary>
    NotFound,

    /// <summary>A complete, structurally valid form and its closing delimiter were found.</summary>
    Complete,

    /// <summary>A valid prefix reaches end of input before its closing delimiter.</summary>
    Incomplete,

    /// <summary>A delimiter candidate contains malformed nested syntax.</summary>
    Invalid,
}

using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Scans arithmetic expansions and commands with shared nesting rules and distinct closing delimiters.</summary>
internal static class ArithmeticExpansionScanner
{
    /// <summary>Gets whether a position begins parenthesized or bracketed arithmetic.</summary>
    public static bool HasPrefix(ShellSource text, int startIndex) =>
        startIndex >= 0 && TryReadExpansionPrefix(text.AsSpan(), startIndex, out _, out _);

    /// <summary>Scans a complete arithmetic expansion and returns its inclusive final delimiter.</summary>
    public static bool TryScan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression) =>
        Scan(text, startIndex, out endIndex, out expression, out _) == ShellScanStatus.Complete;

    /// <summary>
    /// Scans a complete arithmetic expansion and reports command substitution at any nested depth.
    /// </summary>
    public static bool TryScan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression,
        out bool containsCommandSubstitution) =>
        Scan(text, startIndex, out endIndex, out expression, out containsCommandSubstitution)
            == ShellScanStatus.Complete;

    /// <summary>Scans an arithmetic expansion and reports a structured completion status.</summary>
    public static ShellScanStatus Scan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression,
        out bool containsCommandSubstitution)
    {
        var status = Scan(new ShellScanContext(), text, startIndex, out endIndex, out var spelling,
            out containsCommandSubstitution, out _, out _, out _);
        expression = spelling.Source;
        return status;
    }

    internal static ShellScanStatus Scan(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out SourceSpelling expression,
        out bool containsCommandSubstitution,
        out bool bracketed,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        bool trackRenderedLines = false) => Scan(context, text, startIndex, out endIndex, out expression,
            out containsCommandSubstitution, out bracketed, out renderedLineAdjustment, out error, trackRenderedLines,
            trackIncompleteDelimiter: false);

    internal static ShellScanStatus Scan(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out SourceSpelling expression,
        out bool containsCommandSubstitution,
        out bool bracketed,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        bool trackRenderedLines,
        bool trackIncompleteDelimiter) =>
        ScanCore(
            context,
            text,
            startIndex,
            out endIndex,
            out expression,
            out containsCommandSubstitution,
            out bracketed,
            out _,
            out renderedLineAdjustment,
            out error,
            captureExpression: true,
            form: ArithmeticForm.Expansion,
            trackRenderedLines, trackIncompleteDelimiter);

    internal static ShellScanStatus ScanShape(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out bool containsCommandSubstitution,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        bool trackRenderedLines = false) =>
        ScanCore(
            context,
            text,
            startIndex,
            out endIndex,
            out _,
            out containsCommandSubstitution,
            out _,
            out _,
            out renderedLineAdjustment,
            out error,
            captureExpression: false,
            form: ArithmeticForm.Expansion,
            trackRenderedLines, trackIncompleteDelimiter: true);

    /// <summary>
    /// Scans a command with adjacent closers, sharing the tokenizer's nesting budget.
    /// NotFound leaves nested-subshell prefixes to the shell grammar and returns the
    /// first closer when the fallback consumed the last line break of a file.
    /// </summary>
    internal static ShellScanStatus ScanCommand(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression,
        out ParserFailure? error)
    {
        var status = ScanCore(context, text, startIndex, out endIndex, out var spelling, out _, out _, out _, out _, out error,
            captureExpression: true, form: ArithmeticForm.Command, trackRenderedLines: false, trackIncompleteDelimiter: false);
        expression = spelling.Source;
        return status;
    }

    internal static ShellScanStatus ScanForHeader(
        ShellScanContext context, ShellSource text, int startIndex,
        out int endIndex, out ShellSource expression, out ArithmeticForSeparators separators, out ParserFailure? error)
    {
        var status = ScanCore(context, text, startIndex, out endIndex, out var spelling, out _, out _, out separators, out _, out error,
            captureExpression: true, form: ArithmeticForm.ForHeader, trackRenderedLines: false, trackIncompleteDelimiter: false);
        expression = spelling.Source;
        return status;
    }

    /// <summary>Bounds adjacent subshell spelling after arithmetic selection and a failed body parse.</summary>
    internal static bool TryScanAdjacentSubshell(ShellScanContext context, ShellSource text, int startIndex,
        out int endIndex) =>
        ScanCore(context, text, startIndex, out endIndex, out _, out _, out _, out _, out _, out _,
            captureExpression: false, form: ArithmeticForm.AdjacentSubshell, trackRenderedLines: false,
            trackIncompleteDelimiter: false) == ShellScanStatus.Complete;

    private static ParserFailure? ReadPartialCommandCloser(ShellScanContext context, ShellSource text,
        int expressionStart, int closer, bool forHeader, ref int endIndex)
    {
        var afterCloser = closer + 1;
        var following = text.AsSpan()[afterCloser..];
        if (!forHeader)
        {
            if (following.StartsWith("\\\n"u8) || following.SequenceEqual("\\"u8))
                return new ParserFailure(new SyntaxError("Invalid arithmetic command.", afterCloser),
                    ParserFailureKind.ContinuedArithmeticCloser);
            if (!following.IsEmpty && following[0] != (byte)'\n')
                return null;

            if (expressionStart == closer)
            {
                // The fallback reader sees an empty subshell in its replayed prefix.
                return new ParserFailure(new SyntaxError("Invalid arithmetic command.", closer), ParserFailureKind.Grammar)
                {
                    UnexpectedToken = new UnexpectedSyntax(text.Slice(closer, 1), text.Slice(closer - 1, 2), afterCloser)
                };
            }

            // A file reader can resume nested-subshell parsing after the physical LF.
            if (!context.ReadsCommandString)
            {
                if (following.Length <= 1)
                    endIndex = closer;
                return null;
            }
        }

        var line = text.GetPhysicalLine(afterCloser, out var lineStart);
        // The for header rejects immediately after the raw lookahead byte. Its
        // diagnostic includes the byte under the reader's resulting cursor.
        var end = afterCloser - lineStart;
        if (forHeader)
            end += Math.Min(2, line.Length - end);
        var kind = forHeader ? ParserFailureKind.Grammar : ParserFailureKind.Delimiter;
        return new ParserFailure(new SyntaxError("Invalid arithmetic command.", closer), kind)
        {
            UnexpectedToken = new UnexpectedSyntax(line.GetNearToken(end), line, afterCloser)
            {
                UseNearPrefix = true
            }
        };
    }

    private static ShellScanStatus ScanCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out SourceSpelling expression,
        out bool containsCommandSubstitution,
        out bool bracketed,
        out ArithmeticForSeparators separators,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        bool captureExpression,
        ArithmeticForm form,
        bool trackRenderedLines,
        bool trackIncompleteDelimiter)
    {
        endIndex = startIndex;
        expression = default;
        containsCommandSubstitution = false;
        bracketed = false;
        separators = default;
        renderedLineAdjustment = 0;
        error = null;
        if (startIndex < 0)
            return ShellScanStatus.NotFound;

        var span = text.AsSpan();
        var isCommand = form is ArithmeticForm.Command or ArithmeticForm.ForHeader;
        var adjacentSubshell = form == ArithmeticForm.AdjacentSubshell;
        int expressionStart;
        var hasPrefix = isCommand ? ShellSourceCursor.TryMatchLogicalText(span, startIndex, "((", out expressionStart)
            : adjacentSubshell ? ShellSourceCursor.TryMatchLogicalText(span, startIndex, "$((", out expressionStart)
            : TryReadExpansionPrefix(span, startIndex, out expressionStart, out bracketed);
        if (!hasPrefix)
            return ShellScanStatus.NotFound;
        if (!context.TryEnter())
            return ShellScanStatus.Invalid;

        try
        {
            var cursor = new ShellSourceCursor(text, expressionStart, context);
            var opening = bracketed ? (byte)'[' : (byte)'(';
            var closing = bracketed ? (byte)']' : (byte)')';
            var depth = adjacentSubshell ? 1 : 0;
            while (!cursor.IsAtEnd)
            {
                if (cursor.TrySkipReadAheadBodies())
                    continue;
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;

                var nestedResult = cursor.SkipNestedExpansion(isCommand
                    ? BracedParameterRecognition.DoubleQuotedOnly
                    : BracedParameterRecognition.All, trackRenderedLines);
                if (nestedResult is ShellNestedExpansionScan.Complete nested)
                {
                    containsCommandSubstitution |= nested.ContainsCommandSubstitution;
                    renderedLineAdjustment += nested.RenderedLineAdjustment;
                    continue;
                }

                if (nestedResult is ShellNestedExpansionScan.Incomplete incomplete)
                {
                    if (trackIncompleteDelimiter)
                    {
                        // The unquoted arithmetic reader matches its own pair before expanding parameter operands.
                        var failure = incomplete.Failure;
                        error = !cursor.IsQuoted && failure?.UnclosedDelimiter?.Delimiter == '}'
                            ? ReadIncomplete() : failure ?? ReadIncomplete();
                    }
                    return ShellScanStatus.Incomplete;
                }
                if (nestedResult is ShellNestedExpansionScan.Invalid invalid)
                {
                    error = invalid.Failure;
                    return ShellScanStatus.Invalid;
                }

                if (cursor.IsQuoted)
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == opening)
                {
                    depth++;
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current != closing)
                {
                    cursor.Advance();
                    continue;
                }

                if (depth > 0)
                {
                    depth--;
                    cursor.Advance();
                    continue;
                }

                if (adjacentSubshell)
                {
                    endIndex = cursor.Position;
                    return ShellScanStatus.Complete;
                }

                // Brackets use a single closer. Expansion parentheses can cross a
                // continuation, while arithmetic command closers must be adjacent.
                var closingEnd = cursor.Position + (bracketed ? 1 : 2);
                var closed = bracketed || (isCommand
                    ? span[cursor.Position..].StartsWith("))"u8)
                    : ShellSourceCursor.TryMatchLogicalText(text, context, cursor.Position, "))", out closingEnd));
                if (!closed)
                {
                    if (isCommand)
                    {
                        error = ReadPartialCommandCloser(context, text, expressionStart, cursor.Position,
                            form == ArithmeticForm.ForHeader, ref endIndex);
                        return error == null ? ShellScanStatus.NotFound : ShellScanStatus.Invalid;
                    }

                    // This closes the subshell opener in a command substitution.
                    // A later pair belongs to another lexical boundary.
                    if (trackIncompleteDelimiter)
                        error = ReadIncomplete();
                    return ShellSourceCursor.SkipLineContinuations(span, cursor.Position + 1) == text.Length
                        ? ShellScanStatus.Incomplete
                        : ShellScanStatus.NotFound;
                }

                endIndex = closingEnd - 1;
                if (captureExpression)
                {
                    var source = text.Slice(expressionStart, cursor.Position - expressionStart);
                    if (trackRenderedLines && containsCommandSubstitution && context.SubstitutionFailure is
                        SubstitutionSyntaxFailure.HereDocumentSeparator or SubstitutionSyntaxFailure.NestedHereDocumentSeparator
                        or SubstitutionSyntaxFailure.MissingPipelinePrefixCommand or SubstitutionSyntaxFailure.NestedPipelinePrefixCommand)
                    {
                        var edits = new BracedParameterScanner.ParameterSpellingEdits();
                        try
                        {
                            FunctionDefinitionFormatter.AddDiagnosticCaptureEdits(source, 0, source.Length, context, ref edits);
                            var normalized = edits.BuildPayload(context, source, 0, source.Length, ref renderedLineAdjustment);
                            expression = new SourceSpelling(normalized, edits.Captures);
                        }
                        finally { edits.Dispose(); }
                    }
                    else
                    {
                        var normalized = form == ArithmeticForm.ForHeader
                            ? ShellSourceCursor.ReadArithmeticForHeader(context,
                                FunctionDefinitionFormatter.RenderReadAheadCaptures(source, context), out separators)
                            : ShellSourceCursor.PrepareExpansionSource(context, source, ref renderedLineAdjustment);
                        expression = new SourceSpelling(normalized, null);
                    }
                }
                return ShellScanStatus.Complete;
            }

            if (isCommand || trackIncompleteDelimiter)
                error = ReadIncomplete();
            return ShellScanStatus.Incomplete;

            ParserFailure ReadIncomplete() => new(
                new SyntaxError(isCommand ? "Unterminated arithmetic command." : "Unterminated arithmetic expansion.", startIndex),
                isCommand ? ParserFailureKind.IncompleteArithmeticCommand : ParserFailureKind.Delimiter)
            {
                UnclosedDelimiter = cursor.UnclosedQuote
                    ?? new UnclosedDelimiter((char)closing, expressionStart - 1) { AtOpeningDelimiter = true }
            };
        }
        finally
        {
            context.Exit();
        }
    }

    private static bool TryReadExpansionPrefix(ReadOnlySpan<byte> span, int start, out int end, out bool bracketed)
    {
        bracketed = ShellSourceCursor.TryMatchLogicalText(span, start, "$[", out end);
        return bracketed || ShellSourceCursor.TryMatchLogicalText(span, start, "$((", out end);
    }

    /// <summary>Applies an enclosing double quote before expanding a bracketed operand.</summary>
    internal static bool TryPrepareDoubleQuotedBracketOperand(ShellScanContext context, ref ShellSource expression)
    {
        if (!expression.AsSpan().Contains((byte)'"'))
            return true;

        ByteMemoryBuilder? normalized = null;
        var runStart = 0;
        var cursor = new ShellSourceCursor(expression, 0, context);
        while (!cursor.IsAtEnd)
        {
            var position = cursor.Position;
            if (cursor.Current == (byte)'"')
                return false;
            if (cursor.Current == (byte)'\\' && position + 1 < expression.Length)
            {
                // The enclosing quote removes only the escape before a double quote here.
                // Other escapes belong to the bracketed operand's own expansion.
                if (expression[position + 1] == (byte)'"')
                {
                    normalized ??= context.CreateByteBuilder(expression.Length);
                    normalized.Append(expression.Bytes[runStart..position]);
                    normalized.Append((byte)'"');
                    runStart = position + 2;
                }
                cursor.Advance();
                cursor.Advance();
                continue;
            }
            if ((CommandSubstitutionScanner.HasPrefix(expression, position)
                    || BracedParameterScanner.HasPrefix(expression, position))
                && cursor.SkipNestedExpansion(BracedParameterRecognition.All) is ShellNestedExpansionScan.Complete)
                continue;
            cursor.Advance();
        }

        if (normalized != null)
        {
            normalized.Append(expression.Bytes[runStart..]);
            expression = ShellSource.FromBytes(normalized.ToMemory());
        }
        return true;
    }

    private enum ArithmeticForm
    {
        Expansion,
        Command,
        ForHeader,
        AdjacentSubshell,
    }
}

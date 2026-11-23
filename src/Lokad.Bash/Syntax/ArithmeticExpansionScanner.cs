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
        out bool containsCommandSubstitution) =>
        Scan(new ShellScanContext(), text, startIndex, out endIndex, out expression, out containsCommandSubstitution,
            out _, out _, out _);

    internal static ShellScanStatus Scan(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression,
        out bool containsCommandSubstitution,
        out bool bracketed,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        bool trackRenderedLines = false) =>
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
            trackRenderedLines);

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
            trackRenderedLines);

    /// <summary>
    /// Scans a command with adjacent closers, sharing the tokenizer's nesting budget.
    /// NotFound leaves nested-subshell prefixes to the shell grammar; only Complete makes the expression valid.
    /// </summary>
    internal static ShellScanStatus ScanCommand(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression,
        out ParserFailure? error) =>
        ScanCore(context, text, startIndex, out endIndex, out expression, out _, out _, out _, out _, out error,
            captureExpression: true, form: ArithmeticForm.Command);

    internal static ShellScanStatus ScanForHeader(
        ShellScanContext context, ShellSource text, int startIndex,
        out int endIndex, out ShellSource expression, out ArithmeticForSeparators separators, out ParserFailure? error) =>
        ScanCore(context, text, startIndex, out endIndex, out expression, out _, out _, out separators, out _, out error,
            captureExpression: true, form: ArithmeticForm.ForHeader);

    /// <summary>Bounds adjacent subshell spelling after arithmetic selection and a failed body parse.</summary>
    internal static bool TryScanAdjacentSubshell(ShellScanContext context, ShellSource text, int startIndex,
        out int endIndex) =>
        ScanCore(context, text, startIndex, out endIndex, out _, out _, out _, out _, out _, out _,
            captureExpression: false, form: ArithmeticForm.AdjacentSubshell) == ShellScanStatus.Complete;

    private static ShellScanStatus ScanCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression,
        out bool containsCommandSubstitution,
        out bool bracketed,
        out ArithmeticForSeparators separators,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        bool captureExpression,
        ArithmeticForm form,
        bool trackRenderedLines = false)
    {
        endIndex = startIndex;
        expression = ShellSource.Empty;
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

                if (nestedResult is ShellNestedExpansionScan.Incomplete)
                    return ShellScanStatus.Incomplete;
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
                        // A continued double closer is invalid arithmetic, not a subshell fallback.
                        return ShellSourceCursor.TryMatchLogicalText(span, cursor.Position, "))", out _)
                            ? ShellScanStatus.Invalid
                            : ShellScanStatus.NotFound;
                    }

                    // This closes the subshell opener in a command substitution.
                    // A later pair belongs to another lexical boundary.
                    return ShellSourceCursor.SkipLineContinuations(span, cursor.Position + 1) == text.Length
                        ? ShellScanStatus.Incomplete
                        : ShellScanStatus.NotFound;
                }

                endIndex = closingEnd - 1;
                if (captureExpression)
                {
                    var source = text.Slice(expressionStart, cursor.Position - expressionStart);
                    expression = form == ArithmeticForm.ForHeader
                        ? ShellSourceCursor.ReadArithmeticForHeader(context,
                            FunctionDefinitionFormatter.RenderReadAheadCaptures(source, context), out separators)
                        : ShellSourceCursor.PrepareExpansionSource(context, source, ref renderedLineAdjustment);
                }
                return ShellScanStatus.Complete;
            }

            return ShellScanStatus.Incomplete;
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
                // Other escapes belong to the arithmetic operand's own expansion.
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

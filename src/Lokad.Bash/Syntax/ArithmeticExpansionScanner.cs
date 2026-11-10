using System;

namespace Lokad.Bash.Syntax;

/// <summary>Scans arithmetic expansions and commands with shared nesting rules and distinct closing delimiters.</summary>
internal static class ArithmeticExpansionScanner
{
    /// <summary>Gets whether a validated position begins <c>$((</c>.</summary>
    public static bool HasPrefix(ShellSource text, int startIndex) =>
        startIndex >= 0
        && ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "$((", out _);

    /// <summary>Scans a complete arithmetic expansion and returns its inclusive final parenthesis.</summary>
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
        ScanCore(
            new ShellScanContext(),
            text,
            startIndex,
            out endIndex,
            out expression,
            out containsCommandSubstitution,
            captureExpression: true,
            form: ArithmeticForm.Expansion);

    internal static ShellScanStatus ScanShape(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out bool containsCommandSubstitution) =>
        ScanCore(
            context,
            text,
            startIndex,
            out endIndex,
            out _,
            out containsCommandSubstitution,
            captureExpression: false,
            form: ArithmeticForm.Expansion);

    /// <summary>Captures an expression while retaining the tokenizer's shared nesting budget.</summary>
    internal static bool TryScan(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression) =>
        ScanCore(context, text, startIndex, out endIndex, out expression, out _,
            captureExpression: true, form: ArithmeticForm.Expansion)
            == ShellScanStatus.Complete;

    /// <summary>
    /// Scans a command with adjacent closers, sharing the tokenizer's nesting budget.
    /// NotFound leaves nested-subshell prefixes to the shell grammar; only Complete makes the expression valid.
    /// </summary>
    internal static ShellScanStatus ScanCommand(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression) =>
        ScanCore(context, text, startIndex, out endIndex, out expression, out _,
            captureExpression: true, form: ArithmeticForm.Command);

    private static ShellScanStatus ScanCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource expression,
        out bool containsCommandSubstitution,
        bool captureExpression,
        ArithmeticForm form)
    {
        endIndex = startIndex;
        expression = ShellSource.Empty;
        containsCommandSubstitution = false;
        var prefix = form == ArithmeticForm.Expansion ? "$((" : "((";
        if (startIndex < 0
            || !ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, prefix, out var expressionStart))
            return ShellScanStatus.NotFound;
        if (!context.TryEnter())
            return ShellScanStatus.Invalid;

        try
        {
            var cursor = new ShellSourceCursor(text, expressionStart, context);
            var parenthesisDepth = 0;
            while (!cursor.IsAtEnd)
            {
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;

                var nestedResult = cursor.SkipNestedExpansion(form == ArithmeticForm.Command
                    ? BracedParameterRecognition.DoubleQuotedOnly
                    : BracedParameterRecognition.All);
                if (nestedResult is ShellNestedExpansionScan.Complete nested)
                {
                    containsCommandSubstitution |= nested.ContainsCommandSubstitution;
                    continue;
                }

                if (nestedResult is ShellNestedExpansionScan.Incomplete)
                    return ShellScanStatus.Incomplete;
                if (nestedResult is ShellNestedExpansionScan.Invalid)
                    return ShellScanStatus.Invalid;

                if (cursor.IsQuoted)
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == (byte)'(')
                {
                    parenthesisDepth++;
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current != (byte)')')
                {
                    cursor.Advance();
                    continue;
                }

                if (parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                    cursor.Advance();
                    continue;
                }

                // Expansion closers can cross a continuation; command closers must be adjacent.
                var closingEnd = cursor.Position + 2;
                var closed = form == ArithmeticForm.Command
                    ? text.AsSpan()[cursor.Position..].StartsWith("))"u8)
                    : ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), cursor.Position, "))", out closingEnd);
                if (!closed)
                {
                    if (form == ArithmeticForm.Command)
                    {
                        // A continued double closer is invalid arithmetic, not a subshell fallback.
                        return ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), cursor.Position, "))", out _)
                            ? ShellScanStatus.Invalid
                            : ShellScanStatus.NotFound;
                    }

                    cursor.Advance();
                    continue;
                }

                endIndex = closingEnd - 1;
                if (captureExpression)
                    expression = ShellSourceCursor.RemoveLineContinuations(
                        text.Slice(expressionStart, cursor.Position - expressionStart));
                return ShellScanStatus.Complete;
            }

            return ShellScanStatus.Incomplete;
        }
        finally
        {
            context.Exit();
        }
    }

    private enum ArithmeticForm
    {
        Expansion,
        Command,
    }
}

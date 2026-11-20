using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Scans braced parameter expansions while honoring nested expansions, quotes, and substitutions.</summary>
internal static class BracedParameterScanner
{
    /// <summary>Gets whether a validated position begins <c>${</c>.</summary>
    public static bool HasPrefix(ShellSource text, int startIndex) =>
        startIndex >= 0
        && ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "${", out _);

    /// <summary>Scans a complete braced expansion and returns its inclusive closing brace.</summary>
    public static bool TryScan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource payload) =>
        TryScan(text, startIndex, out endIndex, out payload, out _);

    /// <summary>
    /// Scans a complete braced expansion and reports whether command substitution occurs
    /// at any nested depth.
    /// </summary>
    public static bool TryScan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource payload,
        out bool containsCommandSubstitution)
    {
        var status = ScanCore(
            new ShellScanContext(),
            text,
            startIndex,
            out endIndex,
            out _,
            out payload,
            out containsCommandSubstitution,
            out _,
            out _,
            out _,
            capturePayload: true, trackRenderedLines: false);
        return status == BracedParameterScanStatus.Found;
    }

    /// <summary>Scans only the delimiter shape and reports a structured failure status.</summary>
    public static BracedParameterScanStatus Scan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex) =>
        ScanCore(
            new ShellScanContext(),
            text,
            startIndex,
            out endIndex,
            out errorIndex,
            out _,
            out _,
            out _,
            out _,
            out _,
            capturePayload: false, trackRenderedLines: false);

    internal static BracedParameterScanStatus ScanShape(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out bool containsCommandSubstitution,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        bool trackRenderedLines) =>
        ScanCore(
            context,
            text,
            startIndex,
            out endIndex,
            out errorIndex,
            out _,
            out containsCommandSubstitution,
            out renderedLineAdjustment,
            out error,
            out subscriptFailure,
            capturePayload: false, trackRenderedLines);

    /// <summary>Captures normalized payload bytes while retaining the structural scan's lookahead context.</summary>
    internal static BracedParameterScanStatus ScanWithPayload(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out ShellSource payload,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        bool trackRenderedLines = false) =>
        ScanWithPayload(context, text, startIndex, out endIndex, out errorIndex, out payload,
            out renderedLineAdjustment, out error, out _, trackRenderedLines);

    internal static BracedParameterScanStatus ScanWithPayload(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out ShellSource payload,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        bool trackRenderedLines) =>
        ScanCore(context, text, startIndex, out endIndex, out errorIndex, out payload, out _,
            out renderedLineAdjustment, out error, out subscriptFailure, capturePayload: true, trackRenderedLines);

    private static BracedParameterScanStatus ScanCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out ShellSource payload,
        out bool containsCommandSubstitution,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        bool capturePayload,
        bool trackRenderedLines)
    {
        endIndex = startIndex;
        errorIndex = startIndex;
        payload = ShellSource.Empty;
        containsCommandSubstitution = false;
        renderedLineAdjustment = 0;
        error = null;
        subscriptFailure = ParameterSubscriptFailure.None;
        if (!HasPrefix(text, startIndex))
            return BracedParameterScanStatus.NotBraced;
        if (!context.TryEnter())
            return BracedParameterScanStatus.InvalidNestedSyntax;

        try
        {
            _ = ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "${", out var payloadStart);
            var nameEnd = ReadSimpleNameEnd(text.AsSpan(), payloadStart);
            var cursor = new ShellSourceCursor(text, nameEnd, context);
            var openingBracket = nameEnd > payloadStart && nameEnd < text.Length && text[nameEnd] == (byte)'[' ? nameEnd : -1;
            var selectorDepth = 0;
            while (!cursor.IsAtEnd)
            {
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;

                var nestedResult = cursor.SkipNestedExpansion(BracedParameterRecognition.All, trackRenderedLines);
                if (nestedResult is ShellNestedExpansionScan.Complete nested)
                {
                    containsCommandSubstitution |= nested.ContainsCommandSubstitution;
                    renderedLineAdjustment += nested.RenderedLineAdjustment;
                    if (nested.ParameterSubscriptFailure != ParameterSubscriptFailure.None)
                        subscriptFailure = ParameterSubscriptFailure.Nested;
                    continue;
                }

                if (nestedResult is ShellNestedExpansionScan.Incomplete incomplete)
                {
                    errorIndex = cursor.Position;
                    return incomplete.Kind == ShellNestedExpansionKind.Command
                        ? BracedParameterScanStatus.UnterminatedCommandSubstitution
                        : BracedParameterScanStatus.Unterminated;
                }

                if (nestedResult is ShellNestedExpansionScan.Invalid invalid)
                {
                    errorIndex = cursor.Position;
                    error = invalid.Failure;
                    return BracedParameterScanStatus.InvalidNestedSyntax;
                }

                if (!cursor.IsDoubleQuoted && cursor.Current == (byte)'}')
                {
                    endIndex = cursor.Position;
                    if (selectorDepth != 0)
                        subscriptFailure = ParameterSubscriptFailure.Direct;
                    if (capturePayload)
                        payload = ShellSourceCursor.RemoveLineContinuations(
                            context, text.Slice(payloadStart, cursor.Position - payloadStart));
                    return BracedParameterScanStatus.Found;
                }

                if (cursor.Position == openingBracket || selectorDepth != 0 && !cursor.IsQuoted)
                {
                    if (cursor.Current == (byte)'[')
                        selectorDepth++;
                    else if (cursor.Current == (byte)']')
                        selectorDepth--;
                }
                cursor.Advance();
            }

            errorIndex = cursor.Position;
            return BracedParameterScanStatus.Unterminated;
        }
        finally
        {
            context.Exit();
        }
    }

    private static int ReadSimpleNameEnd(ReadOnlySpan<byte> text, int start)
    {
        var index = ShellSourceCursor.SkipLineContinuations(text, start);
        if (index < text.Length && text[index] == (byte)'!')
            index = ShellSourceCursor.SkipLineContinuations(text, index + 1);
        if (index == text.Length || !VariableNames.IsNameStart((char)text[index]))
            return start;
        do { index = ShellSourceCursor.SkipLineContinuations(text, index + 1); }
        while (index < text.Length && VariableNames.IsNamePart((char)text[index]));
        return index;
    }
}

internal enum ParameterSubscriptFailure : byte { None, Direct, Nested }

internal enum BracedParameterScanStatus
{
    /// <summary>The requested position does not begin <c>${</c>.</summary>
    NotBraced,

    /// <summary>A complete expansion and closing brace were found.</summary>
    Found,

    /// <summary>The outer expansion or a non-command nested expansion is incomplete.</summary>
    Unterminated,

    /// <summary>A nested command substitution is incomplete.</summary>
    UnterminatedCommandSubstitution,

    /// <summary>A nested expansion has a closing delimiter candidate but invalid syntax.</summary>
    InvalidNestedSyntax,
}

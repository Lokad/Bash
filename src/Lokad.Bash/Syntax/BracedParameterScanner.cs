using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Scans braced parameter expansions while honoring nested expansions, quotes, and substitutions.</summary>
internal static class BracedParameterScanner
{
    private enum ScanPhase { Lexical, Operand, Expansion }

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
            out _,
            capturePayload: true, trackRenderedLines: false, ScanPhase.Lexical);
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
            out _,
            capturePayload: false, trackRenderedLines: false, ScanPhase.Lexical);

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
        out bool hasLateSubscript,
        bool trackRenderedLines,
        bool resolveLateSubscripts) =>
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
            out hasLateSubscript,
            capturePayload: false, trackRenderedLines, resolveLateSubscripts ? ScanPhase.Operand : ScanPhase.Lexical);

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
        out ParameterSubscriptFailure subscriptFailure,
        bool trackRenderedLines) =>
        ScanCore(context, text, startIndex, out endIndex, out errorIndex, out payload, out _,
            out renderedLineAdjustment, out error, out subscriptFailure, out _, capturePayload: true, trackRenderedLines, ScanPhase.Lexical);

    /// <summary>Reads an expansion within an already delimited word, including a selector spanning an early brace.</summary>
    internal static BracedParameterScanStatus ScanExpansion(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        bool capturePayload,
        out int endIndex,
        out ShellSource payload,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure) =>
        ScanCore(context, text, startIndex, out endIndex, out _, out payload, out _, out _, out error,
            out subscriptFailure, out _, capturePayload, trackRenderedLines: false, ScanPhase.Expansion);

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
        out bool hasLateSubscript,
        bool capturePayload,
        bool trackRenderedLines,
        ScanPhase phase)
    {
        endIndex = startIndex;
        errorIndex = startIndex;
        payload = ShellSource.Empty;
        containsCommandSubstitution = false;
        renderedLineAdjustment = 0;
        error = null;
        subscriptFailure = ParameterSubscriptFailure.None;
        hasLateSubscript = false;
        if (!HasPrefix(text, startIndex))
            return BracedParameterScanStatus.NotBraced;
        if (!context.TryEnter())
            return BracedParameterScanStatus.InvalidNestedSyntax;

        try
        {
            _ = ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "${", out var payloadStart);
            var start = ShellSourceCursor.SkipLineContinuations(text.AsSpan(), payloadStart);
            var lengthForm = start < text.Length && text[start] == (byte)'#';
            var nameStart = lengthForm ? start + 1 : start;
            var nameEnd = ReadSimpleNameEnd(text.AsSpan(), nameStart);
            var cursor = new ShellSourceCursor(text, nameEnd, context);
            var openingBracket = nameEnd > nameStart && nameEnd < text.Length
                && text[nameEnd] == (byte)'[' ? nameEnd : -1;
            var selectorDepth = 0;
            var lateSelectorEnd = -1;
            var firstBrace = -1;
            var resolveLateSubscripts = phase != ScanPhase.Lexical;
            while (!cursor.IsAtEnd)
            {
                if (cursor.TrySkipReadAheadBodies())
                    continue;
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;

                var nestedResult = cursor.SkipNestedExpansion(BracedParameterRecognition.All, trackRenderedLines, resolveLateSubscripts);
                if (nestedResult is ShellNestedExpansionScan.Complete nested)
                {
                    containsCommandSubstitution |= nested.ContainsCommandSubstitution;
                    renderedLineAdjustment += nested.RenderedLineAdjustment;
                    hasLateSubscript |= nested.HasLateSubscript;
                    if (nested.ParameterSubscriptFailure is ParameterSubscriptFailure.Direct or ParameterSubscriptFailure.Nested)
                        subscriptFailure = ParameterSubscriptFailure.Nested;
                    continue;
                }

                if (nestedResult is ShellNestedExpansionScan.Incomplete incomplete)
                {
                    errorIndex = cursor.Position;
                    if (incomplete.Kind == ShellNestedExpansionKind.Command && lateSelectorEnd < 0)
                        return BracedParameterScanStatus.UnterminatedCommandSubstitution;
                    if (resolveLateSubscripts && incomplete.ParameterSubscriptFailure != ParameterSubscriptFailure.None)
                        subscriptFailure = ParameterSubscriptFailure.Nested;
                    break;
                }

                if (nestedResult is ShellNestedExpansionScan.Invalid invalid)
                {
                    errorIndex = cursor.Position;
                    error = invalid.Failure;
                    return BracedParameterScanStatus.InvalidNestedSyntax;
                }

                if (!cursor.IsDoubleQuoted && cursor.Current == (byte)'}')
                {
                    // Operand boundaries treat the leading '#' as an operator. A directly
                    // reached length expression scans its name through the array selector.
                    if (selectorDepth != 0 && resolveLateSubscripts && (!lengthForm || phase == ScanPhase.Expansion)
                        && context.TryScanLateSubscript(text, openingBracket, cursor.Position, out lateSelectorEnd))
                    {
                        hasLateSubscript = true;
                        selectorDepth = 0;
                        firstBrace = cursor.Position;
                        cursor = new ShellSourceCursor(text, lateSelectorEnd + 1, context);
                        continue;
                    }

                    endIndex = cursor.Position;
                    if (selectorDepth != 0)
                        subscriptFailure = lengthForm ? ParameterSubscriptFailure.Length : ParameterSubscriptFailure.Direct;
                    if (capturePayload)
                        payload = ShellSourceCursor.PrepareExpansionSource(
                            context, text.Slice(payloadStart, cursor.Position - payloadStart), ref renderedLineAdjustment);
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
            if (lateSelectorEnd >= 0)
            {
                subscriptFailure = lengthForm ? ParameterSubscriptFailure.Length : ParameterSubscriptFailure.Direct;
                var next = ShellSourceCursor.SkipLineContinuations(text.AsSpan(), lateSelectorEnd + 1);
                var completeName = next == text.Length || "#%^,:-=?+/@}"u8.Contains(text[next]);
                endIndex = completeName ? lateSelectorEnd : firstBrace;
                if (capturePayload)
                {
                    var payloadEnd = completeName ? endIndex + 1 : endIndex;
                    payload = ShellSourceCursor.PrepareExpansionSource(context,
                        text.Slice(payloadStart, payloadEnd - payloadStart), ref renderedLineAdjustment);
                }
                return completeName ? BracedParameterScanStatus.MissingBraceAfterSelector : BracedParameterScanStatus.Found;
            }
            if (hasLateSubscript)
                subscriptFailure = ParameterSubscriptFailure.Nested;
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

internal enum ParameterSubscriptFailure : byte { None, Direct, Nested, Length }

internal enum BracedParameterScanStatus
{
    /// <summary>The requested position does not begin <c>${</c>.</summary>
    NotBraced,

    /// <summary>A complete expansion and closing brace were found.</summary>
    Found,

    /// <summary>A complete array name precedes end of word without an enclosing brace.</summary>
    MissingBraceAfterSelector,

    /// <summary>The outer expansion or a non-command nested expansion is incomplete.</summary>
    Unterminated,

    /// <summary>A nested command substitution is incomplete.</summary>
    UnterminatedCommandSubstitution,

    /// <summary>A nested expansion has a closing delimiter candidate but invalid syntax.</summary>
    InvalidNestedSyntax,
}

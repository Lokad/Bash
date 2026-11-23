using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>Locates array subscripts without confusing nested shell syntax for bracket delimiters.</summary>
internal static class ArraySubscriptScanner
{
    /// <summary>Reuses bracket matches for assignment and arithmetic lookahead over one immutable input.</summary>
    internal sealed class Lookahead(ShellSource source, ShellScanContext context)
    {
        private readonly Dictionary<int, ScanResult> _results = [];

        public ShellScanStatus Scan(int openingBracketIndex, out int closingBracketIndex) =>
            Scan(openingBracketIndex, out closingBracketIndex, out _);

        internal ShellScanStatus Scan(int openingBracketIndex, out int closingBracketIndex,
            out SubstitutionSyntaxFailure quotedCapture) =>
            ArraySubscriptScanner.Scan(context, source, openingBracketIndex, source.Length, _results, recognizeAnsiC: true,
                out closingBracketIndex, out quotedCapture);
    }

    private readonly record struct ScanResult(ShellScanStatus Status, int ClosingBracketIndex,
        SubstitutionSyntaxFailure QuotedCapture);

    /// <summary>Scans from an opening bracket through its matching closing bracket.</summary>
    public static bool TryScan(
        ShellSource text,
        int openingBracketIndex,
        out int closingBracketIndex) =>
        Scan(text, openingBracketIndex, out closingBracketIndex) == ShellScanStatus.Complete;

    internal static bool TryScan(
        ShellScanContext context,
        ShellSource text,
        int openingBracketIndex,
        out int closingBracketIndex) =>
        TryScan(context, text, openingBracketIndex, recognizeAnsiC: true, out closingBracketIndex);

    internal static bool TryScan(ShellScanContext context, ShellSource text, int openingBracketIndex,
        bool recognizeAnsiC, out int closingBracketIndex) =>
        Scan(context, text, openingBracketIndex, text.Length, null, recognizeAnsiC, out closingBracketIndex, out _)
            == ShellScanStatus.Complete;

    /// <summary>Matches brackets within an already read word, retaining the source for nested reads.</summary>
    internal static bool TryScanWord(ShellScanContext context, ShellSource text, int openingBracketIndex, int wordEnd,
        out int closingBracketIndex) =>
        Scan(context, text, openingBracketIndex, wordEnd, null, recognizeAnsiC: true, out closingBracketIndex, out _)
            == ShellScanStatus.Complete;

    /// <summary>Distinguishes an absent bracket, complete subscript, incomplete input, and malformed syntax.</summary>
    public static ShellScanStatus Scan(
        ShellSource text,
        int openingBracketIndex,
        out int closingBracketIndex) =>
        Scan(new ShellScanContext(), text, openingBracketIndex, text.Length, null, recognizeAnsiC: true,
            out closingBracketIndex, out _);

    private static ShellScanStatus Scan(
        ShellScanContext context,
        ShellSource text,
        int openingBracketIndex,
        int end,
        Dictionary<int, ScanResult>? results,
        bool recognizeAnsiC,
        out int closingBracketIndex,
        out SubstitutionSyntaxFailure quotedCapture)
    {
        closingBracketIndex = openingBracketIndex;
        quotedCapture = SubstitutionSyntaxFailure.None;
        if (openingBracketIndex < 0
            || openingBracketIndex >= end
            || text[openingBracketIndex] != (byte)'[')
        {
            return ShellScanStatus.NotFound;
        }

        if (!context.TryEnter())
            return ShellScanStatus.Invalid;

        try
        {
            if (results != null && results.TryGetValue(openingBracketIndex, out var cached))
            {
                closingBracketIndex = cached.ClosingBracketIndex;
                quotedCapture = cached.QuotedCapture;
                return cached.Status;
            }

            // Every unquoted opening has the same suffix scan, even if its enclosing
            // word turns out not to be an assignment. Bound cached entries as syntax.
            Stack<int>? openings = results == null ? null : new();
            if (!TryRememberOpening(openingBracketIndex))
                return ShellScanStatus.Invalid;
            var cursor = new ShellSourceCursor(text, openingBracketIndex + 1, context);
            var bracketDepth = 1;
            var lastRejectedQuote = -1;
            var lastQuotedFailure = SubstitutionSyntaxFailure.None;
            var trackQuotedCaptures = results != null && context.CanReadAhead(text);
            while (cursor.Position < end)
            {
                if (cursor.TrySkipReadAheadBodies())
                    continue;
                if (cursor.TryConsumeQuoteOrEscape(recognizeAnsiC))
                    continue;

                var start = cursor.Position;
                var inspectQuotedExpansion = trackQuotedCaptures && cursor.IsDoubleQuoted && cursor.Current == (byte)'$';
                var reads = inspectQuotedExpansion ? context.RejectedCaptureReads : 0;
                var nestedResult = cursor.SkipNestedExpansion(BracedParameterRecognition.All);
                if (nestedResult is ShellNestedExpansionScan.Complete)
                {
                    if (inspectQuotedExpansion && context.RejectedCaptureReads != reads)
                    {
                        lastRejectedQuote = start;
                        lastQuotedFailure = context.LastRejectedCaptureRead;
                    }
                    continue;
                }
                if (nestedResult is ShellNestedExpansionScan.Incomplete)
                    return FinishIncomplete(ShellScanStatus.Incomplete);
                if (nestedResult is ShellNestedExpansionScan.Invalid)
                    return FinishIncomplete(ShellScanStatus.Invalid);

                if (cursor.IsQuoted)
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == (byte)'[')
                {
                    if (!TryRememberOpening(cursor.Position))
                        return ShellScanStatus.Invalid;
                    bracketDepth++;
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current != (byte)']')
                {
                    cursor.Advance();
                    continue;
                }

                if (results != null && openings != null)
                {
                    var opening = openings.Pop();
                    var failure = lastRejectedQuote > opening ? lastQuotedFailure : SubstitutionSyntaxFailure.None;
                    results[opening] = new ScanResult(ShellScanStatus.Complete, cursor.Position, failure);
                }
                bracketDepth--;
                if (bracketDepth == 0)
                {
                    closingBracketIndex = cursor.Position;
                    quotedCapture = lastQuotedFailure;
                    return ShellScanStatus.Complete;
                }

                cursor.Advance();
            }

            return FinishIncomplete(ShellScanStatus.Incomplete);

            bool TryRememberOpening(int position)
            {
                if (openings == null)
                    return true;
                if (!context.TryReserveSyntax())
                {
                    context.RecordLimitError(new SyntaxError("Script parsing limit exceeded (too many array subscripts).", position));
                    return false;
                }
                openings.Push(position);
                return true;
            }

            ShellScanStatus FinishIncomplete(ShellScanStatus status)
            {
                if (results != null && openings != null)
                    foreach (var opening in openings)
                        results[opening] = new ScanResult(status, opening, SubstitutionSyntaxFailure.None);
                return status;
            }
        }
        finally
        {
            context.Exit();
        }
    }
}

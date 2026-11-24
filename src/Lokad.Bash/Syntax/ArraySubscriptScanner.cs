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
            out SubstitutionSyntaxFailure quotedCapture)
        {
            var result = Read(openingBracketIndex);
            closingBracketIndex = result.ClosingBracketIndex;
            quotedCapture = result.QuotedCapture;
            return result.Status;
        }

        internal ShellScanStatus ScanAssignment(int openingBracketIndex, out int closingBracketIndex, out ParserFailure? failure)
        {
            var result = Read(openingBracketIndex);
            closingBracketIndex = result.ClosingBracketIndex;
            failure = result.Failure;
            return result.Status;
        }

        private ScanResult Read(int openingBracketIndex) =>
            ArraySubscriptScanner.Read(context, source, openingBracketIndex, source.Length, _results, recognizeAnsiC: true);
    }

    private readonly record struct ScanResult(ShellScanStatus Status, int ClosingBracketIndex,
        SubstitutionSyntaxFailure QuotedCapture)
    {
        internal ParserFailure? Failure { get; init; }
    }

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
        bool recognizeAnsiC, out int closingBracketIndex)
    {
        var result = Read(context, text, openingBracketIndex, text.Length, null, recognizeAnsiC);
        closingBracketIndex = result.ClosingBracketIndex;
        return result.Status == ShellScanStatus.Complete;
    }

    /// <summary>Matches brackets within an already read word, retaining the source for nested reads.</summary>
    internal static bool TryScanWord(ShellScanContext context, ShellSource text, int openingBracketIndex, int wordEnd,
        out int closingBracketIndex)
    {
        var result = Read(context, text, openingBracketIndex, wordEnd, null, recognizeAnsiC: true);
        closingBracketIndex = result.ClosingBracketIndex;
        return result.Status == ShellScanStatus.Complete;
    }

    /// <summary>Distinguishes an absent bracket, complete subscript, incomplete input, and malformed syntax.</summary>
    public static ShellScanStatus Scan(
        ShellSource text,
        int openingBracketIndex,
        out int closingBracketIndex)
    {
        var result = Read(new ShellScanContext(), text, openingBracketIndex, text.Length, null, recognizeAnsiC: true);
        closingBracketIndex = result.ClosingBracketIndex;
        return result.Status;
    }

    private static ScanResult Read(
        ShellScanContext context,
        ShellSource text,
        int openingBracketIndex,
        int end,
        Dictionary<int, ScanResult>? results,
        bool recognizeAnsiC)
    {
        if (openingBracketIndex < 0
            || openingBracketIndex >= end
            || text[openingBracketIndex] != (byte)'[')
        {
            return new ScanResult(ShellScanStatus.NotFound, openingBracketIndex, SubstitutionSyntaxFailure.None);
        }

        if (!context.TryEnter())
            return new ScanResult(ShellScanStatus.Invalid, openingBracketIndex, SubstitutionSyntaxFailure.None);

        try
        {
            if (results != null && results.TryGetValue(openingBracketIndex, out var cached))
                return cached;

            // Every unquoted opening has the same suffix scan, even if its enclosing
            // word turns out not to be an assignment. Bound cached entries as syntax.
            Stack<int>? openings = results == null ? null : new();
            if (!TryRememberOpening(openingBracketIndex))
                return new ScanResult(ShellScanStatus.Invalid, openingBracketIndex, SubstitutionSyntaxFailure.None);
            var cursor = new ShellSourceCursor(text, openingBracketIndex + 1, context);
            var bracketDepth = 1;
            var lastRejectedQuote = -1;
            var lastQuotedFailure = SubstitutionSyntaxFailure.None;
            var trackQuotedCaptures = results != null && context.CanReadAhead(text);
            while (cursor.Position < end)
            {
                if (cursor.TrySkipReadAheadBodies())
                    continue;
                var start = cursor.Position;
                if (cursor.TryConsumeQuoteOrEscape(recognizeAnsiC))
                    continue;

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
                if (nestedResult is ShellNestedExpansionScan.Incomplete incomplete)
                    return FinishIncomplete(ShellScanStatus.Incomplete, incomplete.Failure);
                if (nestedResult is ShellNestedExpansionScan.Invalid invalid)
                    return FinishIncomplete(ShellScanStatus.Invalid, invalid.Failure);

                if (cursor.IsQuoted)
                {
                    cursor.Advance();
                    continue;
                }

                if (cursor.Current == (byte)'[')
                {
                    if (!TryRememberOpening(cursor.Position))
                        return new ScanResult(ShellScanStatus.Invalid, openingBracketIndex, SubstitutionSyntaxFailure.None);
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
                    return new ScanResult(ShellScanStatus.Complete, cursor.Position, lastQuotedFailure);

                cursor.Advance();
            }

            ParserFailure? quoteFailure = null;
            if (results != null && cursor.UnclosedQuote is { } unclosedQuote)
            {
                var message = cursor.IsDoubleQuoted ? "Unterminated double-quoted string." : "Unterminated single-quoted string.";
                quoteFailure = new ParserFailure(new SyntaxError(message, cursor.Position), ParserFailureKind.Delimiter)
                {
                    UnclosedDelimiter = unclosedQuote
                };
            }
            return FinishIncomplete(ShellScanStatus.Incomplete, quoteFailure);

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

            ScanResult FinishIncomplete(ShellScanStatus status, ParserFailure? failure)
            {
                var result = new ScanResult(status, openingBracketIndex, SubstitutionSyntaxFailure.None) { Failure = failure };
                if (results != null && openings != null)
                    foreach (var opening in openings)
                        results[opening] = result with { ClosingBracketIndex = opening };
                return result;
            }
        }
        finally
        {
            context.Exit();
        }
    }
}

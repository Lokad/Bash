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
            ArraySubscriptScanner.Scan(context, source, openingBracketIndex, _results, out closingBracketIndex);
    }

    private readonly record struct ScanResult(ShellScanStatus Status, int ClosingBracketIndex);

    /// <summary>Scans from an opening bracket through its matching closing bracket.</summary>
    public static bool TryScan(
        ShellSource text,
        int openingBracketIndex,
        out int closingBracketIndex) =>
        Scan(text, openingBracketIndex, out closingBracketIndex) == ShellScanStatus.Complete;

    /// <summary>Distinguishes an absent bracket, complete subscript, incomplete input, and malformed syntax.</summary>
    public static ShellScanStatus Scan(
        ShellSource text,
        int openingBracketIndex,
        out int closingBracketIndex) =>
        Scan(new ShellScanContext(), text, openingBracketIndex, null, out closingBracketIndex);

    private static ShellScanStatus Scan(
        ShellScanContext context,
        ShellSource text,
        int openingBracketIndex,
        Dictionary<int, ScanResult>? results,
        out int closingBracketIndex)
    {
        closingBracketIndex = openingBracketIndex;
        if (openingBracketIndex < 0
            || openingBracketIndex >= text.Length
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
                return cached.Status;
            }

            // Every unquoted opening has the same suffix scan, even if its enclosing
            // word turns out not to be an assignment. Bound cached entries as syntax.
            Stack<int>? openings = results == null ? null : new();
            if (!TryRememberOpening(openingBracketIndex))
                return ShellScanStatus.Invalid;
            var cursor = new ShellSourceCursor(text, openingBracketIndex + 1, context);
            var bracketDepth = 1;
            while (!cursor.IsAtEnd)
            {
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;

                var nestedResult = cursor.SkipNestedExpansion(BracedParameterRecognition.All);
                if (nestedResult is ShellNestedExpansionScan.Complete)
                    continue;
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
                    results[openings.Pop()] = new ScanResult(ShellScanStatus.Complete, cursor.Position);
                bracketDepth--;
                if (bracketDepth == 0)
                {
                    closingBracketIndex = cursor.Position;
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
                        results[opening] = new ScanResult(status, opening);
                return status;
            }
        }
        finally
        {
            context.Exit();
        }
    }
}

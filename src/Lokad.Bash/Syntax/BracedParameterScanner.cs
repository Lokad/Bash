using System;

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
            capturePayload: true);
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
            capturePayload: false);

    internal static BracedParameterScanStatus ScanShape(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out bool containsCommandSubstitution) =>
        ScanCore(
            context,
            text,
            startIndex,
            out endIndex,
            out errorIndex,
            out _,
            out containsCommandSubstitution,
            capturePayload: false);

    private static BracedParameterScanStatus ScanCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out ShellSource payload,
        out bool containsCommandSubstitution,
        bool capturePayload)
    {
        endIndex = startIndex;
        errorIndex = startIndex;
        payload = ShellSource.Empty;
        containsCommandSubstitution = false;
        if (!HasPrefix(text, startIndex))
            return BracedParameterScanStatus.NotBraced;
        if (!context.TryEnter())
            return BracedParameterScanStatus.InvalidNestedSyntax;

        try
        {
            _ = ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "${", out var payloadStart);
            var cursor = new ShellSourceCursor(text, payloadStart, context);
            while (!cursor.IsAtEnd)
            {
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;

                var nestedResult = cursor.SkipNestedExpansion(BracedParameterRecognition.All);
                if (nestedResult is ShellNestedExpansionScan.Complete nested)
                {
                    containsCommandSubstitution |= nested.ContainsCommandSubstitution;
                    continue;
                }

                if (nestedResult is ShellNestedExpansionScan.Incomplete incomplete)
                {
                    errorIndex = cursor.Position;
                    return incomplete.Kind == ShellNestedExpansionKind.Command
                        ? BracedParameterScanStatus.UnterminatedCommandSubstitution
                        : BracedParameterScanStatus.Unterminated;
                }

                if (nestedResult is ShellNestedExpansionScan.Invalid)
                {
                    errorIndex = cursor.Position;
                    return BracedParameterScanStatus.InvalidNestedSyntax;
                }

                if (!cursor.IsDoubleQuoted && cursor.Current == (byte)'}')
                {
                    endIndex = cursor.Position;
                    if (capturePayload)
                        payload = ShellSourceCursor.RemoveLineContinuations(
                            text.Slice(payloadStart, cursor.Position - payloadStart));
                    return BracedParameterScanStatus.Found;
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
}

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

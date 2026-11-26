using System;
using System.Threading;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Scans command substitution payloads, honoring the delimiter rules of each form.</summary>
internal static class CommandSubstitutionScanner
{
    /// <summary>Writes a backtick word as read, with its lexical continuations removed.</summary>
    internal static bool TryAppendLegacyWordSpelling(CommandSubstitutionSyntax.Legacy legacy, IByteMemoryWriter output,
        CancellationToken cancellationToken)
    {
        var spelling = legacy.Spelling.AsSpan();
        var start = 0;
        if (legacy.RemovedLineContinuations > 0)
        {
            for (var index = 1; index + 1 < spelling.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (spelling[index] != (byte)'\\' || !IsLegacyEscape(spelling[index + 1]))
                    continue;
                if (spelling[index + 1] == (byte)'\n')
                {
                    if (!output.TryAppend(spelling[start..index]))
                        return false;
                    start = index + 2;
                }
                index++;
            }
        }
        return output.TryAppend(spelling[start..]);
    }

    private static bool IsLegacyEscape(byte value) => value is (byte)'$' or (byte)'`' or (byte)'\\' or (byte)'\n';

    /// <summary>Gets whether a position can begin a command substitution after arithmetic selection.</summary>
    public static bool HasPrefix(ShellSource text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length)
            return false;

        if (text[startIndex] == (byte)'`')
            return true;

        return ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "$(", out _);
    }

    /// <summary>
    /// Scans one complete substitution and returns its inclusive closing delimiter and payload.
    /// Malformed and incomplete input return false without accepting an earlier candidate.
    /// </summary>
    public static bool TryScan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource payload)
    {
        var status = Scan(text, startIndex, out endIndex, out payload);
        return status == ShellScanStatus.Complete;
    }

    /// <summary>Distinguishes absent, complete, incomplete, and malformed substitutions.</summary>
    public static ShellScanStatus Scan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource payload)
    {
        var context = new ShellScanContext();
        var arithmetic = ArithmeticExpansionScanner.ScanShape(context, text, startIndex, out _, out _, out _, out _);
        if (arithmetic != ShellScanStatus.NotFound)
        {
            endIndex = startIndex;
            payload = ShellSource.Empty;
            return ShellScanStatus.NotFound;
        }

        var status = ScanParsedForLaterReuse(context, text, startIndex, out endIndex, out var substitution, out _);
        payload = substitution?.Source ?? ShellSource.Empty;
        return status;
    }

    /// <summary>Transfers a lookahead parse to its consuming tokenizer, or parses it once here.</summary>
    internal static ShellScanStatus ScanParsed(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out CommandSubstitutionSyntax? substitution,
        out ParserFailure? error) =>
        ScanParsed(context, text, startIndex, context.PreserveDelimiterLookahead || context.RetainCaptureLookahead,
            out endIndex, out substitution, out error);

    /// <summary>Keeps a structural lookahead result until tokenization reaches the same source position.</summary>
    internal static ShellScanStatus ScanParsedForLaterReuse(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out CommandSubstitutionSyntax? substitution,
        out ParserFailure? error) =>
        ScanParsed(context, text, startIndex, keepLookahead: true, out endIndex, out substitution, out error);

    /// <summary>Uses the arithmetic opener only when its adjacent-subshell fallback runs out of input.</summary>
    internal static ParserFailure? KeepArithmeticOpeningAtEof(
        ShellScanStatus status, ParserFailure? failure, UnclosedDelimiter? arithmeticOpening) =>
        status == ShellScanStatus.Incomplete && arithmeticOpening != null
            && failure?.UnclosedDelimiter is { Delimiter: ')', AtOpeningDelimiter: false }
            ? failure with { UnclosedDelimiter = arithmeticOpening } : failure;

    private static ShellScanStatus ScanParsed(ShellScanContext context, ShellSource text, int startIndex,
        bool keepLookahead, out int endIndex, out CommandSubstitutionSyntax? substitution, out ParserFailure? error)
    {
        error = null;
        if (keepLookahead ? context.TryGetCommandSubstitution(text, startIndex, out var cached)
            : context.TryTakeCommandSubstitution(text, startIndex, out cached))
        {
            endIndex = startIndex + cached.Length - 1;
            substitution = cached.Substitution;
            RecordSyntaxFailure(context, substitution);
            return ShellScanStatus.Complete;
        }
        var previousPeak = context.BeginLookahead();
        var readArithmeticComments = context.ReadArithmeticExpansionComments;
        try
        {
            // A nested command reader locates its syntax before the command's words expand.
            context.ReadArithmeticExpansionComments = false;
            var status = ScanParsedCore(context, text, startIndex, out endIndex, out substitution, out error);
            // Reading the same capture again must reuse the bodies already taken from its source.
            if (substitution != null
                && (keepLookahead || substitution is CommandSubstitutionSyntax.Parsed { Script.HasReadAheadHereDocuments: true }))
                context.StoreCommandSubstitution(text, startIndex, endIndex - startIndex + 1, substitution);
            RecordSyntaxFailure(context, substitution);
            return status;
        }
        finally
        {
            context.ReadArithmeticExpansionComments = readArithmeticComments;
            context.EndLookahead(previousPeak);
        }
    }

    private static void RecordSyntaxFailure(ShellScanContext context, CommandSubstitutionSyntax? substitution)
    {
        if (substitution is not CommandSubstitutionSyntax.Parsed parsed)
            return;
        if (context.SubstitutionFailure == SubstitutionSyntaxFailure.None)
            context.SubstitutionFailure = parsed.Failure;
        if (parsed.HasRejectedDisplaySpelling)
        {
            context.RejectedCaptureReads++;
            context.LastRejectedCaptureRead = parsed.Failure;
        }
    }

    private static ShellScanStatus ScanParsedCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out CommandSubstitutionSyntax? substitution,
        out ParserFailure? error)
    {
        substitution = null;
        error = null;
        endIndex = startIndex;
        if (!HasPrefix(text, startIndex))
            return ShellScanStatus.NotFound;

        if (text[startIndex] == (byte)'`')
            return ScanLegacyBackticks(context, text, startIndex, out endIndex, out substitution);
        if (!context.TryEnter())
            return ShellScanStatus.Invalid;

        var enclosingFailure = context.SubstitutionFailure;
        context.SubstitutionFailure = SubstitutionSyntaxFailure.None;
        try
        {
            _ = ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "$(", out var payloadStart);
            var status = Parser.ScanCommandSubstitutionEnd(
                context,
                text,
                payloadStart,
                out endIndex,
                out var parsed,
                out var failure,
                out var preserveAdjacentOpening,
                out var adjacentSpelling,
                out var deferredWarnings,
                out error);
            if (status == ShellScanStatus.Complete)
            {
                var payload = text.Slice(payloadStart, endIndex - payloadStart);
                substitution = parsed switch
                {
                    null when preserveAdjacentOpening =>
                        new CommandSubstitutionSyntax.AdjacentSubshell(ShellSourceCursor.RemoveLineContinuations(context, payload)),
                    null => new CommandSubstitutionSyntax.Deferred(payload) { ReadWarnings = deferredWarnings },
                    _ => new CommandSubstitutionSyntax.Parsed(payload, parsed, failure, preserveAdjacentOpening)
                        { AdjacentSpelling = adjacentSpelling }
                };
            }
            else if (status == ShellScanStatus.Incomplete && error is { UnclosedDelimiter: not null })
                error = error with { Error = error.Error with { Offset = startIndex } };
            else if (status == ShellScanStatus.Invalid && error is { UnclosedDelimiter: null } && context.LimitError == null
                && ArithmeticExpansionScanner.TryScanAdjacentSubshell(context, text, startIndex, out var adjacentEnd))
            {
                endIndex = adjacentEnd;
                substitution = new CommandSubstitutionSyntax.AdjacentSubshell(text.Slice(payloadStart, endIndex - payloadStart));
                error = null;
                status = ShellScanStatus.Complete;
            }

            return status;
        }
        finally
        {
            context.SubstitutionFailure = enclosingFailure;
            context.Exit();
        }

        static ShellScanStatus ScanLegacyBackticks(
            ShellScanContext context,
            ShellSource source,
            int substitutionStart,
            out int closingIndex,
            out CommandSubstitutionSyntax? legacyPayload)
        {
            legacyPayload = null;
            closingIndex = substitutionStart;
            var builder = context.CreateByteBuilder(0);
            var position = substitutionStart + 1;
            var removedLineContinuations = 0;
            while (position < source.Length)
            {
                var nextPosition = context.HereDocumentReadAhead?.SkipBodies(source, position) ?? position;
                if (nextPosition != position)
                {
                    position = nextPosition;
                    continue;
                }
                var current = source[position];
                if (current == (byte)'`')
                {
                    var spelling = source.Slice(substitutionStart, position - substitutionStart + 1);
                    spelling = context.HereDocumentReadAhead?.CopyWithoutBodies(spelling, context) ?? spelling;
                    legacyPayload = new CommandSubstitutionSyntax.Legacy(ShellSource.FromBytes(builder.ToMemory()), spelling,
                        context.LegacyOrigin)
                    {
                        RemovedLineContinuations = removedLineContinuations
                    };
                    closingIndex = position;
                    return ShellScanStatus.Complete;
                }

                if (current == (byte)'\\' && position + 1 < source.Length && IsLegacyEscape(source[position + 1]))
                {
                    var next = source[position + 1];
                    if (next == (byte)'\n')
                    {
                        removedLineContinuations++;
                        position += 2;
                        continue;
                    }
                    builder.Append(next);
                    position += 2;
                    continue;
                }

                builder.Append(current);
                position++;
            }

            return ShellScanStatus.Incomplete;
        }
    }
}

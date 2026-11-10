using System;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Scans command substitution payloads, honoring the delimiter rules of each form.</summary>
internal static class CommandSubstitutionScanner
{
    /// <summary>Gets whether a position begins <c>$(...)</c> or legacy backticks.</summary>
    public static bool HasPrefix(ShellSource text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length)
            return false;

        if (text[startIndex] == (byte)'`')
            return true;

        if (!ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "$(", out var payloadStart))
            return false;

        var next = ShellSourceCursor.SkipLineContinuations(text.AsSpan(), payloadStart);
        return next >= text.Length || text[next] != (byte)'(';
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
        out ShellSource payload) =>
        Scan(new ShellScanContext(), text, startIndex, out endIndex, out payload);

    internal static ShellScanStatus Scan(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource payload)
    {
        var status = ScanParsedForLaterReuse(context, text, startIndex, out endIndex, out var substitution);
        payload = substitution?.Source ?? ShellSource.Empty;
        return status;
    }

    /// <summary>Returns executable syntax, reusing the body parsed while locating its delimiter.</summary>
    internal static bool TryScanParsed(
        ShellSource text,
        int startIndex,
        out int endIndex,
        [NotNullWhen(true)] out CommandSubstitutionSyntax? substitution) =>
        ScanParsed(new ShellScanContext(), text, startIndex, out endIndex, out substitution) == ShellScanStatus.Complete;

    /// <summary>Transfers a lookahead parse to its consuming tokenizer, or parses it once here.</summary>
    internal static ShellScanStatus ScanParsed(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out CommandSubstitutionSyntax? substitution)
    {
        if (context.TryTakeCommandSubstitution(text, startIndex, out var cached))
        {
            endIndex = cached.EndIndex;
            substitution = cached.Substitution;
            return ShellScanStatus.Complete;
        }
        return ScanParsedCore(context, text, startIndex, out endIndex, out substitution);
    }

    /// <summary>Keeps a structural lookahead result until tokenization reaches the same source position.</summary>
    internal static ShellScanStatus ScanParsedForLaterReuse(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out CommandSubstitutionSyntax? substitution)
    {
        if (context.TryGetCommandSubstitution(text, startIndex, out var cached))
        {
            endIndex = cached.EndIndex;
            substitution = cached.Substitution;
            return ShellScanStatus.Complete;
        }
        var status = ScanParsedCore(context, text, startIndex, out endIndex, out substitution);
        if (substitution != null)
            context.StoreCommandSubstitution(text, startIndex, new ShellScanContext.CachedParse(endIndex, substitution));
        return status;
    }

    private static ShellScanStatus ScanParsedCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out CommandSubstitutionSyntax? substitution)
    {
        substitution = null;
        endIndex = startIndex;
        if (!HasPrefix(text, startIndex))
            return ShellScanStatus.NotFound;

        if (text[startIndex] == (byte)'`')
            return ScanLegacyBackticks(text, startIndex, out endIndex, out substitution);
        if (!context.TryEnter())
            return ShellScanStatus.Invalid;

        try
        {
            _ = ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "$(", out var payloadStart);
            var status = Parser.ScanCommandSubstitutionEnd(
                context,
                text,
                payloadStart,
                out endIndex,
                out var parsed);
            if (status == ShellScanStatus.Complete)
            {
                var payload = text.Slice(payloadStart, endIndex - payloadStart);
                substitution = parsed == null ? new CommandSubstitutionSyntax.Deferred(payload)
                    : new CommandSubstitutionSyntax.Parsed(payload, parsed);
            }

            return status;
        }
        finally
        {
            context.Exit();
        }

        static ShellScanStatus ScanLegacyBackticks(
            ShellSource source,
            int substitutionStart,
            out int closingIndex,
            out CommandSubstitutionSyntax? legacyPayload)
        {
            legacyPayload = null;
            closingIndex = substitutionStart;
            var builder = ByteMemoryBuilder.CreateUnaccounted(0);
            var position = substitutionStart + 1;
            while (position < source.Length)
            {
                var current = source[position];
                if (current == (byte)'`')
                {
                    legacyPayload = new CommandSubstitutionSyntax.Deferred(ShellSource.FromBytes(builder.ToMemory()));
                    closingIndex = position;
                    return ShellScanStatus.Complete;
                }

                if (current == (byte)'\\' && position + 1 < source.Length)
                {
                    var next = source[position + 1];
                    if (next is (byte)'$' or (byte)'`' or (byte)'\\')
                    {
                        builder.Append(next);
                        position += 2;
                        continue;
                    }
                }

                builder.Append(current);
                position++;
            }

            return ShellScanStatus.Incomplete;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal enum TildeExpansion { None, Leading, Assignment, Argument, AssignDefault }

/// <summary>
/// Expands source and literal parts into byte-valued words with preserved quoting.
/// </summary>
internal static partial class WordExpansion
{
    internal static Task<ShellValue> ExpandAssignmentValueAsync(Word word, ScopedEvaluation scope) =>
        ExpandUnsplitValueAsync(word, scope, ExpansionPurpose.Assignment, TildeExpansion.Assignment);

    internal static Task<IReadOnlyList<ExpandedWord>> ExpandHereInputAsync(
        Word word, ScopedEvaluation scope, MemoryCapacity storage) =>
        ExpandWithoutBraceAsync(word, scope, allowWordSplitting: false, ExpansionPurpose.Assignment,
            TildeExpansion.Assignment, destination: null, storage);

    internal static Task<ShellValue> ExpandCompoundValueAsync(Word word, ScopedEvaluation scope, ArrayKind kind) =>
        ExpandUnsplitValueAsync(word, scope,
            kind == ArrayKind.Indexed ? ExpansionPurpose.IndexedCompound : ExpansionPurpose.AssociativeCompound,
            kind == ArrayKind.Indexed ? TildeExpansion.Assignment : TildeExpansion.None);

    internal static Task<ShellValue> ExpandSubscriptAsync(
        ShellSource source, ScopedEvaluation scope, ExpansionPurpose purpose, bool allowLeadingTilde) =>
        ExpandSubscriptAsync(source, scope, purpose, allowLeadingTilde, GetOperandSyntax(purpose));

    private static Task<ShellValue> ExpandSubscriptAsync(
        ShellSource source, ScopedEvaluation scope, ExpansionPurpose purpose, bool allowLeadingTilde, ExpansionWordSyntax syntax) =>
        ExpandUnsplitValueAsync(
            Tokenizer.ReadExpansionWord(source, syntax, ShellQuoteKind.None, scope.Effects.Memory.Workspace),
            scope, purpose, allowLeadingTilde ? TildeExpansion.Leading : TildeExpansion.None);

    private static async Task<ShellValue> ExpandUnsplitValueAsync(
        Word word, ScopedEvaluation scope, ExpansionPurpose purpose, TildeExpansion tildeExpansion)
    {
        var fields = await ExpandWithoutBraceAsync(word, scope, allowWordSplitting: false, purpose, tildeExpansion)
            .ConfigureAwait(false);
        return scope.CanExpand ? ExpandedWord.JoinValuesWithSpaces(fields, scope.Effects.Memory) : ShellValue.Empty;
    }

    /// <summary>
    /// Expands a scalar value or selects the first unsplit field for other consumers.
    /// </summary>
    /// <param name="word">The word to expand.</param>
    /// <param name="scope">The active bindings, lexical context and evaluator-owned effects.</param>
    /// <param name="purpose">Whether the consumer needs a complete scalar or an individual field.</param>
    /// <returns>The expanded content.</returns>
    public static async Task<ShellValue> ExpandAsync(
        Word word,
        ScopedEvaluation scope,
        ExpansionPurpose purpose)
    {
        var expanded = await ExpandInternalAsync(word, scope, allowWordSplitting: false, purpose)
            .ConfigureAwait(false);
        return expanded.Count == 0 ? ShellValue.Empty : expanded[0].ToValue(scope.Effects.Memory);
    }

    /// <summary>
    /// Expands a word into one or more words, applying word splitting when needed.
    /// </summary>
    /// <param name="word">The word to expand.</param>
    /// <param name="scope">The active bindings, lexical context and evaluator-owned effects.</param>
    /// <returns>The expanded words.</returns>
    public static Task<IReadOnlyList<ExpandedWord>> ExpandToWordsAsync(
        Word word,
        ScopedEvaluation scope) =>
        ExpandInternalAsync(word, scope, allowWordSplitting: true, ExpansionPurpose.Normal);

    private static async Task<IReadOnlyList<ExpandedWord>> ExpandInternalAsync(
        Word word,
        ScopedEvaluation scope,
        bool allowWordSplitting,
        ExpansionPurpose purpose)
    {
        if (!scope.CanExpand)
            return [];

        scope.Effects.CancellationToken.ThrowIfCancellationRequested();
        if (word.LiteralFields is { } literal)
            return literal;

        var braceExpanded = ExpandBraceWords(word, scope);
        var results = new List<ExpandedWord>();
        // Scalar consumers join fields per alternative before selecting their value.
        var joinFields = purpose is ExpansionPurpose.Scalar or ExpansionPurpose.Assignment or ExpansionPurpose.AssociativeCompound;

        foreach (var expansion in braceExpanded)
        {
            var expanded = await ExpandWithoutBraceAsync(
                    expansion,
                    scope,
                    allowWordSplitting,
                    purpose,
                    TildeExpansion.Argument,
                    joinFields ? null : results,
                    scope.Effects.Memory.Workspace)
                .ConfigureAwait(false);

            if (!scope.CanExpand)
                return [];

            if (joinFields)
                results.Add(ExpandedWord.JoinWithSpaces(expanded, scope.Effects.Memory));
        }

        return results;
    }

    /// <summary>
    /// Expands brace expressions into a set of words before other expansions.
    /// </summary>
    internal static IReadOnlyList<Word> ExpandBraceWords(Word word, ScopedEvaluation scope) =>
        BraceExpansion.Expand(word, scope.Effects.Memory, scope.Effects.CancellationToken);

    /// <summary>Expands a source word without brace expansion or pathname lookup.</summary>
    public static Task<IReadOnlyList<ExpandedWord>> ExpandWithoutBraceAsync(
        Word word,
        ScopedEvaluation scope,
        bool allowWordSplitting,
        ExpansionPurpose purpose,
        TildeExpansion tildeExpansion) =>
        ExpandWithoutBraceAsync(word, scope, allowWordSplitting, purpose, tildeExpansion, destination: null,
            scope.Effects.Memory.Workspace);

    private static async Task<IReadOnlyList<ExpandedWord>> ExpandWithoutBraceAsync(
        Word word,
        ScopedEvaluation scope,
        bool allowWordSplitting,
        ExpansionPurpose purpose,
        TildeExpansion tildeExpansion,
        List<ExpandedWord>? destination,
        MemoryCapacity storage)
    {
        if (!scope.CanExpand)
            return [];
        scope.Effects.CancellationToken.ThrowIfCancellationRequested();
        if (word.LiteralFields is { } literal)
        {
            if (destination != null)
            {
                destination.AddRange(literal);
                return destination;
            }
            return literal;
        }
        if (word.Parts.Count == 0)
            return [];

        var builder = new ExpandedWordBuilder(
            scope.Variables, scope.Effects.Memory, purpose, ImplicitEmptyPolicy.Preserve, scope.Effects.CancellationToken,
            storage);
        await ExpandWordPartsAsync(word, scope, builder, allowWordSplitting, tildeExpansion)
            .ConfigureAwait(false);

        // Discard unfinished words before encoding or splitting can mask the first failure.
        if (!scope.CanExpand)
            return [];
        if (destination == null)
            return purpose == ExpansionPurpose.CasePattern ? builder.BuildCasePattern() : builder.Build();
        builder.AppendTo(destination);
        return destination;
    }

    private static Task ExpandWordPartsAsync(
        Word word,
        ScopedEvaluation scope,
        ExpandedWordBuilder builder,
        bool allowWordSplitting,
        TildeExpansion tildeExpansion) =>
        ExpandWordPartsAsync(word, scope, builder, allowWordSplitting, tildeExpansion, 0, word.Parts.Count);

    private static async Task ExpandWordPartsAsync(
        Word word,
        ScopedEvaluation scope,
        ExpandedWordBuilder builder,
        bool allowWordSplitting,
        TildeExpansion tildeExpansion,
        int firstPart,
        int endPart)
    {
        if (!scope.CanExpand)
            return;
        if (word.HasIncompleteQuotedParameterSubscript)
        {
            for (var part = firstPart; part < endPart; part++)
            {
                if (word.Parts[part] is SourceWordPart { ParameterSubscriptFailure: not ParameterSubscriptFailure.None } failed)
                {
                    var failure = FindIncompleteQuotedSubscript(failed.Source, scope);
                    if (failure != ParameterSubscriptFailure.None)
                    {
                        await ReportParameterWordSyntaxAsync(failure == ParameterSubscriptFailure.Direct ? word : null,
                            failed.Source, missingBrace: true, scope).ConfigureAwait(false);
                        return;
                    }
                }
            }
        }
        var assignmentEquals = tildeExpansion == TildeExpansion.Argument ? FindAssignmentEquals(word, scope) : -1;
        var assignmentValue = tildeExpansion == TildeExpansion.Assignment;
        var assignment = assignmentValue || assignmentEquals >= 0
            ? new AssignmentTildes(word, assignmentEquals)
            : (AssignmentTildes?)null;
        // An assigning default expands its own leading tilde and leaves its suffix active.
        // Nested parameter words still inherit an assignment context.
        var assigningDefault = tildeExpansion == TildeExpansion.AssignDefault;
        var inheritAssignmentTildes = assigningDefault || assignmentValue && builder.Purpose != ExpansionPurpose.IndexedCompound;
        var position = new WordPosition(firstPart, 0);
        if (tildeExpansion != TildeExpansion.None
            && TryAppendTildePrefix(word, position, scope, builder,
                literalColonTail: !assignment.HasValue && !assigningDefault, out var suffix))
            position = suffix;
        while (position.Part < endPart && scope.CanExpand)
        {
            var part = word.Parts[position.Part];
            if (part is SourceWordPart { Kind: WordPartKind.Unquoted } source)
            {
                var next = await ExpandSegmentAsync(source.Source, source, scope, builder, allowWordSplitting, globQuoted: false,
                        ExpansionQuoteContext.Unquoted, position, assignment, word, inHereDocumentOperand: false,
                        inheritAssignmentTildes)
                    .ConfigureAwait(false);
                if (next is { } selected)
                {
                    position = selected;
                    continue;
                }
            }
            else if (part.IsDoubleQuoted)
            {
                builder.BeginDoubleQuote();
                do
                {
                    await ExpandPartAsync(word.Parts[position.Part], scope, builder, allowWordSplitting)
                        .ConfigureAwait(false);
                    position = new WordPosition(position.Part + 1, 0);
                } while (scope.CanExpand && position.Part < endPart
                    && word.Parts[position.Part].DoubleQuote == DoubleQuoteBoundary.Continuation);
                if (scope.CanExpand)
                    builder.EndDoubleQuote();
                continue;
            }
            else
                await ExpandPartAsync(part, scope, builder, allowWordSplitting).ConfigureAwait(false);
            position = new WordPosition(position.Part + 1, 0);
        }
    }

    private static ParameterSubscriptFailure FindIncompleteQuotedSubscript(ShellSource source, ScopedEvaluation scope)
    {
        // The tokenizer flags the early brace. Check the complete quote segment before applying its expansions.
        var context = CreateExpansionScanContext(scope);
        var cursor = new ShellSourceCursor(source, 0, context);
        while (!cursor.IsAtEnd)
        {
            scope.Effects.CancellationToken.ThrowIfCancellationRequested();
            if (BracedParameterScanner.HasPrefix(source, cursor.Position))
            {
                var status = BracedParameterScanner.ScanExpansion(context, source, cursor.Position, capturePayload: false,
                    out var end, out _, out _, out var failure);
                if (failure is ParameterSubscriptFailure.Direct or ParameterSubscriptFailure.Nested)
                    return failure;
                if (status == BracedParameterScanStatus.Found)
                {
                    cursor = new ShellSourceCursor(source, end + 1, context);
                    continue;
                }
            }
            if (cursor.SkipNestedExpansion(BracedParameterRecognition.All) is not ShellNestedExpansionScan.Complete)
                cursor.Advance();
        }
        return ParameterSubscriptFailure.None;
    }

    private static async Task ReportParameterWordSyntaxAsync(Word? word, ShellSource segment, bool missingBrace,
        ScopedEvaluation scope, ShellScanContext? captures = null)
    {
        if (word == null || captures == null && word.Parts is [SourceWordPart { Kind: WordPartKind.Unquoted }])
        {
            await ReportAsync(segment.ToValue()).ConfigureAwait(false);
            return;
        }
        using var source = new ByteMemoryBuilder(0, scope.Effects.Memory.Budget);
        if (!FunctionDefinitionFormatter.TryAppendWordSource(word, source, scope.Effects.Memory.Budget,
                scope.Effects.CancellationToken, captures))
            throw new ByteMemoryBuilder.LimitExceededException();
        await ReportAsync(ShellValue.FromBytes(source.ToMemory())).ConfigureAwait(false);

        Task ReportAsync(ShellValue spelling) => missingBrace
            ? scope.Effects.ReportUnitExpansionFailureAsync([ShellValue.FromText("bad substitution")],
                [ShellValue.FromText("no closing `}' in "), spelling], scope, ExpansionFailureKind.WordSyntax)
            : scope.Effects.ReportUnitExpansionFailureAsync([spelling], [ShellValue.FromText("bad substitution")],
                scope, ExpansionFailureKind.WordSyntax);
    }

    // Lexical assignment operands still go through ordinary field processing.
    private static int FindAssignmentEquals(Word word, ScopedEvaluation scope)
    {
        if (word.Parts is not [SourceWordPart { Kind: WordPartKind.Unquoted } part, ..])
            return -1;
        var source = part.Source.AsSpan();
        if (source.IsEmpty || !VariableNames.IsNameStart((char)source[0]))
            return -1;
        var index = 1;
        while (index < source.Length && VariableNames.IsNamePart((char)source[index]))
            index++;
        if (index < source.Length && source[index] == (byte)'[')
        {
            if (!source[index..].Contains((byte)'=')
                || !ArraySubscriptScanner.TryScan(new ShellScanContext { SyntaxMemory = scope.Effects.Memory.Workspace },
                    part.Source, index, out var closing))
                return -1;
            index = closing + 1;
        }
        if (index < source.Length && source[index] == (byte)'+')
            index++;
        return index < source.Length && source[index] == (byte)'=' ? index : -1;
    }

    private readonly record struct WordPosition(int Part, int Offset);

    private readonly record struct AssignmentTildes(Word Word, int EqualsOffset);

    private static async Task ExpandPartAsync(
        WordPart part,
        ScopedEvaluation scope,
        ExpandedWordBuilder builder,
        bool allowWordSplitting)
    {
        if (part is BraceExpansionFailureWordPart failure)
        {
            await ReportBraceExpansionFailureAsync(failure.Message, scope).ConfigureAwait(false);
            return;
        }

        switch (part.Kind)
        {
            case WordPartKind.DoubleQuoted:
                if (((SourceWordPart)part).Source.Length == 0)
                {
                    builder.MarkCurrentWordForEmission();
                    break;
                }

                await ExpandSegmentAsync(
                        (SourceWordPart)part,
                        scope,
                        builder,
                        allowWordSplitting: false,
                        globQuoted: true,
                        quoteContext: ExpansionQuoteContext.DoubleQuoted)
                    .ConfigureAwait(false);
                break;
            case WordPartKind.CommandSubstitution:
                await ExpandCommandSubstitutionAsync(
                        ((CommandSubstitutionWordPart)part).Substitution,
                        scope,
                        builder,
                        allowWordSplitting,
                        globQuoted: false)
                    .ConfigureAwait(false);
                break;
            case WordPartKind.CommandSubstitutionQuoted:
                await ExpandCommandSubstitutionAsync(
                        ((CommandSubstitutionWordPart)part).Substitution,
                        scope,
                        builder,
                        allowWordSplitting: false,
                        globQuoted: true)
                    .ConfigureAwait(false);
                break;
            case WordPartKind.HereDocument:
                await ExpandSegmentAsync(
                        (SourceWordPart)part,
                        scope with { ExpansionOrigin = LegacySubstitutionOrigin.HereDocument },
                        builder,
                        allowWordSplitting: false,
                        globQuoted: true,
                        quoteContext: ExpansionQuoteContext.HereDocument)
                    .ConfigureAwait(false);
                break;
            case WordPartKind.SingleQuoted:
            case WordPartKind.Escaped:
                var literal = ((LiteralWordPart)part).Value;
                if (literal.IsEmpty)
                {
                    builder.MarkCurrentWordForEmission();
                }
                else
                {
                    builder.AppendValue(literal, globQuoted: true);
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported word part kind '{part.Kind}'.");
        }
    }

    private static bool TryAppendTildePrefix(
        Word word,
        WordPosition start,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool literalColonTail,
        out WordPosition suffix)
    {
        suffix = start;
        if (!TryReadTildePrefix(word, start, scope, out var end, out var sign, out var ordinal))
            return false;

        suffix = end;
        // In an ordinary word, Bash carries the unquoted colon tail up to the
        // slash as literal text. Other consumers can resume at the colon.
        if (literalColonTail && end.Part < word.Parts.Count
            && ((SourceWordPart)word.Parts[end.Part]).Source[end.Offset] == (byte)':'
            && !TryFindLiteralTailEnd(end, out suffix))
            return false;

        if (!TryResolveTildePrefix(sign, ordinal, scope, pathMemory: null, out var value))
            return false;

        output.AppendValue(value, globQuoted: true);
        if (suffix != end)
        {
            for (var index = end.Part; index < word.Parts.Count && index <= suffix.Part; index++)
            {
                scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                if (word.Parts[index] is SourceWordPart source)
                {
                    var begin = index == end.Part ? end.Offset : 0;
                    var limit = index == suffix.Part ? suffix.Offset : source.Source.Length;
                    if (begin < limit)
                        output.AppendSource(source.Source.Slice(begin, limit - begin), globQuoted: true);
                }
                else
                {
                    var substitution = ((CommandSubstitutionWordPart)word.Parts[index]).Substitution;
                    if (substitution is CommandSubstitutionSyntax.Legacy legacy)
                        output.AppendSource(legacy.Spelling, globQuoted: true);
                    else
                    {
                        output.AppendValue(OpenSubstitution, globQuoted: true);
                        output.AppendSource(substitution.Source, globQuoted: true);
                        output.AppendValue(CloseSubstitution, globQuoted: true);
                    }
                }
            }
        }
        return true;

        bool TryFindLiteralTailEnd(WordPosition tail, out WordPosition stop)
        {
            stop = new WordPosition(word.Parts.Count, 0);
            for (var index = tail.Part; index < word.Parts.Count; index++)
            {
                scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                switch (word.Parts[index])
                {
                    case SourceWordPart { Kind: WordPartKind.Unquoted } source:
                    {
                        var begin = index == tail.Part ? tail.Offset : 0;
                        var bytes = source.Source.AsSpan()[begin..];
                        var delimiter = bytes.IndexOfAny("/\\\"'"u8);
                        if (delimiter < 0)
                            continue;
                        stop = new WordPosition(index, begin + delimiter);
                        return bytes[delimiter] == (byte)'/';
                    }
                    case CommandSubstitutionWordPart { Kind: WordPartKind.CommandSubstitution } command:
                        var spelling = command.Substitution is CommandSubstitutionSyntax.Legacy legacy
                            ? legacy.Spelling : command.Substitution.Source;
                        if (spelling.AsSpan().IndexOfAny("/\\\"'"u8) >= 0)
                            return false;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }

    private static readonly ShellValue OpenSubstitution = ShellValue.FromText("$(");
    private static readonly ShellValue CloseSubstitution = ShellValue.FromText(")");

    private static bool TryReadTildePrefix(
        Word word,
        WordPosition start,
        ScopedEvaluation scope,
        out WordPosition suffix,
        out byte sign,
        out int? ordinal)
    {
        suffix = new WordPosition(word.Parts.Count, 0);
        sign = 0;
        ordinal = null;
        // Read the whole prefix before looking up a binding. Even empty quoted
        // parts make an otherwise supported spelling ineligible.
        var startsWithTilde = false;
        for (var partIndex = start.Part; partIndex < word.Parts.Count; partIndex++)
        {
            scope.Effects.CancellationToken.ThrowIfCancellationRequested();
            if (word.Parts[partIndex] is not SourceWordPart { Kind: WordPartKind.Unquoted } source)
                return false;
            var bytes = source.Source.AsSpan();
            var begin = partIndex == start.Part ? start.Offset : 0;
            for (var offset = begin; offset < bytes.Length; offset++)
            {
                scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                var current = bytes[offset];
                if (!startsWithTilde)
                {
                    if (current != (byte)'~')
                        return false;
                    startsWithTilde = true;
                }
                else if (current is (byte)'/' or (byte)':')
                {
                    suffix = new WordPosition(partIndex, offset);
                    return true;
                }
                else if (!TryAppendTildeSelector(current, ref sign, ref ordinal))
                    return false;
            }
        }
        return startsWithTilde;
    }

    /// <summary>Expands the leading tilde of an already split search-path component.</summary>
    internal static (ReadOnlyMemory<byte> Prefix, ReadOnlyMemory<byte> Suffix) ExpandPathTilde(
        ReadOnlyMemory<byte> path, ScopedEvaluation scope, MemoryCapacity pathMemory)
    {
        if (path.Span is not [(byte)'~', ..])
            return (path, default);

        byte sign = 0;
        int? ordinal = null;
        var end = 1;
        for (; end < path.Length && path.Span[end] != (byte)'/'; end++)
        {
            scope.Effects.CancellationToken.ThrowIfCancellationRequested();
            if (!TryAppendTildeSelector(path.Span[end], ref sign, ref ordinal))
                return (path, default);
        }
        return TryResolveTildePrefix(sign, ordinal, scope, pathMemory, out var expanded)
            ? (expanded.Bytes, path[end..]) : (path, default);
    }

    private static bool TryAppendTildeSelector(byte current, ref byte sign, ref int? ordinal)
    {
        if (sign == 0 && ordinal == null && current is (byte)'+' or (byte)'-')
        {
            sign = current;
            return true;
        }
        if (current is < (byte)'0' or > (byte)'9')
            return false;

        // Arbitrary zero padding needs no buffer.
        var next = 10L * ordinal.GetValueOrDefault() + current - (byte)'0';
        if (next > int.MaxValue)
            return false;
        ordinal = (int)next;
        return true;
    }

    private static bool TryResolveTildePrefix(
        byte sign,
        int? ordinal,
        ScopedEvaluation scope,
        MemoryCapacity? pathMemory,
        out ShellValue expanded)
    {
        if (ordinal is { } index)
        {
            var count = scope.Effects.DirectoryStackCount;
            if (index >= count)
            {
                expanded = ShellValue.Empty;
                return false;
            }
            var position = sign == (byte)'-' ? count - index - 1 : index;
            if (position == 0)
                return scope.Variables.TryGetExpansionValue("PWD", out expanded);

            var entry = scope.Effects.GetDirectoryStackEntry(position);
            if (entry.Path is not { } resolved)
            {
                expanded = entry.DeferredPath;
                return true;
            }
            var path = resolved.Path;
            var bytes = checked(24L + 3L * path.Length);
            if (pathMemory == null)
                scope.Effects.Memory.Charge(bytes);
            else
                scope.Effects.Memory.Charge(pathMemory, bytes);
            scope.Effects.CancellationToken.ThrowIfCancellationRequested();
            expanded = ShellValue.FromText(path);
            return true;
        }

        if (sign == (byte)'+')
        {
            if (scope.Variables.TryGetExpansionValue("PWD", out expanded))
                return true;

            var directory = scope.Effects.CurrentDirectory.Path;
            if (pathMemory != null)
                scope.Effects.Memory.Charge(pathMemory, checked(24L + 3L * directory.Length));
            expanded = ShellValue.FromText(directory);
            return true;
        }

        return scope.Variables.TryGetExpansionValue(sign == (byte)'-' ? "OLDPWD" : "HOME", out expanded);
    }

    private static Task ExpandSegmentAsync(
        SourceWordPart part, ScopedEvaluation scope, ExpandedWordBuilder output,
        bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext) =>
        ExpandSegmentAsync(part.Source, part, scope, output, allowWordSplitting, globQuoted, quoteContext,
            new WordPosition(0, 0), null, null, inHereDocumentOperand: false, inheritAssignmentTildes: false);

    private static async Task<WordPosition?> ExpandSegmentAsync(
        ShellSource content,
        SourceWordPart? syntax,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext,
        WordPosition start,
        AssignmentTildes? assignment,
        Word? originalWord,
        bool inHereDocumentOperand,
        bool inheritAssignmentTildes)
    {
        var length = content.Length;
        ShellScanContext? parameterScan = null;
        var trackSourceIfs = output.Purpose == ExpansionPurpose.Normal && allowWordSplitting && !globQuoted
            && quoteContext == ExpansionQuoteContext.Unquoted;
        var sourceSeparators = 0;
        for (var index = start.Offset; index < length; index++)
        {
            if (!scope.CanExpand)
                return null;

            var value = content[index];
            // Substitutions can change IFS before the next literal run.
            if (trackSourceIfs && value is (byte)'$' or (byte)'`')
                sourceSeparators = 0;
            // Modern Bash removes unescaped double quotes within the selected parameter operand.
            if (inHereDocumentOperand && value == (byte)'"')
                continue;
            if (value == (byte)'\\' && syntax?.IsBraceExpanded == true)
            {
                if (++index == length)
                {
                    output.MarkCurrentWordForEmission();
                    break;
                }
                var escapedLength = Utf8Text.FirstCharacterLength(content.AsSpan()[index..]);
                output.AppendSource(content.Slice(index, escapedLength), globQuoted: true);
                index += escapedLength - 1;
                continue;
            }
            if (quoteContext == ExpansionQuoteContext.HereDocument
                && value == (byte)'\\'
                && index + 1 < length)
            {
                var escaped = content[index + 1];
                if (escaped == (byte)'\n')
                {
                    index++;
                    continue;
                }

                if (escaped is (byte)'$' or (byte)'`' or (byte)'\\'
                    || inHereDocumentOperand && escaped is (byte)'"' or (byte)'}')
                {
                    output.AppendSource(content.Slice(index + 1, 1), globQuoted);
                    index++;
                    continue;
                }
                if (inHereDocumentOperand)
                {
                    var size = Utf8Text.FirstCharacterLength(content.AsSpan()[(index + 1)..]);
                    output.AppendHereDocumentUnprotected(content.Slice(index, size + 1).ToValue());
                    index += size;
                    continue;
                }
            }

            if (value == (byte)'`'
                && quoteContext is ExpansionQuoteContext.Unquoted or ExpansionQuoteContext.HereDocument)
            {
                var legacyStatus = ScanCommandSubstitution(content, index, scope.Effects.Memory.Workspace,
                    scope.ExpansionOrigin,
                    out var legacyScript, out var legacyEndIndex, out var legacyError);
                if (legacyStatus == ShellScanStatus.Complete)
                {
                    await ExpandCommandSubstitutionAsync(
                            legacyScript ?? throw new InvalidOperationException("A complete substitution requires executable syntax."),
                            scope,
                            output,
                            allowWordSplitting,
                            globQuoted)
                        .ConfigureAwait(false);
                    index = legacyEndIndex;
                    continue;
                }
                if (quoteContext == ExpansionQuoteContext.HereDocument
                    && legacyStatus is ShellScanStatus.Incomplete or ShellScanStatus.Invalid)
                {
                    await RejectHereDocumentAsync(scope, legacyStatus, legacyError).ConfigureAwait(false);
                    return null;
                }
                if (syntax?.IsBraceExpanded == true && legacyStatus == ShellScanStatus.Incomplete && index + 1 < length)
                {
                    var message = BraceExpansionFailureWordPart.FormatMissingDelimiter(content.Slice(index), (byte)'`',
                        scope.Effects.Memory.Workspace);
                    await ReportBraceExpansionFailureAsync(message, scope).ConfigureAwait(false);
                    return null;
                }
            }

            if (value != (byte)'$')
            {
                var literal = content.Slice(index, 1);
                // In a here-document operand, punctuation handled by the word scanner
                // can participate in the enclosing body's IFS split.
                if (inHereDocumentOperand && value is (byte)':' or (byte)'=' or (byte)'~' or (byte)'\'')
                    output.AppendHereDocumentUnprotected(literal.ToValue());
                else
                    AppendLiteral(literal);
                // Only literal punctuation reached by this scan opens a prefix.
                // A colon inside a substitution is consumed with that substitution.
                if (assignment is { } tildes
                    && (start.Part == 0 && index == tildes.EqualsOffset
                        || value == (byte)':' && (start.Part != 0 || index > tildes.EqualsOffset))
                    && TryAppendTildePrefix(tildes.Word, new WordPosition(start.Part, index + 1),
                        scope, output, literalColonTail: false, out var suffix))
                {
                    if (suffix.Part != start.Part)
                        return suffix;
                    index = suffix.Offset - 1;
                }
                continue;
            }

            if (index + 1 >= length)
            {
                AppendLiteral(content.Slice(index, 1));
                continue;
            }

            var prepared = syntax != null && syntax.TryGetArithmetic(index, out var captured)
                ? captured : (SourceWordPart.ArithmeticExpansion?)null;
            var arithmeticEnd = prepared?.End ?? -1;
            var expression = ShellSource.Empty;
            var arithmeticStatus = ShellScanStatus.Complete;
            ParserFailure? arithmeticError = null;
            var invalidQuotedBracket = prepared is { Operand: null };
            if (!prepared.HasValue)
            {
                var context = new ShellScanContext { SyntaxMemory = scope.Effects.Memory.Workspace };
                arithmeticStatus = ArithmeticExpansionScanner.Scan(context, content, index, out arithmeticEnd,
                    out expression, out _, out var bracketed, out _, out arithmeticError);
                invalidQuotedBracket = arithmeticStatus == ShellScanStatus.Complete && bracketed
                    && quoteContext == ExpansionQuoteContext.DoubleQuoted
                    && !ArithmeticExpansionScanner.TryPrepareDoubleQuotedBracketOperand(context, ref expression);
            }
            if (arithmeticStatus == ShellScanStatus.Complete)
            {
                if (invalidQuotedBracket)
                {
                    await scope.Effects.ReportFatalExpansionAsync(ShellValue.FromText("$["),
                        ShellValue.FromText("bad substitution: no closing ']'"), ExpansionFailureKind.Arithmetic)
                        .ConfigureAwait(false);
                    return null;
                }
                var arithmetic = prepared?.Operand;
                // Arithmetic and braced parameter operands start their own expansion.
                var operandScope = scope with { PreserveCommandSubstitutionSpelling = false };
                long? result;
                if (arithmetic != null)
                    result = await ExpandArithmeticOperandAsync(arithmetic, operandScope, ExpansionPurpose.ArithmeticSource).ConfigureAwait(false);
                else
                {
                    result = await ExpandArithmeticOperandAsync(expression, operandScope, ExpansionPurpose.ArithmeticSource,
                            ShellValue.Empty, null)
                        .ConfigureAwait(false);
                }
                if (result is { } number)
                    output.AppendIntegerExpansion(number, allowWordSplitting, globQuoted);
                index = arithmeticEnd;
                continue;
            }

            CommandSubstitutionSyntax? script = null;
            ParserFailure? commandError = null;
            var endIndex = index;
            var commandStatus = arithmeticStatus == ShellScanStatus.NotFound
                ? ScanCommandSubstitution(content, index, scope.Effects.Memory.Workspace,
                    scope.ExpansionOrigin, out script, out endIndex, out commandError)
                : ShellScanStatus.NotFound;
            if (commandStatus == ShellScanStatus.Complete)
            {
                await ExpandCommandSubstitutionAsync(
                        script ?? throw new InvalidOperationException("A complete substitution requires executable syntax."),
                        scope,
                        output,
                        allowWordSplitting,
                        globQuoted)
                    .ConfigureAwait(false);
                index = endIndex;
                continue;
            }
            if (quoteContext == ExpansionQuoteContext.HereDocument
                && (commandStatus is ShellScanStatus.Incomplete or ShellScanStatus.Invalid
                    || arithmeticStatus is ShellScanStatus.Incomplete or ShellScanStatus.Invalid))
            {
                await RejectHereDocumentAsync(scope, commandStatus == ShellScanStatus.NotFound ? arithmeticStatus : commandStatus,
                        commandError ?? arithmeticError)
                    .ConfigureAwait(false);
                return null;
            }

            var next = content[index + 1];
            if (next == (byte)'-')
            {
                output.AppendExpansion(scope.Effects.ShellOptionFlags, allowWordSplitting, globQuoted);
                index++;
                continue;
            }
            if (next == (byte)'!')
            {
                output.AppendExpansion(ShellValue.Empty, allowWordSplitting, globQuoted);
                index++;
                continue;
            }

            if (next == (byte)'?')
            {
                output.AppendIntegerExpansion(scope.Effects.LastExitCode ?? 0, allowWordSplitting, globQuoted);
                index++;
                continue;
            }

            if (next == (byte)'#')
            {
                output.AppendIntegerExpansion(scope.Variables.PositionalParameters.Count, allowWordSplitting, globQuoted);
                index++;
                continue;
            }

            if (next == (byte)'@')
            {
                output.AppendFields(
                    scope.Variables.PositionalParameters,
                    allowWordSplitting,
                    globQuoted,
                    quoteContext,
                    AtFieldSource.Positional);
                index++;
                continue;
            }

            if (next == (byte)'*')
            {
                var arguments = scope.Variables.PositionalParameters;
                if (quoteContext == ExpansionQuoteContext.HereDocument)
                    output.AppendHereDocumentFields(arguments, HereDocumentJoin.PositionalStar);
                else
                    output.AppendJoinedFields(arguments, allowWordSplitting, globQuoted, PositionalStarSplit.EvenWhenEmpty,
                        preserveQuotedEmpty: true);
                index++;
                continue;
            }

            if (next is >= (byte)'0' and <= (byte)'9')
            {
                var position = next - (byte)'0';
                var arguments = scope.Variables.PositionalParameters;
                if (position == 0)
                    output.AppendExpansion(scope.Variables.ArgumentZero, allowWordSplitting, globQuoted);
                else if (position <= arguments.Count)
                    output.AppendExpansion(arguments[position - 1], allowWordSplitting, globQuoted);
                else if (quoteContext == ExpansionQuoteContext.DoubleQuoted)
                    output.AppendExpansion(ShellValue.Empty, allowWordSplitting, globQuoted);

                index++;
                continue;
            }

            if (BracedParameterScanner.HasPrefix(content, index))
            {
                parameterScan ??= CreateExpansionScanContext(scope);
                var parameterStatus = BracedParameterScanner.ScanExpansion(parameterScan, content, index, capturePayload: true,
                    out var closingIndex, out var payload, out var parameterError, out var subscriptFailure);
                if (subscriptFailure == ParameterSubscriptFailure.Nested)
                {
                    await ReportParameterWordSyntaxAsync(originalWord, content, missingBrace: true, scope).ConfigureAwait(false);
                    return null;
                }
                var closure = ParameterClosure.Closed;
                if (parameterStatus == BracedParameterScanStatus.MissingBraceAfterSelector)
                {
                    var hasOperator = closingIndex + 1 < content.Length;
                    if (subscriptFailure == ParameterSubscriptFailure.Length || quoteContext == ExpansionQuoteContext.DoubleQuoted
                        || !hasOperator && originalWord != null && start.Part + 1 != originalWord.Parts.Count)
                    {
                        await ReportParameterWordSyntaxAsync(originalWord, content, missingBrace: false, scope).ConfigureAwait(false);
                        return null;
                    }
                    closure = hasOperator ? ParameterClosure.MissingAfterOperator : ParameterClosure.Missing;
                }
                else if (parameterStatus != BracedParameterScanStatus.Found)
                {
                    if (quoteContext == ExpansionQuoteContext.HereDocument)
                    {
                        await RejectHereDocumentAsync(scope, parameterStatus is BracedParameterScanStatus.Unterminated
                            or BracedParameterScanStatus.UnterminatedCommandSubstitution
                            ? ShellScanStatus.Incomplete : ShellScanStatus.Invalid, parameterError).ConfigureAwait(false);
                        return null;
                    }
                    if (output.Purpose is ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource)
                    {
                        await scope.Effects.ReportFatalExpansionAsync(
                            [ShellValue.FromText("bad substitution")],
                            [ShellValue.FromText("no closing `}' in "), ShellValue.FromBytes(content.Bytes)],
                            ExpansionFailureKind.Parameter)
                            .ConfigureAwait(false);
                        return null;
                    }
                    AppendLiteral(content.Slice(index, 1));
                    continue;
                }

                if (parameterScan.SubstitutionFailure != SubstitutionSyntaxFailure.None && !scope.PreserveCommandSubstitutionSpelling)
                {
                    await scope.Effects.ReportCommandSubstitutionSyntaxAsync(parameterScan.SubstitutionFailure)
                        .ConfigureAwait(false);
                    return null;
                }

                await ExpandBracedParameterAsync(
                        payload,
                        parameterScan,
                        scope with { PreserveCommandSubstitutionSpelling = false },
                        output,
                        allowWordSplitting,
                        globQuoted,
                        quoteContext,
                        inheritAssignmentTildes,
                        content, originalWord, closure)
                    .ConfigureAwait(false);
                index = closingIndex;
                continue;
            }

            if (VariableNames.IsNameStart((char)next))
            {
                var nameStart = index + 1;
                var nameEnd = nameStart + 1;
                while (nameEnd < length && VariableNames.IsNamePart((char)content[nameEnd]))
                {
                    nameEnd++;
                }

                var name = content.Slice(nameStart, nameEnd - nameStart).ToUtf8String();
                var target = await ResolveReferenceAsync(VariableReference.ForName(name), ReferenceUse.Read,
                    scope, ExpansionPurpose.ArithmeticSource).ConfigureAwait(false);
                var selectedValue = ShellValue.Empty;
                if (target != null) target.TryRead(scope.Variables, out selectedValue);
                output.AppendExpansion(selectedValue, allowWordSplitting, globQuoted);
                index = nameEnd - 1;
                continue;
            }

            AppendLiteral(content.Slice(index, 1));
        }
        return null;

        void AppendLiteral(ShellSource literal)
        {
            if (trackSourceIfs)
                output.AppendUnquotedSource(literal, ref sourceSeparators);
            else
                output.AppendSource(literal, globQuoted);
        }

        static async Task RejectHereDocumentAsync(ScopedEvaluation failedScope, ShellScanStatus status, ParserFailure? error)
        {
            if (error is { HereDocumentEndOfInput: { } missingCloser })
                await failedScope.Effects.ReportCapturedHereDocumentEndOfInputAsync(error.Error, missingCloser, offset: 0)
                    .ConfigureAwait(false);
            else
            {
                await failedScope.Effects.ReportExpansionDiagnosticAsync(ShellValue.FromText("here-document"),
                    ShellValue.FromText(error?.Error.ToDiagnosticMessage() ?? (status == ShellScanStatus.Incomplete
                        ? "unexpected end of substitution" : "invalid substitution"))).ConfigureAwait(false);
            }
            failedScope.Effects.SetExpansionError(1);
        }
    }

    /// <summary>Expands an arithmetic operand once, preserving its writes and first failure.</summary>
    private static async Task<long?> ExpandArithmeticOperandAsync(
        ShellSource expression, ScopedEvaluation scope, ExpansionPurpose purpose, ShellValue command,
        ShellScanContext? scanContext)
    {
        var operand = await PrepareArithmeticAsync(expression, null, scope, purpose, scanContext).ConfigureAwait(false);
        return operand == null ? null : await EvaluateArithmeticAsync(operand, scope, ArithmeticFailureMode.Expansion, command)
            .ConfigureAwait(false);
    }

    private static async Task<long?> ExpandArithmeticOperandAsync(
        ArithmeticSource expression, ScopedEvaluation scope, ExpansionPurpose purpose)
    {
        var operand = await PrepareArithmeticAsync(expression, scope, purpose).ConfigureAwait(false);
        return operand == null ? null : await EvaluateArithmeticAsync(operand, scope, ArithmeticFailureMode.Expansion).ConfigureAwait(false);
    }

    private static Task ReportBraceExpansionFailureAsync(ShellValue message, ScopedEvaluation scope) =>
        scope.Effects.ReportFatalExpansionAsync(ShellValue.FromText("bad substitution"), message, ExpansionFailureKind.WordSyntax);

    private static async Task ExpandCommandSubstitutionAsync(
        CommandSubstitutionSyntax script,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool allowWordSplitting,
        bool globQuoted)
    {
        if (scope.PreserveCommandSubstitutionSpelling)
        {
            using var spelling = new ByteMemoryBuilder(0, scope.Effects.Memory.Budget);
            if (!FunctionDefinitionFormatter.TryAppendSubstitutionSource(script, spelling, scope.Effects.Memory.Budget,
                    scope.Effects.CancellationToken))
                throw new ByteMemoryBuilder.LimitExceededException();
            output.AppendExpansion(ShellValue.FromBytes(spelling.ToMemory()), allowWordSplitting, globQuoted);
            return;
        }

        if (script is CommandSubstitutionSyntax.Parsed { Failure: not SubstitutionSyntaxFailure.None } parsed)
        {
            await scope.Effects.ReportCommandSubstitutionSyntaxAsync(parsed.Failure).ConfigureAwait(false);
            return;
        }

        var result = await scope.Effects.ExecuteCommandSubstitutionAsync(script, scope).ConfigureAwait(false);
        output.AppendExpansion(result, allowWordSplitting, globQuoted);
    }

    private static ShellScanStatus ScanCommandSubstitution(
        ShellSource content,
        int startIndex,
        MemoryCapacity memory,
        LegacySubstitutionOrigin legacyOrigin,
        out CommandSubstitutionSyntax? script,
        out int endIndex,
        out ParserFailure? error)
    {
        var context = new ShellScanContext { SyntaxMemory = memory, LegacyOrigin = legacyOrigin };
        return CommandSubstitutionScanner.ScanParsed(context, content, startIndex, out endIndex, out script, out error);
    }

    internal static async Task<ExpandedWord> ExpandExpressionAsync(
        ShellSource expression,
        ScopedEvaluation scope,
        ExpansionPurpose purpose,
        bool allowWordSplitting)
    {
        if (expression.Length == 0)
            return new ExpandedWord([]);

        var words = await ExpandWithoutBraceAsync(
                Tokenizer.ReadExpansionWord(expression, GetOperandSyntax(purpose), ShellQuoteKind.None, scope.Effects.Memory.Workspace),
                scope, allowWordSplitting, purpose, purpose is ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource
                    ? TildeExpansion.None : TildeExpansion.Leading)
            .ConfigureAwait(false);
        if (words.Count == 0 || !scope.CanExpand)
            return new ExpandedWord([]);
        return ExpandedWord.JoinWithSpaces(words, scope.Effects.Memory);
    }

    /// <summary>Expands a selected operand once, retaining field provenance for its consumer.</summary>
    private static async Task<ExpandedWordBuilder?> ExpandOperatorWordAsync(
        ShellSource expression,
        ShellScanContext scanContext,
        ScopedEvaluation scope,
        ExpansionQuoteContext quoteContext,
        ExpansionPurpose purpose,
        ImplicitEmptyPolicy implicitEmptyPolicy,
        TildeExpansion tildeExpansion,
        ExpandedWordBuilder? enclosingWord)
    {
        if (expression.Length == 0)
            return null;

        var word = quoteContext == ExpansionQuoteContext.HereDocument ? null
            : Tokenizer.ReadExpansionWord(expression, GetOperandSyntax(purpose),
                quoteContext == ExpansionQuoteContext.Unquoted ? ShellQuoteKind.None : ShellQuoteKind.Double, scanContext);
        var assigningHereDocument = quoteContext == ExpansionQuoteContext.HereDocument && enclosingWord != null;
        var builder = new ExpandedWordBuilder(
            scope.Variables, scope.Effects.Memory, purpose, implicitEmptyPolicy, scope.Effects.CancellationToken)
        {
            AssigningHereDocumentOperand = assigningHereDocument,
            CaseOperand = purpose != ExpansionPurpose.CasePattern ? CasePatternOperand.None
                : enclosingWord?.PreservesCasePatternQuotedNulls == true ? CasePatternOperand.PreserveQuotedNulls
                : CasePatternOperand.SelectFields,
            InheritsCaseDoubleQuotes = purpose == ExpansionPurpose.CasePattern && quoteContext == ExpansionQuoteContext.DoubleQuoted,
            OperandFields = purpose == ExpansionPurpose.Normal && quoteContext == ExpansionQuoteContext.Unquoted
                ? FieldSeparators.FromVariables(scope.Variables).IsEmpty
                    ? ParameterOperandFields.UnquotedWithEmptyIfs : ParameterOperandFields.Unquoted
                : ParameterOperandFields.Default,
        };
        if (word == null)
        {
            await ExpandHereDocumentOperandAsync(expression, scope, builder).ConfigureAwait(false);
        }
        else
        {
            if (purpose is ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource)
                tildeExpansion = TildeExpansion.None;
            await ExpandWordPartsAsync(word, scope, builder, allowWordSplitting: false, tildeExpansion).ConfigureAwait(false);
        }
        if (!scope.CanExpand)
            return null;
        if (assigningHereDocument)
            enclosingWord?.IncludeOperand(builder);
        return builder;
    }

    private static Task ExpandHereDocumentOperandAsync(ShellSource expression, ScopedEvaluation scope, ExpandedWordBuilder builder) =>
        ExpandSegmentAsync(expression, syntax: null, scope, builder,
            allowWordSplitting: false, globQuoted: true, ExpansionQuoteContext.HereDocument, new WordPosition(0, 0),
            assignment: null, originalWord: null, inHereDocumentOperand: true, inheritAssignmentTildes: false);

    private static ExpansionWordSyntax GetOperandSyntax(ExpansionPurpose purpose) => purpose switch
    {
        ExpansionPurpose.Arithmetic => ExpansionWordSyntax.Arithmetic,
        ExpansionPurpose.ArithmeticSource => ExpansionWordSyntax.ArithmeticSource,
        _ => ExpansionWordSyntax.Parameter,
    };

    private static ShellScanContext CreateExpansionScanContext(ScopedEvaluation scope) =>
        new() { SyntaxMemory = scope.Effects.Memory.Workspace, LegacyOrigin = scope.ExpansionOrigin };

    private static ShellValue? TrySetVariable(
        BindingName name,
        ShellValue value,
        ScopedEvaluation scope)
    {
        if (name.IsEmpty)
        {
            return null;
        }

        var key = name;
        if (scope.Variables.IsReadonly(key)
            || scope.Variables.GetNamerefAssignmentFailure(key, key, value, scope.Effects.Memory)
                != NamerefAssignmentFailure.None)
        {
            return null;
        }

        return scope.SetExpansionValueIgnoringReadonly(key, value);
    }
}

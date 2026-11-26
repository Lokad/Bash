using System;
using System.Buffers;
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
    private static readonly SearchValues<byte> LiteralRunBoundaries = SearchValues.Create("$`\\"u8);
    private static readonly SearchValues<byte> AssignmentLiteralRunBoundaries = SearchValues.Create("$`\\=:"u8);

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

    private static Task<IReadOnlyList<ExpandedWord>> ExpandInternalAsync(
        Word word,
        ScopedEvaluation scope,
        bool allowWordSplitting,
        ExpansionPurpose purpose)
    {
        if (!scope.CanExpand)
            return Task.FromResult<IReadOnlyList<ExpandedWord>>([]);

        scope.Effects.CancellationToken.ThrowIfCancellationRequested();
        if (word.LiteralFields is { } literal)
            return Task.FromResult(literal);

        var alternatives = ExpandBraceAlternatives(word, scope);
        return alternatives == null
            ? ExpandWordsAsync([word], scope, allowWordSplitting, purpose, TildeExpansion.Argument)
            : ExpandWordsAsync(alternatives, scope, allowWordSplitting, purpose, TildeExpansion.Leading);
    }

    internal static IReadOnlyList<Word>? ExpandBraceAlternatives(Word word, ScopedEvaluation scope) =>
        word.LiteralFields == null ? BraceExpansion.ExpandAlternatives(
            word, scope.Effects.Memory, scope.Effects.CancellationToken) : null;

    internal static async Task<bool> CheckBraceDelimitersAsync(IReadOnlyList<Word> words, ScopedEvaluation scope)
    {
        for (var index = 0; index < words.Count; index++)
            if (words[index].HasBraceDelimiterCandidate
                && !await CheckBraceCandidateAsync(words[index], scope, protectSubscript: false).ConfigureAwait(false))
                return false;
        return true;
    }

    internal static Task<bool> CheckBraceDelimitersAsync(Word word, ScopedEvaluation scope) =>
        CheckBraceDelimitersAsync(word, scope, protectSubscript: false);

    internal static Task<bool> CheckBraceDelimitersAsync(Word word, ScopedEvaluation scope, bool protectSubscript) =>
        word.HasBraceDelimiterCandidate ? CheckBraceCandidateAsync(word, scope, protectSubscript) : Task.FromResult(true);

    private static async Task<bool> CheckBraceCandidateAsync(Word word, ScopedEvaluation scope, bool protectSubscript)
    {
        var budget = scope.Effects.Memory.Budget;
        var cancellation = scope.Effects.CancellationToken;
        using var spelling = new ByteMemoryBuilder(0, budget);
        if (!FunctionDefinitionFormatter.TryAppendWordSource(word, spelling, budget, cancellation))
            throw new ByteMemoryBuilder.LimitExceededException();

        var source = ShellSource.FromBytes(spelling.ToMemory());
        using var normalized = source.AsSpan().IndexOf("\\\n"u8) < 0 ? null : new ByteMemoryBuilder(0, budget);
        int diagnosticStart;
        var closingBracket = -1;
        using (var scratch = new MemoryCapacity(budget, 0))
        {
            if (normalized != null)
                source = BraceExpansion.RemoveLegacyContinuations(source, scratch, normalized, cancellation);
            if (!BraceExpansion.TryFindUnclosedParenthesis(source, scratch, cancellation, out diagnosticStart))
                return true;
            if (protectSubscript && diagnosticStart == 0 && ArraySubscriptScanner.TryScan(
                    new ShellScanContext { SyntaxMemory = scratch }, source, 0, out var bracket))
                closingBracket = bracket;
        }

        if (closingBracket >= 0)
        {
            using var diagnostic = new ByteMemoryBuilder(0, budget);
            diagnostic.Append((byte)'\\').Append(source.AsSpan()[..closingBracket])
                .Append((byte)'\\').Append(source.AsSpan()[closingBracket..]);
            await ReportAsync(ShellValue.FromBytes(diagnostic.ToMemory())).ConfigureAwait(false);
        }
        else
            await ReportAsync(source[diagnosticStart..].ToValue()).ConfigureAwait(false);
        return false;

        Task ReportAsync(ShellValue value) => ReportWordSyntaxAsync(value, WordSyntaxFailure.MissingArithmeticParenthesis,
            scope, hereDocument: false);
    }

    internal static Task<IReadOnlyList<ExpandedWord>> ExpandBraceArgumentsAsync(
        IReadOnlyList<Word> alternatives, ScopedEvaluation scope) =>
        ExpandWordsAsync(alternatives, scope, allowWordSplitting: true, ExpansionPurpose.Normal, TildeExpansion.Leading);

    private static async Task<IReadOnlyList<ExpandedWord>> ExpandWordsAsync(
        IReadOnlyList<Word> words, ScopedEvaluation scope, bool allowWordSplitting, ExpansionPurpose purpose,
        TildeExpansion tildeExpansion)
    {
        var results = new List<ExpandedWord>();
        // Scalar consumers join fields per alternative before selecting their value.
        var joinFields = purpose is ExpansionPurpose.Scalar or ExpansionPurpose.Assignment or ExpansionPurpose.AssociativeCompound;

        foreach (var expansion in words)
        {
            var expanded = await ExpandWithoutBraceAsync(
                    expansion,
                    scope,
                    allowWordSplitting,
                    purpose,
                    tildeExpansion,
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
        if (scope.ExpansionWord == null && word.Parts is not [{ Kind: WordPartKind.HereDocument }])
            scope = scope with { ExpansionWord = word };
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
            && TryReadTildePrefix(word, position, scope, out var suffix, out var sign, out var ordinal))
        {
            if (!assignment.HasValue && !assigningDefault && suffix.Part < endPart
                && ((SourceWordPart)word.Parts[suffix.Part]).Source[suffix.Offset] == (byte)':')
                position = await ExpandTildeColonTailAsync(word, suffix, sign, ordinal, scope, builder, allowWordSplitting)
                    .ConfigureAwait(false) ?? position;
            else if (TryAppendTildeValue(sign, ordinal, scope, builder))
                position = suffix;
        }
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
                    await ExpandPartAsync(word.Parts[position.Part], word, scope, builder, allowWordSplitting)
                        .ConfigureAwait(false);
                    position = new WordPosition(position.Part + 1, 0);
                } while (scope.CanExpand && position.Part < endPart
                    && word.Parts[position.Part].DoubleQuote == DoubleQuoteBoundary.Continuation);
                if (scope.CanExpand)
                    builder.EndDoubleQuote();
                continue;
            }
            else
                await ExpandPartAsync(part, word, scope, builder, allowWordSplitting).ConfigureAwait(false);
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
                    out var end, out _, out _, out var failure, out _);
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

    private static Task ReportParameterWordSyntaxAsync(Word? word, ShellSource segment, bool missingBrace,
        ScopedEvaluation scope, ShellScanContext? captures = null) =>
        ReportParameterWordSyntaxAsync(word, segment, missingBrace, scope, captures, quoteSegment: false);

    private static Task ReportParameterWordSyntaxAsync(Word? word, ShellSource segment, bool missingBrace,
        ScopedEvaluation scope, ShellScanContext? captures, bool quoteSegment) =>
        ReportWordSyntaxAsync(word, segment, ParameterSyntaxFailure(missingBrace), scope, captures, quoteSegment);

    private enum WordSyntaxFailure { BadSubstitution, MissingBrace, MissingArithmeticParenthesis }

    private static WordSyntaxFailure ParameterSyntaxFailure(bool missingBrace) =>
        missingBrace ? WordSyntaxFailure.MissingBrace : WordSyntaxFailure.BadSubstitution;

    private static async Task ReportWordSyntaxAsync(Word? word, ShellSource segment, WordSyntaxFailure failure,
        ScopedEvaluation scope, ShellScanContext? captures, bool quoteSegment)
    {
        if (word != null)
            word = scope.ExpansionWord ?? word;
        var hereDocument = failure == WordSyntaxFailure.MissingArithmeticParenthesis
            && scope.ExpansionOrigin == LegacySubstitutionOrigin.HereDocument;
        if (word == null && !quoteSegment || captures == null && word?.Parts is [SourceWordPart { Kind: WordPartKind.Unquoted }])
        {
            var spelling = word?.Parts is [SourceWordPart unquoted] ? unquoted.Source : segment;
            await ReportWordSyntaxAsync(spelling.ToValue(), failure, scope, hereDocument).ConfigureAwait(false);
            return;
        }
        using var source = new ByteMemoryBuilder(0, scope.Effects.Memory.Budget);
        if (word == null)
            source.Append((byte)'"').Append(segment.AsSpan()).Append((byte)'"');
        else if (!FunctionDefinitionFormatter.TryAppendWordSource(word, source, scope.Effects.Memory.Budget,
                scope.Effects.CancellationToken, captures))
            throw new ByteMemoryBuilder.LimitExceededException();
        await ReportWordSyntaxAsync(ShellValue.FromBytes(source.ToMemory()), failure, scope, hereDocument)
            .ConfigureAwait(false);
    }

    private static Task ReportParameterSyntaxAsync(ShellValue spelling, bool missingBrace, ScopedEvaluation scope, bool hereDocument) =>
        ReportWordSyntaxAsync(spelling, ParameterSyntaxFailure(missingBrace), scope, hereDocument);

    private static Task ReportWordSyntaxAsync(ShellValue spelling, WordSyntaxFailure failure, ScopedEvaluation scope, bool hereDocument)
    {
        var badSubstitution = ShellValue.FromText("bad substitution");
        ReadOnlySpan<ShellValue> subject = failure == WordSyntaxFailure.BadSubstitution ? [spelling] : [badSubstitution];
        ReadOnlySpan<ShellValue> message = failure switch
        {
            WordSyntaxFailure.MissingBrace => [ShellValue.FromText("no closing `}' in "), spelling],
            WordSyntaxFailure.MissingArithmeticParenthesis => [ShellValue.FromText("no closing `)' in "), spelling],
            _ => [badSubstitution]
        };
        return hereDocument
            ? scope.Effects.ReportHereDocumentParameterFailureAsync(subject, message, scope, fatalKind: null)
            : scope.Effects.ReportParameterExpansionFailureAsync(subject, message, scope, ExpansionFailureKind.WordSyntax);
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
        Word word,
        ScopedEvaluation scope,
        ExpandedWordBuilder builder,
        bool allowWordSplitting)
    {
        if (part is BraceExpansionFailureWordPart failure)
        {
            await scope.Effects.ReportBraceExpansionFailureAsync(failure.Message, scope).ConfigureAwait(false);
            return;
        }

        switch (part.Kind)
        {
            case WordPartKind.DoubleQuoted:
                var quoted = (SourceWordPart)part;
                if (quoted.Source.IsEmpty)
                {
                    builder.MarkCurrentWordForEmission();
                    break;
                }

                await ExpandSegmentAsync(
                        quoted.Source,
                        quoted,
                        scope,
                        builder,
                        allowWordSplitting: false,
                        globQuoted: true,
                        ExpansionQuoteContext.DoubleQuoted, new WordPosition(0, 0), assignment: null,
                        originalWord: quoted.HasExpandedParameterQuoteSyntax ? word : null,
                        inHereDocumentOperand: false, inheritAssignmentTildes: false)
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

    private static bool TryAppendTildeValue(byte sign, int? ordinal, ScopedEvaluation scope, ExpandedWordBuilder output)
    {
        if (!TryResolveTildePrefix(sign, ordinal, scope, pathMemory: null, out var value))
            return false;

        output.AppendValue(value, globQuoted: true);
        return true;
    }

    private static async Task<WordPosition?> ExpandTildeColonTailAsync(
        Word word, WordPosition colon, byte sign, int? ordinal, ScopedEvaluation scope,
        ExpandedWordBuilder output, bool allowWordSplitting)
    {
        // Bash inspects the spelling up to the first slash. A quote or escape
        // in that prefix rejects the tilde before its binding is read.
        var cancellation = scope.Effects.CancellationToken;
        var memory = scope.Effects.Memory;
        var end = new WordPosition(word.Parts.Count, 0);
        var suffix = end;
        var firstRenderedPart = end.Part;
        var slashInCapture = -1;
        var capturePart = -1;
        ByteMemoryBuilder? spelling = null;
        try
        {
            for (var index = colon.Part; index < end.Part; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                switch (word.Parts[index])
                {
                    case SourceWordPart { Kind: WordPartKind.Unquoted } source:
                    {
                        var begin = index == colon.Part ? colon.Offset : 0;
                        var bytes = source.Source.AsSpan()[begin..];
                        var delimiter = bytes.IndexOfAny("/\\\"'"u8);
                        if (delimiter >= 0)
                        {
                            if (bytes[delimiter] != (byte)'/')
                                return null;
                            suffix = new WordPosition(index, begin + delimiter);
                            bytes = bytes[..delimiter];
                        }
                        spelling?.Append(bytes);
                        break;
                    }
                    case CommandSubstitutionWordPart { Kind: WordPartKind.CommandSubstitution } command:
                    {
                        if (spelling == null)
                        {
                            firstRenderedPart = index;
                            spelling = new ByteMemoryBuilder(0, memory.Budget);
                        }
                        var begin = spelling.Length;
                        if (!FunctionDefinitionFormatter.TryAppendSubstitutionWord(command.Substitution, spelling,
                                memory.Budget, cancellation))
                            throw new ByteMemoryBuilder.LimitExceededException();
                        var bytes = spelling.ToMemory().Span[begin..];
                        var delimiter = bytes.IndexOfAny("/\\\"'"u8);
                        if (delimiter >= 0)
                        {
                            if (bytes[delimiter] != (byte)'/')
                                return null;
                            slashInCapture = begin + delimiter;
                            capturePart = index;
                        }
                        break;
                    }
                    default:
                        return null;
                }
                if (suffix != end || slashInCapture >= 0)
                    break;
            }

            if (!TryAppendTildeValue(sign, ordinal, scope, output))
                return null;

            var borrowedEnd = spelling == null ? suffix : new WordPosition(firstRenderedPart, 0);
            for (var index = colon.Part; index < borrowedEnd.Part || index == borrowedEnd.Part && borrowedEnd.Offset > 0; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var source = ((SourceWordPart)word.Parts[index]).Source;
                var begin = index == colon.Part ? colon.Offset : 0;
                var limit = index == borrowedEnd.Part ? borrowedEnd.Offset : source.Length;
                output.AppendSource(source.Slice(begin, limit - begin), globQuoted: true);
            }
            if (spelling == null)
                return suffix;

            var prefixLength = slashInCapture >= 0 ? slashInCapture : spelling.Length;
            output.AppendSource(ShellSource.FromBytes(spelling.ToMemory()[..prefixLength]), globQuoted: true,
                retainCompactBacking: false);
            if (slashInCapture < 0)
                return suffix;

            // The outer word resumes inside the capture spelling. Its closing
            // delimiter and the following word parts now share this lexical context.
            if (!FunctionDefinitionFormatter.TryAppendWordSource(word.Parts, capturePart + 1, spelling,
                    memory.Budget, cancellation, normalizeLegacySpelling: true))
                throw new ByteMemoryBuilder.LimitExceededException();
            var tail = Tokenizer.ReadGeneratedWord(ShellSource.FromBytes(spelling.ToMemory()[slashInCapture..]), memory.Workspace);
            await ExpandWordPartsAsync(tail, scope with { ExpansionOrigin = LegacySubstitutionOrigin.Temporary },
                    output, allowWordSplitting, TildeExpansion.None)
                .ConfigureAwait(false);
            return end;
        }
        finally { spelling?.Dispose(); }
    }

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
        var checkArithmeticComments = false;
        var trackSourceIfs = output.Purpose == ExpansionPurpose.Normal && allowWordSplitting && !globQuoted
            && quoteContext == ExpansionQuoteContext.Unquoted;
        // Selected operands and here-document operands assign provenance to individual literal bytes.
        var literalBoundaries = inHereDocumentOperand || !globQuoted && output.OperandFields != ParameterOperandFields.Default
            ? null : assignment.HasValue ? AssignmentLiteralRunBoundaries : LiteralRunBoundaries;
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
                    await scope.Effects.ReportBraceExpansionFailureAsync(message, scope).ConfigureAwait(false);
                    return null;
                }
            }

            if (value != (byte)'$')
            {
                var literalLength = 1;
                if (literalBoundaries != null && !literalBoundaries.Contains(value))
                {
                    const int MaximumLiteralRun = 64 * 1024;
                    scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                    var scanLength = Math.Min(length - index, MaximumLiteralRun);
                    var boundary = content.AsSpan(index, scanLength).IndexOfAny(literalBoundaries);
                    literalLength = boundary < 0 ? scanLength : boundary;
                }
                var literal = content.Slice(index, literalLength);
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
                    && TryReadTildePrefix(tildes.Word, new WordPosition(start.Part, index + 1), scope,
                        out var suffix, out var tildeSign, out var tildeOrdinal)
                    && TryAppendTildeValue(tildeSign, tildeOrdinal, scope, output))
                {
                    if (suffix.Part != start.Part)
                        return suffix;
                    index = suffix.Offset - 1;
                }
                else
                    index += literalLength - 1;
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
            var invalidQuotedBracket = prepared is { Operand: null, Bracketed: true };
            var missingReachedParenthesis = prepared is { Operand: null, Bracketed: false, Subshell: null };
            var reachedSubshell = prepared?.Subshell;
            var bracketed = prepared?.Bracketed ?? false;
            if (!prepared.HasValue)
            {
                var context = new ShellScanContext { SyntaxMemory = scope.Effects.Memory.Workspace };
                arithmeticStatus = ArithmeticExpansionScanner.Scan(context, content, index, out arithmeticEnd,
                    out var spelling, out _, out bracketed, out _, out arithmeticError);
                expression = spelling.Source;
                invalidQuotedBracket = arithmeticStatus == ShellScanStatus.Complete && bracketed
                    && quoteContext == ExpansionQuoteContext.DoubleQuoted
                    && !ArithmeticExpansionScanner.TryPrepareDoubleQuotedBracketOperand(context, ref expression);
                if (arithmeticStatus == ShellScanStatus.Complete && !bracketed)
                {
                    var reached = ArithmeticExpansionScanner.ReadReachedArithmeticParentheses(content, index, arithmeticEnd,
                        context.SyntaxMemory);
                    arithmeticEnd = reached.End;
                    missingReachedParenthesis = reached.Missing;
                    reachedSubshell = reached.Subshell;
                }
            }
            if (arithmeticStatus == ShellScanStatus.Complete)
            {
                if (missingReachedParenthesis)
                {
                    await ReportMissingReachedParenthesisAsync().ConfigureAwait(false);
                    return null;
                }
                if (reachedSubshell != null)
                {
                    await ExpandCommandSubstitutionAsync(reachedSubshell, scope, output, allowWordSplitting, globQuoted)
                        .ConfigureAwait(false);
                    index = arithmeticEnd;
                    continue;
                }
                if (invalidQuotedBracket)
                {
                    await scope.Effects.ReportFatalExpansionAsync(ShellValue.FromText("$["),
                        ShellValue.FromText("bad substitution: no closing ']'"), ExpansionFailureKind.Arithmetic)
                        .ConfigureAwait(false);
                    return null;
                }
                var arithmetic = prepared?.Operand;
                if (arithmetic is not { DirectOperand: not null }
                    && ArithmeticExpansionScanner.HasMissingCloserInOperandQuotes(arithmetic?.Source ?? expression,
                        skipLegacyCaptures: bracketed, scope.Effects.Memory.Workspace))
                {
                    var spelling = bracketed ? content : arithmetic?.Source ?? expression;
                    await ReportWordSyntaxAsync(spelling.ToValue(), WordSyntaxFailure.MissingArithmeticParenthesis, scope,
                            hereDocument: quoteContext == ExpansionQuoteContext.HereDocument).ConfigureAwait(false);
                    return null;
                }
                // Arithmetic and braced parameter operands start their own expansion.
                var operandScope = scope with
                {
                    PreserveCommandSubstitutionSpelling = false,
                    ExpansionWord = bracketed && quoteContext != ExpansionQuoteContext.DoubleQuoted ? null : scope.ExpansionWord
                };
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
                if (parameterScan == null)
                {
                    parameterScan = CreateExpansionScanContext(scope);
                    checkArithmeticComments = ArithmeticExpansionScanner.HasPossibleExpansionComment(content.AsSpan());
                }
                parameterScan.ReadArithmeticExpansionComments = checkArithmeticComments;
                var parameterStatus = BracedParameterScanner.ScanExpansion(parameterScan, content, index, capturePayload: true,
                    out var closingIndex, out var payload, out var parameterError, out var subscriptFailure, out var rejectedSelector);
                parameterScan.ReadArithmeticExpansionComments = false;
                if (parameterScan.MissingReachedArithmeticParenthesis)
                {
                    await ReportMissingReachedParenthesisAsync().ConfigureAwait(false);
                    return null;
                }
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
                    if (syntax?.HasExpandedParameterQuoteSyntax == true)
                    {
                        await ReportParameterWordSyntaxAsync(originalWord, content, missingBrace: true, scope, captures: null,
                            quoteSegment: quoteContext == ExpansionQuoteContext.DoubleQuoted).ConfigureAwait(false);
                        return null;
                    }
                    if (quoteContext == ExpansionQuoteContext.HereDocument)
                    {
                        if (parameterStatus is BracedParameterScanStatus.Unterminated or BracedParameterScanStatus.UnterminatedOperand)
                            await ReportParameterSyntaxAsync(content.ToValue(),
                                parameterStatus == BracedParameterScanStatus.UnterminatedOperand, scope, hereDocument: true).ConfigureAwait(false);
                        else
                            await RejectHereDocumentAsync(scope, parameterStatus == BracedParameterScanStatus.UnterminatedCommandSubstitution
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
                    var reportParameterDiagnostic = scope.SelectorParameterDiagnostics
                        ?? output.Purpose is ExpansionPurpose.Assignment or ExpansionPurpose.CompoundPrefix;
                    if (!reportParameterDiagnostic && rejectedSelector.NeedsParameterDiagnostic)
                        rejectedSelector = default;
                    await scope.Effects.ReportCommandSubstitutionSyntaxAsync(parameterScan.SubstitutionFailure, null, scope, globQuoted,
                        rejectedSelector)
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
                var target = await ResolveReferenceAsync(VariableReference.ForName(name), ReferenceUse.ShortRead,
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

        Task ReportMissingReachedParenthesisAsync() => ReportWordSyntaxAsync(
            quoteContext == ExpansionQuoteContext.HereDocument ? null : scope.ExpansionWord,
            content, WordSyntaxFailure.MissingArithmeticParenthesis, scope, captures: null,
            quoteSegment: quoteContext == ExpansionQuoteContext.DoubleQuoted);

        void AppendLiteral(ShellSource literal)
        {
            // A batched append copies into the result, just like the earlier byte-wise appends.
            var retainCompactBacking = literal.Length == 1;
            if (trackSourceIfs)
                output.AppendUnquotedSource(literal, ref sourceSeparators, retainCompactBacking);
            else
                output.AppendSource(literal, globQuoted, retainCompactBacking);
        }

        static async Task RejectHereDocumentAsync(ScopedEvaluation failedScope, ShellScanStatus status, ParserFailure? error)
        {
            if (error is { UnclosedDelimiter: { } missingCloser })
                await failedScope.Effects.ReportHereDocumentCaptureEndOfInputAsync(missingCloser, error.CaptureWarnings, failedScope)
                    .ConfigureAwait(false);
            else
            {
                await failedScope.Effects.ReportExpansionDiagnosticAsync(ShellValue.FromText("here-document"),
                    ShellValue.FromText(error?.Error.ToDiagnosticMessage() ?? (status == ShellScanStatus.Incomplete
                        ? "unexpected end of substitution" : "invalid substitution"))).ConfigureAwait(false);
            }
            failedScope.Effects.SetHereDocumentSyntaxError();
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
            await scope.Effects.ReportCommandSubstitutionSyntaxAsync(parsed.Failure, parsed, scope, globQuoted, default).ConfigureAwait(false);
            return;
        }

        if (script is CommandSubstitutionSyntax.AdjacentSubshell { MissingReachedParenthesis: true })
        {
            await ReportWordSyntaxAsync(scope.ExpansionWord, script.Source, WordSyntaxFailure.MissingArithmeticParenthesis,
                scope, captures: null, quoteSegment: globQuoted).ConfigureAwait(false);
            return;
        }

        if (script is CommandSubstitutionSyntax.Deferred { ReadWarnings: { } warnings })
        {
            await scope.Effects.ReportHereDocumentCaptureWarningsAsync(warnings, scope).ConfigureAwait(false);
            var coordinates = scope.Context.Coordinates with { Line = Evaluation.HereDocumentExpansionLine(scope) };
            scope = scope with { Context = scope.Context.WithCoordinates(coordinates) };
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

    private static ShellValue? TrySetWritableVariable(
        BindingName name,
        VariableBinding? binding,
        ShellValue value,
        ScopedEvaluation scope)
    {
        if (binding is { } reached
            && Variables.GetNamerefAssignmentFailure(name, reached, value, scope.Effects.Memory) != NamerefAssignmentFailure.None)
            return null;

        return scope.SetExpansionValueIgnoringReadonly(name, value);
    }
}

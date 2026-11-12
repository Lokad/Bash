using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Expands source and literal parts into byte-valued words with preserved quoting.
/// </summary>
internal static partial class WordExpansion
{
    internal static Task<ShellValue> ExpandAssignmentValueAsync(Word word, ScopedEvaluation scope) =>
        ExpandUnsplitValueAsync(word, scope, ExpansionPurpose.Scalar, allowLeadingTilde: true);

    internal static Task<ShellValue> ExpandCompoundValueAsync(Word word, ScopedEvaluation scope, ArrayKind kind) =>
        ExpandUnsplitValueAsync(word, scope,
            kind == ArrayKind.Indexed ? ExpansionPurpose.IndexedCompound : ExpansionPurpose.Scalar,
            allowLeadingTilde: kind == ArrayKind.Indexed);

    internal static Task<ShellValue> ExpandSubscriptAsync(
        ShellSource source, ScopedEvaluation scope, ExpansionPurpose purpose, bool allowLeadingTilde) =>
        ExpandUnsplitValueAsync(
            Tokenizer.ReadExpansionWord(source, GetOperandSyntax(purpose), ShellQuoteKind.None),
            scope, purpose, allowLeadingTilde);

    private static async Task<ShellValue> ExpandUnsplitValueAsync(
        Word word, ScopedEvaluation scope, ExpansionPurpose purpose, bool allowLeadingTilde)
    {
        var expanded = await ExpandUnsplitWordAsync(word, scope, purpose, allowLeadingTilde).ConfigureAwait(false);
        return expanded.ToValue(scope.Effects.Memory);
    }

    /// <summary>Expands one conditional/case/assignment operand without splitting, globbing or brace generation.</summary>
    internal static async Task<ExpandedWord> ExpandUnsplitWordAsync(
        Word word, ScopedEvaluation scope, ExpansionPurpose purpose, bool allowLeadingTilde)
    {
        var fields = await ExpandWithoutBraceAsync(word, scope, allowWordSplitting: false, purpose, allowLeadingTilde)
            .ConfigureAwait(false);
        if (!scope.CanExpand)
            return new ExpandedWord([]);
        var budget = scope.Effects.Memory;
        return ExpandedWord.JoinWithSpaces(fields, budget);
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
    public static async Task<IReadOnlyList<ExpandedWord>> ExpandToWordsAsync(
        Word word,
        ScopedEvaluation scope)
    {
        return await ExpandInternalAsync(word, scope, allowWordSplitting: true, ExpansionPurpose.Normal).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ExpandedWord>> ExpandInternalAsync(
        Word word,
        ScopedEvaluation scope,
        bool allowWordSplitting,
        ExpansionPurpose purpose)
    {
        if (!scope.CanExpand)
            return [];

        if (word.Parts is [LiteralWordPart { Kind: WordPartKind.SingleQuoted } literal])
        {
            // Retain frozen fields for scalar and argument consumers; only the word/segment entries are new.
            scope.Effects.Memory.Charge(checked(512L));
            return [new ExpandedWord([new ExpandedSegment(literal.Value, isGlobQuoted: true)])];
        }

        var braceExpanded = ExpandBraceWords(word, scope);
        var results = new List<ExpandedWord>();

        foreach (var expansion in braceExpanded)
        {
            var expanded = await ExpandWithoutBraceAsync(
                    expansion,
                    scope,
                    allowWordSplitting,
                    purpose,
                    allowLeadingTilde: true)
                .ConfigureAwait(false);

            if (!scope.CanExpand)
                return [];

            // Join operand fields, not brace alternatives: scalar consumers still select the first alternative.
            if (purpose == ExpansionPurpose.Scalar)
                results.Add(ExpandedWord.JoinWithSpaces(expanded, scope.Effects.Memory));
            else
                results.AddRange(expanded);
        }

        return results;
    }

    /// <summary>
    /// Expands brace expressions into a set of words before other expansions.
    /// </summary>
    internal static IReadOnlyList<Word> ExpandBraceWords(Word word, ScopedEvaluation scope)
    {
        var expanded = new List<List<WordPart>> { new() };
        var changed = false;
        // Shared source or literal parts are copied when each generated word is flattened.
        var totalCharacters = 0L;
        var budget = scope.Effects.Memory;

        foreach (var part in word.Parts)
        {
            var source = part is SourceWordPart sourcePart ? sourcePart.Source : ShellSource.Empty;
            var length = part.GetUnexpandedValue().Bytes.Length;
            var variants = part.Kind == WordPartKind.Unquoted
                ? BraceExpansion.Expand(source, budget, scope.Effects.CancellationToken)
                : [source];
            if (variants.Count == 1 && variants[0].AsSpan().SequenceEqual(source.AsSpan()))
            {
                var addedCharacters = (long)expanded.Count * length;
                if (changed)
                    budget.Charge(checked(256L * (expanded.Count) + 4L * addedCharacters));
                foreach (var branch in expanded)
                    branch.Add(part);
                totalCharacters += addedCharacters;
                continue;
            }

            changed = true;
            var nextCount = (long)expanded.Count * variants.Count;
            if (nextCount >= Array.MaxLength)
                throw new ByteMemoryBuilder.LimitExceededException();
            var nextCharacters = totalCharacters * variants.Count
                + expanded.Count * variants.Sum(static variant => (long)variant.Length);
            budget.Charge(checked(256L * (nextCount * (expanded[0].Count + 1L)) + 4L * nextCharacters));
            var nextExpanded = new List<List<WordPart>>((int)nextCount);

            foreach (var baseParts in expanded)
            {
                foreach (var variant in variants)
                {
                    scope.Effects.CancellationToken.ThrowIfCancellationRequested();
                    var newParts = new List<WordPart>(baseParts.Count + 1);
                    newParts.AddRange(baseParts);
                    newParts.Add(WordPart.FromSource(WordPartKind.Unquoted, variant));
                    nextExpanded.Add(newParts);
                }
            }

            expanded = nextExpanded;
            totalCharacters = nextCharacters;
        }

        return changed
            ? expanded.Select(w => new Word(w)).ToArray()
            : [word];
    }

    /// <summary>Expands a source word without brace expansion or pathname lookup.</summary>
    public static async Task<IReadOnlyList<ExpandedWord>> ExpandWithoutBraceAsync(
        Word word,
        ScopedEvaluation scope,
        bool allowWordSplitting,
        ExpansionPurpose purpose,
        bool allowLeadingTilde)
    {
        if (word.Parts.Count == 0)
            return [];

        var builder = new ExpandedWordBuilder(
            scope.Variables, scope.Effects.Memory, purpose, ImplicitEmptyPolicy.Preserve, scope.Effects.CancellationToken);
        await ExpandWordPartsAsync(word, scope, builder, allowWordSplitting, allowLeadingTilde)
            .ConfigureAwait(false);

        // Discard unfinished words before encoding or splitting can mask the first failure.
        return !scope.CanExpand ? [] : builder.Build();
    }

    private static async Task ExpandWordPartsAsync(
        Word word,
        ScopedEvaluation scope,
        ExpandedWordBuilder builder,
        bool allowWordSplitting,
        bool allowLeadingTilde)
    {
        if (!scope.CanExpand)
            return;
        // Compound-prefix items keep leading tildes literal; nested parameter words expand them separately.
        if (!allowLeadingTilde || !await TryExpandLeadingTildeAsync(word, scope, builder, allowWordSplitting)
            .ConfigureAwait(false))
        {
            foreach (var part in word.Parts)
            {
                if (!scope.CanExpand)
                    break;

                await ExpandPartAsync(part, scope, builder, allowWordSplitting)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task ExpandPartAsync(
        WordPart part,
        ScopedEvaluation scope,
        ExpandedWordBuilder builder,
        bool allowWordSplitting)
    {
        switch (part.Kind)
        {
            case WordPartKind.Unquoted:
                await ExpandSegmentAsync(
                        ((SourceWordPart)part).Source,
                        scope,
                        builder,
                        allowWordSplitting,
                        globQuoted: false,
                        quoteContext: ExpansionQuoteContext.Unquoted)
                    .ConfigureAwait(false);
                break;
            case WordPartKind.DoubleQuoted:
                if (((SourceWordPart)part).Source.Length == 0)
                {
                    builder.MarkCurrentWordForEmission();
                    break;
                }

                await ExpandSegmentAsync(
                        ((SourceWordPart)part).Source,
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
                        ((SourceWordPart)part).Source,
                        scope,
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

    private static async Task<bool> TryExpandLeadingTildeAsync(
        Word word,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool allowWordSplitting)
    {
        if (word.Parts.Count == 0)
            return false;

        var firstPart = word.Parts[0];
        if (firstPart.Kind != WordPartKind.Unquoted)
            return false;

        var source = ((SourceWordPart)firstPart).Source;
        var span = source.AsSpan();
        if (span.IsEmpty || span[0] != '~')
            return false;

        var slashIndex = -1;
        for (var index = 0; index < span.Length; index++)
        {
            if (span[index] == (byte)'/')
            {
                slashIndex = index;
                break;
            }
        }

        var prefix = slashIndex >= 0 ? span[..slashIndex] : span;
        if (!TryExpandTildePrefix(prefix, scope, out var expanded))
            return false;
        output.AppendValue(expanded, globQuoted: true);
        if (slashIndex >= 0)
        {
            await ExpandSegmentAsync(
                    source.Slice(slashIndex),
                    scope,
                    output,
                    allowWordSplitting,
                    globQuoted: false,
                    quoteContext: ExpansionQuoteContext.Unquoted)
                .ConfigureAwait(false);
        }

        for (var index = 1; index < word.Parts.Count; index++)
        {
            if (!scope.CanExpand)
                break;

            await ExpandPartAsync(word.Parts[index], scope, output, allowWordSplitting)
                .ConfigureAwait(false);
        }

        return true;
    }

    private static bool TryExpandTildePrefix(
        ReadOnlySpan<byte> prefix,
        ScopedEvaluation scope,
        out ShellValue expanded)
    {
        if (prefix.Length == 1 && prefix[0] == (byte)'~')
            return scope.Variables.TryGetExpansionValue("HOME", out expanded);

        if (prefix.Length == 2 && prefix[0] == (byte)'~' && prefix[1] == (byte)'+')
        {
            if (scope.Variables.TryGetExpansionValue("PWD", out expanded))
                return true;

            expanded = ShellValue.FromText(scope.Effects.CurrentDirectory.Path);
            return true;
        }

        if (prefix.Length == 2 && prefix[0] == (byte)'~' && prefix[1] == (byte)'-')
            return scope.Variables.TryGetExpansionValue("OLDPWD", out expanded);

        expanded = ShellValue.Empty;
        return false;
    }

    private static async Task ExpandSegmentAsync(
        ShellSource content,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext)
    {
        var length = content.Length;
        for (var index = 0; index < length; index++)
        {
            if (!scope.CanExpand)
                return;

            var value = content[index];
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

                if (escaped is (byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\')
                {
                    output.AppendSource(content.Slice(index + 1, 1), globQuoted);
                    index++;
                    continue;
                }
            }

            if (value == (byte)'`'
                && quoteContext is ExpansionQuoteContext.Unquoted or ExpansionQuoteContext.HereDocument)
            {
                if (TryExtractCommandSubstitution(content, index, out var legacyScript, out var legacyEndIndex))
                {
                    await ExpandCommandSubstitutionAsync(
                            legacyScript,
                            scope,
                            output,
                            allowWordSplitting,
                            globQuoted)
                        .ConfigureAwait(false);
                    index = legacyEndIndex;
                    continue;
                }
            }

            if (value != (byte)'$')
            {
                output.AppendSource(content.Slice(index, 1), globQuoted);
                continue;
            }

            if (index + 1 >= length)
            {
                output.AppendSource(content.Slice(index, 1), globQuoted);
                continue;
            }

            if (ArithmeticExpansionScanner.TryScan(content, index, out var arithmeticEnd, out var expression))
            {
                var result = await ExpandArithmeticOperandAsync(expression, scope, ExpansionPurpose.ArithmeticSource).ConfigureAwait(false);
                if (result is { } number)
                    AppendNumber(output, number, allowWordSplitting, globQuoted);
                index = arithmeticEnd;
                continue;
            }

            if (TryExtractCommandSubstitution(content, index, out var script, out var endIndex))
            {
                await ExpandCommandSubstitutionAsync(
                        script,
                        scope,
                        output,
                        allowWordSplitting,
                        globQuoted)
                    .ConfigureAwait(false);
                index = endIndex;
                continue;
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
                AppendNumber(output, scope.Effects.LastExitCode ?? 0, allowWordSplitting, globQuoted);
                index++;
                continue;
            }

            if (next == (byte)'#')
            {
                AppendNumber(output, scope.Variables.PositionalParameters.Count, allowWordSplitting, globQuoted);
                index++;
                continue;
            }

            if (next == (byte)'@')
            {
                output.AppendFields(
                    scope.Variables.PositionalParameters,
                    allowWordSplitting,
                    globQuoted,
                    quoteContext);
                index++;
                continue;
            }

            if (next == (byte)'*')
            {
                output.AppendJoinedFields(
                    scope.Variables.PositionalParameters,
                    allowWordSplitting,
                    globQuoted);
                index++;
                continue;
            }

            if (next is >= (byte)'0' and <= (byte)'9')
            {
                if (next == (byte)'0')
                {
                    output.AppendExpansion(
                        scope.Variables.ArgumentZero,
                        allowWordSplitting,
                        globQuoted);
                }
                else
                {
                    var position = next - (byte)'1';
                    if (position >= 0 && position < scope.Variables.PositionalParameters.Count)
                    {
                        output.AppendExpansion(
                            scope.Variables.PositionalParameters[position],
                            allowWordSplitting,
                            globQuoted);
                    }
                }

                index++;
                continue;
            }

            if (BracedParameterScanner.HasPrefix(content, index))
            {
                if (!BracedParameterScanner.TryScan(content, index, out var closingIndex, out var payload))
                {
                    if (output.Purpose is ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource)
                    {
                        var message = ByteMemoryBuilder.CreateUnaccounted(0);
                        message.Append("no closing `}' in "u8).Append(content.Bytes);
                        await scope.Effects.ReportFatalExpansionAsync(
                            ShellValue.FromText("bad substitution"), ShellValue.FromBytes(message.ToMemory()), ExpansionFailureKind.Parameter)
                            .ConfigureAwait(false);
                        return;
                    }
                    output.AppendSource(content.Slice(index, 1), globQuoted);
                    continue;
                }

                await ExpandBracedParameterAsync(
                        payload,
                        scope,
                        output,
                        allowWordSplitting,
                        globQuoted,
                        quoteContext)
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

            output.AppendSource(content.Slice(index, 1), globQuoted);
        }
    }

    /// <summary>Expands an arithmetic operand once, preserving its writes and first failure.</summary>
    private static async Task<long?> ExpandArithmeticOperandAsync(
        ShellSource expression, ScopedEvaluation scope, ExpansionPurpose purpose)
    {
        var operand = await PrepareArithmeticAsync(expression, scope, purpose).ConfigureAwait(false);
        return operand == null ? null : await EvaluateArithmeticAsync(operand, scope, ArithmeticFailureMode.Fatal).ConfigureAwait(false);
    }

    private static async Task ExpandCommandSubstitutionAsync(
        CommandSubstitutionSyntax script,
        ScopedEvaluation scope,
        ExpandedWordBuilder output,
        bool allowWordSplitting,
        bool globQuoted)
    {
        var result = await scope.Effects.ExecuteCommandSubstitutionAsync(script, scope).ConfigureAwait(false);
        output.AppendExpansion(result, allowWordSplitting, globQuoted);
    }

    private static bool TryExtractCommandSubstitution(
        ShellSource content,
        int startIndex,
        [NotNullWhen(true)] out CommandSubstitutionSyntax? script,
        out int endIndex)
    {
        return CommandSubstitutionScanner.TryScanParsed(content, startIndex, out endIndex, out script);
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
                Tokenizer.ReadExpansionWord(expression, GetOperandSyntax(purpose), ShellQuoteKind.None),
                scope, allowWordSplitting, purpose, allowLeadingTilde: purpose is not (ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource))
            .ConfigureAwait(false);
        if (words.Count == 0 || !scope.CanExpand)
            return new ExpandedWord([]);
        return ExpandedWord.JoinWithSpaces(words, scope.Effects.Memory);
    }

    /// <summary>Expands a selected operand once, retaining fields and quotes until its consumer joins or splits them.</summary>
    private static async Task<IReadOnlyList<ExpandedWord>> ExpandOperatorWordAsync(
        ShellSource expression,
        ScopedEvaluation scope,
        ExpansionQuoteContext quoteContext,
        ExpansionPurpose purpose,
        ImplicitEmptyPolicy implicitEmptyPolicy)
    {
        if (expression.Length == 0)
            return [];

        var word = Tokenizer.ReadExpansionWord(expression,
            GetOperandSyntax(purpose),
            quoteContext == ExpansionQuoteContext.Unquoted ? ShellQuoteKind.None : ShellQuoteKind.Double);
        var builder = new ExpandedWordBuilder(
            scope.Variables, scope.Effects.Memory, purpose, implicitEmptyPolicy, scope.Effects.CancellationToken);
        await ExpandWordPartsAsync(word, scope, builder, allowWordSplitting: false, allowLeadingTilde: purpose is not (ExpansionPurpose.Arithmetic or ExpansionPurpose.ArithmeticSource))
            .ConfigureAwait(false);
        return !scope.CanExpand ? [] : builder.Build();
    }

    private static ExpansionWordSyntax GetOperandSyntax(ExpansionPurpose purpose) => purpose switch
    {
        ExpansionPurpose.Arithmetic => ExpansionWordSyntax.Arithmetic,
        ExpansionPurpose.ArithmeticSource => ExpansionWordSyntax.ArithmeticSource,
        _ => ExpansionWordSyntax.Parameter,
    };

    private static bool TrySetVariable(
        BindingName name,
        ShellValue value,
        ScopedEvaluation scope)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        var key = name;
        if (scope.Variables.IsReadonly(key)
            || scope.Variables.GetNamerefAssignmentFailure(key, key, value, scope.Effects.Memory)
                != NamerefAssignmentFailure.None)
        {
            return false;
        }

        scope.SetExpansionValueIgnoringReadonly(key, value);
        return true;
    }

    /// <summary>
    /// Appends an integer value to the output as text.
    /// </summary>
    private static void AppendNumber(ExpandedWordBuilder output, long value, bool allowWordSplitting, bool globQuoted)
    {
        output.AppendTextExpansion(Integers.FormatNumber(value).AsSpan(), allowWordSplitting, globQuoted);
    }
}

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Expands brace expressions in unquoted source before other expansions.
/// </summary>
/// <remarks>
/// Supports comma lists and sequence expressions.
/// </remarks>
internal static class BraceExpansion
{
    private static readonly SearchValues<byte> BraceScanMarkers = SearchValues.Create("{},.$`\\\"'"u8);

    private readonly record struct Position(int Part, int Offset);
    private readonly record struct Boundary(Position Position, int BraceIndex)
    {
        public Boundary AfterDelimiter() => this with { Position = Position with { Offset = Position.Offset + 1 } };
    }

    private readonly record struct FragmentRange(Boundary Start, Boundary End);
    private enum BraceKind { None, Alternatives, Sequence, LiteralSequence }
    private readonly record struct Sequence(long Start, Int128 Step, int Count, int Width, bool IsCharacter);

    private sealed class Brace(Boundary opening, int previousCommas)
    {
        public Boundary Opening { get; } = opening;
        public int PreviousCommas { get; } = previousCommas;
        public Boundary Closing { get; set; }
        public BraceKind Kind { get; set; }
        public bool HasNestedBrace { get; set; }
        public bool HasSequenceSeparator { get; set; }
        public List<Boundary>? Commas { get; set; }
        public Sequence? Sequence { get; set; }
        public ShellSource[]? SequenceValues { get; set; }
        public int ExpansionCount { get; set; }

        public int AlternativeCount => (Commas?.Count ?? 0) + 1;

        public FragmentRange Alternative(int index)
        {
            if (Commas is not { } commas)
                return new FragmentRange(Opening.AfterDelimiter(), Closing);
            var start = index == 0 ? Opening : commas[index - 1];
            var end = index == commas.Count ? Closing : commas[index];
            return new FragmentRange(start.AfterDelimiter(), end);
        }
    }

    // A branch borrows its prefix and continuation until its complete word can be materialized.
    private sealed record Continuation(FragmentRange Range, Continuation? Next);
    private sealed record Fragment(ShellSource Source, WordPart? Opaque, Fragment? Previous, int Count, bool GeneratedPunctuation);
    private readonly record struct Branch(Continuation? Continuation, Fragment? Prefix);

    /// <summary>Expands brace expressions across existing lexical parts in encounter order.</summary>
    public static IReadOnlyList<Word> Expand(Word word, EvaluationMemory memory, CancellationToken cancellationToken) =>
        ExpandAlternatives(word, memory, cancellationToken) ?? [word];

    /// <summary>Uses the legacy reader's continuation rules before scanning brace syntax.</summary>
    internal static ShellSource RemoveLegacyContinuations(ShellSource spelling, MemoryCapacity scratch,
        ByteMemoryBuilder output, CancellationToken cancellationToken)
    {
        var cursor = new ShellSourceCursor(spelling, 0, new ShellScanContext { SyntaxMemory = scratch });
        var bytes = spelling.AsSpan();
        var written = 0;
        while (!cursor.IsAtEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursor.TryConsumeQuoteOrEscape())
                continue;
            var start = cursor.Position;
            var legacy = cursor.Current == (byte)'`';
            if ((legacy || ShellSourceCursor.TryMatchLogicalText(bytes, start, "$(", out _))
                && cursor.SkipNestedExpansion(BracedParameterRecognition.DoubleQuotedOnly) is ShellNestedExpansionScan.Complete)
            {
                if (legacy)
                    for (var index = start + 1; index < cursor.Position - 1; index++)
                    {
                        if (bytes[index] != (byte)'\\')
                            continue;
                        if (bytes[index + 1] == (byte)'\n')
                        {
                            output.Append(bytes[written..index]);
                            written = index + 2;
                        }
                        index++;
                    }
                continue;
            }
            cursor.Advance();
        }
        if (written == 0)
            return spelling;
        output.Append(bytes[written..]);
        return ShellSource.FromBytes(output.ToMemory());
    }

    /// <summary>Checks the reached delimiter pass before materializing alternatives.</summary>
    internal static bool TryFindUnclosedParenthesis(ShellSource spelling, MemoryCapacity scratch,
        CancellationToken cancellationToken, out int diagnosticStart)
    {
        diagnosticStart = 0;
        if (!ArithmeticExpansionScanner.HasPossibleExpansionComment(spelling.AsSpan()))
            return false;

        var bytes = spelling.AsSpan();
        var context = new ShellScanContext { SyntaxMemory = scratch, ReadArithmeticExpansionComments = true };
        var cursor = new ShellSourceCursor(spelling, 0, context);
        while (!cursor.IsAtEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = cursor.Position;
            if (cursor.TryConsumeQuoteOrEscape(recognizeAnsiC: false))
                continue;
            // The brace reader treats backticks as ordinary bytes inside double quotes.
            if (cursor.Current == (byte)'$' && ShellSourceCursor.TryMatchLogicalText(bytes, start, "$(", out _)
                || !cursor.IsQuoted && cursor.Current == (byte)'`')
            {
                var result = cursor.SkipNestedExpansion(BracedParameterRecognition.DoubleQuotedOnly);
                if (context.MissingReachedArithmeticParenthesis)
                {
                    diagnosticStart = FindDiagnosticStart(start);
                    return true;
                }
                if (result is ShellNestedExpansionScan.Complete)
                    continue;
            }
            cursor.Advance();
        }
        return false;

        int FindDiagnosticStart(int failure)
        {
            // After a complete brace, the brace reader processes its suffix separately.
            var source = new SourceWordPart(WordPartKind.Unquoted, spelling, scratch);
            var braces = Scan([source], scratch, cancellationToken);
            var position = 0;
            for (var index = 0; index < braces.Count;)
            {
                var brace = braces[index];
                if (brace.Kind == BraceKind.None)
                {
                    index++;
                    continue;
                }
                if (brace.Closing.Position.Offset >= failure)
                    break;
                position = brace.Closing.Position.Offset + 1;
                index = brace.Closing.BraceIndex;
            }
            return position;
        }
    }

    internal static IReadOnlyList<Word>? ExpandAlternatives(Word word, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!word.Parts.Any(static part => part is SourceWordPart { Kind: WordPartKind.Unquoted } source
            && source.Source.AsSpan().Contains((byte)'{')))
            return null;
        using var scratch = new MemoryCapacity(memory.Budget, 0);
        var braces = Scan(word.Parts, scratch, cancellationToken);
        if (braces.Count == 0)
            return null;

        // Invalid and unclosed groups remain literal. Link around them so their valid
        // descendants can be found without revisiting the enclosing text.
        scratch.Grow(MemoryCapacity.ArrayBackingBytes<int>(braces.Count + 1));
        var nextBrace = new int[braces.Count + 1];
        nextBrace[^1] = braces.Count;
        for (var index = braces.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var brace = braces[index];
            if (brace.Kind == BraceKind.LiteralSequence)
            {
                nextBrace[index] = nextBrace[brace.Closing.BraceIndex];
                continue;
            }
            if (brace.Kind == BraceKind.None)
            {
                nextBrace[index] = nextBrace[index + 1];
                continue;
            }

            nextBrace[index] = index;
            if (brace.Sequence is { } sequence)
            {
                brace.ExpansionCount = sequence.Count;
                continue;
            }

            long count = 0;
            for (var alternative = 0; alternative < brace.AlternativeCount && count < Array.MaxLength; alternative++)
                count += Count(brace.Alternative(alternative));
            brace.ExpansionCount = (int)Math.Min(count, Array.MaxLength);
        }

        if (nextBrace[0] == braces.Count)
            return null;

        var wholeWord = new FragmentRange(new(new(0, 0), 0), new(new(word.Parts.Count, 0), braces.Count));
        var total = Count(wholeWord);
        if (total >= Array.MaxLength)
            throw new ByteMemoryBuilder.LimitExceededException();

        memory.Charge(checked(256L * total));
        var result = new List<Word>(total);
        var pending = new List<Branch>();
        var fragments = Array.Empty<Fragment>();
        scratch.PrepareListCapacity(pending, 1);
        scratch.Grow(64);
        pending.Add(new Branch(new Continuation(wholeWord, null), null));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (continuation, prefix) = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            while (continuation is { Range: var range, Next: var remainder })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = nextBrace[range.Start.BraceIndex];
                if (index >= range.End.BraceIndex)
                {
                    prefix = AppendRange(range.Start.Position, range.End.Position, prefix);
                    continuation = remainder;
                    continue;
                }

                var brace = braces[index];
                prefix = AppendRange(range.Start.Position, brace.Opening.Position, prefix);
                var suffix = Continue(new(brace.Closing.AfterDelimiter(), range.End), remainder);
                if (brace.Sequence is { } sequence)
                {
                    var values = brace.SequenceValues ??= MaterializeSequence(sequence, memory, scratch, cancellationToken);
                    scratch.PrepareListCapacity(pending, sequence.Count);
                    for (var value = sequence.Count - 1; value >= 0; value--)
                        pending.Add(new Branch(suffix, Append(values[value], null, prefix,
                            sequence.IsCharacter && values[value][0] is (byte)'\\' or (byte)'`')));
                }
                else
                {
                    scratch.PrepareListCapacity(pending, brace.AlternativeCount);
                    for (var alternative = brace.AlternativeCount - 1; alternative >= 0; alternative--)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pending.Add(new Branch(Continue(brace.Alternative(alternative), suffix), prefix));
                    }
                }
                break;
            }

            if (continuation == null)
                result.Add(Materialize(prefix, memory, scratch, ref fragments, cancellationToken));
        }
        return result;

        Continuation? Continue(FragmentRange range, Continuation? next)
        {
            if (range.Start.Position == range.End.Position)
                return next;
            scratch.Grow(64);
            return new Continuation(range, next);
        }

        int Count(FragmentRange range)
        {
            long count = 1;
            for (var index = nextBrace[range.Start.BraceIndex]; index < range.End.BraceIndex;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var brace = braces[index];
                count *= brace.ExpansionCount;
                if (count >= Array.MaxLength)
                    return Array.MaxLength;
                index = nextBrace[brace.Closing.BraceIndex];
            }
            return (int)count;
        }

        Fragment? AppendRange(Position start, Position end, Fragment? prefix)
        {
            for (var partIndex = start.Part; partIndex < word.Parts.Count && partIndex <= end.Part; partIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (partIndex == end.Part && end.Offset == 0)
                    break;
                var part = word.Parts[partIndex];
                if (part is SourceWordPart { Kind: WordPartKind.Unquoted } source)
                {
                    var begin = partIndex == start.Part ? start.Offset : 0;
                    var limit = partIndex == end.Part ? end.Offset : source.Source.Length;
                    if (begin < limit)
                        prefix = Append(source.Source.Slice(begin, limit - begin), null, prefix, generatedPunctuation: false);
                }
                else
                    prefix = Append(default, part, prefix, generatedPunctuation: false);
            }
            return prefix;
        }

        Fragment Append(ShellSource source, WordPart? opaque, Fragment? prefix, bool generatedPunctuation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scratch.Grow(64);
            return new Fragment(source, opaque, prefix, (prefix?.Count ?? 0) + 1, generatedPunctuation);
        }
    }

    private static List<Brace> Scan(IReadOnlyList<WordPart> parts, MemoryCapacity scratch, CancellationToken cancellationToken)
    {
        const int BraceBytes = 192;
        const int MaximumScanBytes = 4096;
        var braces = new List<Brace>();
        var open = new List<Brace>();
        var context = new ShellScanContext { SyntaxMemory = scratch };
        var rawCommas = 0;
        var parameterBraces = 0;
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = parts[partIndex];
            var rawOffset = 0;
            if (part is not SourceWordPart { Kind: WordPartKind.Unquoted } unquoted)
            {
                if (open.Count != 0 && part.Kind != WordPartKind.Escaped)
                {
                    var opaque = part.GetUnexpandedValue().Bytes.Span;
                    CountRawCommas(opaque, opaque.Length, ref rawOffset);
                }
                continue;
            }

            var source = unquoted.Source;
            var bytes = source.AsSpan();
            if (bytes.IndexOfAny("{},."u8) < 0)
                continue;
            var cursor = new ShellSourceCursor(source, 0, context);
            while (!cursor.IsAtEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = cursor.Position;
                if (!BraceScanMarkers.Contains(cursor.Current) && !cursor.IsQuoted)
                {
                    // Plain bytes cannot change the cursor's quote or brace state.
                    var next = offset + 1;
                    var window = bytes.Slice(next, Math.Min(bytes.Length - next, MaximumScanBytes - 1));
                    var marker = window.IndexOfAny(BraceScanMarkers);
                    cursor = new ShellSourceCursor(source, next + (marker < 0 ? window.Length : marker), context);
                    continue;
                }

                if (cursor.TryConsumeQuoteOrEscape())
                    continue;
                if (cursor.Current is (byte)'$' or (byte)'`')
                {
                    if (!cursor.IsQuoted)
                    {
                        // During brace generation, plain braces inside a parameter
                        // expression also balance its outer delimiter.
                        if (ShellSourceCursor.TryMatchLogicalText(bytes, offset, "${", out var after))
                        {
                            parameterBraces++;
                            cursor = new ShellSourceCursor(source, after, context);
                            continue;
                        }
                        if (unquoted.TryGetArithmetic(offset, out var arithmetic))
                        {
                            cursor = new ShellSourceCursor(source, arithmetic.End + 1, context);
                            continue;
                        }
                    }
                    if (cursor.SkipNestedExpansion(BracedParameterRecognition.DoubleQuotedOnly) is ShellNestedExpansionScan.Complete)
                        continue;
                }

                if (!cursor.IsQuoted)
                {
                    if (parameterBraces != 0)
                    {
                        if (cursor.Current == (byte)'{') parameterBraces++;
                        if (cursor.Current == (byte)'}') parameterBraces--;
                    }
                    else
                    {
                        switch (cursor.Current)
                        {
                            case (byte)'{':
                                if (open.Count == 0)
                                {
                                    // Only comma counts inside the current group affect its expansion.
                                    rawOffset = offset + 1;
                                    rawCommas = 0;
                                }
                                else
                                {
                                    CountRawCommas(bytes, offset, ref rawOffset);
                                    open[^1].HasNestedBrace = true;
                                }
                                scratch.PrepareListCapacity(braces, 1);
                                scratch.PrepareListCapacity(open, 1);
                                scratch.Grow(BraceBytes);
                                var candidate = new Brace(new(new(partIndex, offset), braces.Count + 1), rawCommas);
                                braces.Add(candidate);
                                open.Add(candidate);
                                break;
                            case (byte)',' when open.Count != 0:
                                scratch.Grow(64);
                                (open[^1].Commas ??= []).Add(new(new(partIndex, offset), braces.Count));
                                break;
                            case (byte)'.' when open.Count != 0
                                && bytes[offset..].StartsWith(".."u8)
                                && (offset + 2 == bytes.Length || bytes[offset + 2] != (byte)'}'):
                                open[^1].HasSequenceSeparator = true;
                                break;
                            case (byte)'}' when open.Count != 0:
                                var brace = open[^1];
                                open.RemoveAt(open.Count - 1);
                                CountRawCommas(bytes, offset, ref rawOffset);
                                brace.Closing = new(new(partIndex, offset), braces.Count);
                                var opening = brace.Opening;
                                if (brace.Commas != null || brace.HasSequenceSeparator && rawCommas != brace.PreviousCommas)
                                    brace.Kind = BraceKind.Alternatives;
                                else if (brace.HasSequenceSeparator)
                                {
                                    brace.Kind = BraceKind.LiteralSequence;
                                    if (opening.Position.Part == partIndex && !brace.HasNestedBrace
                                        && TryParseSequence(bytes[(opening.Position.Offset + 1)..offset],
                                            cancellationToken, out var parsed))
                                    {
                                        brace.Sequence = parsed;
                                        brace.Kind = BraceKind.Sequence;
                                    }
                                }
                                else if (opening.BraceIndex == braces.Count)
                                {
                                    // Earlier boundaries still point to the next reached group at this index.
                                    braces.RemoveAt(braces.Count - 1);
                                    scratch.Resize(scratch.Bytes - BraceBytes);
                                }
                                break;
                        }
                    }
                }
                cursor.Advance();
            }
            if (open.Count != 0)
                CountRawCommas(bytes, bytes.Length, ref rawOffset);
        }
        return braces;

        // A sequence-shaped group containing a comma in its raw body expands
        // that body as alternatives, even when the comma is nested or protected.
        void CountRawCommas(ReadOnlySpan<byte> bytes, int end, ref int offset)
        {
            while (offset < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes[offset] == (byte)'\\')
                {
                    offset += 2;
                    continue;
                }
                if (end - offset < 16)
                {
                    if (bytes[offset++] == (byte)',')
                        rawCommas++;
                    continue;
                }

                // Count commas in unescaped runs, keeping cancellation checks between bounded scans.
                var window = bytes.Slice(offset, Math.Min(end - offset, MaximumScanBytes));
                var escape = window.IndexOf((byte)'\\');
                var literalLength = escape < 0 ? window.Length : escape;
                rawCommas += window[..literalLength].Count((byte)',');
                offset += literalLength + (escape < 0 ? 0 : 2);
            }
        }
    }

    private static Word Materialize(Fragment? prefix, EvaluationMemory memory, MemoryCapacity scratch,
        ref Fragment[] fragmentBuffer, CancellationToken cancellationToken)
    {
        if (prefix == null)
            return new Word([]);

        if (prefix.Count > fragmentBuffer.Length)
        {
            var previous = fragmentBuffer.Length;
            var capacity = (int)Math.Max(prefix.Count, Math.Min(Array.MaxLength, 2L * previous));
            scratch.Grow(MemoryCapacity.ArrayBackingBytes<Fragment>(capacity));
            fragmentBuffer = new Fragment[capacity];
            scratch.Resize(scratch.Bytes - MemoryCapacity.ArrayBackingBytes<Fragment>(previous));
        }
        var fragments = fragmentBuffer.AsSpan(0, prefix.Count);
        var partCount = 0;
        var adjoiningSource = false;
        for (var current = prefix; current != null; current = current.Previous)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fragments[current.Count - 1] = current;
            // Adjacent source fragments become one part. Each opaque part breaks the run.
            var isSource = current.Opaque == null;
            if (!isSource || !adjoiningSource)
                partCount++;
            adjoiningSource = isSource;
        }

        memory.Charge(MemoryCapacity.ArrayBackingBytes<WordPart>(partCount));
        var parts = new List<WordPart>(partCount);
        var rebuildFrom = -1;
        for (var index = 0; index < fragments.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fragments[index].Opaque is { } part)
            {
                parts.Add(part);
                index++;
                continue;
            }

            // Rejoin adjacent unquoted pieces so suffixes keep their original
            // parameter, tilde, generated escape and pathname semantics.
            var source = fragments[index].Source;
            var generatedPunctuation = fragments[index].GeneratedPunctuation;
            long length = source.Length;
            var end = index + 1;
            while (end < fragments.Length && fragments[end].Opaque == null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                length += fragments[end].Source.Length;
                generatedPunctuation |= fragments[end].GeneratedPunctuation;
                end++;
            }

            if (end > index + 1)
            {
                if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                    throw new ByteMemoryBuilder.LimitExceededException();
                memory.Charge(checked(32L + length));
                var combined = new byte[(int)length];
                var destination = combined.AsSpan();
                for (; index < end; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = fragments[index].Source.AsSpan();
                    bytes.CopyTo(destination);
                    destination = destination[bytes.Length..];
                }
                source = ShellSource.FromBytes(combined);
            }

            if (rebuildFrom < 0 && generatedPunctuation && end < fragments.Length)
                rebuildFrom = parts.Count;
            parts.Add(new SourceWordPart(WordPartKind.Unquoted, source, memory.Workspace) { IsBraceExpanded = true });
            index = end;
        }

        fragments.Clear();
        if (rebuildFrom >= 0)
        {
            // A generated escape or backtick can reinterpret an adjacent lexical part.
            // Re-read only that tail. Earlier parts keep their original expansion syntax.
            var tailCount = parts.Count - rebuildFrom;
            var spelling = new ShellScanContext { SyntaxMemory = memory.Workspace }
                .CreateBoundedByteBuilder(0, ByteMemoryBuilder.MaxMemoryBuilderBytes - 1);
            if (!FunctionDefinitionFormatter.TryAppendWordSource(parts, rebuildFrom, spelling, memory.Budget,
                    cancellationToken, normalizeLegacySpelling: false))
                throw new ByteMemoryBuilder.LimitExceededException();
            var rebuilt = Tokenizer.ReadGeneratedWord(ShellSource.FromBytes(spelling.ToMemory()), memory.Workspace);
            parts.RemoveRange(rebuildFrom, tailCount);
            memory.PrepareListCapacity(parts, rebuilt.Parts.Count);
            parts.AddRange(rebuilt.Parts);
        }
        return new Word(parts);
    }

    /// <summary>
    /// Parses a bounded sequence before preparing any of its values.
    /// </summary>
    /// <param name="source">The source bytes inside braces.</param>
    /// <param name="sequence">The parsed sequence and its length.</param>
    /// <returns>True when the content is a valid sequence expression.</returns>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    private static bool TryParseSequence(
        ReadOnlySpan<byte> source,
        CancellationToken cancellationToken,
        out Sequence sequence)
    {
        sequence = default;
        var first = IndexOfDoubleDot(source, 0, cancellationToken);
        if (first < 0) return false;

        var second = IndexOfDoubleDot(source, first + 2, cancellationToken);
        var startSpan = source[..first];
        var endSpan = second >= 0
            ? source.Slice(first + 2, second - first - 2)
            : source[(first + 2)..];

        long increment = 1;
        if (second >= 0 && !Integers.TryParseNumber(source[(second + 2)..], out increment))
            return false;
        var step = increment == 0 ? 1 : Int128.Abs(increment);

        if (Integers.TryParseNumber(startSpan, out var startValue)
            && Integers.TryParseNumber(endSpan, out var endValue))
        {
            var difference = (Int128)endValue - startValue;
            var distance = Int128.Abs(difference);
            // The Bash 5.2 subtraction guard leaves room for the first value and terminator.
            if (distance > long.MaxValue
                || startValue < 0 && difference > long.MaxValue - 2
                || startValue > 0 && difference < long.MinValue + 3)
                return false;

            var width = IsPadded(startSpan) || IsPadded(endSpan) ? Math.Max(startSpan.Length, endSpan.Length) : 0;
            return TryCreateSequence(startValue, endValue, distance, width, isCharacter: false, out sequence);
        }

        if (IsLetter(startSpan) && IsLetter(endSpan))
        {
            var start = startSpan[0];
            var end = endSpan[0];
            return TryCreateSequence(start, end, Math.Abs(end - start), 0, isCharacter: true, out sequence);
        }

        return false;

        bool TryCreateSequence(long start, long end, Int128 distance, int width, bool isCharacter, out Sequence range)
        {
            range = default;
            // An ascending range must be able to reverse a negative increment.
            if (start < end && increment == long.MinValue)
                return false;
            // Widen magnitude and count to cover long.MinValue and avoid an overflowing terminal step.
            var count = distance / step + 1;
            if (count > int.MaxValue - 2)
                return false;
            range = new Sequence(start, start <= end ? step : -step, (int)count, width, isCharacter);
            return true;
        }

        static bool IsPadded(ReadOnlySpan<byte> endpoint)
        {
            var digits = endpoint[0] == (byte)'-' ? endpoint[1..] : endpoint;
            return digits.Length > 1 && digits[0] == (byte)'0';
        }

        static bool IsLetter(ReadOnlySpan<byte> token) =>
            token.Length == 1 && AsciiByte.ToLowerAscii(token[0]) is >= (byte)'a' and <= (byte)'z';
    }

    private static ShellSource[] MaterializeSequence(
        Sequence sequence, EvaluationMemory memory, MemoryCapacity scratch, CancellationToken cancellationToken)
    {
        var maximumValueBytes = sequence.IsCharacter ? 1 : Math.Max(20, sequence.Width);
        scratch.Grow(MemoryCapacity.ArrayBackingBytes<ShellSource>(sequence.Count));
        // A generated value can become a returned word's source bytes.
        memory.Charge(checked(sequence.Count * MemoryCapacity.ArrayBackingBytes<byte>(maximumValueBytes)));
        var values = new ShellSource[sequence.Count];
        for (var index = 0; index < values.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = (long)(sequence.Start + index * sequence.Step);
            values[index] = sequence.IsCharacter
                ? ShellSource.FromBytes(new byte[] { (byte)value })
                : FormatNumber(value, sequence.Width);
        }
        return values;

        static ShellSource FormatNumber(long value, int width)
        {
            // The pinned Bash 5.2 builds narrow padded output to signed int after stepping in intmax_t.
            if (width != 0)
                value = unchecked((int)value);
            Span<byte> text = stackalloc byte[20];
            if (!Utf8Formatter.TryFormat(value, text, out var written))
                throw new InvalidOperationException("A signed decimal must fit its format buffer.");

            var padding = Math.Max(0, width - written);
            var bytes = new byte[written + padding];
            var signWidth = value < 0 ? 1 : 0;
            if (signWidth != 0)
                bytes[0] = (byte)'-';
            bytes.AsSpan(signWidth, padding).Fill((byte)'0');
            text[signWidth..written].CopyTo(bytes.AsSpan(signWidth + padding));
            return ShellSource.FromBytes(bytes);
        }
    }

    /// <summary>
    /// Finds the next ".." sequence starting at the provided index.
    /// </summary>
    /// <param name="span">The sequence text to scan.</param>
    /// <param name="start">The index to start scanning.</param>
    /// <returns>The index of the first dot in a ".." sequence, or -1 if none found.</returns>
    /// <param name="cancellationToken">Cancels scanning the source.</param>
    private static int IndexOfDoubleDot(ReadOnlySpan<byte> span, int start, CancellationToken cancellationToken)
    {
        for (var index = start; index + 1 < span.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (span[index] == (byte)'.' && span[index + 1] == (byte)'.')
            {
                return index;
            }
        }

        return -1;
    }
}

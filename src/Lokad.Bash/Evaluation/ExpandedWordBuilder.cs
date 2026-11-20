using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Controls whether expansion preserves quoted aggregate fields.</summary>
internal enum ExpansionQuoteContext
{
    Unquoted,
    DoubleQuoted,
    HereDocument,
}

/// <summary>Controls aggregate handling and operand syntax for values, diagnostics and arithmetic.</summary>
internal enum ExpansionPurpose
{
    Normal,
    Scalar,
    Assignment,
    IndexedCompound,
    IndexedCompoundOperand,
    CompoundPrefix,
    ErrorMessage,
    Arithmetic,
    ArithmeticSource,
}

/// <summary>Controls empty unquoted substitutions while collecting unsplit operands.</summary>
internal enum ImplicitEmptyPolicy
{
    Preserve,
    Remove,
}

/// <summary>Assembles word bytes and their provenance before applying IFS boundaries.</summary>
internal sealed class ExpandedWordBuilder
{
    private readonly Variables _variables;
    private readonly EvaluationMemory _memory;
    private readonly MemoryCapacity _storage;
    private readonly CancellationToken _cancellationToken;
    private readonly ImplicitEmptyPolicy _implicitEmptyPolicy;
    private readonly List<InputWord> _words = [];
    private InputWord _current;
    private DoubleQuoteState _doubleQuote;

    private enum DoubleQuoteState : byte { Outside, Open, EmptyExpansion, EmptyAggregate }

    public ExpandedWordBuilder(
        Variables variables,
        EvaluationMemory memory,
        ExpansionPurpose purpose,
        ImplicitEmptyPolicy implicitEmptyPolicy)
        : this(variables, memory, purpose, implicitEmptyPolicy, CancellationToken.None)
    {
    }

    public ExpandedWordBuilder(
        Variables variables,
        EvaluationMemory memory,
        ExpansionPurpose purpose,
        ImplicitEmptyPolicy implicitEmptyPolicy,
        CancellationToken cancellationToken)
        : this(variables, memory, purpose, implicitEmptyPolicy, cancellationToken, memory.Workspace)
    {
    }

    internal ExpandedWordBuilder(
        Variables variables,
        EvaluationMemory memory,
        ExpansionPurpose purpose,
        ImplicitEmptyPolicy implicitEmptyPolicy,
        CancellationToken cancellationToken,
        MemoryCapacity storage)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken = cancellationToken;
        Purpose = purpose;
        _variables = variables;
        _memory = memory;
        _storage = storage;
        _implicitEmptyPolicy = implicitEmptyPolicy;
        memory.Charge(storage, 256);
        _current = new();
        _words.Add(_current);
    }

    public ExpansionPurpose Purpose { get; }

    /// <summary>Whether substitutions contributed data, even if their output was empty.</summary>

    internal void BeginDoubleQuote() => _doubleQuote = DoubleQuoteState.Open;

    internal void EndDoubleQuote()
    {
        if (_doubleQuote == DoubleQuoteState.EmptyExpansion)
            MarkCurrentWordForEmission();
        _doubleQuote = DoubleQuoteState.Outside;
    }

    /// <summary>Retains literal-source provenance before field splitting.</summary>
    public void AppendSource(ShellSource source, bool globQuoted) =>
        GetPart(globQuoted ? PartOrigin.Protected : PartOrigin.Source, globQuoted).AppendValue(source.ToValue());

    public void AppendValue(ShellValue value, bool globQuoted) =>
        GetPart(PartOrigin.Protected, globQuoted).AppendValue(value);

    public void AppendExpansion(ShellValue value, bool allowWordSplitting, bool globQuoted)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        if (!value.IsEmpty)
            GetPart(allowWordSplitting ? PartOrigin.Expansion : PartOrigin.Protected, globQuoted).AppendValue(value);
        else if (!allowWordSplitting && (globQuoted || _implicitEmptyPolicy == ImplicitEmptyPolicy.Preserve))
            AppendEmptyExpansion(globQuoted);
    }

    public void AppendIntegerExpansion(long value, bool allowWordSplitting, bool globQuoted)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        GetPart(allowWordSplitting ? PartOrigin.Expansion : PartOrigin.Protected, globQuoted).AppendInteger(value);
    }

    private void AppendEmptyExpansion(bool globQuoted)
    {
        // An empty scalar or substitution in these quotes emits a field only if the
        // same quotes contain no fieldless aggregate. Literal empty fields are explicit.
        if (globQuoted && _doubleQuote != DoubleQuoteState.Outside)
        {
            if (_doubleQuote == DoubleQuoteState.Open)
                _doubleQuote = DoubleQuoteState.EmptyExpansion;
        }
        else
            GetPart(PartOrigin.Protected, globQuoted);
    }

    public void AppendFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext)
    {
        var fieldSeparators = FieldSeparators.FromVariables(_variables);
        if (Purpose == ExpansionPurpose.IndexedCompound
            || Purpose == ExpansionPurpose.IndexedCompoundOperand && quoteContext == ExpansionQuoteContext.DoubleQuoted)
        {
            // Compound @ joins use IFS except in unquoted parameter operands; empty IFS uses a space.
            var separator = fieldSeparators.IsEmpty ? ShellValue.FromText(" ") : fieldSeparators.First;
            AppendSeparatedFields(fields, separator, allowWordSplitting, globQuoted);
            return;
        }
        if (allowWordSplitting && !fieldSeparators.IsEmpty)
        {
            AppendJoinedFields(fields, allowWordSplitting, globQuoted);
            return;
        }

        // In diagnostic parameter words, @ protects its fields when IFS does not start with a space.
        var protectFields = quoteContext == ExpansionQuoteContext.DoubleQuoted
            || (Purpose == ExpansionPurpose.ErrorMessage && !allowWordSplitting
                && !fieldSeparators.IsEmpty && fieldSeparators.Bytes.Span[0] != (byte)' ');
        var first = true;
        foreach (var value in fields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                StartNewWord();
            first = false;

            if (protectFields)
            {
                if (value.IsEmpty)
                    AppendEmptyAggregateField();
                else
                    AppendValue(value, globQuoted: true);
            }
            else
            {
                AppendExpansion(value, allowWordSplitting, globQuoted);
            }
        }
        if (first && quoteContext == ExpansionQuoteContext.DoubleQuoted && _doubleQuote != DoubleQuoteState.Outside)
            _doubleQuote = DoubleQuoteState.EmptyAggregate;
    }

    /// <summary>Splices operand fields into the enclosing word without flattening their quote metadata.</summary>
    public void AppendExpandedWords(
        IReadOnlyList<ExpandedWord> words,
        bool allowWordSplitting,
        bool globQuoted)
    {
        if (words.Count == 0)
        {
            AppendExpansion(ShellValue.Empty, allowWordSplitting, globQuoted);
            return;
        }

        for (var index = 0; index < words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
                StartNewWord();
            var word = words[index];
            if (word.IsEmptyAggregateField)
            {
                AppendEmptyAggregateField();
                continue;
            }
            if (word.Segments.Count == 0)
                MarkCurrentWordForEmission();
            foreach (var segment in word.Segments)
                AppendExpansion(segment.Content, allowWordSplitting && !segment.IsGlobQuoted,
                    globQuoted || segment.IsGlobQuoted);
        }
    }

    private void StartNewWord()
    {
        _memory.Charge(_storage, 256);
        _current = new InputWord();
        _words.Add(_current);
    }

    public void AppendJoinedFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted)
    {
        var fieldSeparators = FieldSeparators.FromVariables(_variables);
        if (fieldSeparators.IsEmpty && allowWordSplitting)
        {
            AppendFields(fields, allowWordSplitting, globQuoted, ExpansionQuoteContext.Unquoted);
            return;
        }

        var separator = fieldSeparators.IsEmpty ? ShellValue.Empty : fieldSeparators.First;
        AppendSeparatedFields(fields, separator, allowWordSplitting, globQuoted);
    }

    internal void AppendSeparatedFields(
        IEnumerable<ShellValue> fields,
        ShellValue separator,
        bool allowWordSplitting,
        bool globQuoted)
    {
        var first = true;
        foreach (var value in fields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                AppendExpansion(separator, allowWordSplitting, globQuoted);
            first = false;
            AppendExpansion(value, allowWordSplitting, globQuoted);
        }
        if (first && !allowWordSplitting)
            AppendEmptyExpansion(globQuoted: true);
    }

    public void MarkCurrentWordForEmission() =>
        GetPart(PartOrigin.Protected, globQuoted: true);

    private void AppendEmptyAggregateField()
    {
        _current.HasEmptyAggregateField = true;
        MarkCurrentWordForEmission();
    }

    public IReadOnlyList<ExpandedWord> Build()
    {
        var results = new List<ExpandedWord>();
        AppendTo(results);
        return results;
    }

    internal void AppendTo(List<ExpandedWord> results)
    {
        // Expansions can assign IFS: joins use its value at expansion time, splitting uses the final value.
        var fieldSeparators = FieldSeparators.FromVariables(_variables);
        foreach (var word in _words)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var split = word.NeedsSplitting && !fieldSeparators.IsEmpty;
            if (!split)
            {
                // No boundary processing is needed: retain completed segment buffers.
                var segments = word.Parts.Select(part =>
                    new ExpandedSegment(part.Segment.ToValue(), part.Segment.IsGlobQuoted)).ToArray();
                var hasBytes = segments.Any(segment => !segment.Content.IsEmpty);
                if (hasBytes || word.Parts.Any(part => part.Origin == PartOrigin.Protected))
                    results.Add(new ExpandedWord(segments, !hasBytes && word.HasEmptyAggregateField));
                continue;
            }

            _memory.Charge(_storage, 256);
            var field = new List<SegmentBuilder>();
            var emitEmpty = false;
            var pendingBreak = false;
            using var pendingBytes = new ByteMemoryBuilder(0, _memory.Budget);
            using var quoteScratch = new MemoryCapacity(_memory.Budget, 0);
            var quoteRuns = new List<QuoteRun>();

            foreach (var part in word.Parts)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var value = part.Segment.ToValue();
                var quoted = part.Segment.IsGlobQuoted;
                if (part.Origin == PartOrigin.Protected)
                {
                    if (value.IsEmpty)
                    {
                        FlushExpansion();
                        ApplyPendingBreak();
                        emitEmpty = true;
                    }
                    else
                    {
                        AppendLiteral(value.Bytes, quoted);
                    }
                }
                else if (part.Origin == PartOrigin.Expansion)
                {
                    QueueExpansion(value.Bytes, quoted);
                }
                else
                {
                    var bytes = value.Bytes;
                    for (var position = 0; position < bytes.Length;)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var length = Utf8Text.FirstCharacterLength(bytes.Span[position..]);
                        // Bash protects one source byte per literal character before field splitting.
                        AppendLiteral(bytes.Slice(position, 1), quoted);
                        if (length > 1)
                            QueueExpansion(bytes.Slice(position + 1, length - 1), quoted);
                        position += length;
                    }
                }
            }
            FlushExpansion();
            CompleteField();

            void QueueExpansion(ReadOnlyMemory<byte> bytes, bool globQuoted)
            {
                if (bytes.IsEmpty)
                    return;
                pendingBytes.Append(bytes);
                var run = new QuoteRun(pendingBytes.Length, globQuoted);
                if (quoteRuns.Count > 0 && quoteRuns[^1].IsGlobQuoted == globQuoted)
                    quoteRuns[^1] = run;
                else
                {
                    // Cover both backing arrays at list growth, then reuse that allowance on every flush.
                    var needed = 32L * (quoteRuns.Count + 1L) + 32;
                    if (needed > quoteScratch.Bytes)
                        quoteScratch.Resize(needed);
                    quoteRuns.Add(run);
                }
            }

            void AppendLiteral(ReadOnlyMemory<byte> bytes, bool globQuoted)
            {
                FlushExpansion();
                if (bytes.IsEmpty)
                    return;
                ApplyPendingBreak();
                AppendBytes(bytes, globQuoted);
            }

            void AppendBytes(ReadOnlyMemory<byte> bytes, bool globQuoted)
            {
                if (field.Count == 0 || field[^1].IsGlobQuoted != globQuoted)
                    field.Add(new SegmentBuilder(globQuoted, _memory, _storage));
                field[^1].AppendCopy(bytes.Span);
            }

            void FlushExpansion()
            {
                if (pendingBytes.Length == 0)
                    return;

                var bytes = pendingBytes.ToMemory();
                var literalStart = 0;
                var position = 0;
                var quoteIndex = 0;
                while (position < bytes.Length)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var status = Rune.DecodeFromUtf8(bytes.Span[position..], out var character, out var length);
                    var isCharacter = status == OperationStatus.Done;
                    if (!isCharacter)
                        length = 1;
                    fieldSeparators = isCharacter
                        ? fieldSeparators.PrepareCharacters(_variables, _memory, _cancellationToken)
                        : fieldSeparators.PrepareBytes(_variables, _memory, _cancellationToken);
                    var isDelimiter = isCharacter ? fieldSeparators.Contains(character)
                        : fieldSeparators.Contains(bytes.Span[position]);
                    if (!isDelimiter)
                    {
                        position += length;
                        continue;
                    }

                    if (position > literalStart)
                    {
                        AppendPendingBytes(literalStart, position);
                    }
                    if (isCharacter && FieldSeparators.HasWhitespaceSemantics(character))
                    {
                        // Adjacent expansions can contribute to the same whitespace boundary.
                        pendingBreak = true;
                    }
                    else
                    {
                        emitEmpty = true;
                        StartNewField();
                    }
                    position += length;
                    literalStart = position;
                }

                if (literalStart < bytes.Length)
                {
                    AppendPendingBytes(literalStart, bytes.Length);
                }
                // Output segment assembly copied the bytes before this buffer is reused.
                pendingBytes.Truncate(0);
                quoteRuns.Clear();

                void AppendPendingBytes(int start, int end)
                {
                    ApplyPendingBreak();
                    while (start < end)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        while (start >= quoteRuns[quoteIndex].End)
                            quoteIndex++;
                        var run = quoteRuns[quoteIndex];
                        var next = Math.Min(end, run.End);
                        AppendBytes(bytes[start..next], run.IsGlobQuoted);
                        start = next;
                    }
                }
            }

            void ApplyPendingBreak()
            {
                if (!pendingBreak)
                    return;
                pendingBreak = false;
                if (field.Count > 0 || emitEmpty)
                    StartNewField();
            }

            void StartNewField()
            {
                _memory.Charge(_storage, 256);
                pendingBreak = false;
                CompleteField();
            }

            void CompleteField()
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (field.Count == 0 && !emitEmpty)
                    return;
                results.Add(new ExpandedWord(field
                    .Select(segment => new ExpandedSegment(segment.ToValue(), segment.IsGlobQuoted))
                    .ToArray()));
                field.Clear();
                emitEmpty = false;
            }
        }
    }

    private SegmentBuilder GetPart(PartOrigin origin, bool globQuoted)
    {
        if (_current.Parts.Count == 0
            || _current.Parts[^1].Origin != origin
            || _current.Parts[^1].Segment.IsGlobQuoted != globQuoted)
        {
            _current.Parts.Add(new InputPart(origin, new SegmentBuilder(globQuoted, _memory, _storage)));
        }
        return _current.Parts[^1].Segment;
    }

    private enum PartOrigin
    {
        /// <summary>Literal source characters, each protecting its leading byte during splitting.</summary>
        Source,
        /// <summary>Quoted values or literal expansion results; every byte is protected.</summary>
        Protected,
        /// <summary>Unquoted expansion bytes that can join across adjacent parts before splitting.</summary>
        Expansion
    }

    private sealed class InputWord
    {
        public List<InputPart> Parts { get; } = [];
        public bool NeedsSplitting { get; set; }
        public bool HasEmptyAggregateField { get; set; }
    }

    private readonly record struct InputPart(PartOrigin Origin, SegmentBuilder Segment);

    /// <summary>A glob-quote run ending at a byte offset, independent of IFS character boundaries.</summary>
    private readonly record struct QuoteRun(int End, bool IsGlobQuoted);

    private sealed class SegmentBuilder
    {
        private readonly EvaluationMemory _memory;
        private readonly MemoryCapacity _storage;
        private ShellValue _retained;
        private ByteMemoryBuilder? _bytes;

        public SegmentBuilder(bool isGlobQuoted, EvaluationMemory memory, MemoryCapacity storage)
        {
            memory.Charge(storage, 256);
            _memory = memory;
            _storage = storage;
            IsGlobQuoted = isGlobQuoted;
        }

        public bool IsGlobQuoted { get; }

        public void AppendValue(ShellValue value)
        {
            if (value.IsEmpty)
                return;
            if (_bytes == null && _retained.IsEmpty && value.TryGetCompactArrayCapacity(out var capacity))
            {
                // The result retains the array even when its producer gives up its charge.
                _memory.Charge(_storage, 32L + capacity);
                _retained = value;
                return;
            }
            AppendCopy(value.Bytes.Span);
        }

        public void AppendInteger(long value)
        {
            // A single result can retain exact-sized backing. Joined results copy digits
            // from scratch directly into the segment's admitted buffer.
            if (_bytes == null && _retained.IsEmpty)
            {
                AppendValue(ShellValue.FromInteger(value));
                return;
            }

            Span<byte> buffer = stackalloc byte[NumberFormatting.MaximumNumberLength];
            AppendCopy(NumberFormatting.FormatNumber(value, buffer));
        }

        public ShellValue ToValue() => _bytes == null ? _retained : ShellValue.FromBytes(_bytes.ToCompactMemory(_storage));

        public void AppendCopy(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return;
            if (_bytes != null)
            {
                _bytes.Append(bytes);
                return;
            }

            var prefix = _retained.Bytes.Span;
            if (bytes.Length > int.MaxValue - prefix.Length)
                throw new ByteMemoryBuilder.LimitExceededException();
            var buffer = new ByteMemoryBuilder(0, _storage);
            var destination = buffer.AppendSpan(prefix.Length + bytes.Length);
            prefix.CopyTo(destination);
            bytes.CopyTo(destination[prefix.Length..]);
            _bytes = buffer;
            _retained = ShellValue.Empty;
        }
    }
}

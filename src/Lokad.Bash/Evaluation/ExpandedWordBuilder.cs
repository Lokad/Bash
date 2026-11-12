using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
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
    private readonly CancellationToken _cancellationToken;
    private readonly ImplicitEmptyPolicy _implicitEmptyPolicy;
    private readonly List<InputWord> _words = [];
    private InputWord _current;

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
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken = cancellationToken;
        Purpose = purpose;
        _variables = variables;
        _memory = memory;
        _implicitEmptyPolicy = implicitEmptyPolicy;
        memory.Charge(256);
        _current = new();
        _words.Add(_current);
    }

    public ExpansionPurpose Purpose { get; }

    /// <summary>Whether substitutions contributed data, even if their output was empty.</summary>
    internal bool HasExpansion { get; private set; }

    /// <summary>Retains literal-source provenance before field splitting.</summary>
    public void AppendSource(ShellSource source, bool globQuoted) =>
        GetPart(globQuoted ? PartOrigin.Protected : PartOrigin.Source, globQuoted).AppendValue(source.ToValue());

    public void AppendText(ReadOnlySpan<char> text, bool globQuoted) =>
        GetPart(PartOrigin.Protected, globQuoted).AppendText(text);

    public void AppendValue(ShellValue value, bool globQuoted) =>
        GetPart(PartOrigin.Protected, globQuoted).AppendValue(value);

    public void AppendTextExpansion(ReadOnlySpan<char> text, bool allowWordSplitting, bool globQuoted)
    {
        HasExpansion = true;
        _current.NeedsSplitting |= allowWordSplitting;
        if (!text.IsEmpty)
            GetPart(allowWordSplitting ? PartOrigin.Expansion : PartOrigin.Protected, globQuoted).AppendText(text);
        else if (!allowWordSplitting && (globQuoted || _implicitEmptyPolicy == ImplicitEmptyPolicy.Preserve))
            GetPart(PartOrigin.Protected, globQuoted);
    }

    public void AppendExpansion(ShellValue value, bool allowWordSplitting, bool globQuoted)
    {
        HasExpansion = true;
        _current.NeedsSplitting |= allowWordSplitting;
        if (!value.IsEmpty)
            GetPart(allowWordSplitting ? PartOrigin.Expansion : PartOrigin.Protected, globQuoted).AppendValue(value);
        else if (!allowWordSplitting && (globQuoted || _implicitEmptyPolicy == ImplicitEmptyPolicy.Preserve))
            GetPart(PartOrigin.Protected, globQuoted);
    }

    public void AppendFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext)
    {
        HasExpansion = true;
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
                    MarkCurrentWordForEmission();
                else
                    AppendValue(value, globQuoted: true);
            }
            else
            {
                AppendExpansion(value, allowWordSplitting, globQuoted);
            }
        }
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
            if (word.Segments.Count == 0)
                MarkCurrentWordForEmission();
            foreach (var segment in word.Segments)
                AppendExpansion(segment.Content, allowWordSplitting && !segment.IsGlobQuoted,
                    globQuoted || segment.IsGlobQuoted);
        }
    }

    private void StartNewWord()
    {
        _memory.Charge(256);
        _current = new InputWord();
        _words.Add(_current);
    }

    public void AppendJoinedFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted)
    {
        HasExpansion = true;
        var fieldSeparators = FieldSeparators.FromVariables(_variables);
        if (fieldSeparators.IsEmpty && allowWordSplitting)
        {
            AppendFields(fields, allowWordSplitting, globQuoted, ExpansionQuoteContext.Unquoted);
            return;
        }

        var separator = fieldSeparators.IsEmpty ? ShellValue.Empty : fieldSeparators.First;
        AppendSeparatedFields(fields, separator, allowWordSplitting, globQuoted);
    }

    private void AppendSeparatedFields(
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
            MarkCurrentWordForEmission();
    }

    public void MarkCurrentWordForEmission() =>
        GetPart(PartOrigin.Protected, globQuoted: true);

    public IReadOnlyList<ExpandedWord> Build()
    {
        // Expansions can assign IFS: joins use its value at expansion time, splitting uses the final value.
        var fieldSeparators = FieldSeparators.FromVariables(_variables);
        var results = new List<ExpandedWord>();
        foreach (var word in _words)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var split = word.NeedsSplitting && !fieldSeparators.IsEmpty;
            if (!split)
            {
                // No boundary processing is needed: retain completed segment buffers.
                var segments = word.Parts.Select(part =>
                    new ExpandedSegment(part.Segment.ToValue(), part.Segment.IsGlobQuoted)).ToArray();
                if (segments.Any(segment => !segment.Content.IsEmpty)
                    || word.Parts.Any(part => part.Origin == PartOrigin.Protected))
                    results.Add(new ExpandedWord(segments));
                continue;
            }

            _memory.Charge(256);
            var fields = new List<List<SegmentBuilder>> { new() };
            var emitEmpty = new List<bool> { false };
            var pendingBreak = false;
            var pendingBytes = ByteMemoryBuilder.CreateUnaccounted(0);
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
                        emitEmpty[^1] = true;
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

            for (var index = 0; index < fields.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (fields[index].Count > 0 || emitEmpty[index])
                {
                    results.Add(new ExpandedWord(fields[index]
                        .Select(segment => new ExpandedSegment(segment.ToValue(), segment.IsGlobQuoted))
                        .ToArray()));
                }
            }

            void QueueExpansion(ReadOnlyMemory<byte> bytes, bool globQuoted)
            {
                if (bytes.IsEmpty)
                    return;
                _memory.Charge(checked(4L * bytes.Length));
                pendingBytes.Append(bytes);
                var run = new QuoteRun(pendingBytes.Length, globQuoted);
                if (quoteRuns.Count > 0 && quoteRuns[^1].IsGlobQuoted == globQuoted)
                    quoteRuns[^1] = run;
                else
                {
                    _memory.Charge(256);
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
                var current = fields[^1];
                if (current.Count == 0 || current[^1].IsGlobQuoted != globQuoted)
                    current.Add(new SegmentBuilder(globQuoted, _memory));
                current[^1].AppendValue(ShellValue.FromBytes(bytes));
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
                    var isDelimiter = isCharacter
                        ? fieldSeparators.Contains(character)
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
                        emitEmpty[^1] = true;
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
                if (fields[^1].Count > 0 || emitEmpty[^1])
                    StartNewField();
            }

            void StartNewField()
            {
                _memory.Charge(256);
                pendingBreak = false;
                fields.Add([]);
                emitEmpty.Add(false);
            }
        }
        return results;
    }

    private SegmentBuilder GetPart(PartOrigin origin, bool globQuoted)
    {
        if (_current.Parts.Count == 0
            || _current.Parts[^1].Origin != origin
            || _current.Parts[^1].Segment.IsGlobQuoted != globQuoted)
        {
            _current.Parts.Add(new InputPart(origin, new SegmentBuilder(globQuoted, _memory)));
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
    }

    private readonly record struct InputPart(PartOrigin Origin, SegmentBuilder Segment);

    /// <summary>A glob-quote run ending at a byte offset, independent of IFS character boundaries.</summary>
    private readonly record struct QuoteRun(int End, bool IsGlobQuoted);

    private sealed class SegmentBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly ByteMemoryBuilder _bytes = ByteMemoryBuilder.CreateUnaccounted(0);
        private readonly EvaluationMemory _memory;

        public SegmentBuilder(bool isGlobQuoted, EvaluationMemory memory)
        {
            memory.Charge(256);
            _memory = memory;
            IsGlobQuoted = isGlobQuoted;
        }

        public bool IsGlobQuoted { get; }

        public void AppendText(ReadOnlySpan<char> text)
        {
            _memory.Charge(checked(4L * text.Length));
            _text.Append(text);
        }

        public void AppendValue(ShellValue value)
        {
            FlushText();
            _memory.Charge(checked(4L * value.Bytes.Length));
            if (value.IsEmpty)
                return;
            AppendBytes(value.Bytes.Span);
        }

        public ShellValue ToValue()
        {
            FlushText();
            return ShellValue.FromBytes(_bytes.ToMemory());
        }

        private void AppendBytes(ReadOnlySpan<byte> bytes)
        {
            // The enclosing command admits segment storage before appending; its
            // coarse estimate includes geometric capacity and overlapping copies.
            _bytes.Append(bytes);
        }

        private void FlushText()
        {
            if (_text.Length == 0)
                return;

            // Encode text-only consumer runs; raw source and value bytes bypass this buffer.
            Utf8Text.TryEncode(_text, AppendEncoded);
            _text.Clear();

            bool AppendEncoded(ReadOnlySpan<byte> bytes)
            {
                _memory.Charge(checked(4L * bytes.Length));
                AppendBytes(bytes);
                return true;
            }
        }
    }
}

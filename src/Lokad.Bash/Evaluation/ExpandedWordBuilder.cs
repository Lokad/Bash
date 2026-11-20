using System;
using System.Buffers;
using System.Collections.Generic;
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

internal enum HereDocumentJoin
{
    At,
    PositionalStar,
    SpaceBetweenProtectedValues,
    UnprotectedIfs,
    IfsBetweenProtectedValues,
}

internal enum ParameterFieldJoin { Default, IfsOrSpace, Space }

/// <summary>Controls aggregate handling and operand syntax for values, diagnostics and arithmetic.</summary>
internal enum ExpansionPurpose
{
    Normal,
    Scalar,
    CasePattern,
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
    private static readonly ShellValue Space = ShellValue.FromText(" ");
    private readonly Variables _variables;
    private readonly EvaluationMemory _memory;
    private readonly MemoryCapacity _storage;
    private readonly CancellationToken _cancellationToken;
    private readonly ImplicitEmptyPolicy _implicitEmptyPolicy;
    private readonly bool _casePatternStartsWithEmptyIfs;
    private readonly List<InputWord> _words = [];
    private InputWord _current;
    private DoubleQuoteState _doubleQuote;
    private bool _hereDocumentHasAt;
    private bool _hasQuotedAggregate;

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
        _casePatternStartsWithEmptyIfs = purpose == ExpansionPurpose.CasePattern
            && FieldSeparators.FromVariables(variables).IsEmpty;
        memory.Charge(storage, 256);
        _current = new();
        _words.Add(_current);
    }

    public ExpansionPurpose Purpose { get; }
    internal bool AssigningHereDocumentOperand { get; init; }

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
        GetPart(globQuoted ? PartOrigin.Protected : Purpose == ExpansionPurpose.CasePattern
            ? PartOrigin.Expansion : PartOrigin.Source, globQuoted).AppendValue(source.ToValue());

    public void AppendValue(ShellValue value, bool globQuoted) =>
        GetPart(PartOrigin.Protected, globQuoted).AppendValue(value);

    public void AppendExpansion(ShellValue value, bool allowWordSplitting, bool globQuoted)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        if (!value.IsEmpty)
            GetPart(ExpansionOrigin(allowWordSplitting, globQuoted), globQuoted).AppendValue(value);
        else if (!allowWordSplitting && (globQuoted || _implicitEmptyPolicy == ImplicitEmptyPolicy.Preserve))
            AppendEmptyExpansion(globQuoted);
    }

    public void AppendIntegerExpansion(long value, bool allowWordSplitting, bool globQuoted)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        GetPart(ExpansionOrigin(allowWordSplitting, globQuoted), globQuoted).AppendInteger(value);
    }

    private PartOrigin ExpansionOrigin(bool allowWordSplitting, bool globQuoted) =>
        allowWordSplitting || (Purpose == ExpansionPurpose.CasePattern && !globQuoted)
            ? PartOrigin.Expansion : PartOrigin.Protected;

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
            GetPart(ExpansionOrigin(allowWordSplitting: false, globQuoted), globQuoted);
    }

    public void AppendFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext)
    {
        if (quoteContext == ExpansionQuoteContext.HereDocument)
        {
            AppendHereDocumentFields(fields, HereDocumentJoin.At);
            return;
        }

        _hasQuotedAggregate |= Purpose == ExpansionPurpose.CasePattern
            && quoteContext == ExpansionQuoteContext.DoubleQuoted;
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
        // Non-space IFS can protect aggregate bytes from field selection while retaining glob activity.
        var protectCaseFields = Purpose == ExpansionPurpose.CasePattern && HasNonSpaceSeparator(fieldSeparators);
        var caseSeparator = Purpose == ExpansionPurpose.CasePattern ? CasePatternSeparator(fieldSeparators) : default;
        var first = true;
        foreach (var value in fields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                StartNewWord(caseSeparator);
            first = false;

            if (protectFields)
            {
                if (value.IsEmpty)
                    AppendEmptyAggregateField();
                else
                    AppendValue(value, globQuoted: true);
            }
            else if (protectCaseFields)
                AppendValue(value, globQuoted);
            else
            {
                AppendExpansion(value, allowWordSplitting, globQuoted);
            }
        }
        if (first && quoteContext == ExpansionQuoteContext.DoubleQuoted && _doubleQuote != DoubleQuoteState.Outside)
            _doubleQuote = DoubleQuoteState.EmptyAggregate;
    }

    /// <summary>Records the protected text and exposed separators until the complete body is expanded.</summary>
    internal void AppendHereDocumentFields(IEnumerable<ShellValue> fields, HereDocumentJoin join)
    {
        if (join == HereDocumentJoin.At)
            _hereDocumentHasAt = true;
        var separator = Space;
        if (join is not (HereDocumentJoin.PositionalStar or HereDocumentJoin.SpaceBetweenProtectedValues)
            && !(join == HereDocumentJoin.At && AssigningHereDocumentOperand))
        {
            var ifs = FieldSeparators.FromVariables(_variables);
            separator = ifs.IsEmpty ? join == HereDocumentJoin.At ? Space : ShellValue.Empty : ifs.First;
        }
        var origin = join is HereDocumentJoin.PositionalStar or HereDocumentJoin.UnprotectedIfs
            ? PartOrigin.Expansion : PartOrigin.Protected;
        var first = true;
        foreach (var value in fields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!first && !separator.IsEmpty)
                GetPart(PartOrigin.Expansion, globQuoted: true).AppendValue(separator);
            first = false;
            if (join == HereDocumentJoin.At || !value.IsEmpty)
                GetPart(origin, globQuoted: true).AppendValue(value);
        }
    }

    internal void IncludeOperand(ExpandedWordBuilder operand)
    {
        _hereDocumentHasAt |= operand._hereDocumentHasAt;
        _hasQuotedAggregate |= operand._hasQuotedAggregate;
    }

    internal void AppendHereDocumentUnprotected(ShellValue value) =>
        GetPart(PartOrigin.Expansion, globQuoted: true).AppendValue(value);

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

        var caseSeparator = Purpose == ExpansionPurpose.CasePattern && words.Count > 1
            ? CasePatternSeparator(FieldSeparators.FromVariables(_variables)) : default;
        for (var index = 0; index < words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
                StartNewWord(caseSeparator);
            var word = words[index];
            if (word.IsEmptyAggregateField)
            {
                AppendEmptyAggregateField();
                continue;
            }
            if (word.Segments.Count == 0)
                MarkCurrentWordForEmission();
            foreach (var segment in word.Segments)
            {
                var quoted = globQuoted || segment.IsGlobQuoted;
                if (word.IsJoinedCasePatternOperand)
                    AppendValue(segment.Content, quoted);
                else
                    AppendExpansion(segment.Content, allowWordSplitting && !segment.IsGlobQuoted, quoted);
            }
        }
    }

    private void StartNewWord(ShellValue casePatternSeparator)
    {
        _memory.Charge(_storage, 256);
        _current = new InputWord { CasePatternSeparator = casePatternSeparator };
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

    internal bool TryAppendSelectedFields(ParameterFieldJoin join, IEnumerable<ShellValue> fields,
        bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext)
    {
        if (join == ParameterFieldJoin.Default)
            return false;
        var ifs = FieldSeparators.FromVariables(_variables);
        var useSpace = join == ParameterFieldJoin.Space || ifs.IsEmpty;
        if (quoteContext == ExpansionQuoteContext.HereDocument)
            AppendHereDocumentFields(fields, useSpace ? HereDocumentJoin.SpaceBetweenProtectedValues : HereDocumentJoin.IfsBetweenProtectedValues);
        else
            AppendSeparatedFields(fields, useSpace ? Space : ifs.First, allowWordSplitting, globQuoted);
        return true;
    }

    public void MarkCurrentWordForEmission() =>
        GetPart(PartOrigin.Protected, globQuoted: true);

    private void AppendEmptyAggregateField()
    {
        _current.HasEmptyAggregateField = true;
        MarkCurrentWordForEmission();
    }

    public IReadOnlyList<ExpandedWord> Build() => Build(selectFirstCasePatternField: false);

    internal IReadOnlyList<ExpandedWord> BuildCasePattern()
    {
        if (!_hasQuotedAggregate)
            return Build();
        var separators = FieldSeparators.FromVariables(_variables);
        var selectFirst = !_casePatternStartsWithEmptyIfs && !separators.IsEmpty;
        var separator = selectFirst ? separators.First : Space;
        for (var index = 1; index < _words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_words[index].CasePatternSeparator.Bytes.Span.SequenceEqual(separator.Bytes.Span))
            {
                // An expansion changed IFS. The previously emitted separators still
                // participate with their original bytes in the final pattern.
                ExpandCasePatternSeparators();
                break;
            }
        }
        return Build(selectFirst);
    }

    private void ExpandCasePatternSeparators()
    {
        _current = _words[0];
        for (var index = 1; index < _words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var word = _words[index];
            AppendExpansion(word.CasePatternSeparator, allowWordSplitting: false, globQuoted: false);
            _memory.Charge(_storage, 32L * word.Parts.Count);
            _current.Parts.AddRange(word.Parts);
        }
        _words.RemoveRange(1, _words.Count - 1);
    }

    internal IReadOnlyList<ExpandedWord> BuildUnquotedCasePatternOperand()
    {
        var fields = Build();
        if (_hasQuotedAggregate || fields.Count < 2 || !HasNonSpaceSeparator(FieldSeparators.FromVariables(_variables)))
            return fields;
        // A nested unquoted aggregate uses spaces after expanding with a non-space IFS.
        var joined = ExpandedWord.JoinWithSpaces(fields, _memory);
        return [new ExpandedWord(joined.Segments) { IsJoinedCasePatternOperand = true }];
    }

    private static bool HasNonSpaceSeparator(FieldSeparators separators) =>
        !separators.IsEmpty && separators.Bytes.Span[0] != (byte)' ';

    private static ShellValue CasePatternSeparator(FieldSeparators separators)
    {
        if (separators.IsEmpty || separators.Bytes.Span[0] == (byte)' ')
            return Space;
        var first = separators.First;
        // A later expansion can replace IFS. Retain only the separator bytes from its old value.
        return first.Bytes.Length == separators.Bytes.Length ? first : ShellValue.FromBytes(first.Bytes.Span.ToArray());
    }

    private IReadOnlyList<ExpandedWord> Build(bool selectFirstCasePatternField)
    {
        var results = new List<ExpandedWord>();
        AppendTo(results, selectFirstCasePatternField);
        return results;
    }

    internal void AppendTo(List<ExpandedWord> results) => AppendTo(results, selectFirstCasePatternField: false);

    private void AppendTo(List<ExpandedWord> results, bool selectFirstCasePatternField)
    {
        // Expansions can assign IFS: joins use its value at expansion time, splitting uses the final value.
        var fieldSeparators = FieldSeparators.FromVariables(_variables);
        var firstFieldOnly = selectFirstCasePatternField && !fieldSeparators.IsEmpty;
        var splitHereDocument = _hereDocumentHasAt && !AssigningHereDocumentOperand;
        var splitOnSpace = splitHereDocument && fieldSeparators.IsEmpty;
        foreach (var word in _words)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (word.Parts.Count == 0)
                continue;
            var split = (splitHereDocument || firstFieldOnly) && word.Parts.Exists(static part => part.Origin == PartOrigin.Expansion)
                || word.NeedsSplitting && !fieldSeparators.IsEmpty;
            if (!split)
            {
                // Retain completed buffers and decide whether to emit the field in the same pass.
                var segments = new ExpandedSegment[word.Parts.Count];
                var hasBytes = false;
                var hasProtectedPart = false;
                for (var index = 0; index < segments.Length; index++)
                {
                    var part = word.Parts[index];
                    var segment = part.Segment.ToSegment();
                    segments[index] = segment;
                    hasBytes |= !segment.Content.IsEmpty;
                    hasProtectedPart |= part.Origin == PartOrigin.Protected;
                }
                if (hasBytes || hasProtectedPart || Purpose == ExpansionPurpose.CasePattern)
                {
                    results.Add(new ExpandedWord(segments, !hasBytes && word.HasEmptyAggregateField));
                    if (firstFieldOnly)
                        return;
                }
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
                if (HasFirstCasePatternField())
                    return;
                _cancellationToken.ThrowIfCancellationRequested();
                if (splitHereDocument && part.Origin == PartOrigin.Protected)
                {
                    // The body owns this segment through emission. Transfer it without copying its bytes.
                    FlushExpansion();
                    ApplyPendingBreak();
                    if (part.Segment.IsEmpty)
                        emitEmpty = true;
                    else
                        field.Add(part.Segment);
                    continue;
                }
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
            if (HasFirstCasePatternField())
                return;
            FlushExpansion();
            if (HasFirstCasePatternField())
                return;
            CompleteField();
            if (HasFirstCasePatternField())
                return;

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
                if (bytes.IsEmpty || HasFirstCasePatternField())
                    return;
                ApplyPendingBreak();
                if (!HasFirstCasePatternField())
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
                    bool isDelimiter;
                    if (splitOnSpace)
                    {
                        isDelimiter = isCharacter && character.Value == ' ';
                    }
                    else
                    {
                        fieldSeparators = isCharacter
                            ? fieldSeparators.PrepareCharacters(_variables, _memory, _cancellationToken)
                            : fieldSeparators.PrepareBytes(_variables, _memory, _cancellationToken);
                        isDelimiter = isCharacter ? fieldSeparators.Contains(character)
                            : fieldSeparators.Contains(bytes.Span[position]);
                    }
                    if (!isDelimiter)
                    {
                        position += length;
                        continue;
                    }

                    if (position > literalStart)
                    {
                        AppendPendingBytes(literalStart, position);
                        if (HasFirstCasePatternField())
                            return;
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
                        if (HasFirstCasePatternField())
                            return;
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
                    if (HasFirstCasePatternField())
                        return;
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
                ExpandedSegment[] segments = field.Count == 0 ? [] : new ExpandedSegment[field.Count];
                for (var index = 0; index < segments.Length; index++)
                    segments[index] = field[index].ToSegment();
                results.Add(new ExpandedWord(segments));
                field.Clear();
                emitEmpty = false;
            }
        }

        bool HasFirstCasePatternField() => firstFieldOnly && results.Count != 0;
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
        public ShellValue CasePatternSeparator { get; init; }
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
        public bool IsEmpty => _bytes == null ? _retained.IsEmpty : _bytes.Length == 0;

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

        public ExpandedSegment ToSegment() => new(ToValue(), IsGlobQuoted);

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

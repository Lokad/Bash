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

/// <summary>Controls whether an unquoted positional star selects spaces with empty IFS.</summary>
internal enum PositionalStarSplit { Default, WhenPresent, EvenWhenEmpty }

/// <summary>Controls aggregate handling and operand syntax for values, diagnostics and arithmetic.</summary>
internal enum ExpansionPurpose
{
    Normal,
    Scalar,
    CasePattern,
    ParameterReplacement,
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
    private const int UntrackedSourceSeparators = 0;
    private const int EmptySourceSeparators = -1;
    private const int DefaultSourceSeparators = -2;

    private static readonly ShellValue Space = ShellValue.FromText(" ");
    private static readonly ExpandedWord ReplacementEmptyField =
        new([new ExpandedSegment(ShellValue.FromText("\u007f"), isGlobQuoted: true)]);
    private readonly Variables _variables;
    private readonly EvaluationMemory _memory;
    private readonly MemoryCapacity _storage;
    private readonly CancellationToken _cancellationToken;
    private readonly ImplicitEmptyPolicy _implicitEmptyPolicy;
    private readonly bool _casePatternStartsWithEmptyIfs;
    private readonly List<InputWord> _words = [];
    private List<FieldSeparators.ByteMembership>? _sourceSeparators;
    private InputWord _current;
    private DoubleQuoteState _doubleQuote;
    private int _doubleQuoteFirstWord;
    private int _doubleQuoteFirstPart;
    private bool _doubleQuoteFirstEmptyField;
    private bool _doubleQuoteHasAggregate;
    private bool _doubleQuoteHasNumber;
    private bool _hereDocumentHasAt;
    private bool _hasQuotedAggregate;
    private bool _hasNormalQuotedAggregate;
    private bool _hasUnquotedAggregate;
    private bool _positionalStarSplitsOnSpaces;

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

    internal void BeginDoubleQuote()
    {
        _doubleQuote = DoubleQuoteState.Open;
        _doubleQuoteFirstWord = _words.Count - 1;
        _doubleQuoteFirstPart = _current.Parts.Count;
        _doubleQuoteFirstEmptyField = _current.HasEmptyAggregateField;
        _doubleQuoteHasAggregate = false;
        _doubleQuoteHasNumber = false;
    }

    internal void EndDoubleQuote()
    {
        if (_doubleQuote == DoubleQuoteState.EmptyExpansion)
            MarkCurrentWordForEmission();
        _doubleQuote = DoubleQuoteState.Outside;

        if (!_doubleQuoteHasAggregate || !_doubleQuoteHasNumber && _doubleQuoteFirstWord == _words.Count - 1)
            return;

        // A quoted group selects its fields before rejoining them for the enclosing word.
        var separators = FieldSeparators.FromVariables(_variables);
        var join = AggregateSeparator(separators);
        if (_doubleQuoteHasNumber && HasNumericSeparator(ref separators))
        {
            using var groupStorage = new MemoryCapacity(_memory.Budget, 0);
            var fields = new List<ExpandedWord>();
            AppendTo(fields, separators, firstFieldOnly: false, _doubleQuoteFirstWord, _doubleQuoteFirstPart,
                quotedGroup: true, groupStorage);
            _current = _words[_doubleQuoteFirstWord];
            _current.Parts.RemoveRange(_doubleQuoteFirstPart, _current.Parts.Count - _doubleQuoteFirstPart);
            _current.HasEmptyAggregateField = _doubleQuoteFirstEmptyField;
            var following = _doubleQuoteFirstWord + 1;
            _words.RemoveRange(following, _words.Count - following);
            for (var index = 0; index < fields.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (index != 0)
                    StartNewWord(join, WordBoundary.QuotedAggregate);
                var field = fields[index];
                if (field.Segments.Count == 0)
                    AppendEmptyAggregateField();
                else
                    foreach (var segment in field.Segments)
                        AppendValue(segment.Content, globQuoted: true);
            }
            return;
        }

        for (var index = _doubleQuoteFirstWord + 1; index < _words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var boundary = _words[index];
            if (boundary.BoundaryBefore != WordBoundary.QuotedAggregate)
                continue;
            if (ContainsAggregateSeparator(ref separators, join, boundary.SeparatorBefore))
                boundary.SeparatorBefore = join;
            else
                boundary.BoundaryBefore = WordBoundary.ProtectedQuoteSeparator;
        }

        bool HasNumericSeparator(ref FieldSeparators ifs)
        {
            var membership = FieldSeparators.CaptureSourceBytes(ref ifs, _variables, _memory, _cancellationToken);
            if (membership.Contains((byte)'-'))
                return true;
            for (var digit = (byte)'0'; digit <= (byte)'9'; digit++)
                if (membership.Contains(digit))
                    return true;
            return false;
        }
    }

    /// <summary>Retains literal-source provenance before field splitting.</summary>
    public void AppendSource(ShellSource source, bool globQuoted) =>
        GetPart(globQuoted ? PartOrigin.Protected : Purpose == ExpansionPurpose.CasePattern
            ? PartOrigin.Expansion : PartOrigin.Source, globQuoted).AppendValue(source.ToValue());

    /// <summary>Records the source run's IFS membership before later expansions can change it.</summary>
    internal void AppendUnquotedSource(ShellSource source, ref int sourceSeparators)
    {
        if (_positionalStarSplitsOnSpaces)
        {
            AppendSource(source, globQuoted: false);
            return;
        }
        if (sourceSeparators == UntrackedSourceSeparators)
        {
            var ifs = FieldSeparators.FromVariables(_variables);
            if (ifs.IsEmpty)
                sourceSeparators = EmptySourceSeparators;
            else if (ReferenceEquals(ifs, FieldSeparators.Default))
                sourceSeparators = DefaultSourceSeparators;
            else
            {
                var membership = FieldSeparators.CaptureSourceBytes(ref ifs, _variables, _memory, _cancellationToken);
                var snapshots = _sourceSeparators;
                if (snapshots == null || snapshots[^1] != membership)
                {
                    _memory.Charge(_storage, 128);
                    (snapshots ??= _sourceSeparators = []).Add(membership);
                }
                sourceSeparators = snapshots.Count;
            }
        }
        GetPart(PartOrigin.Source, globQuoted: false, sourceSeparators).AppendValue(source.ToValue());
    }

    public void AppendValue(ShellValue value, bool globQuoted) =>
        GetPart(PartOrigin.Protected, globQuoted).AppendValue(value);

    public void AppendExpansion(ShellValue value, bool allowWordSplitting, bool globQuoted)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        if (!value.IsEmpty)
        {
            var origin = ExpansionOrigin(allowWordSplitting, globQuoted);
            if (Purpose == ExpansionPurpose.Normal && allowWordSplitting && value.Bytes.Span.Contains((byte)' '))
            {
                if (FieldSeparators.FromVariables(_variables).IsEmpty)
                    origin = PartOrigin.ExpansionWithProtectedSpaces;
                else
                    _current.HasUnquotedSpaces = true;
            }
            GetPart(origin, globQuoted).AppendValue(value);
        }
        else if (!allowWordSplitting && (globQuoted || _implicitEmptyPolicy == ImplicitEmptyPolicy.Preserve))
            AppendEmptyExpansion(globQuoted);
    }

    public void AppendIntegerExpansion(long value, bool allowWordSplitting, bool globQuoted)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        GetPart(IntegerExpansionOrigin(allowWordSplitting, globQuoted), globQuoted).AppendInteger(value);
    }

    internal void AppendIntegerExpansion(ShellValue value, bool allowWordSplitting, bool globQuoted)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        GetPart(IntegerExpansionOrigin(allowWordSplitting, globQuoted), globQuoted).AppendValue(value);
    }

    private PartOrigin IntegerExpansionOrigin(bool allowWordSplitting, bool globQuoted)
    {
        var quotedNumber = Purpose == ExpansionPurpose.Normal && globQuoted && _doubleQuote != DoubleQuoteState.Outside;
        _doubleQuoteHasNumber |= quotedNumber;
        return quotedNumber ? PartOrigin.QuotedNumber : ExpansionOrigin(allowWordSplitting, globQuoted);
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

        AppendFields(fields, allowWordSplitting, globQuoted, quoteContext, FieldSeparators.FromVariables(_variables),
            positionalStarWithEmptyIfs: false);
    }

    private bool AppendFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext,
        FieldSeparators fieldSeparators,
        bool positionalStarWithEmptyIfs)
    {
        _hasQuotedAggregate |= Purpose == ExpansionPurpose.CasePattern
            && quoteContext == ExpansionQuoteContext.DoubleQuoted;
        if (Purpose == ExpansionPurpose.IndexedCompound
            || Purpose == ExpansionPurpose.IndexedCompoundOperand && quoteContext == ExpansionQuoteContext.DoubleQuoted)
        {
            // Compound @ joins use IFS except in unquoted parameter operands; empty IFS uses a space.
            var compoundSeparator = fieldSeparators.IsEmpty ? Space : fieldSeparators.First;
            return AppendSeparatedFields(fields, compoundSeparator, allowWordSplitting, globQuoted);
        }
        var unquotedAggregate = Purpose == ExpansionPurpose.Normal && allowWordSplitting && !positionalStarWithEmptyIfs;
        _hasUnquotedAggregate |= unquotedAggregate;
        if (allowWordSplitting && !fieldSeparators.IsEmpty)
        {
            return AppendSeparatedFields(fields, fieldSeparators.First, allowWordSplitting, globQuoted);
        }

        // In diagnostic parameter words, @ protects its fields when IFS does not start with a space.
        var protectFields = quoteContext == ExpansionQuoteContext.DoubleQuoted
            || (Purpose == ExpansionPurpose.ErrorMessage && !allowWordSplitting
                && !fieldSeparators.IsEmpty && fieldSeparators.Bytes.Span[0] != (byte)' ');
        // Non-space IFS can protect aggregate bytes from field selection while retaining glob activity.
        var protectCaseFields = Purpose == ExpansionPurpose.CasePattern && HasNonSpaceSeparator(fieldSeparators);
        var quotedAggregate = Purpose == ExpansionPurpose.Normal && quoteContext == ExpansionQuoteContext.DoubleQuoted
            && _doubleQuote != DoubleQuoteState.Outside;
        _doubleQuoteHasAggregate |= quotedAggregate;
        _hasNormalQuotedAggregate |= quotedAggregate;
        var separator = unquotedAggregate ? Space
            : Purpose == ExpansionPurpose.CasePattern || quotedAggregate ? AggregateSeparator(fieldSeparators) : default;
        var boundary = unquotedAggregate ? WordBoundary.UnquotedAggregate
            : quotedAggregate ? WordBoundary.QuotedAggregate : WordBoundary.Fixed;
        var first = true;
        foreach (var value in fields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                StartNewWord(separator, boundary);
            first = false;

            if (protectFields)
            {
                if (value.IsEmpty)
                    AppendEmptyAggregateField();
                else
                    AppendValue(value, globQuoted: true);
            }
            else if (protectCaseFields || positionalStarWithEmptyIfs)
            {
                if (!positionalStarWithEmptyIfs || !value.IsEmpty)
                    AppendValue(value, globQuoted);
            }
            else if (unquotedAggregate)
            {
                _current.NeedsSplitting = true;
                if (!value.IsEmpty)
                    GetPart(PartOrigin.ExpansionWithProtectedSpaces, globQuoted).AppendValue(value);
            }
            else
            {
                AppendExpansion(value, allowWordSplitting, globQuoted);
            }
        }
        if (first && quoteContext == ExpansionQuoteContext.DoubleQuoted && _doubleQuote != DoubleQuoteState.Outside)
            _doubleQuote = DoubleQuoteState.EmptyAggregate;
        return !first;
    }

    /// <summary>Records the protected text and exposed separators until the complete body is expanded.</summary>
    internal void AppendHereDocumentFields(IEnumerable<ShellValue> fields, HereDocumentJoin join)
    {
        var preserveEmptyFields = join == HereDocumentJoin.At;
        _hereDocumentHasAt |= preserveEmptyFields;
        var separator = Space;
        if (join is not (HereDocumentJoin.PositionalStar or HereDocumentJoin.SpaceBetweenProtectedValues)
            && !(preserveEmptyFields && AssigningHereDocumentOperand))
        {
            var ifs = FieldSeparators.FromVariables(_variables);
            separator = ifs.IsEmpty ? preserveEmptyFields ? Space : ShellValue.Empty : ifs.First;
        }
        var origin = join is HereDocumentJoin.PositionalStar or HereDocumentJoin.UnprotectedIfs
            ? PartOrigin.Expansion : PartOrigin.Protected;
        AppendHereDocumentFields(fields, separator, origin, preserveEmptyFields);
    }

    private void AppendHereDocumentFields(
        IEnumerable<ShellValue> fields, ShellValue separator, PartOrigin origin, bool preserveEmptyFields)
    {
        var first = true;
        foreach (var value in fields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!first && !separator.IsEmpty)
                GetPart(PartOrigin.Expansion, globQuoted: true).AppendValue(separator);
            first = false;
            if (preserveEmptyFields || !value.IsEmpty)
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
            ? AggregateSeparator(FieldSeparators.FromVariables(_variables)) : default;
        for (var index = 0; index < words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
                StartNewWord(caseSeparator, WordBoundary.Fixed);
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
                if (segment.IsCasePatternIfsProtected)
                    AppendValue(segment.Content, quoted);
                else
                    AppendExpansion(segment.Content, allowWordSplitting && !segment.IsGlobQuoted, quoted);
            }
        }
    }

    private void StartNewWord(ShellValue separator, WordBoundary boundary)
    {
        _memory.Charge(_storage, 256);
        _current = new InputWord
        {
            SeparatorBefore = separator,
            BoundaryBefore = boundary,
        };
        _words.Add(_current);
    }

    public void AppendJoinedFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted) =>
        AppendJoinedFields(fields, allowWordSplitting, globQuoted, PositionalStarSplit.Default);

    internal void AppendJoinedFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted,
        PositionalStarSplit positionalStar)
    {
        var fieldSeparators = FieldSeparators.FromVariables(_variables);
        if (fieldSeparators.IsEmpty && allowWordSplitting)
        {
            var positionalStarWithEmptyIfs = positionalStar != PositionalStarSplit.Default && Purpose == ExpansionPurpose.Normal;
            var hasFields = AppendFields(fields, allowWordSplitting, globQuoted, ExpansionQuoteContext.Unquoted, fieldSeparators,
                positionalStarWithEmptyIfs);
            // An unquoted positional star expanded with empty IFS selects spaces
            // for this word, even if a later expansion assigns another separator.
            _positionalStarSplitsOnSpaces |= positionalStarWithEmptyIfs
                && (hasFields || positionalStar == PositionalStarSplit.EvenWhenEmpty);
            return;
        }

        _hasUnquotedAggregate |= Purpose == ExpansionPurpose.Normal && allowWordSplitting;
        var separator = fieldSeparators.IsEmpty ? ShellValue.Empty : fieldSeparators.First;
        AppendSeparatedFields(fields, separator, allowWordSplitting, globQuoted);
    }

    internal bool AppendSeparatedFields(
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
        return !first;
    }

    internal bool TryAppendSelectedFields(ParameterFieldJoin join, IEnumerable<ShellValue> fields,
        bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext)
    {
        if (join == ParameterFieldJoin.Default)
            return false;
        var ifs = FieldSeparators.FromVariables(_variables);
        var separator = join == ParameterFieldJoin.Space || ifs.IsEmpty ? Space : ifs.First;
        if (quoteContext == ExpansionQuoteContext.HereDocument)
            AppendHereDocumentFields(fields, separator, PartOrigin.Protected, preserveEmptyFields: false);
        else
            AppendSeparatedFields(fields, separator, allowWordSplitting, globQuoted);
        return true;
    }

    public void MarkCurrentWordForEmission() =>
        GetPart(PartOrigin.Protected, globQuoted: true);

    private void AppendEmptyAggregateField()
    {
        _current.HasEmptyAggregateField = true;
        MarkCurrentWordForEmission();
    }

    public IReadOnlyList<ExpandedWord> Build() => Build(FieldSeparators.FromVariables(_variables), firstFieldOnly: false);

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
            if (!_words[index].SeparatorBefore.Bytes.Span.SequenceEqual(separator.Bytes.Span))
            {
                // An expansion changed IFS. The previously emitted separators still
                // participate with their original bytes in the final pattern.
                ExpandCasePatternSeparators();
                break;
            }
        }
        return Build(separators, selectFirst);
    }

    private void ExpandCasePatternSeparators()
    {
        _current = _words[0];
        for (var index = 1; index < _words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var word = _words[index];
            AppendExpansion(word.SeparatorBefore, allowWordSplitting: false, globQuoted: false);
            _memory.Charge(_storage, 32L * word.Parts.Count);
            _current.Parts.AddRange(word.Parts);
        }
        _words.RemoveRange(1, _words.Count - 1);
    }

    internal IReadOnlyList<ExpandedWord> BuildUnquotedCasePatternOperand()
    {
        var separators = FieldSeparators.FromVariables(_variables);
        var fields = Build(separators, firstFieldOnly: false);
        if (_hasQuotedAggregate || fields.Count < 2 || !HasNonSpaceSeparator(separators))
            return fields;
        // A nested unquoted aggregate uses spaces after expanding with a non-space IFS.
        return [ExpandedWord.JoinCasePatternOperandWithSpaces(fields, _memory)];
    }

    private static bool HasNonSpaceSeparator(FieldSeparators separators) =>
        !separators.IsEmpty && separators.Bytes.Span[0] != (byte)' ';

    private static ShellValue AggregateSeparator(FieldSeparators separators)
    {
        if (separators.IsEmpty || separators.Bytes.Span[0] == (byte)' ')
            return Space;
        var first = separators.First;
        // A later expansion can replace IFS. Retain only the separator bytes from its old value.
        return first.Bytes.Length == separators.Bytes.Length ? first : ShellValue.FromBytes(first.Bytes.Span.ToArray());
    }

    private bool ContainsAggregateSeparator(ref FieldSeparators separators, ShellValue first, ShellValue value)
    {
        if (value.Bytes.Span.SequenceEqual(first.Bytes.Span))
            return true;
        if (separators.IsEmpty)
            return false;

        var bytes = value.Bytes.Span;
        if (Rune.DecodeFromUtf8(bytes, out var character, out _) == OperationStatus.Done)
        {
            separators = separators.PrepareCharacters(_variables, _memory, _cancellationToken);
            return separators.Contains(character);
        }
        separators = separators.PrepareBytes(_variables, _memory, _cancellationToken);
        return separators.Contains(bytes[0]);
    }

    private IReadOnlyList<ExpandedWord> Build(FieldSeparators fieldSeparators, bool firstFieldOnly)
    {
        var results = new List<ExpandedWord>();
        AppendTo(results, fieldSeparators, firstFieldOnly);
        return results;
    }

    // Expansions can assign IFS: joins use its value at expansion time, splitting uses the final value.
    internal void AppendTo(List<ExpandedWord> results) =>
        AppendTo(results, FieldSeparators.FromVariables(_variables), firstFieldOnly: false);

    private void AppendTo(List<ExpandedWord> results, FieldSeparators fieldSeparators, bool firstFieldOnly) =>
        AppendTo(results, fieldSeparators, firstFieldOnly, firstWord: 0, firstPart: 0, quotedGroup: false, _storage);

    private void AppendTo(List<ExpandedWord> results, FieldSeparators fieldSeparators, bool firstFieldOnly,
        int firstWord, int firstPart, bool quotedGroup, MemoryCapacity fieldStorage)
    {
        var splitHereDocument = !quotedGroup && _hereDocumentHasAt && !AssigningHereDocumentOperand;
        var splitAggregateOnSpace = (!quotedGroup && _positionalStarSplitsOnSpaces)
            || (_hasUnquotedAggregate || _hasNormalQuotedAggregate) && fieldSeparators.IsEmpty;
        var splitOnSpace = splitAggregateOnSpace || splitHereDocument && fieldSeparators.IsEmpty;
        // Preserve the original space joins when the word finishes with a different IFS.
        var joinUnquotedAggregate = !quotedGroup && _hasUnquotedAggregate && !splitAggregateOnSpace;
        var quotedSplitSeparator = _hasNormalQuotedAggregate
            ? splitOnSpace ? Space : fieldSeparators.First : default;
        FieldSeparators.ByteMembership? finalSourceSeparators = null;
        for (var inputIndex = firstWord; inputIndex < _words.Count;)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var first = inputIndex;
            var word = _words[inputIndex++];
            if (joinUnquotedAggregate || _hasNormalQuotedAggregate)
            {
                while (inputIndex < _words.Count && JoinBoundary(_words[inputIndex]))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    inputIndex++;
                }
            }
            var joinWords = inputIndex > first + 1;
            if (!joinWords && word.Parts.Count == (first == firstWord ? firstPart : 0))
                continue;
            var split = quotedGroup || joinWords
                || (splitHereDocument || firstFieldOnly) && word.Parts.Exists(static part => part.Origin == PartOrigin.Expansion)
                || (splitAggregateOnSpace ? word.HasUnquotedSpaces : !fieldSeparators.IsEmpty
                    && (word.NeedsSplitting || _hasNormalQuotedAggregate && HasExposedSource(word)));
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
                    segments[index] = Purpose == ExpansionPurpose.CasePattern && part.Origin == PartOrigin.Protected
                        ? segment with { IsCasePatternIfsProtected = true } : segment;
                    hasBytes |= !segment.Content.IsEmpty;
                    hasProtectedPart |= part.Origin == PartOrigin.Protected;
                }
                if (hasBytes || hasProtectedPart || Purpose == ExpansionPurpose.CasePattern)
                {
                    var emptyAggregateField = !hasBytes && word.HasEmptyAggregateField;
                    // A replacement retains Bash's quoted-null byte in later supplied fields.
                    results.Add(Purpose == ExpansionPurpose.ParameterReplacement && emptyAggregateField && results.Count != 0
                        ? ReplacementEmptyField : new ExpandedWord(segments, emptyAggregateField));
                    if (firstFieldOnly)
                        return;
                }
                continue;
            }

            _memory.Charge(fieldStorage, 256);
            var field = new List<SegmentBuilder>();
            var emitEmpty = false;
            var pendingBreak = false;
            using var pendingBytes = new ByteMemoryBuilder(0, _memory.Budget);
            using var quoteScratch = new MemoryCapacity(_memory.Budget, 0);
            var quoteRuns = new List<QuoteRun>();

            for (var wordIndex = first; wordIndex < inputIndex; wordIndex++)
            {
                if (wordIndex > first)
                {
                    FlushExpansion();
                    var boundary = _words[wordIndex];
                    if (boundary.BoundaryBefore == WordBoundary.ProtectedQuoteSeparator)
                        AppendLiteral(boundary.SeparatorBefore.Bytes, globQuoted: true);
                    else
                        QueueExpansion(boundary.SeparatorBefore.Bytes, globQuoted: false);
                }
                var parts = _words[wordIndex].Parts;
                for (var partIndex = wordIndex == firstWord ? firstPart : 0; partIndex < parts.Count; partIndex++)
                {
                    if (HasFirstCasePatternField())
                        return;
                    AppendPart(parts[partIndex]);
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

            void AppendPart(InputPart part)
            {
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
                    return;
                }
                var value = part.Segment.ToValue();
                var quoted = part.Segment.IsGlobQuoted;
                if (part.Origin == PartOrigin.Protected || !quotedGroup && part.Origin == PartOrigin.QuotedNumber
                    || splitAggregateOnSpace && part.Origin == PartOrigin.ExpansionWithProtectedSpaces)
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
                else if (part.Origin is PartOrigin.Expansion or PartOrigin.ExpansionWithProtectedSpaces or PartOrigin.QuotedNumber)
                {
                    var bytes = value.Bytes;
                    if (part.Origin == PartOrigin.ExpansionWithProtectedSpaces)
                    {
                        while (bytes.Span.IndexOf((byte)' ') is >= 0 and var space)
                        {
                            _cancellationToken.ThrowIfCancellationRequested();
                            QueueExpansion(bytes[..space], quoted);
                            var length = bytes.Span[space..].IndexOfAnyExcept((byte)' ');
                            var afterSpace = length < 0 ? bytes.Length : space + length;
                            AppendLiteral(bytes[space..afterSpace], quoted);
                            bytes = bytes[afterSpace..];
                        }
                    }
                    QueueExpansion(bytes, quoted);
                }
                else
                {
                    var bytes = value.Bytes;
                    var sourceSeparators = EarlierSourceSeparators(part);
                    if (sourceSeparators == default(FieldSeparators.ByteMembership))
                    {
                        QueueExpansion(bytes, quoted);
                        return;
                    }
                    for (var position = 0; position < bytes.Length;)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var length = Utf8Text.FirstCharacterLength(bytes.Span[position..]);
                        // With a changed IFS, protect only leading source bytes that belonged to the earlier IFS.
                        var protectedLength = sourceSeparators == null || sourceSeparators.Value.Contains(bytes.Span[position])
                            ? 1 : 0;
                        if (protectedLength != 0)
                            AppendLiteral(bytes.Slice(position, protectedLength), quoted);
                        if (length > protectedLength)
                            QueueExpansion(bytes.Slice(position + protectedLength, length - protectedLength), quoted);
                        position += length;
                    }
                }
            }

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
                    field.Add(new SegmentBuilder(globQuoted, _memory, fieldStorage));
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
                _memory.Charge(fieldStorage, 256);
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

        FieldSeparators.ByteMembership? EarlierSourceSeparators(InputPart part)
        {
            if (splitOnSpace || part.SourceSeparators == UntrackedSourceSeparators)
                return null;
            var earlier = part.SourceSeparators switch
            {
                EmptySourceSeparators => default(FieldSeparators.ByteMembership),
                DefaultSourceSeparators => FieldSeparators.ByteMembership.Default,
                _ => _sourceSeparators?[part.SourceSeparators - 1],
            };
            finalSourceSeparators ??= FieldSeparators.CaptureSourceBytes(
                ref fieldSeparators, _variables, _memory, _cancellationToken);
            return earlier == finalSourceSeparators ? null : earlier;
        }

        bool HasExposedSource(InputWord word)
        {
            foreach (var part in word.Parts)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (EarlierSourceSeparators(part) != null)
                    return true;
            }
            return false;
        }

        bool JoinBoundary(InputWord word) => word.BoundaryBefore switch
        {
            WordBoundary.UnquotedAggregate => joinUnquotedAggregate,
            WordBoundary.QuotedAggregate => quotedGroup || (splitOnSpace
                ? !word.SeparatorBefore.Bytes.Span.SequenceEqual(Space.Bytes.Span)
                : !ContainsAggregateSeparator(ref fieldSeparators, quotedSplitSeparator, word.SeparatorBefore)),
            WordBoundary.ProtectedQuoteSeparator => true,
            _ => false,
        };
    }

    private SegmentBuilder GetPart(PartOrigin origin, bool globQuoted) =>
        GetPart(origin, globQuoted, UntrackedSourceSeparators);

    private SegmentBuilder GetPart(PartOrigin origin, bool globQuoted, int sourceSeparators)
    {
        if (_current.Parts.Count == 0
            || Purpose == ExpansionPurpose.Normal && _doubleQuote != DoubleQuoteState.Outside
                && _current.Parts.Count == _doubleQuoteFirstPart && _words.Count - 1 == _doubleQuoteFirstWord
            || _current.Parts[^1].Origin != origin
            || _current.Parts[^1].SourceSeparators != sourceSeparators
            || _current.Parts[^1].Segment.IsGlobQuoted != globQuoted)
        {
            _current.Parts.Add(new InputPart(origin, sourceSeparators, new SegmentBuilder(globQuoted, _memory, _storage)));
        }
        return _current.Parts[^1].Segment;
    }

    private enum PartOrigin
    {
        /// <summary>Literal source characters with leading-byte protection from the IFS at emission.</summary>
        Source,
        /// <summary>Quoted values or literal expansion results; every byte is protected.</summary>
        Protected,
        /// <summary>Unquoted expansion bytes that can join across adjacent parts before splitting.</summary>
        Expansion,
        /// <summary>Spaces in values expanded with empty IFS stay protected during splitting.</summary>
        ExpansionWithProtectedSpaces,
        /// <summary>Numeric bytes can delimit fields inside a quoted aggregate before they are protected.</summary>
        QuotedNumber,
    }

    private enum WordBoundary : byte { Fixed, UnquotedAggregate, QuotedAggregate, ProtectedQuoteSeparator }

    private sealed class InputWord
    {
        public List<InputPart> Parts { get; } = [];
        public bool NeedsSplitting { get; set; }
        public bool HasUnquotedSpaces { get; set; }
        public bool HasEmptyAggregateField { get; set; }
        public WordBoundary BoundaryBefore { get; set; }
        public ShellValue SeparatorBefore { get; set; }
    }

    private readonly record struct InputPart(PartOrigin Origin, int SourceSeparators, SegmentBuilder Segment);

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

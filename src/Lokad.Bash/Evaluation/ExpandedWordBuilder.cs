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

internal enum AtFieldSource { Other, Array, Positional, Projected }

internal enum CasePatternOperand { None, SelectFields, PreserveQuotedNulls }

[Flags]
internal enum CaseQuoteBytes : byte { None, Escape = 1, Null = 2, Captured = 4 }

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
    AssociativeCompound,
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
    PreserveAggregate,
    Remove,
}

/// <summary>Records the starting IFS mode of an unquoted default or alternate operand.</summary>
internal enum ParameterOperandFields : byte { Default, Unquoted, UnquotedWithEmptyIfs }

/// <summary>Assembles word bytes and their provenance before applying IFS boundaries.</summary>
internal sealed class ExpandedWordBuilder
{
    private const int UntrackedSourceSeparators = 0;
    private const int EmptySourceSeparators = -1;
    private const int DefaultSourceSeparators = -2;
    private const byte CaseQuoteEscape = 0x01;
    private const byte CaseQuoteNull = 0x7f;

    private static readonly ShellValue Space = ShellValue.FromText(" ");
    private static readonly ShellValue QuotedNullByte = ShellValue.FromText("\u007f");
    private static readonly ExpandedWord QuotedNullField =
        new([new ExpandedSegment(QuotedNullByte, isGlobQuoted: true)]);
    private static readonly ExpandedWord CaseQuotedNullField =
        new([new ExpandedSegment(QuotedNullByte, isGlobQuoted: true) { IsCaseQuotedNull = true }]);
    private readonly Variables _variables;
    private readonly EvaluationMemory _memory;
    private readonly MemoryCapacity _storage;
    private readonly CancellationToken _cancellationToken;
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
    private bool _casePatternHasAt;
    private bool _casePatternHasRawQuoteBytes;
    private bool _hasQuotedAggregate;
    private bool _hasNormalQuotedAggregate;
    private bool _hasUnquotedAggregate;
    private bool _splitOnSpaces;
    private bool _hasOperandAt;

    private enum DoubleQuoteState : byte { Outside, Open, EmptyExpansion, EmptyAggregate }
    private enum CaseFieldSelection : byte { None, All, First, Unsplit }

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
        EmptyPolicy = implicitEmptyPolicy;
        _casePatternStartsWithEmptyIfs = purpose == ExpansionPurpose.CasePattern
            && FieldSeparators.FromVariables(variables).IsEmpty;
        memory.Charge(storage, 256);
        _current = new();
        _words.Add(_current);
    }

    public ExpansionPurpose Purpose { get; }
    internal ImplicitEmptyPolicy EmptyPolicy { get; }
    internal bool AssigningHereDocumentOperand { get; init; }
    internal CasePatternOperand CaseOperand { get; init; }
    internal bool InheritsCaseDoubleQuotes { get; init; }
    internal ParameterOperandFields OperandFields { get; init; }
    private bool LeavesCasePatternUnsplit => _casePatternStartsWithEmptyIfs && CaseOperand == CasePatternOperand.None;
    internal bool PreservesCasePatternQuotedNulls => LeavesCasePatternUnsplit || CaseOperand == CasePatternOperand.PreserveQuotedNulls;

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

        var casePattern = Purpose == ExpansionPurpose.CasePattern;
        // The selected operand was expanded inside the enclosing quote group.
        if (casePattern && InheritsCaseDoubleQuotes)
            return;
        if (!_doubleQuoteHasAggregate || !casePattern && !_doubleQuoteHasNumber && _doubleQuoteFirstWord == _words.Count - 1)
            return;

        // A quoted group selects its fields before rejoining them for the enclosing word.
        var separators = FieldSeparators.FromVariables(_variables);
        if (casePattern && !UsesCaseQuoteBytes(separators))
            return;

        var join = AggregateSeparator(separators);
        if (casePattern || _doubleQuoteHasNumber && HasNumericSeparator(ref separators))
        {
            using var groupStorage = new MemoryCapacity(_memory.Budget, 0);
            var fields = new List<ExpandedWord>();
            AppendTo(fields, separators, casePattern ? CaseFieldSelection.All : CaseFieldSelection.None,
                _doubleQuoteFirstWord, _doubleQuoteFirstPart,
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
                    StartNewWord(join, casePattern ? WordBoundary.Fixed : WordBoundary.QuotedAggregate);
                var field = fields[index];
                if (casePattern && (ReferenceEquals(field, CaseQuotedNullField) || field.Segments.Count == 0))
                    GetPart(PartOrigin.CaseQuotedNull, globQuoted: true).AppendValue(QuotedNullByte);
                else if (field.Segments.Count == 0)
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
    public void AppendSource(ShellSource source, bool globQuoted)
    {
        if (!globQuoted && OperandFields != ParameterOperandFields.Default)
            AppendExpansion(source.ToValue(), allowWordSplitting: false, globQuoted: false);
        else
            GetPart(globQuoted ? PartOrigin.Protected : Purpose == ExpansionPurpose.CasePattern
                ? PartOrigin.Expansion : PartOrigin.Source, globQuoted).AppendValue(source.ToValue());
    }

    /// <summary>Records the source run's IFS membership before later expansions can change it.</summary>
    internal void AppendUnquotedSource(ShellSource source, ref int sourceSeparators)
    {
        if (_splitOnSpaces)
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
            if (Purpose == ExpansionPurpose.Normal && (allowWordSplitting || !globQuoted && OperandFields != ParameterOperandFields.Default)
                && value.Bytes.Span.Contains((byte)' '))
            {
                if (FieldSeparators.FromVariables(_variables).IsEmpty)
                    origin = PartOrigin.ExpansionWithProtectedSpaces;
                else
                    _current.HasUnquotedSpaces = true;
            }
            if (Purpose == ExpansionPurpose.CasePattern && origin == PartOrigin.Expansion
                && value.Bytes.Span.ContainsAny(CaseQuoteEscape, CaseQuoteNull))
            {
                var raw = GetCaseQuoteBytes(value.Bytes.Span)
                    & GetCaseQuoteBytes(FieldSeparators.FromVariables(_variables).Bytes.Span);
                GetPart(origin, globQuoted, UntrackedSourceSeparators, CaseQuoteBytes.Captured | raw).AppendValue(value);
            }
            else
                GetPart(origin, globQuoted).AppendValue(value);
        }
        else if (!allowWordSplitting && (globQuoted || EmptyPolicy == ImplicitEmptyPolicy.Preserve))
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
        allowWordSplitting || (!globQuoted && (Purpose == ExpansionPurpose.CasePattern || OperandFields != ParameterOperandFields.Default))
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

    internal void AppendFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext,
        AtFieldSource source = AtFieldSource.Other)
    {
        if (quoteContext == ExpansionQuoteContext.HereDocument)
        {
            AppendHereDocumentFields(fields, HereDocumentJoin.At);
            return;
        }

        var separators = FieldSeparators.FromVariables(_variables);
        if (OperandFields != ParameterOperandFields.Default && quoteContext == ExpansionQuoteContext.Unquoted)
        {
            _hasOperandAt = true;
            var protectFields = HasNonSpaceSeparator(separators);
            // Array @ references use the assignment join when their values require protection.
            var separator = source == AtFieldSource.Array && protectFields ? Space : AggregateSeparator(separators);
            AppendOperandFields(fields, separator, separators.IsEmpty, protectFields);
            return;
        }
        AppendFields(fields, allowWordSplitting, globQuoted, quoteContext, separators, positionalStarWithEmptyIfs: false, source);
    }

    private bool AppendFields(
        IEnumerable<ShellValue> fields,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext,
        FieldSeparators fieldSeparators,
        bool positionalStarWithEmptyIfs,
        AtFieldSource source = AtFieldSource.Other)
    {
        if (Purpose == ExpansionPurpose.CasePattern)
            return AppendCaseFields();
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
        var quotedAggregate = Purpose == ExpansionPurpose.Normal && quoteContext == ExpansionQuoteContext.DoubleQuoted
            && _doubleQuote != DoubleQuoteState.Outside;
        _doubleQuoteHasAggregate |= quotedAggregate;
        _hasNormalQuotedAggregate |= quotedAggregate;
        var separator = unquotedAggregate ? Space : quotedAggregate ? AggregateSeparator(fieldSeparators) : default;
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
            else if (positionalStarWithEmptyIfs)
            {
                if (!value.IsEmpty)
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
                AppendAggregateValue(value, allowWordSplitting, globQuoted);
            }
        }
        if (first && quoteContext == ExpansionQuoteContext.DoubleQuoted && _doubleQuote != DoubleQuoteState.Outside)
            _doubleQuote = DoubleQuoteState.EmptyAggregate;
        return !first;

        bool AppendCaseFields()
        {
            var explicitlyQuoted = quoteContext == ExpansionQuoteContext.DoubleQuoted;
            _casePatternHasAt = true;
            _hasQuotedAggregate |= explicitlyQuoted;
            _doubleQuoteHasAggregate |= explicitlyQuoted && _doubleQuote != DoubleQuoteState.Outside;
            var protect = explicitlyQuoted || source != AtFieldSource.Projected && HasNonSpaceSeparator(fieldSeparators);
            var join = source == AtFieldSource.Array && !explicitlyQuoted ? Space : AggregateSeparator(fieldSeparators);
            if (!protect)
                return AppendSeparatedFields(fields, join, allowWordSplitting: false, globQuoted: false);

            var hasField = false;
            foreach (var value in fields)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (hasField)
                    AppendExpansion(join, allowWordSplitting: false, globQuoted: false);
                hasField = true;
                if (value.IsEmpty)
                    GetPart(PartOrigin.CaseQuotedNull, globQuoted: true).AppendValue(QuotedNullByte);
                else
                    AppendValue(value, globQuoted: true);
            }
            if (!hasField && explicitlyQuoted && _doubleQuote != DoubleQuoteState.Outside)
                _doubleQuote = DoubleQuoteState.EmptyAggregate;
            return hasField;
        }
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
        _casePatternHasAt |= operand._casePatternHasAt;
        _hasQuotedAggregate |= operand._hasQuotedAggregate;
        _doubleQuoteHasAggregate |= operand._casePatternHasAt && _doubleQuote != DoubleQuoteState.Outside;
    }

    internal void AppendHereDocumentUnprotected(ShellValue value) =>
        GetPart(PartOrigin.Expansion, globQuoted: true).AppendValue(value);

    /// <summary>Splices operand fields into the enclosing word without flattening their quote metadata.</summary>
    internal void AppendSelectedOperand(
        ExpandedWordBuilder? operand,
        bool allowWordSplitting,
        bool globQuoted,
        ExpansionQuoteContext quoteContext)
    {
        var separators = FieldSeparators.FromVariables(_variables);
        var words = operand == null ? [] : SelectFields(operand);
        if (words.Count == 0)
        {
            AppendExpansion(ShellValue.Empty, allowWordSplitting, globQuoted);
            return;
        }

        var quotedAggregate = words.Count > 1 && operand?._hasNormalQuotedAggregate == true;
        var unquotedAt = !globQuoted && operand?._hasOperandAt == true;
        var spaceSplit = !globQuoted && operand?._splitOnSpaces == true;
        // The operand's unquoted @ fields rejoin with spaces after any IFS selection in the operand.
        // A forced space split uses the current IFS to join its resulting fields instead.
        var rejoinOnSpace = unquotedAt && !quotedAggregate && !spaceSplit;
        if (unquotedAt || spaceSplit)
        {
            _hasOperandAt |= OperandFields != ParameterOperandFields.Default;
            _hasUnquotedAggregate |= OperandFields == ParameterOperandFields.Default;
            _splitOnSpaces |= spaceSplit && words.Count > 1 && separators.IsEmpty;
        }
        if (quotedAggregate)
        {
            // The selected operand has already chosen its fields. Its exposed join still belongs to the enclosing word.
            _hasNormalQuotedAggregate = true;
            _doubleQuoteHasAggregate |= globQuoted && _doubleQuote != DoubleQuoteState.Outside;
            if (!globQuoted && separators.IsEmpty)
                _splitOnSpaces = true;
        }
        var separator = quotedAggregate || spaceSplit || Purpose == ExpansionPurpose.CasePattern && words.Count > 1
            ? AggregateSeparator(separators) : default;
        for (var index = 0; index < words.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
            {
                if (rejoinOnSpace || spaceSplit && !quotedAggregate)
                    AppendExposedOperandValue(rejoinOnSpace ? Space : separator, allowWordSplitting);
                else
                    StartNewWord(separator, quotedAggregate ? WordBoundary.QuotedAggregate : WordBoundary.Fixed);
            }
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
                var quoted = segment.IsGlobQuoted || (globQuoted && Purpose != ExpansionPurpose.CasePattern);
                if (segment.IsCaseQuotedNull)
                    GetPart(PartOrigin.CaseQuotedNull, globQuoted: true).AppendValue(segment.Content);
                else if (segment.IsCasePatternIfsProtected)
                    AppendValue(segment.Content, quoted);
                else if (segment.CaseQuoteBytes != CaseQuoteBytes.None)
                    GetPart(ExpansionOrigin(allowWordSplitting: false, quoted), quoted,
                        UntrackedSourceSeparators, segment.CaseQuoteBytes).AppendValue(segment.Content);
                else if (!quoted && segment.Spaces == OperandSpaces.Exposed)
                    AppendExposedOperandValue(segment.Content, allowWordSplitting);
                else if (!quoted && segment.Spaces == OperandSpaces.Protected)
                {
                    _current.NeedsSplitting |= allowWordSplitting;
                    GetPart(PartOrigin.ExpansionWithProtectedSpaces, globQuoted: false).AppendValue(segment.Content);
                }
                else
                    AppendExpansion(segment.Content, allowWordSplitting && !segment.IsGlobQuoted, quoted);
            }
        }

        IReadOnlyList<ExpandedWord> SelectFields(ExpandedWordBuilder selected)
        {
            IncludeOperand(selected);
            if (Purpose == ExpansionPurpose.CasePattern)
            {
                if (quoteContext == ExpansionQuoteContext.Unquoted)
                    return selected.BuildUnquotedCasePatternOperand();
                if (selected._casePatternHasAt && !separators.IsEmpty && selected.UsesCaseQuoteBytes(separators))
                    return selected.Build(separators, CaseFieldSelection.All);
            }
            return selected.Build(separators, CaseFieldSelection.None);
        }
    }

    private void StartNewWord(ShellValue separator, WordBoundary boundary)
    {
        if (Purpose == ExpansionPurpose.CasePattern)
            _casePatternHasRawQuoteBytes |= separator.Bytes.Span.ContainsAny(CaseQuoteEscape, CaseQuoteNull);
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
        if (!globQuoted && fieldSeparators.IsEmpty && OperandFields == ParameterOperandFields.UnquotedWithEmptyIfs)
        {
            // At-like array stars retain their joins. Positional stars select spaces inside the operand.
            var hasFields = AppendOperandFields(fields, Space, protectSpaces: true, protectFields: false);
            if (positionalStar == PositionalStarSplit.Default)
                _hasOperandAt = true;
            else
                _splitOnSpaces |= hasFields || positionalStar == PositionalStarSplit.EvenWhenEmpty;
            return;
        }
        if (fieldSeparators.IsEmpty && allowWordSplitting)
        {
            var positionalStarWithEmptyIfs = positionalStar != PositionalStarSplit.Default && Purpose == ExpansionPurpose.Normal;
            var hasFields = AppendFields(fields, allowWordSplitting, globQuoted, ExpansionQuoteContext.Unquoted, fieldSeparators,
                positionalStarWithEmptyIfs);
            // An unquoted positional star expanded with empty IFS selects spaces
            // for this word, even if a later expansion assigns another separator.
            _splitOnSpaces |= positionalStarWithEmptyIfs
                && (hasFields || positionalStar == PositionalStarSplit.EvenWhenEmpty);
            return;
        }

        _hasUnquotedAggregate |= Purpose == ExpansionPurpose.Normal && allowWordSplitting;
        var separator = fieldSeparators.IsEmpty ? ShellValue.Empty : fieldSeparators.First;
        AppendSeparatedFields(fields, separator, allowWordSplitting, globQuoted);
    }

    private bool AppendOperandFields(
        IEnumerable<ShellValue> fields, ShellValue separator, bool protectSpaces, bool protectFields)
    {
        var first = true;
        foreach (var value in fields)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                AppendExposedOperandValue(separator, allowWordSplitting: false);
            first = false;
            if (protectFields)
            {
                if (value.IsEmpty)
                    AppendEmptyAggregateField();
                else
                    AppendValue(value, globQuoted: true);
            }
            else if (!value.IsEmpty)
            {
                if (protectSpaces)
                    GetPart(PartOrigin.ExpansionWithProtectedSpaces, globQuoted: false).AppendValue(value);
                else
                    AppendExposedOperandValue(value, allowWordSplitting: false);
            }
        }
        return !first;
    }

    private void AppendExposedOperandValue(ShellValue value, bool allowWordSplitting)
    {
        _current.NeedsSplitting |= allowWordSplitting;
        _current.HasUnquotedSpaces |= value.Bytes.Span.Contains((byte)' ');
        GetPart(PartOrigin.Expansion, globQuoted: false).AppendValue(value);
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
            AppendAggregateValue(value, allowWordSplitting, globQuoted);
        }
        if (first && !allowWordSplitting && (globQuoted || EmptyPolicy != ImplicitEmptyPolicy.PreserveAggregate))
            AppendEmptyExpansion(globQuoted: true);
        return !first;
    }

    private void AppendAggregateValue(ShellValue value, bool allowWordSplitting, bool globQuoted)
    {
        if (EmptyPolicy == ImplicitEmptyPolicy.PreserveAggregate && value.IsEmpty)
            AppendEmptyAggregateField();
        else
            AppendExpansion(value, allowWordSplitting, globQuoted);
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

    public IReadOnlyList<ExpandedWord> Build() => Build(FieldSeparators.FromVariables(_variables), CaseFieldSelection.None);

    internal IReadOnlyList<ExpandedWord> BuildCasePattern()
    {
        var separators = FieldSeparators.FromVariables(_variables);
        var selection = LeavesCasePatternUnsplit ? CaseFieldSelection.None : SelectCaseFields(separators);
        if (selection == CaseFieldSelection.None)
        {
            if (UsesCaseQuoteBytes(separators))
                selection = CaseFieldSelection.Unsplit;
            else if (HasSingleCaseQuotedNull())
                return [];
        }
        var fields = Build(separators, selection);
        return fields.Count == 1 && ReferenceEquals(fields[0], CaseQuotedNullField) ? [] : fields;

        bool HasSingleCaseQuotedNull()
        {
            if (_words.Count != 1)
                return false;
            var foundNull = false;
            foreach (var part in _words[0].Parts)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var length = part.Segment.Length;
                if (length == 0)
                    continue;
                if (foundNull || length != 1 || part.Origin != PartOrigin.CaseQuotedNull)
                    return false;
                foundNull = true;
            }
            return foundNull;
        }
    }

    private CaseFieldSelection SelectCaseFields(FieldSeparators separators) => _hasQuotedAggregate && !separators.IsEmpty
        ? CaseFieldSelection.First : _casePatternHasAt && HasNonSpaceSeparator(separators)
            ? CaseFieldSelection.All : CaseFieldSelection.None;

    private IReadOnlyList<ExpandedWord> BuildUnquotedCasePatternOperand()
    {
        var separators = FieldSeparators.FromVariables(_variables);
        var selection = SelectCaseFields(separators);
        if (selection == CaseFieldSelection.First && CaseOperand == CasePatternOperand.PreserveQuotedNulls
            && !UsesCaseQuoteBytes(separators))
            return Build(separators, CaseFieldSelection.None);
        var fields = Build(separators, selection == CaseFieldSelection.None ? selection : CaseFieldSelection.All);
        // An unquoted operand rejoins selected fields before the enclosing pattern applies its IFS.
        return selection == CaseFieldSelection.All && fields.Count > 1
            ? [ExpandedWord.JoinWithSpaces(fields, _memory)] : fields;
    }

    private static bool HasNonSpaceSeparator(FieldSeparators separators) =>
        !separators.IsEmpty && separators.Bytes.Span[0] != (byte)' ';

    private bool UsesCaseQuoteBytes(FieldSeparators separators) =>
        _casePatternHasRawQuoteBytes || separators.Bytes.Span.ContainsAny(CaseQuoteEscape, CaseQuoteNull);

    private static CaseQuoteBytes GetCaseQuoteBytes(ReadOnlySpan<byte> bytes) =>
        (bytes.Contains(CaseQuoteEscape) ? CaseQuoteBytes.Escape : CaseQuoteBytes.None)
        | (bytes.Contains(CaseQuoteNull) ? CaseQuoteBytes.Null : CaseQuoteBytes.None);

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

    private IReadOnlyList<ExpandedWord> Build(FieldSeparators fieldSeparators, CaseFieldSelection selection)
    {
        var results = new List<ExpandedWord>();
        AppendTo(results, fieldSeparators, selection, firstWord: 0, firstPart: 0, quotedGroup: false, _storage);
        return results;
    }

    // Expansions can assign IFS: joins use its value at expansion time, splitting uses the final value.
    internal void AppendTo(List<ExpandedWord> results) =>
        AppendTo(results, FieldSeparators.FromVariables(_variables), CaseFieldSelection.None,
            firstWord: 0, firstPart: 0, quotedGroup: false, _storage);

    private void AppendTo(List<ExpandedWord> results, FieldSeparators fieldSeparators, CaseFieldSelection selection,
        int firstWord, int firstPart, bool quotedGroup, MemoryCapacity fieldStorage)
    {
        var splitCasePattern = selection is CaseFieldSelection.All or CaseFieldSelection.First;
        var assembleCasePattern = selection != CaseFieldSelection.None;
        var singleCaseSeparator = splitCasePattern && fieldSeparators.Bytes.Span is [var single] ? single : -1;
        var splitsCaseEscape = assembleCasePattern && fieldSeparators.Bytes.Span.Contains(CaseQuoteEscape);
        var splitsCaseNull = assembleCasePattern && fieldSeparators.Bytes.Span.Contains(CaseQuoteNull);
        var encodesCaseQuoting = assembleCasePattern && (_casePatternHasRawQuoteBytes || splitsCaseEscape || splitsCaseNull);
        var splitHereDocument = !quotedGroup && _hereDocumentHasAt && !AssigningHereDocumentOperand;
        var splitAggregateOnSpace = (!quotedGroup && _splitOnSpaces)
            || (_hasUnquotedAggregate || _hasNormalQuotedAggregate) && fieldSeparators.IsEmpty;
        var splitOnSpace = splitAggregateOnSpace || splitHereDocument && fieldSeparators.IsEmpty;
        // A selected @ operand resolves non-space IFS before the enclosing word receives its join.
        var splitOperandAt = _hasOperandAt && (HasNonSpaceSeparator(fieldSeparators) || _hasNormalQuotedAggregate);
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
            if (assembleCasePattern)
                inputIndex = _words.Count;
            else if (joinUnquotedAggregate || _hasNormalQuotedAggregate)
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
            var split = assembleCasePattern || quotedGroup || joinWords || splitOperandAt
                || splitHereDocument && word.Parts.Exists(static part => part.Origin == PartOrigin.Expansion)
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
                    var segment = part.Segment.ToSegment() with { CaseQuoteBytes = part.CaseQuoteBytes };
                    if (part.Origin == PartOrigin.CaseQuotedNull)
                        segment = segment with { IsCaseQuotedNull = true };
                    else if (OperandFields != ParameterOperandFields.Default)
                        segment = part.Origin switch
                        {
                            PartOrigin.ExpansionWithProtectedSpaces => segment with { Spaces = OperandSpaces.Protected },
                            PartOrigin.Expansion => segment with { Spaces = OperandSpaces.Exposed },
                            _ => segment,
                        };
                    segments[index] = segment;
                    hasBytes |= !segment.Content.IsEmpty;
                    hasProtectedPart |= part.Origin == PartOrigin.Protected;
                }
                if (hasBytes || hasProtectedPart || Purpose == ExpansionPurpose.CasePattern)
                {
                    var emptyAggregateField = !hasBytes && word.HasEmptyAggregateField;
                    results.Add(Purpose == ExpansionPurpose.ParameterReplacement && emptyAggregateField && results.Count != 0
                        ? QuotedNullField : new ExpandedWord(segments, emptyAggregateField));
                }
                continue;
            }

            using var caseFieldScratch = assembleCasePattern ? new MemoryCapacity(_memory.Budget, 0) : null;
            _memory.Charge(fieldStorage, 256);
            var field = new List<SegmentBuilder>();
            var borrowedTail = false;
            var emitEmpty = false;
            var caseNullCount = 0;
            var pendingBreak = false;
            ReadOnlyMemory<byte> pendingSlice = default;
            var pendingSliceQuoted = false;
            using var pendingBytes = new ByteMemoryBuilder(0, _memory.Budget);
            using var quoteScratch = new MemoryCapacity(_memory.Budget, 0);
            var quoteRuns = new List<QuoteRun>();

            for (var wordIndex = first; wordIndex < inputIndex; wordIndex++)
            {
                if (wordIndex > first)
                {
                    var boundary = _words[wordIndex];
                    if (encodesCaseQuoting)
                        pendingBytes.Append(boundary.SeparatorBefore.Bytes);
                    else
                    {
                        if (!splitCasePattern)
                            FlushExpansion();
                        if (boundary.BoundaryBefore == WordBoundary.ProtectedQuoteSeparator)
                            AppendLiteral(boundary.SeparatorBefore.Bytes, globQuoted: true);
                        else
                            QueueExpansion(boundary.SeparatorBefore.Bytes, globQuoted: false);
                    }
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
                if (encodesCaseQuoting)
                {
                    BufferCasePart(part);
                    return;
                }
                if (part.Origin == PartOrigin.CaseQuotedNull)
                {
                    FlushExpansion();
                    if (!HasFirstCasePatternField())
                        ApplyPendingBreak();
                    if (!HasFirstCasePatternField())
                    {
                        caseNullCount = Math.Min(2, caseNullCount + Math.Min(2, part.Segment.Length));
                        emitEmpty = true;
                    }
                    return;
                }
                if ((splitHereDocument || splitCasePattern) && part.Origin == PartOrigin.Protected)
                {
                    if (splitCasePattern && part.Segment.IsEmpty)
                        return;
                    // The input owns this segment through emission. Retain it without copying its bytes.
                    FlushExpansion();
                    if (HasFirstCasePatternField())
                        return;
                    ApplyPendingBreak();
                    if (HasFirstCasePatternField())
                        return;
                    if (part.Segment.IsEmpty)
                        emitEmpty = true;
                    else
                    {
                        AddFieldSegment(part.Segment);
                        borrowedTail = true;
                    }
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

            void BufferCasePart(InputPart part)
            {
                var bytes = part.Segment.ToValue().Bytes.Span;
                if (part.Origin == PartOrigin.CaseQuotedNull)
                {
                    pendingBytes.Append(bytes);
                    return;
                }

                // Bash's quote bytes become visible to the splitter when IFS contains them.
                var quoteAll = part.Origin == PartOrigin.Protected;
                if (!quoteAll && !bytes.ContainsAny(CaseQuoteEscape, CaseQuoteNull))
                {
                    pendingBytes.Append(bytes);
                    return;
                }
                for (var position = 0; position < bytes.Length;)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var length = Utf8Text.FirstCharacterLength(bytes[position..]);
                    if (quoteAll || bytes[position] == CaseQuoteEscape && (part.CaseQuoteBytes & CaseQuoteBytes.Escape) == 0
                        || bytes[position] == CaseQuoteNull && (part.CaseQuoteBytes & CaseQuoteBytes.Null) == 0)
                        pendingBytes.Append(CaseQuoteEscape);
                    pendingBytes.Append(bytes.Slice(position, length));
                    position += length;
                }
            }

            void QueueExpansion(ReadOnlyMemory<byte> bytes, bool globQuoted)
            {
                if (bytes.IsEmpty)
                    return;
                if (pendingBytes.Length == 0)
                {
                    if (pendingSlice.IsEmpty)
                    {
                        pendingSlice = bytes;
                        pendingSliceQuoted = globQuoted;
                        return;
                    }
                    // Adjacent spans can form one separator character. Combine them before decoding.
                    BufferExpansion(pendingSlice, pendingSliceQuoted);
                    pendingSlice = default;
                }
                BufferExpansion(bytes, globQuoted);
            }

            void BufferExpansion(ReadOnlyMemory<byte> bytes, bool globQuoted)
            {
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
                    AppendBytes(bytes, globQuoted, borrowInput: false);
            }

            void AddFieldSegment(SegmentBuilder segment)
            {
                if (caseFieldScratch is { } scratch && field.Count == field.Capacity)
                {
                    // The splitter's initial estimate covers the first four references.
                    if (field.Capacity == 0)
                        field.Capacity = 4;
                    else
                        _memory.PrepareListCapacity(scratch, field, 1);
                }
                field.Add(segment);
            }

            void AppendBytes(ReadOnlyMemory<byte> bytes, bool globQuoted, bool borrowInput)
            {
                var protectedCaseBytes = encodesCaseQuoting && globQuoted;
                if (field.Count == 0 || borrowedTail || field[^1].IsGlobQuoted != globQuoted)
                {
                    AddFieldSegment(new SegmentBuilder(globQuoted, _memory, fieldStorage)
                    {
                        IsCasePatternIfsProtected = protectedCaseBytes,
                    });
                    borrowedTail = false;
                }
                if (borrowInput)
                    field[^1].AppendBorrowed(bytes);
                else
                    field[^1].AppendCopy(bytes.Span);
            }

            void FlushExpansion()
            {
                var buffered = pendingBytes.Length != 0;
                var bytes = buffered ? pendingBytes.ToMemory() : pendingSlice;
                if (bytes.IsEmpty)
                    return;

                if (selection == CaseFieldSelection.Unsplit)
                {
                    if (bytes.Span is [CaseQuoteNull])
                    {
                        emitEmpty = true;
                        caseNullCount = 1;
                    }
                    else
                        AppendCaseBytes(0, bytes.Length);
                    pendingBytes.Truncate(0);
                    return;
                }

                var literalStart = 0;
                var position = 0;
                var quoteIndex = 0;
                while (position < bytes.Length)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (encodesCaseQuoting && bytes.Span[position] == CaseQuoteEscape
                        && position + 1 < bytes.Length
                        && (!splitsCaseEscape || !splitsCaseNull && bytes.Span[position + 1] == CaseQuoteNull))
                    {
                        position += 1 + Utf8Text.FirstCharacterLength(bytes.Span[(position + 1)..]);
                        continue;
                    }
                    var status = Rune.DecodeFromUtf8(bytes.Span[position..], out var character, out var length);
                    var isCharacter = status == OperationStatus.Done;
                    if (!isCharacter)
                        length = 1;
                    bool isDelimiter;
                    if (splitOnSpace)
                    {
                        isDelimiter = isCharacter && character.Value == ' ';
                    }
                    else if (singleCaseSeparator >= 0)
                    {
                        // A single byte serves as its own membership index.
                        isDelimiter = length == 1 && bytes.Span[position] == singleCaseSeparator;
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
                        if (splitCasePattern && field.Count == 0 && caseNullCount == 0)
                            caseNullCount = 1;
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
                if (buffered)
                {
                    // Output segments own copies made from reusable scratch.
                    pendingBytes.Truncate(0);
                    quoteRuns.Clear();
                }
                pendingSlice = default;

                void AppendPendingBytes(int start, int end)
                {
                    ApplyPendingBreak();
                    if (HasFirstCasePatternField())
                        return;
                    if (encodesCaseQuoting)
                    {
                        AppendCaseBytes(start, end);
                        return;
                    }
                    if (!buffered)
                    {
                        // Case fields and their immutable input share the pattern workspace.
                        AppendBytes(bytes[start..end], pendingSliceQuoted, borrowInput: splitCasePattern);
                        return;
                    }
                    while (start < end)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        while (start >= quoteRuns[quoteIndex].End)
                            quoteIndex++;
                        var run = quoteRuns[quoteIndex];
                        var next = Math.Min(end, run.End);
                        AppendBytes(bytes[start..next], run.IsGlobQuoted, borrowInput: false);
                        start = next;
                    }
                }

                void AppendCaseBytes(int start, int end)
                {
                    var span = bytes.Span;
                    while (start < end)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var nextControl = span[start..end].IndexOfAny(CaseQuoteEscape, CaseQuoteNull);
                        if (nextControl < 0)
                        {
                            AppendBytes(bytes[start..end], globQuoted: false, borrowInput: false);
                            return;
                        }
                        if (nextControl > 0)
                        {
                            AppendBytes(bytes.Slice(start, nextControl), globQuoted: false, borrowInput: false);
                            start += nextControl;
                        }
                        var quote = span[start] == CaseQuoteEscape && start + 1 < end;
                        if (quote)
                            start++;
                        else if (splitCasePattern && span[start] == CaseQuoteNull)
                        {
                            caseNullCount = Math.Min(2, caseNullCount + 1);
                            emitEmpty = true;
                            start++;
                            continue;
                        }
                        var length = Utf8Text.FirstCharacterLength(span[start..end]);
                        AppendBytes(bytes.Slice(start, length), globQuoted: quote, borrowInput: false);
                        start += length;
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
                if (!splitCasePattern)
                    _memory.Charge(fieldStorage, 256);
                pendingBreak = false;
                CompleteField();
            }

            void CompleteField()
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (field.Count == 0 && !emitEmpty)
                    return;
                if (assembleCasePattern)
                    _memory.PrepareListCapacity(fieldStorage, results, 1);
                if (field.Count == 0 && caseNullCount == 1)
                    results.Add(CaseQuotedNullField);
                else
                {
                    if (assembleCasePattern)
                        _memory.Charge(fieldStorage, 64L + MemoryCapacity.ArrayBackingBytes<ExpandedSegment>(field.Count));
                    ExpandedSegment[] segments = field.Count == 0 ? [] : new ExpandedSegment[field.Count];
                    for (var index = 0; index < segments.Length; index++)
                        segments[index] = field[index].ToSegment() with
                        {
                            CaseQuoteBytes = assembleCasePattern ? CaseQuoteBytes.Captured : CaseQuoteBytes.None,
                        };
                    results.Add(new ExpandedWord(segments));
                }
                field.Clear();
                emitEmpty = false;
                caseNullCount = 0;
            }
        }

        bool HasFirstCasePatternField() => selection == CaseFieldSelection.First && results.Count != 0;

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

    private SegmentBuilder GetPart(PartOrigin origin, bool globQuoted, int sourceSeparators) =>
        GetPart(origin, globQuoted, sourceSeparators,
            Purpose == ExpansionPurpose.CasePattern ? CaseQuoteBytes.Captured : CaseQuoteBytes.None);

    private SegmentBuilder GetPart(PartOrigin origin, bool globQuoted, int sourceSeparators, CaseQuoteBytes caseQuoteBytes)
    {
        _casePatternHasRawQuoteBytes |= (caseQuoteBytes & (CaseQuoteBytes.Escape | CaseQuoteBytes.Null)) != 0;
        if (_current.Parts.Count == 0
            || (Purpose is ExpansionPurpose.Normal or ExpansionPurpose.CasePattern) && _doubleQuote != DoubleQuoteState.Outside
                && _current.Parts.Count == _doubleQuoteFirstPart && _words.Count - 1 == _doubleQuoteFirstWord
            || _current.Parts[^1].Origin != origin
            || _current.Parts[^1].SourceSeparators != sourceSeparators
            || _current.Parts[^1].CaseQuoteBytes != caseQuoteBytes
            || _current.Parts[^1].Segment.IsGlobQuoted != globQuoted)
        {
            var segment = new SegmentBuilder(globQuoted, _memory, _storage)
            {
                IsCasePatternIfsProtected = Purpose == ExpansionPurpose.CasePattern && origin == PartOrigin.Protected,
            };
            _current.Parts.Add(new InputPart(origin, caseQuoteBytes, sourceSeparators, segment));
        }
        return _current.Parts[^1].Segment;
    }

    private enum PartOrigin : byte
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
        /// <summary>A synthesized DEL in a case pattern whose initial empty IFS bypasses splitting.</summary>
        CaseQuotedNull,
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

    private readonly record struct InputPart(PartOrigin Origin, CaseQuoteBytes CaseQuoteBytes, int SourceSeparators, SegmentBuilder Segment);

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
        public bool IsCasePatternIfsProtected { get; init; }
        public int Length => _bytes?.Length ?? _retained.Bytes.Length;
        public bool IsEmpty => Length == 0;

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

        public ExpandedSegment ToSegment() => new(ToValue(), IsGlobQuoted)
        {
            IsCasePatternIfsProtected = IsCasePatternIfsProtected,
        };

        /// <summary>Retains a span from immutable input already charged to the result's workspace.</summary>
        public void AppendBorrowed(ReadOnlyMemory<byte> bytes)
        {
            if (_bytes == null && _retained.IsEmpty)
                _retained = ShellValue.FromBytes(bytes);
            else
                AppendCopy(bytes.Span);
        }

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

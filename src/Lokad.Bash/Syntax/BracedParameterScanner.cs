using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>Scans braced parameter expansions while honoring nested expansions, quotes, and substitutions.</summary>
internal static class BracedParameterScanner
{
    private static ReadOnlySpan<byte> ParameterNameTerminators => "#%^,:-=?+/@}"u8;
    private enum ScanPhase { Lexical, Operand, Expansion }
    internal enum LexicalQuotes { Preserve, Unquoted, DoubleQuoted }

    /// <summary>Gets whether a validated position begins <c>${</c>.</summary>
    public static bool HasPrefix(ShellSource text, int startIndex) =>
        startIndex >= 0
        && ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "${", out _);

    /// <summary>Scans a complete braced expansion and returns its inclusive closing brace.</summary>
    public static bool TryScan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource payload) =>
        TryScan(text, startIndex, out endIndex, out payload, out _);

    /// <summary>
    /// Scans a complete braced expansion and reports whether command substitution occurs
    /// at any nested depth.
    /// </summary>
    public static bool TryScan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out ShellSource payload,
        out bool containsCommandSubstitution)
    {
        var status = ScanCore(
            new ShellScanContext(),
            text,
            startIndex,
            out endIndex,
            out _,
            out payload,
            out containsCommandSubstitution,
            out _,
            out _,
            out _,
            out _,
            capturePayload: true, trackRenderedLines: false, ScanPhase.Lexical);
        return status == BracedParameterScanStatus.Found;
    }

    /// <summary>Scans only the delimiter shape and reports a structured failure status.</summary>
    public static BracedParameterScanStatus Scan(
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex) =>
        ScanCore(
            new ShellScanContext(),
            text,
            startIndex,
            out endIndex,
            out errorIndex,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            capturePayload: false, trackRenderedLines: false, ScanPhase.Lexical);

    internal static BracedParameterScanStatus ScanShape(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out bool containsCommandSubstitution,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        out bool hasLateSubscript,
        bool trackRenderedLines,
        bool resolveLateSubscripts,
        LexicalQuotes quotes,
        ref ParameterSpellingEdits edits) =>
        ScanCore(
            context,
            text,
            startIndex,
            out endIndex,
            out errorIndex,
            out _,
            out containsCommandSubstitution,
            out renderedLineAdjustment,
            out error,
            out subscriptFailure,
            out hasLateSubscript,
            capturePayload: false, trackRenderedLines, resolveLateSubscripts ? ScanPhase.Operand : ScanPhase.Lexical, quotes, ref edits);

    /// <summary>Captures normalized payload bytes while retaining the structural scan's lookahead context.</summary>
    internal static BracedParameterScanStatus ScanWithPayload(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out SourceSpelling spelling,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        bool trackRenderedLines,
        LexicalQuotes quotes,
        bool resolveLateSubscripts,
        out bool hasSpellingEdits,
        out bool expandedQuoteSyntax)
    {
        var edits = new ParameterSpellingEdits();
        try
        {
            var status = ScanCore(context, text, startIndex, out endIndex, out errorIndex, out var payload, out _,
                out renderedLineAdjustment, out error, out subscriptFailure, out _, capturePayload: true, trackRenderedLines,
                resolveLateSubscripts ? ScanPhase.Operand : ScanPhase.Lexical, quotes, ref edits);
            hasSpellingEdits = edits.HasEdits;
            expandedQuoteSyntax = edits.HasUnprotectedEdits;
            spelling = new SourceSpelling(payload, edits.Captures) { EndsAtDecodedNul = edits.EndsAtDecodedNul };
            return status;
        }
        finally { edits.Dispose(); }
    }

    /// <summary>Reads an expansion within an already delimited word, including a selector spanning an early brace.</summary>
    internal static BracedParameterScanStatus ScanExpansion(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        bool capturePayload,
        out int endIndex,
        out ShellSource payload,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        out RejectedSelectorCapture rejectedSelector)
    {
        var edits = new ParameterSpellingEdits();
        var status = ScanCore(context, text, startIndex, out endIndex, out _, out payload, out _, out _, out error,
            out subscriptFailure, out _, capturePayload, trackRenderedLines: false, ScanPhase.Expansion,
            LexicalQuotes.Preserve, ref edits);
        rejectedSelector = edits.RejectedSelector;
        return status;
    }

    private static BracedParameterScanStatus ScanCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out ShellSource payload,
        out bool containsCommandSubstitution,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        out bool hasLateSubscript,
        bool capturePayload,
        bool trackRenderedLines,
        ScanPhase phase)
    {
        var edits = new ParameterSpellingEdits();
        return ScanCore(context, text, startIndex, out endIndex, out errorIndex, out payload, out containsCommandSubstitution,
            out renderedLineAdjustment, out error, out subscriptFailure, out hasLateSubscript, capturePayload, trackRenderedLines,
            phase, LexicalQuotes.Preserve, ref edits);
    }

    private static BracedParameterScanStatus ScanCore(
        ShellScanContext context,
        ShellSource text,
        int startIndex,
        out int endIndex,
        out int errorIndex,
        out ShellSource payload,
        out bool containsCommandSubstitution,
        out int renderedLineAdjustment,
        out ParserFailure? error,
        out ParameterSubscriptFailure subscriptFailure,
        out bool hasLateSubscript,
        bool capturePayload,
        bool trackRenderedLines,
        ScanPhase phase,
        LexicalQuotes quotes,
        ref ParameterSpellingEdits edits)
    {
        endIndex = startIndex;
        errorIndex = startIndex;
        payload = ShellSource.Empty;
        containsCommandSubstitution = false;
        renderedLineAdjustment = 0;
        error = null;
        subscriptFailure = ParameterSubscriptFailure.None;
        hasLateSubscript = false;
        if (!HasPrefix(text, startIndex))
            return BracedParameterScanStatus.NotBraced;
        if (!context.TryEnter())
            return BracedParameterScanStatus.InvalidNestedSyntax;

        try
        {
            _ = ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), startIndex, "${", out var payloadStart);
            var start = ShellSourceCursor.SkipLineContinuations(text.AsSpan(), payloadStart);
            var lengthForm = start < text.Length && text[start] == (byte)'#';
            var nameStart = lengthForm ? start + 1 : start;
            var nameEnd = ReadSimpleNameEnd(text.AsSpan(), nameStart);
            var cursor = new ShellSourceCursor(text, nameEnd, context);
            var openingBracket = nameEnd > nameStart && nameEnd < text.Length
                && text[nameEnd] == (byte)'[' ? nameEnd : -1;
            var selectorDepth = 0;
            var selectorEnd = -1;
            var quotePattern = quotes == LexicalQuotes.DoubleQuoted &&
                IsPatternOperator(text, nameEnd == nameStart ? ReadSpecialNameEnd(text, start) : nameEnd);
            var lateSelectorEnd = -1;
            var firstBrace = -1;
            var rejectedSelectorStart = -1;
            var rejectedSelectorDepth = 0;
            var resolveLateSubscripts = phase != ScanPhase.Lexical;
            ParserFailure? incompleteFailure = null;
            while (!cursor.IsAtEnd)
            {
                if (cursor.TrySkipReadAheadBodies())
                    continue;
                if (quotes != LexicalQuotes.Preserve && selectorDepth == 0 && !cursor.IsQuoted && cursor.Current == (byte)'$'
                    && ShellSourceCursor.TryMatchLogicalText(text.AsSpan(), cursor.Position, "$'", out _))
                {
                    var literalStart = cursor.Position;
                    if (!cursor.TryReadQuotedLiteralSource(out var literal, out _))
                        break;
                    // Bash retains literal quoting in unquoted operands and in pattern operators.
                    // Other double-quoted parameters receive decoded bytes as operand syntax.
                    edits.AddQuote(context, literalStart, cursor.Position, literal,
                        protect: quotes != LexicalQuotes.DoubleQuoted || quotePattern);
                    continue;
                }
                if (cursor.TryConsumeQuoteOrEscape())
                    continue;

                var nestedStart = cursor.Position;
                var previousFailure = context.SubstitutionFailure;
                var nestedResult = cursor.SkipNestedExpansion(BracedParameterRecognition.All, trackRenderedLines, resolveLateSubscripts,
                    selectorDepth == 0 ? quotes : LexicalQuotes.Preserve, ref edits);
                if (nestedResult is ShellNestedExpansionScan.Complete nested)
                {
                    containsCommandSubstitution |= nested.ContainsCommandSubstitution;
                    renderedLineAdjustment += nested.RenderedLineAdjustment;
                    hasLateSubscript |= nested.HasLateSubscript;
                    if (nested.ParameterSubscriptFailure is ParameterSubscriptFailure.Direct or ParameterSubscriptFailure.Nested)
                        subscriptFailure = ParameterSubscriptFailure.Nested;
                    if (phase == ScanPhase.Expansion && capturePayload && selectorDepth != 0
                        && previousFailure == SubstitutionSyntaxFailure.None
                        && (context.SubstitutionFailure is SubstitutionSyntaxFailure.HereDocumentSeparator
                            or SubstitutionSyntaxFailure.NestedHereDocumentSeparator))
                    {
                        if (nested.Kind == ShellNestedExpansionKind.Command && !cursor.IsQuoted)
                        {
                            rejectedSelectorStart = nestedStart;
                            rejectedSelectorDepth = selectorDepth;
                        }
                        else if (!lengthForm)
                            edits.RejectedSelector = new RejectedSelectorCapture(default, NeedsParameterDiagnostic: true)
                            {
                                NeedsCaptureEofDiagnostic = nested.Kind == ShellNestedExpansionKind.Command && cursor.IsDoubleQuoted
                                    && !HasParameterNameTerminator(text.AsSpan(openingBracket + 1, nestedStart - openingBracket - 1))
                            };
                    }
                    if (quotes != LexicalQuotes.Preserve)
                    {
                        if (nested.Kind == ShellNestedExpansionKind.Command
                            && context.TryGetCommandSubstitution(text, nestedStart, out var cached)
                            && cached.Substitution is CommandSubstitutionSyntax.Parsed normalized
                            && (normalized.HasDiagnosticSpelling || selectorDepth == 0 && normalized.CanRenderBody))
                            edits.AddCapture(context, nestedStart, cursor.Position, normalized);
                        else if ((nested.Kind == ShellNestedExpansionKind.Arithmetic
                                || nested.Kind == ShellNestedExpansionKind.Parameter && selectorDepth != 0)
                            && nested.ContainsCommandSubstitution
                            && (context.SubstitutionFailure is SubstitutionSyntaxFailure.HereDocumentSeparator
                                or SubstitutionSyntaxFailure.NestedHereDocumentSeparator or SubstitutionSyntaxFailure.MissingPipelinePrefixCommand
                                or SubstitutionSyntaxFailure.NestedPipelinePrefixCommand))
                            FunctionDefinitionFormatter.AddDiagnosticCaptureEdits(text, nestedStart, cursor.Position, context, ref edits);
                    }
                    continue;
                }

                if (nestedResult is ShellNestedExpansionScan.Incomplete incomplete)
                {
                    errorIndex = cursor.Position;
                    if (phase != ScanPhase.Expansion)
                        incompleteFailure = incomplete.Failure;
                    var unclosedHereDocumentBacktick = phase == ScanPhase.Expansion
                        && context.LegacyOrigin == LegacySubstitutionOrigin.HereDocument
                        && incomplete.Failure?.UnclosedDelimiter?.Delimiter == '`';
                    if (incomplete.Kind == ShellNestedExpansionKind.Command && lateSelectorEnd < 0 && !unclosedHereDocumentBacktick)
                    {
                        error = incompleteFailure;
                        return BracedParameterScanStatus.UnterminatedCommandSubstitution;
                    }
                    if (resolveLateSubscripts && incomplete.ParameterSubscriptFailure != ParameterSubscriptFailure.None)
                        subscriptFailure = ParameterSubscriptFailure.Nested;
                    break;
                }

                if (nestedResult is ShellNestedExpansionScan.Invalid invalid)
                {
                    errorIndex = cursor.Position;
                    error = invalid.Failure;
                    return BracedParameterScanStatus.InvalidNestedSyntax;
                }

                if (!cursor.IsDoubleQuoted && cursor.Current == (byte)'}')
                {
                    if (selectorDepth != 0 && lateSelectorEnd >= 0)
                    {
                        cursor.Advance();
                        continue;
                    }
                    // Operand boundaries treat the leading '#' as an operator. A directly
                    // reached length expression scans its name through the array selector.
                    var failureBeforeLookahead = context.SubstitutionFailure;
                    if (selectorDepth != 0 && resolveLateSubscripts && (!lengthForm || phase == ScanPhase.Expansion)
                        && context.TryScanLateSubscript(text, openingBracket, cursor.Position, out lateSelectorEnd))
                    {
                        hasLateSubscript = true;
                        firstBrace = cursor.Position;
                        if (capturePayload)
                        {
                            context.SubstitutionFailure = failureBeforeLookahead;
                            cursor.Advance();
                        }
                        else
                        {
                            selectorDepth = 0;
                            cursor = new ShellSourceCursor(text, lateSelectorEnd + 1, context);
                        }
                        continue;
                    }

                    endIndex = cursor.Position;
                    if (selectorDepth != 0)
                        subscriptFailure = lengthForm ? ParameterSubscriptFailure.Length : ParameterSubscriptFailure.Direct;
                    if (capturePayload)
                        payload = edits.BuildPayload(context, text, payloadStart, endIndex, ref renderedLineAdjustment);
                    return BracedParameterScanStatus.Found;
                }

                if (cursor.Position == openingBracket || selectorDepth != 0 && !cursor.IsQuoted)
                {
                    if (cursor.Current == (byte)'[')
                        selectorDepth++;
                    else if (cursor.Current == (byte)']')
                    {
                        if (selectorDepth == rejectedSelectorDepth)
                        {
                            edits.RejectedSelector = new RejectedSelectorCapture(
                                text.Slice(rejectedSelectorStart, cursor.Position - rejectedSelectorStart), NeedsParameterDiagnostic: false);
                            rejectedSelectorDepth = 0;
                        }
                        selectorDepth--;
                        if (selectorDepth == 0)
                        {
                            selectorEnd = cursor.Position + 1;
                            if (quotes == LexicalQuotes.DoubleQuoted)
                                quotePattern = IsPatternOperator(text, selectorEnd);
                        }
                    }
                }
                cursor.Advance();
            }

            errorIndex = cursor.Position;
            if (lateSelectorEnd >= 0)
            {
                subscriptFailure = lengthForm ? ParameterSubscriptFailure.Length : ParameterSubscriptFailure.Direct;
                var next = ShellSourceCursor.SkipLineContinuations(text.AsSpan(), lateSelectorEnd + 1);
                var completeName = next == text.Length || ParameterNameTerminators.Contains(text[next]);
                endIndex = completeName ? lateSelectorEnd : firstBrace;
                if (capturePayload)
                {
                    var payloadEnd = completeName ? endIndex + 1 : endIndex;
                    payload = ShellSourceCursor.PrepareExpansionSource(context,
                        text.Slice(payloadStart, payloadEnd - payloadStart), ref renderedLineAdjustment);
                }
                return completeName ? BracedParameterScanStatus.MissingBraceAfterSelector : BracedParameterScanStatus.Found;
            }
            if (hasLateSubscript)
                subscriptFailure = ParameterSubscriptFailure.Nested;
            if (phase == ScanPhase.Expansion && context.LegacyOrigin == LegacySubstitutionOrigin.HereDocument)
            {
                var operandStart = openingBracket >= 0 ? selectorEnd
                    : nameEnd != nameStart ? nameEnd : ReadSpecialNameEnd(text, start);
                // These special parameter spellings enter the brace reader even without a suffix.
                if (start < text.Length && (text[start] == (byte)'@'
                    || lengthForm && nameStart < text.Length && text[nameStart] is (byte)'@' or (byte)'?'))
                    return BracedParameterScanStatus.UnterminatedOperand;
                if (operandStart > start && operandStart < text.Length && ParameterNameTerminators.Contains(text[operandStart]))
                    return BracedParameterScanStatus.UnterminatedOperand;
            }
            if (phase != ScanPhase.Expansion)
                error = incompleteFailure ?? new ParserFailure(
                    new SyntaxError("Unterminated parameter expansion.", errorIndex), ParserFailureKind.Delimiter)
                {
                    UnclosedDelimiter = cursor.UnclosedQuote
                        ?? new UnclosedDelimiter('}', payloadStart - 1) { AtOpeningDelimiter = true }
                };
            return BracedParameterScanStatus.Unterminated;
        }
        finally
        {
            context.Exit();
        }
    }

    private static bool IsPatternOperator(ShellSource text, int position)
    {
        position = ShellSourceCursor.SkipLineContinuations(text.AsSpan(), position);
        return position < text.Length && "#%/^,"u8.Contains(text[position]);
    }

    private static bool HasParameterNameTerminator(ReadOnlySpan<byte> selectorPrefix)
    {
        // The unquoted parameter reader falls back to its name terminators when rereading a failed selector.
        for (var offset = 0; offset < selectorPrefix.Length; offset++)
        {
            var value = selectorPrefix[offset];
            if (value == (byte)'\\')
                offset++;
            else if (ParameterNameTerminators.Contains(value))
                return true;
        }
        return false;
    }

    private static int ReadSpecialNameEnd(ShellSource text, int position)
    {
        var bytes = text.AsSpan();
        if (position < bytes.Length && bytes[position] == (byte)'!')
            position = ShellSourceCursor.SkipLineContinuations(bytes, position + 1);
        if (position < bytes.Length && "@*#?-$!"u8.Contains(bytes[position]))
            return position + 1;
        while (position < bytes.Length && bytes[position] is >= (byte)'0' and <= (byte)'9')
            position = ShellSourceCursor.SkipLineContinuations(bytes, position + 1);
        return position;
    }

    /// <summary>Shares ordered edits on the physical backing through nested parameter scans.</summary>
    internal struct ParameterSpellingEdits
    {
        private List<Edit>? _edits;
        private MemoryCapacity? _scratch;
        private List<SpellingCapture>? _captures;
        private bool _hasQuoteEdits;
        private bool _endsAtCapture;

        internal readonly bool HasEdits => _edits != null;
        internal RejectedSelectorCapture RejectedSelector { get; set; }
        internal bool HasUnprotectedEdits { get; private set; }
        internal bool EndsAtDecodedNul { get; private set; }
        internal readonly IReadOnlyList<SpellingCapture>? Captures => _captures;

        internal void AddQuote(ShellScanContext context, int start, int end, ShellSource literal, bool protect)
        {
            // The edit is consumed while the physical backing still belongs to this scan.
            var edits = PrepareEdit(context);
            var value = ShellEscapeDecoder.DecodeAnsiCValue(literal.ToValue(), _scratch, out var decodedNul);
            edits.Add(new Edit(start, end, value, protect, null, EndsAtDecodedNul: !protect && decodedNul));
            _hasQuoteEdits = true;
            HasUnprotectedEdits |= !protect;
        }

        internal void AddCapture(ShellScanContext context, int start, int end, CommandSubstitutionSyntax capture)
        {
            var endsAtNul = capture is CommandSubstitutionSyntax.Parsed { AdjacentSpelling.EndsAtDecodedNul: true };
            PrepareEdit(context).Add(new Edit(start, end, default, Protect: false, capture, endsAtNul));
            _endsAtCapture |= endsAtNul;
        }

        private List<Edit> PrepareEdit(ShellScanContext context)
        {
            if (_edits == null)
            {
                if (context.SyntaxMemory is { } memory)
                {
                    _scratch = new MemoryCapacity(memory.Budget, 0);
                    _scratch.Grow(32);
                }
                _edits = [];
            }
            _scratch?.PrepareListCapacity(_edits, 1);
            return _edits;
        }

        internal ShellSource BuildPayload(ShellScanContext context, ShellSource source, int start, int end,
            ref int adjustment)
        {
            var payload = source.Slice(start, end - start);
            if (_edits == null)
                return ShellSourceCursor.PrepareExpansionSource(context, payload, ref adjustment);
            if (!_hasQuoteEdits && !_endsAtCapture && context.HereDocumentReadAhead == null && payload.AsSpan().IndexOf("\\\n"u8) < 0)
            {
                // Display-only edits can retain the operand bytes and the capture offsets.
                foreach (var edit in _edits)
                    if (edit.Capture is { } capture)
                        KeepCapture(context.SyntaxMemory, edit.Start - start, edit.End - edit.Start, capture);
                return payload;
            }
            using var output = _scratch is { } scratch ? new ByteMemoryBuilder(0, scratch)
                : ByteMemoryBuilder.CreateUnaccounted(0);
            foreach (var edit in _edits)
            {
                AppendSource(output, context, source.Slice(start, edit.Start - start), ref adjustment);
                if (edit.Capture is { } capture)
                {
                    var offset = output.Length;
                    AppendSource(output, context, source.Slice(edit.Start, edit.End - edit.Start), ref adjustment);
                    KeepCapture(context.SyntaxMemory, offset, output.Length - offset, capture);
                }
                else if (edit.Protect)
                {
                    if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, edit.Value.Bytes.Span))
                        throw new ByteMemoryBuilder.LimitExceededException();
                }
                else
                    output.Append(edit.Value.Bytes.Span);
                if (edit.EndsAtDecodedNul)
                {
                    EndsAtDecodedNul = true;
                    return ShellSource.FromBytes(output.ToCompactMemory(context.SyntaxMemory));
                }
                start = edit.End;
            }
            AppendSource(output, context, source.Slice(start, end - start), ref adjustment);
            return ShellSource.FromBytes(output.ToCompactMemory(context.SyntaxMemory));

            static void AppendSource(ByteMemoryBuilder output, ShellScanContext context, ShellSource source, ref int adjustment)
            {
                if (!source.IsEmpty)
                    output.Append(ShellSourceCursor.PrepareExpansionSource(context, source, ref adjustment).AsSpan());
            }
        }

        private void KeepCapture(MemoryCapacity? memory, int offset, int length, CommandSubstitutionSyntax capture)
        {
            if (_captures == null)
            {
                memory?.Grow(32);
                _captures = [];
            }
            memory?.PrepareListCapacity(_captures, 1);
            _captures.Add(new SpellingCapture(offset, length, capture));
        }

        internal readonly void Dispose() => _scratch?.Dispose();

        private readonly record struct Edit(int Start, int End, ShellValue Value, bool Protect,
            CommandSubstitutionSyntax? Capture, bool EndsAtDecodedNul);
    }

    private static int ReadSimpleNameEnd(ReadOnlySpan<byte> text, int start)
    {
        var index = ShellSourceCursor.SkipLineContinuations(text, start);
        if (index < text.Length && text[index] == (byte)'!')
            index = ShellSourceCursor.SkipLineContinuations(text, index + 1);
        if (index == text.Length || !VariableNames.IsNameStart((char)text[index]))
            return start;
        do { index = ShellSourceCursor.SkipLineContinuations(text, index + 1); }
        while (index < text.Length && VariableNames.IsNamePart((char)text[index]));
        return index;
    }
}

/// <summary>The narrowed diagnostic operand or the outer parameter failure after a rejected selector.</summary>
internal readonly record struct RejectedSelectorCapture(ShellSource Operand, bool NeedsParameterDiagnostic)
{
    internal bool NeedsCaptureEofDiagnostic { get; init; }
}

internal enum ParameterSubscriptFailure : byte { None, Direct, Nested, Length }

internal enum BracedParameterScanStatus
{
    /// <summary>The requested position does not begin <c>${</c>.</summary>
    NotBraced,

    /// <summary>A complete expansion and closing brace were found.</summary>
    Found,

    /// <summary>A complete array name precedes end of word without an enclosing brace.</summary>
    MissingBraceAfterSelector,

    /// <summary>The outer expansion or a non-command nested expansion is incomplete.</summary>
    Unterminated,
    /// <summary>An unclosed here-document parameter reached the brace-delimited operand reader.</summary>
    UnterminatedOperand,

    /// <summary>A nested command substitution is incomplete.</summary>
    UnterminatedCommandSubstitution,

    /// <summary>A nested expansion has a closing delimiter candidate but invalid syntax.</summary>
    InvalidNestedSyntax,
}

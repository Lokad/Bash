using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Parses bash script text into a syntax tree.
/// </summary>
public static class Parser
{
    public const int MaximumScriptBytes = 4 * 1024 * 1024;
    public const string ScriptSizeLimitMessage = "Script exceeds the 4 MiB UTF-8 limit.";

    /// <summary> Shell keyword for <c>if</c>. </summary>
    private const string KeywordIf = "if";
    /// <summary> Shell keyword for <c>then</c>. </summary>
    private const string KeywordThen = "then";
    /// <summary> Shell keyword for <c>elif</c>. </summary>
    private const string KeywordElif = "elif";
    /// <summary> Shell keyword for <c>else</c>. </summary>
    private const string KeywordElse = "else";
    /// <summary> Shell keyword for <c>fi</c>. </summary>
    private const string KeywordFi = "fi";
    /// <summary> Shell keyword for <c>while</c>. </summary>
    private const string KeywordWhile = "while";
    /// <summary> Shell keyword for <c>until</c>. </summary>
    private const string KeywordUntil = "until";
    /// <summary> Shell keyword for <c>do</c>. </summary>
    private const string KeywordDo = "do";
    /// <summary> Shell keyword for <c>done</c>. </summary>
    private const string KeywordDone = "done";
    /// <summary> Shell keyword for <c>for</c>. </summary>
    private const string KeywordFor = "for";
    /// <summary> Shell keyword for <c>in</c>. </summary>
    private const string KeywordIn = "in";
    /// <summary> Shell keyword for <c>function</c>. </summary>
    private const string KeywordFunction = "function";
    /// <summary> Shell keyword for <c>case</c>. </summary>
    private const string KeywordCase = "case";
    /// <summary> Shell keyword for <c>esac</c>. </summary>
    private const string KeywordEsac = "esac";

    // A missing arm body has no command location or source to retain.
    private static readonly SimpleCommand EmptyCaseBody =
        new([], [], [], ReadOnlyDictionary<Word, AssignmentWord>.Empty);

    private enum KeywordKind { None, If, While, Until, For, Function, Case, Conditional, Time, Reserved }

    private static class ImplicitForList
    {
        internal static readonly IReadOnlyList<Word> Items = Create();

        private static IReadOnlyList<Word> Create()
        {
            var positional = Tokenizer.ReadExpansionWord(ShellSource.FromText("\"$@\""),
                ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
            // Freeze the parts before sharing the word between parsed scripts.
            return [new Word(Array.AsReadOnly(positional.Parts.ToArray()))];
        }
    }

    /// <summary>Returns true when a word is a reserved keyword supported by this parser.</summary>
    internal static bool IsSupportedKeyword(ReadOnlySpan<byte> word) => ClassifyKeyword(word) != KeywordKind.None;

    private static KeywordKind ClassifyKeyword(ReadOnlySpan<byte> word)
    {
        // "function" is the longest supported keyword.
        if (word.IsEmpty || word.Length > KeywordFunction.Length)
            return KeywordKind.None;

        return word[0] switch
        {
            (byte)'i' when AsciiByte.EqualsAscii(word, KeywordIf) => KeywordKind.If,
            (byte)'w' when AsciiByte.EqualsAscii(word, KeywordWhile) => KeywordKind.While,
            (byte)'u' when AsciiByte.EqualsAscii(word, KeywordUntil) => KeywordKind.Until,
            (byte)'f' when AsciiByte.EqualsAscii(word, KeywordFor) => KeywordKind.For,
            (byte)'f' when AsciiByte.EqualsAscii(word, KeywordFunction) => KeywordKind.Function,
            (byte)'c' when AsciiByte.EqualsAscii(word, KeywordCase) => KeywordKind.Case,
            (byte)'[' when word.SequenceEqual("[["u8) => KeywordKind.Conditional,
            (byte)'t' when word.SequenceEqual("time"u8) => KeywordKind.Time,
            (byte)'d' when AsciiByte.EqualsAscii(word, KeywordDo) || AsciiByte.EqualsAscii(word, KeywordDone) => KeywordKind.Reserved,
            (byte)'e' when AsciiByte.EqualsAscii(word, KeywordElif) || AsciiByte.EqualsAscii(word, KeywordElse)
                || AsciiByte.EqualsAscii(word, KeywordEsac) => KeywordKind.Reserved,
            (byte)'f' when AsciiByte.EqualsAscii(word, KeywordFi) => KeywordKind.Reserved,
            (byte)'i' when AsciiByte.EqualsAscii(word, KeywordIn) => KeywordKind.Reserved,
            (byte)'t' when AsciiByte.EqualsAscii(word, KeywordThen) => KeywordKind.Reserved,
            (byte)']' when word.SequenceEqual("]]"u8) => KeywordKind.Reserved,
            (byte)'!' or (byte)'{' or (byte)'}' when word.Length == 1 => KeywordKind.Reserved,
            _ => KeywordKind.None
        };
    }

    /// <summary>Measures text that fits the script limit before allocating its byte representation.</summary>
    internal static bool TryMeasureTextSource(string input, out int byteCount)
    {
        // An oversized UTF-16 count already establishes that the bytes exceed the limit.
        byteCount = input.Length > MaximumScriptBytes ? input.Length : Encoding.UTF8.GetByteCount(input);
        return byteCount <= MaximumScriptBytes;
    }

    /// <summary>
    /// Parses the provided script text into a <see cref="ParsedScript"/>.
    /// </summary>
    /// <param name="input">The script contents.</param>
    /// <returns>The parsed script AST.</returns>
    /// <exception cref="SyntaxException">Thrown for invalid syntax or exceeded source, syntax, or nesting limits.</exception>
    public static ParsedScript Parse(string input) =>
        TryParse(input, out var script, out var error) ? script : throw error.ToException();

    /// <summary>Parses text without throwing for syntax errors; diagnostic offsets count UTF-16 characters.</summary>
    public static bool TryParse(string input,
        [NotNullWhen(true)] out ParsedScript? script, [NotNullWhen(false)] out SyntaxError? error)
    {
        script = null;
        if (!TryMeasureTextSource(input, out var byteCount))
        {
            error = new SyntaxError(ScriptSizeLimitMessage, 0);
            return false;
        }

        var source = ShellSource.FromMeasuredText(input, byteCount);
        if (!TryParse(source.Bytes, out script, out var byteError))
        {
            // Text callers retain UTF-16 coordinates; the shared grammar uses byte offsets.
            error = byteError with { Offset = Encoding.UTF8.GetCharCount(source.Bytes.Span[..byteError.Offset]) };
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Parses source bytes without requiring literal data to form valid UTF-8.</summary>
    /// <remarks>Retains input storage; do not mutate it while the parsed script is in use.</remarks>
    public static ParsedScript Parse(ReadOnlyMemory<byte> input) =>
        TryParse(input, out var script, out var error) ? script : throw error.ToException();

    internal static ParsedScript Parse(ReadOnlyMemory<byte> input, ShellScanContext context) =>
        TryParse(input, context, out var script, out var error) ? script : throw error.ToException();

    /// <summary>Parses borrowed source bytes without throwing for invalid syntax or parsing limits.</summary>
    /// <remarks>Retains input storage; do not mutate it while the parsed syntax is in use.</remarks>
    public static bool TryParse(ReadOnlyMemory<byte> input,
        [NotNullWhen(true)] out ParsedScript? script, [NotNullWhen(false)] out SyntaxError? error) =>
        TryParse(input, new ShellScanContext(), out script, out error);

    internal static bool TryParse(ReadOnlyMemory<byte> input, ShellScanContext context,
        [NotNullWhen(true)] out ParsedScript? script, [NotNullWhen(false)] out SyntaxError? error)
    {
        if (!TryParseScript(input, context, out script, out var failure))
        {
            error = failure.Error;
            return false;
        }
        error = null;
        return true;
    }

    internal static bool TryParseScript(ReadOnlyMemory<byte> input, ShellScanContext context,
        [NotNullWhen(true)] out ParsedScript? script, [NotNullWhen(false)] out ParserFailure? failure)
    {
        script = null;
        if (input.Length > MaximumScriptBytes)
        {
            failure = new ParserFailure(new SyntaxError(ScriptSizeLimitMessage, 0), ParserFailureKind.Limit);
            return false;
        }
        context.LegacyOrigin = LegacySubstitutionOrigin.Script;
        context.ScriptInput = ShellSource.FromBytes(input);
        var tokenizer = new Tokenizer(context.ScriptInput, 0, context)
        {
            TrackCommandLocations = true,
            AllowEofHereDocuments = true
        };
        SkipNewlines(tokenizer);
        var (command, _) = ParseListWithTerminatorInfo(tokenizer, null, [], null, CommandUnitMetadata.All, out var lastCommandEnd);
        SkipNewlines(tokenizer);
        var trailing = tokenizer.PeekTokenCore();
        if (trailing.Kind != TokenKind.EndOfInput)
            tokenizer.RecordUnexpectedToken("Unexpected token after command list.", trailing, trailing.Source.Start.Value);

        failure = tokenizer.Failure;
        if (failure != null)
            return false;
        script = CompleteScript(tokenizer, command);
        script.LastCommandEnd = lastCommandEnd;
        return true;
    }

    /// <summary>
    /// Lazily returns complete top-level command units or a syntax failure.
    /// Later syntax is checked only when requested; failures retain absolute byte offsets.
    /// </summary>
    internal static IEnumerable<CommandUnitParseResult> TryParseCommandUnits(ReadOnlyMemory<byte> input) =>
        TryParseCommandUnits(input, new ShellScanContext());

    /// <summary>Shares one structural context while the execution owner supplies each unit's allowance.</summary>
    internal static IEnumerable<CommandUnitParseResult> TryParseCommandUnits(
        ReadOnlyMemory<byte> input, ShellScanContext scanContext)
    {
        if (input.Length > MaximumScriptBytes)
        {
            yield return new CommandUnitParseResult.Failed(
                new ParserFailure(new SyntaxError(ScriptSizeLimitMessage, 0), ParserFailureKind.Limit));
            yield break;
        }

        scanContext.LegacyOrigin = LegacySubstitutionOrigin.Script;
        scanContext.ScriptInput = ShellSource.FromBytes(input);
        var tokenizer = new Tokenizer(scanContext.ScriptInput, 0, scanContext)
        {
            TrackCommandLocations = true,
            TrackUnexpectedTokens = true,
            AllowEofHereDocuments = true,
            EndInputOnQuotedSubstitutionFailure = true
        };
        while (true)
        {
            tokenizer.BeginCommandUnit();
            SkipNewlines(tokenizer);
            var (command, _) = ParseListWithTerminatorInfo(tokenizer, null, [], TokenKind.Newline);
            var trailing = tokenizer.PeekTokenCore();
            if (trailing.Kind is not TokenKind.Newline and not TokenKind.EndOfInput)
                tokenizer.RecordUnexpectedToken("Unexpected token after command list.", trailing, trailing.Source.Start.Value);
            var diagnostics = new CommandReadDiagnostics(scanContext.TakeHereDocumentWarnings(tokenizer.HereDocuments?.Warnings),
                scanContext.TakeCompoundItemDiagnostics()) { FunctionBraceLine = tokenizer.TakeFunctionBraceLine() };

            var quotedFailure = scanContext.ParsingLimitExceeded ? null : tokenizer.QuotedEndOfInputFailure;
            if ((quotedFailure ?? tokenizer.Failure) is { } failure)
            {
                var prefix = scanContext.ReadQuotedRecovery && quotedFailure != null && tokenizer.Error == null
                    && command != null && tokenizer.HereDocuments is not { HasPending: true }
                    ? CompleteHereDocuments(tokenizer, command) : null;
                // Array syntax rejects the whole unit, but a direct quoted item can still leave a delimiter to read.
                var recoverQuotedSuffix = scanContext.ReadQuotedRecovery && (quotedFailure != null
                    || failure is { Kind: ParserFailureKind.ArrayQuotedSubstitutionForHeader, IsNestedSubstitution: false });
                var nextUnit = scanContext.ReadArrayRecovery
                    && failure is { Kind: ParserFailureKind.ArrayDelimiter, UnexpectedToken: { } rejected }
                    ? tokenizer.ArrayRecoveryStart(rejected.ReadPosition) : input.Length;
                yield return new CommandUnitParseResult.Failed(failure)
                {
                    CompletedPrefix = prefix,
                    Locations = prefix != null ? tokenizer.Locations : null,
                    ReadDiagnostics = diagnostics,
                    ResumesAfterArrayToken = nextUnit < input.Length,
                    DiagnosticLine = failure switch
                    {
                        { UnclosedDelimiter: { } captureEnd } =>
                            tokenizer.GetLine(captureEnd.ReadPosition) + captureEnd.LineAdvance,
                        { UnexpectedToken: { } diagnostic } => tokenizer.GetLine(diagnostic.ReadPosition) + diagnostic.LineAdvance,
                        { ArithmeticForHeader: { } header } => tokenizer.GetLine(header.Start),
                        { Kind: ParserFailureKind.Grammar or ParserFailureKind.RedirectionOperand } when trailing.Kind == TokenKind.EndOfInput
                            && failure.Error.Offset == input.Length && tokenizer.Position == input.Length =>
                            tokenizer.GetLine(input.Length) + tokenizer.EndOfInputLineAdvance,
                        _ => 0
                    },
                    QuotedRecovery = recoverQuotedSuffix && failure.ArithmeticForHeader is { } recovery
                        ? tokenizer.ReadQuotedRecoveryDelimiter(recovery.BodyEnd) : null
                };
                if (nextUnit == input.Length)
                    yield break;
                tokenizer.ResumeCommandUnits(nextUnit);
                continue;
            }
            if (command == null)
                yield break;

            var completed = CompleteHereDocuments(tokenizer, command);
            tokenizer.HereDocuments = null;
            if (trailing.Kind == TokenKind.Newline)
                tokenizer.NextTokenCore();
            yield return new CommandUnitParseResult.Parsed(completed)
                { Locations = tokenizer.Locations, ReadDiagnostics = diagnostics };
        }
    }

    /// <summary>
    /// Parses a command-substitution body from an offset and returns the closing
    /// parenthesis consumed by the grammar, retaining the executable tree it built.
    /// </summary>
    internal static ShellScanStatus ScanCommandSubstitutionEnd(
        ShellScanContext context,
        ShellSource input,
        int payloadStart,
        out int closingParenthesisIndex,
        out ParsedScript? parsed,
        out SubstitutionSyntaxFailure failure,
        out bool preserveAdjacentOpening,
        out SourceSpelling? adjacentSpelling,
        out IReadOnlyList<HereDocumentWarning>? deferredWarnings,
        out ParserFailure? syntaxError)
    {
        parsed = null;
        failure = SubstitutionSyntaxFailure.None;
        preserveAdjacentOpening = false;
        adjacentSpelling = null;
        deferredWarnings = null;
        syntaxError = null;
        closingParenthesisIndex = payloadStart;
        if (payloadStart < 0 || payloadStart > input.Length)
            return ShellScanStatus.NotFound;

        var retainLookahead = context.RetainCaptureLookahead;
        var rawHereDocument = !retainLookahead && context.LegacyOrigin == LegacySubstitutionOrigin.HereDocument;
        try
        {
            // The delimiter parse can replay a changed list while borrowing its original words and captures.
            context.RetainCaptureLookahead = true;
            var readAheadBefore = context.HereDocumentReadAhead?.CaptureCount ?? 0;
            var tokenizer = new Tokenizer(input, payloadStart, context)
            {
                TrackCommandLocations = true,
                TrackUnexpectedTokens = true,
                AllowEofHereDocuments = context.LegacyOrigin != LegacySubstitutionOrigin.Temporary,
                IsCommandSubstitution = true
            };
            var status = ReadBody(tokenizer, input, payloadStart, out var command, out closingParenthesisIndex, out syntaxError);
            if (status != ShellScanStatus.Complete)
                return status;

            // Adjacent parentheses retain the outer body's separators. Nested
            // substitutions still undergo validation when Bash reads that body.
            var adjacentParenthesis = ShellSourceCursor.TryMatchLogicalText(input.AsSpan(), payloadStart, "(", out _);
            var nestedFailure = context.SubstitutionFailure;
            var barePrefix = tokenizer.HasSubshellEndingInBarePipelinePrefix || EndsInBarePipelinePrefix(command);
            preserveAdjacentOpening = adjacentParenthesis && (barePrefix || nestedFailure != SubstitutionSyntaxFailure.None);
            if (nestedFailure != SubstitutionSyntaxFailure.None
                && !(rawHereDocument && IsHereDocumentSeparatorFailure(nestedFailure)))
                failure = ClassifyNestedFailure(nestedFailure, adjacentParenthesis);
            else if (!adjacentParenthesis && barePrefix)
                failure = SubstitutionSyntaxFailure.MissingPipelinePrefixCommand;

            var collectedAhead = false;
            if (tokenizer.HereDocuments is { HasPending: true } pending)
            {
                if (adjacentParenthesis)
                {
                    // The matched pair defers its pending documents to the capture reader.
                    // Later lines still belong to the surrounding input.
                    preserveAdjacentOpening = true;
                    return ShellScanStatus.Complete;
                }
                if (context.LegacyOrigin == LegacySubstitutionOrigin.HereDocument)
                {
                    if (tokenizer.ReadHereDocumentExpansionCaptureBodies(pending))
                    {
                        deferredWarnings = pending.Warnings;
                        return ShellScanStatus.Complete;
                    }
                    syntaxError = MissingCloser(GetCaptureWarnings(pending.Warnings, input, payloadStart),
                        input.Length, tokenizer.EndOfInputLineAdvance, payloadLine: 0);
                    return ShellScanStatus.Invalid;
                }
                if (!context.CanReadAhead(input))
                    return ShellScanStatus.Complete;
                tokenizer.ReadCaptureHereDocuments(pending);
                collectedAhead = true;
            }

            parsed = CompleteScript(tokenizer, command);
            parsed.HasReadAheadHereDocuments = (context.HereDocumentReadAhead?.CaptureCount ?? 0) != readAheadBefore;
            if (failure != SubstitutionSyntaxFailure.None || preserveAdjacentOpening || rawHereDocument)
            {
                if (preserveAdjacentOpening && !rawHereDocument && !parsed.HasReadAheadHereDocuments
                    && (IsHereDocumentSeparatorFailure(nestedFailure) || IsPipelinePrefixFailure(nestedFailure)))
                {
                    using var workspace = context.SyntaxMemory is { } syntax ? new MemoryCapacity(syntax.Budget, 0) : null;
                    // The enclosing matched pair stays intact while its failed inner capture can be displayed.
                    adjacentSpelling = ShellSourceCursor.ReadAdjacentCaptureSpelling(context, input, payloadStart,
                        closingParenthesisIndex, workspace, prepareReplay: false, out _, out _, out _);
                }
                return ShellScanStatus.Complete;
            }

            if (adjacentParenthesis && !parsed.HasReadAheadHereDocuments)
            {
                using var workspace = context.SyntaxMemory is { } syntax ? new MemoryCapacity(syntax.Budget, 0) : null;
                adjacentSpelling = ShellSourceCursor.ReadAdjacentCaptureSpelling(context, input, payloadStart,
                    closingParenthesisIndex, workspace, prepareReplay: true, out var projectedInput, out var lineAdjustments,
                    out var renderedLines);
                var projected = adjacentSpelling.GetValueOrDefault();
                var changedBytes = !projectedInput.IsEmpty;
                if (changedBytes || lineAdjustments != null)
                {
                    // Byte edits can also change a comment or here-document body. Share the nested
                    // syntax in the edited source. For line-only edits reuse the original backing.
                    var locatedSource = input;
                    var locatedStart = payloadStart;
                    var expectedEnd = closingParenthesisIndex;
                    if (changedBytes)
                    {
                        locatedSource = projectedInput;
                        locatedStart = 0;
                        expectedEnd = projected.Source.Length;
                        lineAdjustments = InstallProjectedCaptures(locatedSource, projected.Captures, workspace);
                    }
                    var located = new Tokenizer(locatedSource, locatedStart, context)
                    {
                        TrackCommandLocations = true,
                        AllowEofHereDocuments = tokenizer.AllowEofHereDocuments,
                        IsCommandSubstitution = true,
                        LineAdjustments = lineAdjustments
                    };
                    var locationStatus = ReadBody(located, locatedSource, locatedStart,
                        out var locatedCommand, out var locatedEnd, out var locationError);
                    if (locationStatus == ShellScanStatus.Complete && locatedEnd == expectedEnd
                        && located.HereDocuments is not { HasPending: true })
                    {
                        parsed = CompleteScript(located, locatedCommand);
                        if (context.SubstitutionFailure != SubstitutionSyntaxFailure.None)
                            failure = ClassifyNestedFailure(context.SubstitutionFailure, adjacent: true);
                    }
                    else if (locationError is { Kind: ParserFailureKind.Limit })
                    {
                        syntaxError = locationError;
                        return ShellScanStatus.Invalid;
                    }
                    else if (changedBytes)
                        failure = SubstitutionSyntaxFailure.NestedSyntax;
                }
                parsed.RenderedLineBreaks = renderedLines;
                return ShellScanStatus.Complete;
            }

            var edits = FunctionDefinitionFormatter.ProjectCaptureLocations(parsed, context.SyntaxMemory,
                collectOmittedSeparators: !adjacentParenthesis && !collectedAhead);
            if (edits == null)
                return ShellScanStatus.Complete;

            // Keep positions on the original backing so delimiter lookahead can reuse nested parses.
            var replay = new Tokenizer(input, payloadStart, context) { TrackCommandLocations = true, OmittedSeparators = edits };
            status = ReadBody(replay, input, payloadStart, out var corrected, out var replayEnd, out var replayFailure);
            if (status == ShellScanStatus.Complete && replayEnd == closingParenthesisIndex
                && replay.HereDocuments is not { HasPending: true })
            {
                var authoredCommands = parsed.Commands;
                parsed = CompleteScript(replay, corrected);
                parsed.AuthoredCaptureCommands = authoredCommands;
                parsed.HasReadAheadHereDocuments = (context.HereDocumentReadAhead?.CaptureCount ?? 0) != readAheadBefore;
                if (context.SubstitutionFailure != SubstitutionSyntaxFailure.None)
                    failure = ClassifyNestedFailure(context.SubstitutionFailure, adjacentParenthesis);
                FunctionDefinitionFormatter.ProjectCaptureLocations(parsed, context.SyntaxMemory, collectOmittedSeparators: false);
            }
            else if (replayFailure is { Kind: ParserFailureKind.Limit })
            {
                syntaxError = replayFailure;
                return ShellScanStatus.Invalid;
            }
            else
                failure = SubstitutionSyntaxFailure.HereDocumentSeparator;
            return ShellScanStatus.Complete;
        }
        finally { context.RetainCaptureLookahead = retainLookahead; }

        IReadOnlyList<SourceLineAdjustment>? InstallProjectedCaptures(ShellSource source,
            IReadOnlyList<SpellingCapture>? captures, MemoryCapacity? workspace)
        {
            if (captures == null)
                return null;
            List<SourceLineAdjustment>? lines = null;
            var adjustment = 0;
            foreach (var capture in captures)
            {
                context.StoreCommandSubstitution(source, capture.Offset, capture.Length, capture.Syntax);
                if (capture.Syntax is not CommandSubstitutionSyntax.Parsed { Script.RenderedLineBreaks: { } rendered })
                    continue;
                var delta = rendered - source.AsSpan(capture.Offset, capture.Length).Count((byte)'\n');
                if (delta == 0)
                    continue;
                if (lines == null)
                {
                    workspace?.Grow(32);
                    lines = [];
                }
                workspace?.PrepareListCapacity(lines, 1);
                adjustment += delta;
                lines.Add(new SourceLineAdjustment(capture.Offset + capture.Length, adjustment));
            }
            return lines;
        }

        ShellScanStatus ReadBody(Tokenizer tokenizer, ShellSource source, int start, out ICommand? command,
            out int end, out ParserFailure? error)
        {
            end = start;
            error = null;
            (command, _) = ParseListWithTerminatorInfo(tokenizer, TokenKind.CloseParen, [], null,
                rawHereDocument ? CommandUnitMetadata.HereDocumentLocations : CommandUnitMetadata.None);
            SkipNewlines(tokenizer);
            var closing = tokenizer.PeekTokenCore();
            var bodyFailure = tokenizer.Failure;
            var missingOperand = bodyFailure is { Kind: ParserFailureKind.RedirectionOperand, UnexpectedToken: not null };
            var reachedEnd = tokenizer.Position == source.Length && closing.Kind == TokenKind.EndOfInput
                && !tokenizer.HasPendingClosingParenthesis && !context.ParsingLimitExceeded;
            if (!missingOperand && reachedEnd && tokenizer is { AllowEofHereDocuments: true, HereDocuments: { } documents }
                && (bodyFailure == null || documents.Warnings != null))
            {
                error = MissingCloser(GetCaptureWarnings(documents.Warnings, source, start), source.Length,
                    tokenizer.EndOfInputLineAdvance, GetPayloadEndLine(tokenizer, source.Length));
                return ShellScanStatus.Invalid;
            }
            if (bodyFailure != null && (missingOperand || context.ParsingLimitExceeded || bodyFailure.UnclosedDelimiter != null
                || bodyFailure.ConditionalEndOfInput.HasValue
                || bodyFailure.Error.Offset < source.Length || tokenizer.HasPendingClosingParenthesis))
            {
                error = bodyFailure.ConditionalEndOfInput.HasValue
                    ? bodyFailure with { Kind = ParserFailureKind.Delimiter }
                    : bodyFailure.InSubstitution();
                if (error.UnexpectedToken is { } unexpected)
                    error = error with { UnexpectedToken = unexpected with { CapturePayloadStart = start } };
                if (error.ConditionalEndOfInput is { CaptureLineOffset: null } conditionalEnd)
                    error = error with
                    {
                        ConditionalEndOfInput = conditionalEnd with { CaptureLineOffset = source.AsSpan(0, start).Count((byte)'\n') }
                    };
                if (missingOperand)
                    error = error with { CaptureWarnings = GetCaptureWarnings(tokenizer.HereDocuments?.Warnings, source, start) };
                return ShellScanStatus.Invalid;
            }

            if (reachedEnd && tokenizer.AllowEofHereDocuments)
            {
                error = new ParserFailure(new SyntaxError("Unterminated command substitution.", start), ParserFailureKind.Delimiter)
                {
                    UnclosedDelimiter = new UnclosedDelimiter(')', source.Length)
                    {
                        LineAdvance = tokenizer.EndOfInputLineAdvance,
                        IncludeScriptLine = true,
                        PayloadLine = GetPayloadEndLine(tokenizer, source.Length)
                    }
                };
                return ShellScanStatus.Incomplete;
            }
            if (bodyFailure != null || closing.Kind == TokenKind.EndOfInput)
                return ShellScanStatus.Incomplete;
            if (closing.Kind != TokenKind.CloseParen)
            {
                error = new ParserFailure(new SyntaxError("Unexpected token after command substitution body.",
                    closing.Source.Start.Value), ParserFailureKind.SubstitutionGrammar)
                {
                    UnexpectedToken = tokenizer.GetUnexpectedSyntax(closing) with { CapturePayloadStart = start }
                };
                return ShellScanStatus.Invalid;
            }
            tokenizer.NextTokenCore();
            end = closing.Source.Start.Value;
            return ShellScanStatus.Complete;
        }

        int GetPayloadEndLine(Tokenizer tokenizer, int end) => context.LegacyOrigin == LegacySubstitutionOrigin.HereDocument
            ? tokenizer.GetLine(end) + tokenizer.EndOfInputLineAdvance : 0;

        static ParserFailure MissingCloser(CaptureHereDocumentWarnings? warnings, int eofOffset, int lineAdvance, int payloadLine) =>
            new(new SyntaxError("unexpected EOF while looking for matching `)'", eofOffset), ParserFailureKind.Delimiter)
            {
                UnclosedDelimiter = new UnclosedDelimiter(')', eofOffset)
                {
                    LineAdvance = lineAdvance,
                    IncludeScriptLine = true,
                    PayloadLine = payloadLine
                },
                CaptureWarnings = warnings
            };

        static CaptureHereDocumentWarnings? GetCaptureWarnings(
            IReadOnlyList<HereDocumentWarning>? warnings, ShellSource source, int start) =>
            warnings == null ? null : new CaptureHereDocumentWarnings(warnings, source.AsSpan(0, start).Count((byte)'\n'));

        static SubstitutionSyntaxFailure ClassifyNestedFailure(SubstitutionSyntaxFailure nested, bool adjacent) =>
            IsHereDocumentSeparatorFailure(nested)
                ? adjacent ? SubstitutionSyntaxFailure.HereDocumentSeparator : SubstitutionSyntaxFailure.NestedHereDocumentSeparator
                : IsPipelinePrefixFailure(nested)
                    ? adjacent ? SubstitutionSyntaxFailure.MissingPipelinePrefixCommand : SubstitutionSyntaxFailure.NestedPipelinePrefixCommand
                    : adjacent ? SubstitutionSyntaxFailure.AdjacentNestedSyntax : SubstitutionSyntaxFailure.NestedSyntax;

        static bool IsPipelinePrefixFailure(SubstitutionSyntaxFailure failure) =>
            failure is SubstitutionSyntaxFailure.MissingPipelinePrefixCommand or SubstitutionSyntaxFailure.NestedPipelinePrefixCommand;

        static bool IsHereDocumentSeparatorFailure(SubstitutionSyntaxFailure nested) =>
            nested is SubstitutionSyntaxFailure.HereDocumentSeparator or SubstitutionSyntaxFailure.NestedHereDocumentSeparator;
    }

    private static ParsedScript CompleteScript(Tokenizer tokenizer, ICommand? command) =>
        command == null ? new ParsedScript([]) :
        new ParsedScript([CompleteHereDocuments(tokenizer, command)])
        {
            Locations = tokenizer.Locations
        };

    private static ICommand CompleteHereDocuments(Tokenizer tokenizer, ICommand command)
    {
        tokenizer.CompleteCompoundHereDocumentLocations();
        return tokenizer.HereDocuments?.Complete(command, tokenizer.Locations, tokenizer.ScanContext.SyntaxMemory?.Budget) ?? command;
    }

    private static bool EndsInBarePipelinePrefix(ICommand? command)
    {
        while (true)
        {
            switch (command)
            {
                case ListCommand list: command = list.Right; break;
                case NegatedCommand { Inner: SimpleCommand { IsEmpty: true } }: return true;
                case NegatedCommand negated: command = negated.Inner; break;
                default: return command is TimedCommand { Inner: SimpleCommand { IsEmpty: true } };
            }
        }
    }

    /// <summary>
    /// Parses a command list joined by &amp;&amp; or || operators.
    /// </summary>
    private static ICommand? ParseAndOrList(Tokenizer tokenizer)
    {
        var first = ParsePipelineCore(tokenizer);
        if (first == null)
        {
            return null;
        }

        ICommand current = first;
        while (true)
        {
            var token = tokenizer.PeekTokenCore();
            if (token.Kind != TokenKind.ListOperator || token.ListOperator == null)
            {
                break;
            }

            tokenizer.NextTokenCore();
            var right = ParsePipelineCore(tokenizer);
            if (right == null)
            {
                tokenizer.RecordUnexpectedToken("Expected command after list operator.");
                return null;
            }

            current = new ListCommand(current, token.ListOperator.Value, right);
        }

        return current;
    }

    /// <summary>
    /// Parses a pipeline, including optional negation and pipe operators.
    /// Stops at the first list boundary, leaving that token available to the caller.
    /// Bodies are attached by Parse after the full grammar consumes them; the
    /// command-chain display consumer uses only words and source boundaries.
    /// </summary>
    internal static ICommand? ParsePipeline(Tokenizer tokenizer) =>
        TryParsePipeline(tokenizer, out var command, out var error) ? command : throw error.ToException();

    internal static bool TryParsePipeline(Tokenizer tokenizer, out ICommand? command,
        [NotNullWhen(false)] out SyntaxError? error)
    {
        var parsed = ParsePipelineCore(tokenizer);
        error = tokenizer.Error;
        command = error == null ? parsed : null;
        return error == null;
    }

    private static ICommand? ParsePipelineCore(Tokenizer tokenizer)
    {
        SkipNewlines(tokenizer);
        var negated = false;
        var timed = false;
        var portable = false;
        var prefixed = false;
        while (true)
        {
            var text = GetUnquotedTokenText(tokenizer.PeekTokenCore());
            if (text.SequenceEqual("!"u8))
            {
                tokenizer.NextTokenCore();
                negated = !negated;
            }
            else if (text.SequenceEqual("time"u8))
            {
                tokenizer.NextTokenCore();
                timed = true;
                if (IsKeyword(tokenizer.PeekTokenCore(), "-p"))
                {
                    tokenizer.NextTokenCore();
                    portable = true;
                }
                // Bash also selects the portable format for a bare `time --`.
                if (IsKeyword(tokenizer.PeekTokenCore(), "--"))
                {
                    tokenizer.NextTokenCore();
                    portable = true;
                }
            }
            else
                break;
            prefixed = true;
        }
        // A standalone prefix applies to an empty command, without consuming the list terminator.
        if (prefixed)
        {
            var terminator = tokenizer.PeekTokenCore().Kind;
            if (terminator is TokenKind.Newline or TokenKind.Semicolon or TokenKind.EndOfInput)
            {
                if (terminator == TokenKind.EndOfInput)
                    tokenizer.RecordEmptyPipelinePrefixEndOfInput();
                return Wrap(new SimpleCommand([], [], [], SimpleCommand.NoDeclarationAssignments));
            }
        }

        var first = ParseCommandAtom(tokenizer, afterPipe: false);
        if (first == null)
        {
            if (prefixed)
                tokenizer.RecordUnexpectedToken(negated ? "Expected command after !." : "Expected command after pipeline prefix.");
            return null;
        }

        ICommand current = first;
        while (true)
        {
            var token = tokenizer.PeekTokenCore();
            if (token.Kind != TokenKind.PipelineOperator || token.PipelineOperator == null)
            {
                break;
            }

            tokenizer.NextTokenCore();
            var right = ParseCommandAtom(tokenizer, afterPipe: true);
            if (right == null)
            {
                tokenizer.RecordUnexpectedToken("Expected command after pipeline operator.");
                return null;
            }

            current = new PipelineCommand(current, token.PipelineOperator.Value, right, false);
        }

        return Wrap(current);

        ICommand Wrap(ICommand body)
        {
            if (negated)
                body = body is PipelineCommand pipeline
                    ? pipeline with { Negated = true }
                    : new NegatedCommand(body);
            if (!timed)
                return body;

            // A timed subshell prints from inside that subshell, after its redirections
            // and with its final TIMEFORMAT. Other commands print in the enclosing shell.
            return Time(body, inherited: false);

            ICommand Time(ICommand inner, bool inherited) => inner switch
            {
                NegatedCommand negative => negative with { Inner = Time(negative.Inner, inherited) },
                RedirectedCommand { Body: SubshellCommand subshell } redirected =>
                    redirected with { Body = TimeSubshell(subshell, inherited) },
                SubshellCommand subshell => TimeSubshell(subshell, inherited),
                // Bash propagates timing flags into a subshell's sole command. Merge them
                // for evaluation while retaining each authored prefix for serialization.
                TimedCommand { Portable: false } existing when portable => existing with
                {
                    Portable = true,
                    Spelling = existing.Spelling == TimingSpelling.AsEvaluated ? TimingSpelling.Ordinary : existing.Spelling
                },
                TimedCommand existing => existing,
                _ => new TimedCommand(inner, portable)
                {
                    Spelling = inherited ? TimingSpelling.Omitted : TimingSpelling.AsEvaluated
                }
            };

            SubshellCommand TimeSubshell(SubshellCommand subshell, bool inherited)
            {
                // A prefix parsed at this subshell has already applied its timing to the body.
                // Only a portable timer can add to an existing ordinary timer.
                if (inherited && subshell.AuthoredTimingPortable is { } authoredPortable && (!portable || authoredPortable))
                    return subshell;

                return subshell with
                {
                    Body = Time(subshell.Body, inherited: true),
                    AuthoredTimingPortable = inherited ? subshell.AuthoredTimingPortable : portable
                };
            }
        }
    }

    /// <summary>
    /// Parses a single command atom: simple command, compound command, or control structure.
    /// </summary>
    private static ICommand? ParseCommandAtom(Tokenizer tokenizer, bool afterPipe)
    {
        if (tokenizer.Error != null)
            return null;
        var context = tokenizer.ScanContext;
        if (!context.TryEnterCommand())
        {
            tokenizer.RecordError("Command nesting exceeds the parsing limit.", tokenizer.Position);
            return null;
        }
        try
        {
            SkipNewlines(tokenizer);
            var token = tokenizer.PeekTokenCore();
            if (token.Kind is TokenKind.EndOfInput or TokenKind.CloseParen or TokenKind.CloseBrace)
                return null;
            var startLine = tokenizer.GetLine(token.Source.Start.Value);
            var commandLine = startLine;
            ICommand? command;
            switch (token.Kind)
            {
                case TokenKind.ArithmeticCommand:
                    tokenizer.NextTokenCore();
                    command = new ArithmeticCommand(token.CommandExpression, tokenizer.ScanContext.SyntaxMemory);
                    break;
                case TokenKind.OpenParen:
                    command = ParseSubshell(tokenizer);
                    break;
                case TokenKind.OpenBrace:
                    var groupBody = ParseBraceList(tokenizer);
                    command = groupBody == null ? null : new GroupCommand(groupBody);
                    break;
                default:
                    var keyword = ClassifyKeyword(GetUnquotedTokenText(token));
                    switch (keyword)
                    {
                        case KeywordKind.Conditional:
                            if (!tokenizer.TryReadConditionalCommand(tokenizer.NextTokenCore(), out var conditionalCommand))
                                return null;
                            command = conditionalCommand;
                            break;
                        case KeywordKind.Function:
                            command = ParseFunctionDefinition(tokenizer);
                            break;
                        case KeywordKind.Case:
                            command = ParseCase(tokenizer);
                            break;
                        case KeywordKind.If:
                            command = ParseIf(tokenizer);
                            break;
                        case KeywordKind.While:
                            command = ParseWhile(tokenizer);
                            break;
                        case KeywordKind.Until:
                            command = ParseUntil(tokenizer);
                            break;
                        case KeywordKind.For:
                            command = ParseFor(tokenizer, out var bindingLine);
                            if (bindingLine != 0)
                                commandLine = bindingLine;
                            break;
                        default:
                            if (TryParseNameFunctionDefinition(tokenizer, token, out var functionDefinition))
                            {
                                command = functionDefinition;
                                break;
                            }
                            if (keyword != KeywordKind.None && !(afterPipe && keyword == KeywordKind.Time))
                            {
                                tokenizer.RecordUnexpectedToken("Unexpected reserved word.", token, tokenizer.Position);
                                return null;
                            }
                            var simple = ParseSimpleCommand(tokenizer, startLine);
                            return tokenizer.RejectArithmeticCommandInWordPosition() ? null : simple;
                    }
                    break;
            }

            if (command == null)
            {
                return null;
            }

            tokenizer.RecordLocation(command, startLine, commandLine);

            var redirections = ReadTrailingRedirections(tokenizer);
            if (tokenizer.Error != null)
                return null;
            if (redirections == null)
                return command;
            var redirected = new RedirectedCommand(command, redirections);
            tokenizer.RecordRedirectedCommandLocation(redirected, startLine);
            return redirected;
        }
        finally
        {
            context.ExitCommand();
        }
    }

    /// <summary>
    /// Parses a subshell command enclosed in parentheses.
    /// </summary>
    private static ICommand? ParseSubshell(Tokenizer tokenizer)
    {
        var open = tokenizer.NextTokenCore();
        if (open.Kind != TokenKind.OpenParen)
        {
            tokenizer.RecordError("Expected ( to start a subshell.", tokenizer.Position);
            return null;
        }

        var result = ParseListWithTerminatorInfo(tokenizer, TokenKind.CloseParen);
        if (result.Command == null)
        {
            tokenizer.RecordUnexpectedToken("Subshell requires a list body.");
            return null;
        }

        var close = tokenizer.PeekTokenCore();
        if (close.Kind != TokenKind.CloseParen)
        {
            tokenizer.RecordUnexpectedToken("Expected ) to close a subshell.", close, tokenizer.Position);
            return null;
        }

        tokenizer.NextTokenCore();
        tokenizer.HasSubshellEndingInBarePipelinePrefix |= EndsInBarePipelinePrefix(result.Command);
        return new SubshellCommand(result.Command);
    }

    /// <summary>
    /// Parses the list enclosed in a brace group or for-loop body.
    /// </summary>
    private static ICommand? ParseBraceList(Tokenizer tokenizer)
    {
        var open = tokenizer.NextTokenCore();
        if (open.Kind != TokenKind.OpenBrace)
        {
            tokenizer.RecordError("Expected { to start a group.", tokenizer.Position);
            return null;
        }

        var result = ParseListWithTerminatorInfo(tokenizer, TokenKind.CloseBrace);
        if (result.Command == null)
        {
            tokenizer.RecordUnexpectedToken("Group requires a list body.");
            return null;
        }

        var close = tokenizer.PeekTokenCore();
        if (!result.TrailingTerminator
            && (close.Kind != TokenKind.CloseBrace || !EndsInCompoundCommand(result.Command)))
        {
            tokenizer.RecordUnexpectedToken("Group list must end with a terminator before }.", close, tokenizer.Position);
            return null;
        }

        if (close.Kind != TokenKind.CloseBrace)
        {
            tokenizer.RecordUnexpectedToken("Expected } to close a group.", close, tokenizer.Position);
            return null;
        }

        tokenizer.NextTokenCore();
        return result.Command;
    }

    /// <summary>
    /// Parses an if/elif/else/fi command structure.
    /// </summary>
    private static ICommand? ParseIf(Tokenizer tokenizer)
    {
        tokenizer.NextTokenCore();
        var condition = ParseListUntilKeyword(tokenizer, KeywordThen);
        if (condition == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordThen, "then"))
            return null;

        var thenBody = ParseListUntilKeyword(
            tokenizer,
            KeywordElif,
            KeywordElse,
            KeywordFi);
        if (thenBody == null)
            return null;

        List<ElifClause>? elifClauses = null;
        while (IsKeyword(tokenizer.PeekTokenCore(), KeywordElif))
        {
            if (!TryConsumeKeyword(tokenizer, KeywordElif, "elif"))
                return null;
            var elifCondition = ParseListUntilKeyword(tokenizer, KeywordThen);
            if (elifCondition == null)
                return null;
            if (!TryConsumeKeyword(tokenizer, KeywordThen, "then"))
                return null;
            var elifBody = ParseListUntilKeyword(
                tokenizer,
                KeywordElif,
                KeywordElse,
                KeywordFi);
            if (elifBody == null)
                return null;
            (elifClauses ??= new List<ElifClause>()).Add(new ElifClause(elifCondition, elifBody));
        }

        ICommand? elseBody = null;
        if (IsKeyword(tokenizer.PeekTokenCore(), KeywordElse))
        {
            if (!TryConsumeKeyword(tokenizer, KeywordElse, "else"))
                return null;
            elseBody = ParseListUntilKeyword(tokenizer, KeywordFi);
            if (elseBody == null)
                return null;
        }

        if (!TryConsumeKeyword(tokenizer, KeywordFi, "fi"))
            return null;
        return new IfCommand(condition, thenBody, elifClauses is null ? Array.Empty<ElifClause>() : elifClauses, elseBody);
    }

    /// <summary>
    /// Parses a while loop command.
    /// </summary>
    private static ICommand? ParseWhile(Tokenizer tokenizer)
    {
        tokenizer.NextTokenCore();
        var condition = ParseListUntilKeyword(tokenizer, KeywordDo);
        if (condition == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDo, "do"))
            return null;
        var body = ParseListUntilKeyword(tokenizer, KeywordDone);
        if (body == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDone, "done"))
            return null;
        return new WhileCommand(condition, body);
    }

    /// <summary>
    /// Parses an until loop command.
    /// </summary>
    private static ICommand? ParseUntil(Tokenizer tokenizer)
    {
        tokenizer.NextTokenCore();
        var condition = ParseListUntilKeyword(tokenizer, KeywordDo);
        if (condition == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDo, "do"))
            return null;
        var body = ParseListUntilKeyword(tokenizer, KeywordDone);
        if (body == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDone, "done"))
            return null;
        return new UntilCommand(condition, body);
    }

    /// <summary>
    /// Parses a for-in or arithmetic for loop command.
    /// </summary>
    private static ICommand? ParseFor(Tokenizer tokenizer, out int bindingLine)
    {
        bindingLine = 0;
        tokenizer.NextTokenCore();

        var nameToken = tokenizer.ReadForHeader(out var separators);
        if (nameToken.Kind == TokenKind.ArithmeticCommand)
        {
            bindingLine = tokenizer.GetLine(nameToken.Source.Start.Value);
            if (tokenizer.PeekTokenCore().Kind == TokenKind.Semicolon)
                tokenizer.NextTokenCore();
            var arithmeticBody = ParseBody(acceptsBraces: true);
            if (arithmeticBody == null)
                return null;
            if (!ArithmeticForSyntax.TryParse(nameToken.CommandExpression, separators, tokenizer.ScanContext.SyntaxMemory,
                    out var syntax, out var error))
            {
                tokenizer.RecordArithmeticForHeaderFailure(nameToken, error, separators.HasExtra);
                return null;
            }
            return new ArithmeticForCommand(syntax, arithmeticBody);
        }

        if (!tokenizer.TryGetOperandWord(nameToken, out var nameTokenWord))
        {
            tokenizer.RecordUnexpectedTokenBeforeNewline("Expected name after for.", nameToken);
            return null;
        }

        bindingLine = tokenizer.GetLine(nameToken.Source.End.Value);
        var validName = TryGetPlainUnquotedSource(nameTokenWord.Word, out var nameSource)
            && VariableNames.IsValidVariableName(nameSource.AsSpan());
        var nameValue = validName ? nameSource.ToValue() : tokenizer.ReadForNameSpelling(nameTokenWord);
        if (tokenizer.Error != null)
            return null;
        tokenizer.ScanContext.SyntaxMemory?.Grow(checked(32L + 2L * nameValue.Bytes.Length));
        nameValue.TryGetUtf8Text(out var name);

        IReadOnlyList<Word> items;
        var canUseBraceBody = tokenizer.PeekTokenCore().Kind == TokenKind.Newline;
        SkipNewlines(tokenizer);
        if (IsKeyword(tokenizer.PeekTokenCore(), KeywordIn))
        {
            tokenizer.NextTokenCore();
            List<Word>? explicitItems = null;
            while (true)
            {
                var token = tokenizer.PeekTokenCore();
                if (token.Kind is TokenKind.Newline or TokenKind.Semicolon)
                {
                    tokenizer.NextTokenCore();
                    canUseBraceBody = true;
                    break;
                }

                if (tokenizer.TryGetOperandWord(token, out var itemToken))
                {
                    (explicitItems ??= new List<Word>()).Add(itemToken.Word);
                    tokenizer.NextTokenCore();
                    continue;
                }

                tokenizer.RecordUnexpectedToken("Expected word list or terminator after in.", token, tokenizer.Position);
                return null;
            }
            items = explicitItems is null ? Array.Empty<Word>() : explicitItems;
        }
        else
        {
            // Bash's implicit list is exactly the quoted positional expansion. Lower it
            // to the existing word path, including the snapshot taken before the body.
            items = ImplicitForList.Items;
            if (!canUseBraceBody && tokenizer.PeekTokenCore().Kind == TokenKind.Semicolon)
            {
                tokenizer.NextTokenCore();
                canUseBraceBody = true;
            }
        }

        var body = ParseBody(canUseBraceBody);
        return body == null ? null : new ForCommand(name, items, body)
        {
            InvalidName = validName ? ShellValue.Empty : nameValue
        };

        ICommand? ParseBody(bool acceptsBraces)
        {
            SkipNewlines(tokenizer);
            if (acceptsBraces && tokenizer.PeekTokenCore().Kind == TokenKind.OpenBrace)
                return ParseBraceList(tokenizer);

            if (!TryConsumeKeyword(tokenizer, KeywordDo, "do"))
                return null;
            var list = ParseListUntilKeyword(tokenizer, KeywordDone);
            return list != null && TryConsumeKeyword(tokenizer, KeywordDone, "done") ? list : null;
        }
    }

    /// <summary>
    /// Parses a function definition introduced by the function keyword.
    /// </summary>
    private static ICommand? ParseFunctionDefinition(Tokenizer tokenizer)
    {
        tokenizer.NextTokenCore();

        var nameToken = tokenizer.NextTokenCore();
        if (!tokenizer.TryGetOperandWord(nameToken, out var nameTokenWord))
        {
            tokenizer.RecordUnexpectedTokenBeforeNewline("Expected function name.", nameToken);
            return null;
        }

        if (!tokenizer.TryReadFunctionName(nameTokenWord, out var name))
            return null;

        if (tokenizer.IsFollowedByFunctionParentheses(nameToken))
        {
            tokenizer.NextTokenCore();
            var close = tokenizer.NextTokenCore();
            if (close.Kind != TokenKind.CloseParen)
            {
                tokenizer.RecordUnexpectedTokenBeforeNewline("Expected ) after function name.", close);
                return null;
            }
        }

        var body = ParseFunctionBody(tokenizer);
        return body == null ? null : new FunctionDefinition(name, body);
    }

    /// <summary>
    /// Parses a case/esac command with pattern clauses.
    /// </summary>
    private static ICommand? ParseCase(Tokenizer tokenizer)
    {
        tokenizer.NextTokenCore();

        var subjectToken = tokenizer.NextTokenCore();
        if (!tokenizer.TryGetOperandWord(subjectToken, out var subjectWord))
        {
            tokenizer.RejectArithmeticCommandInWordPosition();
            tokenizer.RecordUnexpectedTokenBeforeNewline("Expected case subject word.", subjectToken);
            return null;
        }

        var subject = subjectWord.Word;
        SkipNewlines(tokenizer);
        if (!TryConsumeKeyword(tokenizer, KeywordIn, "in"))
            return null;

        List<CaseItem>? items = null;
        while (true)
        {
            SkipNewlines(tokenizer);
            if (IsKeyword(tokenizer.PeekTokenCore(), KeywordEsac))
                break;

            var hasOpeningParenthesis = tokenizer.PeekTokenCore().Kind == TokenKind.OpenParen;
            if (hasOpeningParenthesis)
                tokenizer.NextTokenCore();

            var patterns = new List<Word>();
            while (true)
            {
                var patternToken = tokenizer.NextTokenCore();
                if (!tokenizer.TryGetOperandWord(patternToken, out var patternWord))
                {
                    if (hasOpeningParenthesis || patterns.Count != 0)
                        tokenizer.RecordUnexpectedTokenBeforeNewline("Expected case pattern.", patternToken);
                    else
                        tokenizer.RecordUnexpectedToken("Expected case pattern.", patternToken, tokenizer.Position);
                    return null;
                }

                patterns.Add(patternWord.Word);
                var separator = tokenizer.PeekTokenCore();
                if (separator is { Kind: TokenKind.PipelineOperator, PipelineOperator: PipelineOperator.Pipe })
                {
                    tokenizer.NextTokenCore();
                    continue;
                }

                if (separator.Kind == TokenKind.CloseParen)
                {
                    tokenizer.NextTokenCore();
                    break;
                }

                tokenizer.RecordUnexpectedTokenBeforeNewline("Expected | or ) after case pattern.", separator);
                return null;
            }

            var (body, trailingTerminator) = ParseListWithTerminatorInfo(tokenizer, null, [KeywordEsac], TokenKind.CaseTerminator);
            var endToken = tokenizer.PeekTokenCore();
            if (endToken.Kind == TokenKind.CaseTerminator)
                tokenizer.NextTokenCore();
            else if (!IsKeyword(endToken, KeywordEsac)
                || (body != null && !trailingTerminator && !EndsInCompoundCommand(body)))
            {
                tokenizer.RecordUnexpectedToken("Expected case item terminator.", endToken, tokenizer.Position);
                return null;
            }

            // An empty matching branch succeeds without executing a command.
            var item = new CaseItem(patterns, body ?? EmptyCaseBody)
            {
                Continuation = endToken.CaseContinuation
            };
            (items ??= new List<CaseItem>()).Add(item);
        }

        if (!TryConsumeKeyword(tokenizer, KeywordEsac, "esac"))
            return null;
        return new CaseCommand(subject, items is null ? Array.Empty<CaseItem>() : items);
    }

    /// <summary>
    /// Attempts to parse a function definition introduced by name () syntax.
    /// </summary>
    private static bool TryParseNameFunctionDefinition(Tokenizer tokenizer, Token first, out ICommand? functionDefinition)
    {
        functionDefinition = null;
        if (first.Word is not { } word || !tokenizer.IsFollowedByFunctionParentheses(first))
            return false;

        // Assignment-shaped names are only valid with the function keyword.
        // Leave the raw parentheses unread so an array assignment can own them.
        if (!tokenizer.TryReadFunctionName(new WordToken(first, word), out var name)
            || name.Syntax.HasFlag(FunctionNameSyntax.KeywordOnly))
            return false;

        var token1 = tokenizer.NextTokenCore();
        var token2 = tokenizer.NextTokenCore();
        if (token2.Kind != TokenKind.OpenParen)
        {
            tokenizer.PushBack(token2);
            tokenizer.PushBack(token1);
            return false;
        }

        var token3 = tokenizer.NextTokenCore();
        if (token3.Kind == TokenKind.CloseParen)
        {
            var body = ParseFunctionBody(tokenizer);
            if (body == null)
                return false;

            functionDefinition = new FunctionDefinition(name, body);
            return true;
        }

        tokenizer.PushBack(token3);
        tokenizer.PushBack(token2);
        tokenizer.PushBack(token1);
        return false;
    }

    /// <summary>Both function headers require a compound body, including its trailing redirections.</summary>
    private static ICommand? ParseFunctionBody(Tokenizer tokenizer)
    {
        SkipNewlines(tokenizer);
        var opening = tokenizer.PeekTokenCore();
        if (opening.Kind == TokenKind.OpenBrace)
            tokenizer.RecordFunctionBrace(opening.Source.Start.Value);
        var body = ParseCommandAtom(tokenizer, afterPipe: false);
        var compound = body;
        while (compound is RedirectedCommand redirected)
            compound = redirected.Body;

        if (IsCompoundCommand(compound))
            return body;

        tokenizer.RecordUnexpectedToken("Expected compound command as function body.", opening, tokenizer.Position);
        return null;
    }

    private static bool IsCompoundCommand(ICommand? command) =>
        command is GroupCommand or SubshellCommand or IfCommand or WhileCommand or UntilCommand
            or ForCommand or ArithmeticForCommand or CaseCommand or ArithmeticCommand or ConditionalCommand;

    /// <summary>Checks whether the last command's closing token already gives the following keyword a boundary.</summary>
    private static bool EndsInCompoundCommand(ICommand command)
    {
        while (true)
        {
            var tail = command switch
            {
                ListCommand list => list.Right,
                PipelineCommand pipeline => pipeline.Right,
                NegatedCommand negated => negated.Inner,
                TimedCommand timed => timed.Inner,
                FunctionDefinition function => function.Body,
                _ => null
            };
            // A trailing redirection ends in an operand and still needs a list separator.
            if (tail == null)
                return IsCompoundCommand(command);
            command = tail;
        }
    }

    /// <summary>Reads the interior of a reached declaration initializer as a compound word list.</summary>
    internal static bool TryParseCompoundInitializer(ShellSource source, MemoryCapacity memory,
        out IReadOnlyList<CompoundArrayItem> items, [NotNullWhen(false)] out SyntaxError? error)
    {
        if (source.Length == 0)
        {
            items = [];
            error = null;
            return true;
        }
        if (source.Length > MaximumScriptBytes)
        {
            items = [];
            error = new SyntaxError(ScriptSizeLimitMessage, 0);
            return false;
        }
        var tokenizer = CreateCompoundInitializerTokenizer(source, memory, trackDiagnostics: false);
        items = ReadCompoundArrayValues(tokenizer, openingOffset: null, declaration: false);
        error = tokenizer.Error ?? tokenizer.QuotedEndOfInputFailure?.Error;
        return error == null;
    }

    /// <summary>Finds the first error in the rendered spelling of a reached initializer.</summary>
    internal static ParserFailure? ReadCompoundInitializerFailure(ShellSource source, MemoryCapacity memory, out int line)
    {
        var tokenizer = CreateCompoundInitializerTokenizer(source, memory, trackDiagnostics: true);
        ReadCompoundArrayValues(tokenizer, openingOffset: null, declaration: false);
        var failure = tokenizer.Failure;
        line = failure?.UnexpectedToken is { } token
            ? tokenizer.GetLine(token.ReadPosition) + token.LineAdvance : tokenizer.GetLine(tokenizer.Position);
        return failure;
    }

    private static Tokenizer CreateCompoundInitializerTokenizer(ShellSource source, MemoryCapacity memory, bool trackDiagnostics) =>
        new(source, 0, new ShellScanContext { SyntaxMemory = memory, LegacyOrigin = LegacySubstitutionOrigin.Temporary })
        {
            TrackCommandLocations = trackDiagnostics,
            TrackUnexpectedTokens = trackDiagnostics
        };

    private static IReadOnlyList<CompoundArrayItem> ReadCompoundArrayValues(Tokenizer tokenizer, int? openingOffset, bool declaration)
    {
        var terminator = openingOffset.HasValue ? TokenKind.CloseParen : TokenKind.EndOfInput;
        List<CompoundArrayItem>? values = null;
        while (true)
        {
            var token = openingOffset.HasValue ? tokenizer.NextCompoundArrayToken() : tokenizer.NextTokenCore();
            if (tokenizer.Error != null || tokenizer.QuotedEndOfInputFailure != null)
            {
                tokenizer.MarkCompoundArrayFailure();
                break;
            }
            if (token.Kind == terminator)
                break;
            if (token.Kind == TokenKind.EndOfInput && openingOffset is { } start)
            {
                tokenizer.RecordFailure(new ParserFailure(
                    new SyntaxError("Unexpected token in array assignment.", tokenizer.Position), ParserFailureKind.ArrayDelimiter)
                {
                    UnclosedDelimiter = new UnclosedDelimiter(')', start) { AtOpeningDelimiter = true }
                });
                break;
            }
            if (token.Kind == TokenKind.Newline)
                continue;
            if (token.Kind is not (TokenKind.Word or TokenKind.OpenBrace or TokenKind.CloseBrace))
            {
                if (openingOffset.HasValue)
                    tokenizer.RecordUnexpectedArrayToken(token);
                else
                    tokenizer.RecordError("Unexpected token in array assignment.", tokenizer.Position);
                break;
            }
            if (!tokenizer.TryGetOperandWord(token, out var wordToken))
            {
                tokenizer.RecordError("Expected word in array assignment.", tokenizer.Position);
                break;
            }
            if (!tokenizer.TryReadCompoundArrayItem(wordToken, declaration, out var item, out _))
                break;

            (values ??= []).Add(item);
        }
        return values is null ? Array.Empty<CompoundArrayItem>() : values;
    }

    /// <summary>
    /// Parses a simple command with assignments, words, and redirections.
    /// </summary>
    private static SimpleCommand? ParseSimpleCommand(Tokenizer tokenizer, int startLine)
    {
        bool TrySplitScalarAssignment(Word word, out AssignmentWord assignment)
        {
            assignment = default;
            // The tokenizer keeps the leading unquoted run in one part, including across backslash-LF.
            if (word.Parts is not [SourceWordPart { Kind: WordPartKind.Unquoted } part, ..])
                return false;
            var source = part.Source;
            var equals = source.AsSpan().IndexOf((byte)'=');
            if (equals < 0)
                return false;

            var append = equals > 0 && source[equals - 1] == (byte)'+';
            var name = source.AsSpan(0, append ? equals - 1 : equals);
            if (!VariableNames.IsValidVariableName(name))
                return false;

            var memory = tokenizer.ScanContext.SyntaxMemory;
            // Keep the retained name independent of the original assignment word's value bytes.
            memory?.Grow(MemoryCapacity.ArrayBackingBytes<byte>(name.Length));
            var target = ShellSource.FromBytes(name.ToArray());

            var operation = append ? AssignmentOperator.Append : AssignmentOperator.Replace;
            assignment = new AssignmentWord(target, word.AfterSourcePrefix(part, equals + 1, memory), operation);
            return true;
        }

        List<AssignmentWord>? assignments = null;
        List<Word>? words = null;
        Dictionary<Word, AssignmentWord>? declarationAssignments = null;
        List<Redirection>? redirections = null;
        var allowAssignments = true;
        var allowDeclarationAssignments = false;
        var sawAny = false;
        var commandLine = 0;
        var trackNextTokenLine = false;
        var redirectionDiagnosticLine = 0;
        var redirectionOpenLine = 0;

        while (true)
        {
            if (tokenizer.Error != null)
                return null;
            if (sawAny && commandLine == 0 && tokenizer.TrackCommandLocations)
                commandLine = tokenizer.GetCommandWordLine(tokenizer.ConsumedPosition);
            var peek = tokenizer.PeekTokenCore();
            if (tokenizer.Error != null)
                return null;
            switch (peek.Kind)
            {
                case TokenKind.EndOfInput:
                    return sawAny
                        ? CreateCommand(peek)
                        : null;
                case TokenKind.Newline:
                case TokenKind.Semicolon:
                    if (sawAny)
                    {
                        return CreateCommand(peek);
                    }

                    if (peek.Kind == TokenKind.Semicolon)
                    {
                        tokenizer.RecordUnexpectedToken("Unexpected command terminator.", peek, peek.Source.Start.Value);
                        return null;
                    }

                    tokenizer.NextTokenCore();
                    continue;
                case TokenKind.ListOperator:
                    if (sawAny)
                        return CreateCommand(peek);
                    tokenizer.RecordUnexpectedToken("Unexpected list operator.", peek, tokenizer.Position);
                    return null;
                case TokenKind.PipelineOperator:
                    if (sawAny)
                        return CreateCommand(peek);
                    tokenizer.RecordUnexpectedToken("Unexpected pipeline operator.", peek, tokenizer.Position);
                    return null;
                case TokenKind.CloseParen:
                    return sawAny
                        ? CreateCommand(peek)
                        : null;
                case TokenKind.CaseTerminator:
                    return sawAny
                        ? CreateCommand(peek)
                        : null;
            }

            var token = tokenizer.NextTokenCore();
            var trackRedirectionLine = trackNextTokenLine;
            switch (token.Kind)
            {
                case TokenKind.Redirection:
                    sawAny = true;
                    if (trackRedirectionLine)
                    {
                        redirectionOpenLine = tokenizer.GetLine(token.Source.Start.Value);
                        RecordNextTokenLine(tokenizer.GetRedirectionLookaheadLine(token));
                    }
                    ReadRedirection(tokenizer, token, null, string.Empty, ref redirections);
                    if (trackRedirectionLine)
                        redirectionDiagnosticLine = tokenizer.GetLine(tokenizer.ConsumedPosition);
                    continue;
                case TokenKind.Word:
                case TokenKind.OpenBrace:
                case TokenKind.CloseBrace:
                    sawAny = true;
                    if (!tokenizer.TryGetOperandWord(token, out var wordToken))
                    {
                        tokenizer.RecordError("Expected a word token.", tokenizer.Position);
                        return null;
                    }

                    if (TryGetFileDescriptor(tokenizer, wordToken, out var fileDescriptor, out var redirectionToken))
                    {
                        if (trackRedirectionLine)
                        {
                            redirectionDiagnosticLine = redirectionOpenLine = tokenizer.GetLine(token.Source.End.Value);
                            RecordNextTokenLine(tokenizer.GetRedirectionLookaheadLine(redirectionToken));
                        }
                        ReadRedirection(tokenizer, redirectionToken, fileDescriptor, string.Empty, ref redirections);
                        continue;
                    }

                    if (TryGetAllocationVariable(tokenizer, wordToken, out var variableName, out var allocationToken))
                    {
                        if (trackRedirectionLine)
                        {
                            redirectionDiagnosticLine = redirectionOpenLine = tokenizer.GetLine(token.Source.End.Value);
                            RecordNextTokenLine(tokenizer.GetRedirectionLookaheadLine(allocationToken));
                        }
                        var operandOffset = ReadRedirection(tokenizer, allocationToken, null, variableName, ref redirections);
                        if (trackRedirectionLine && operandOffset is { } offset
                            && allocationToken.RedirectionKind is RedirectionKind.Input or RedirectionKind.Output)
                            redirectionDiagnosticLine = tokenizer.GetLine(offset);
                        continue;
                    }

                    var assignment = default(AssignmentWord);
                    var assignmentPosition = allowAssignments || allowDeclarationAssignments;
                    var arrayAssignment = assignmentPosition
                        && tokenizer.TryReadArrayAssignment(wordToken, groupSubscript: allowAssignments, out assignment);
                    if (tokenizer.Error != null)
                        return null;
                    if (arrayAssignment)
                        token = token with { Source = token.Source.Start..tokenizer.Position };
                    if (assignmentPosition && (arrayAssignment || TrySplitScalarAssignment(wordToken.Word, out assignment)))
                    {
                        if (assignment.Value.Parts.Count == 0 && tokenizer.HasArrayAssignmentOpener(token))
                        {
                            var opening = tokenizer.NextTokenCore();
                            if (!tokenizer.AreAdjacent(token, opening))
                            {
                                tokenizer.RecordUnexpectedToken("An array assignment requires an adjacent parenthesis.",
                                    opening, opening.Source.Start.Value);
                                return null;
                            }

                            var arrayValues = ReadCompoundArrayValues(tokenizer, opening.Source.Start.Value, declaration: !allowAssignments);
                            if (tokenizer.Error != null)
                                return null;
                            if (tokenizer.IsAtWordBoundary())
                            {
                                tokenizer.RecordWordLookahead();
                                AddAssignment(new AssignmentWord(assignment.Target, arrayValues, assignment.Operator), wordToken.Word);
                                continue;
                            }

                            var suffix = tokenizer.NextTokenCore();
                            if (!tokenizer.TryGetOperandWord(suffix, out var suffixWord) || !tokenizer.IsAtWordBoundary())
                            {
                                tokenizer.RecordError("Unexpected parenthesized assignment suffix.", suffix.Source.Start.Value);
                                return null;
                            }

                            // Bash joins parsed items with one space; quoted whitespace remains in each word.
                            var parts = new List<WordPart> { WordPart.FromText(WordPartKind.Unquoted, "(") };
                            for (var index = 0; index < arrayValues.Count; index++)
                            {
                                if (index > 0)
                                    parts.Add(WordPart.FromText(WordPartKind.Unquoted, " "));
                                parts.AddRange(arrayValues[index].Word.Parts);
                            }
                            parts.Add(WordPart.FromText(WordPartKind.Unquoted, ")"));
                            parts.AddRange(suffixWord.Word.Parts);
                            // Scalar operands keep their complete ordinary word as well as assignment metadata.
                            var wholeWord = allowAssignments ? wordToken.Word : new Word([.. wordToken.Word.Parts, .. parts]);
                            AddAssignment(new AssignmentWord(assignment.Target, new Word(parts), assignment.Operator), wholeWord);
                            continue;
                        }

                        AddAssignment(assignment, wordToken.Word);
                        continue;
                    }

                    if (trackNextTokenLine)
                        RecordNextTokenLine(tokenizer.GetCommandWordLine(tokenizer.ConsumedPosition));
                    if (words == null)
                    {
                        // An initial word has a grammar lookahead for a possible function definition.
                        trackNextTokenLine = tokenizer.TrackCommandLocations && assignments == null && redirections == null;
                        if (TryGetPlainUnquotedSource(wordToken.Word, out var commandName))
                            allowDeclarationAssignments = BuiltinClassification.TryResolve(commandName.AsSpan(), out var kind)
                                && kind is BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local;
                    }
                    allowAssignments = false;
                    (words ??= []).Add(wordToken.Word);
                    continue;
                default:
                    const string message = "Unexpected token while parsing a simple command.";
                    if (token.Kind == TokenKind.OpenParen)
                    {
                        // A lone command word can start a function header. Read the token expected to close it.
                        if (tokenizer.TrackUnexpectedTokens && words?.Count == 1 && assignments == null && redirections == null)
                            tokenizer.RecordUnexpectedFunctionHeaderToken(message);
                        else
                            tokenizer.RecordUnexpectedToken(message, token, tokenizer.Position);
                    }
                    else if (token.Kind == TokenKind.ArithmeticCommand)
                        tokenizer.RecordUnexpectedToken(message, token, tokenizer.Position);
                    else
                        tokenizer.RecordError(message, tokenizer.Position);
                    return null;
            }
        }

        void RecordNextTokenLine(int line)
        {
            commandLine = line;
            trackNextTokenLine = false;
        }

        SimpleCommand CreateCommand(Token trailing)
        {
            if (trackNextTokenLine)
            {
                var position = trailing.Kind == TokenKind.Newline ? trailing.Source.Start.Value : trailing.Source.End.Value;
                if (position >= tokenizer.ConsumedPosition)
                    RecordNextTokenLine(tokenizer.GetCommandTerminatorLine(trailing));
            }
            var command = new SimpleCommand(
                assignments is null ? Array.Empty<AssignmentWord>() : assignments,
                words is null ? Array.Empty<Word>() : words,
                redirections is null ? Array.Empty<Redirection>() : redirections,
                declarationAssignments ?? SimpleCommand.NoDeclarationAssignments);
            tokenizer.RecordLocation(command, startLine, commandLine, redirectionDiagnosticLine, redirectionOpenLine);
            return command;
        }

        void AddAssignment(AssignmentWord assignment, Word word)
        {
            if (trackNextTokenLine)
                RecordNextTokenLine(tokenizer.GetCommandWordLine(tokenizer.ConsumedPosition));
            if (allowAssignments)
            {
                (assignments ??= []).Add(assignment);
                return;
            }

            // Word identity survives command-field forwarding; numeric argument indices do not.
            (words ??= []).Add(word);
            (declarationAssignments ??= new Dictionary<Word, AssignmentWord>()).Add(word, assignment);
        }
    }

    private static bool TryGetFileDescriptor(
        Tokenizer tokenizer,
        WordToken token,
        out int? fileDescriptor,
        out Token redirectionToken)
    {
        fileDescriptor = null;
        redirectionToken = Token.None();

        if (!TryGetPlainUnquotedSource(token.Word, out var wordText))
            return false;

        if (wordText.IsEmpty || wordText.AsSpan().IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0)
            return false;

        var peek = tokenizer.PeekTokenCore();
        // An out-of-range number remains a word. Consume the operator only for a valid prefix.
        if (peek.Kind != TokenKind.Redirection || !tokenizer.AreAdjacent(token.Token, peek)
            || !Integers.TryParseExitStatus(wordText.AsSpan(), out var descriptor))
            return false;

        redirectionToken = tokenizer.NextTokenCore();
        fileDescriptor = descriptor;
        return true;
    }

    private static bool TryGetAllocationVariable(
        Tokenizer tokenizer,
        WordToken token,
        out string variableName,
        out Token redirectionToken)
    {
        variableName = string.Empty;
        redirectionToken = Token.None();

        if (!TryGetPlainUnquotedSource(token.Word, out var text))
            return false;

        var span = text.AsSpan();
        if (span.Length < 3 || span[0] != '{' || span[^1] != '}')
            return false;

        var inner = text.Slice(1, span.Length - 2);
        if (!VariableNames.IsValidVariableName(inner.AsSpan()))
            return false;

        var peek = tokenizer.PeekTokenCore();
        if (peek.Kind != TokenKind.Redirection || !tokenizer.AreAdjacent(token.Token, peek))
            return false;

        if (peek.RedirectionKind is not (RedirectionKind.Input or RedirectionKind.Output
            or RedirectionKind.OutputAppend or RedirectionKind.InputOutput or RedirectionKind.ForceOutput
            or RedirectionKind.DuplicateInput or RedirectionKind.DuplicateOutput
            or RedirectionKind.HereString or RedirectionKind.HereDocument or RedirectionKind.HereDocumentStripTabs))
            return false;

        redirectionToken = tokenizer.NextTokenCore();
        tokenizer.ScanContext.SyntaxMemory?.Grow(checked(32L + 2L * inner.Length));
        variableName = inner.ToUtf8String();
        return true;
    }

    /// <summary>Reads a redirection and returns the source offset of its operand on success.</summary>
    private static int? ReadRedirection(
        Tokenizer tokenizer,
        Token token,
        int? fileDescriptor,
        string variableName,
        ref List<Redirection>? redirections)
    {
        if (token.RedirectionKind == null)
        {
            tokenizer.RecordError("Redirection token missing kind.", tokenizer.Position);
            return null;
        }

        var kind = token.RedirectionKind.Value;
        if (variableName.Length != 0)
            kind = kind switch
            {
                RedirectionKind.Input => RedirectionKind.AllocateInput,
                RedirectionKind.Output => RedirectionKind.AllocateOutput,
                _ => kind
            };
        var hereDocument = kind is RedirectionKind.HereDocument or RedirectionKind.HereDocumentStripTabs;
        if (!tokenizer.TryReadRedirectionWord(hereDocument, out var target, out var targetWord, out var delimiter))
        {
            tokenizer.RecordMissingRedirectionOperand(token, target);
            return null;
        }
        redirections ??= [];
        if (hereDocument)
        {
            var redirection = new Redirection(kind, fileDescriptor, targetWord.Word, variableName, hereDocumentDelimiter: null)
            {
                DelimiterSyntax = delimiter
            };
            redirections.Add(redirection);
            (tokenizer.HereDocuments ??= new HereDocumentReader()).Enqueue(redirection);
            return target.Source.Start.Value;
        }

        if (kind is RedirectionKind.DuplicateOutput or RedirectionKind.DuplicateInput
            && targetWord.Word.Parts is [SourceWordPart { Kind: WordPartKind.Unquoted } first, ..]
            && first.Source.Length > 0 && first.Source[0] == (byte)'-')
        {
            // The source '-' is a close operand; subsequent source remains an ordinary
            // argument, even without whitespace (>&-3). Quoted/expanded '-' stays a word.
            if (first.Source.Length > 1 || targetWord.Word.Parts.Count > 1)
            {
                var remaining = new List<WordPart>();
                if (first.Source.Length > 1)
                    remaining.Add(first.Slice(1, first.Source.Length - 1, tokenizer.ScanContext.SyntaxMemory));
                for (var index = 1; index < targetWord.Word.Parts.Count; index++)
                    remaining.Add(targetWord.Word.Parts[index]);
                tokenizer.PushBack(Token.FromWord(new Word(remaining)) with
                {
                    Source = (target.Source.Start.Value + 1)..target.Source.End.Value
                });
            }
            else
                tokenizer.RecordDescriptorCloseOperand();
            kind = kind == RedirectionKind.DuplicateOutput
                ? RedirectionKind.CloseOutput
                : RedirectionKind.CloseInput;
            redirections.Add(new Redirection(kind, fileDescriptor,
                new Word([WordPart.FromText(WordPartKind.Unquoted, "-")]), variableName, hereDocumentDelimiter: null));
            return target.Source.Start.Value;
        }

        redirections.Add(new Redirection(kind, fileDescriptor, targetWord.Word, variableName,
            hereDocumentDelimiter: null, tokenizer.ScanContext.SyntaxMemory));
        return target.Source.Start.Value;
    }

    private static List<Redirection>? ReadTrailingRedirections(Tokenizer tokenizer)
    {
        List<Redirection>? redirections = null;

        while (true)
        {
            var peek = tokenizer.PeekTokenCore();
            switch (peek.Kind)
            {
                case TokenKind.Redirection:
                    tokenizer.NextTokenCore();
                    ReadRedirection(tokenizer, peek, null, string.Empty, ref redirections);
                    continue;
                case TokenKind.Word:
                    var token = tokenizer.NextTokenCore();
                    if (!token.TryGetWordToken(out var wordToken))
                    {
                        tokenizer.RecordError("Expected a word token.", tokenizer.Position);
                        return redirections;
                    }

                    if (TryGetFileDescriptor(tokenizer, wordToken, out var fileDescriptor, out var redirectionToken))
                    {
                        ReadRedirection(tokenizer, redirectionToken, fileDescriptor, string.Empty, ref redirections);
                        continue;
                    }

                    if (TryGetAllocationVariable(tokenizer, wordToken, out var variableName, out var allocationToken))
                    {
                        ReadRedirection(tokenizer, allocationToken, null, variableName, ref redirections);
                        continue;
                    }

                    tokenizer.PushBack(token);
                    return redirections;
                default:
                    return redirections;
            }
        }
    }

    /// <summary>
    /// Borrows source bytes when a word is a single unquoted part.
    /// </summary>
    internal static bool TryGetPlainUnquotedSource(Word word, out ShellSource source)
    {
        if (word.Parts is [{ Kind: WordPartKind.Unquoted }])
        {
            source = ShellSource.FromBytes(word.Parts[0].GetUnexpandedValue().Bytes);
            return true;
        }

        source = default;
        return false;
    }

    /// <summary>
    /// Borrows unquoted token text for reserved-word comparisons.
    /// </summary>
    private static ReadOnlySpan<byte> GetUnquotedTokenText(Token token) =>
        token.Word is { } word && TryGetPlainUnquotedSource(word, out var source) ? source.AsSpan() : [];

    private static ICommand? ParseListUntilKeyword(Tokenizer tokenizer, string keyword)
    {
        return ParseListUntilKeyword(tokenizer, [keyword]);
    }

    private static ICommand? ParseListUntilKeyword(
        Tokenizer tokenizer,
        string keyword1,
        string keyword2,
        string keyword3)
    {
        return ParseListUntilKeyword(tokenizer, [keyword1, keyword2, keyword3]);
    }

    private static ICommand? ParseListUntilKeyword(Tokenizer tokenizer, string[] keywords)
    {
        var result = ParseListWithTerminatorInfo(tokenizer, null, keywords, null);
        if (result.Command == null)
        {
            tokenizer.RecordUnexpectedToken("Expected list before reserved word.");
            return null;
        }

        if (!result.TrailingTerminator
            && (!IsEndKeyword(tokenizer, keywords) || !EndsInCompoundCommand(result.Command)))
        {
            tokenizer.RecordUnexpectedToken("Expected terminator before reserved word.");
            return null;
        }

        return result.Command;
    }

    /// <summary>
    /// Consumes a required reserved keyword or records its syntax failure.
    /// </summary>
    private static bool TryConsumeKeyword(Tokenizer tokenizer, string keyword, string name)
    {
        var token = tokenizer.PeekTokenCore();
        if (!IsKeyword(token, keyword))
        {
            tokenizer.RecordUnexpectedToken($"Expected {name} keyword.", token, tokenizer.Position);
            return false;
        }

        tokenizer.NextTokenCore();
        return tokenizer.Error == null;
    }

    /// <summary>
    /// Returns true when a token matches a specific reserved keyword.
    /// </summary>
    private static bool IsKeyword(Token token, string keyword) =>
        token.Word is { } word && IsKeyword(word, keyword);

    internal static bool IsKeyword(Word word, string keyword) =>
        TryGetPlainUnquotedSource(word, out var source) && AsciiByte.EqualsAscii(source.AsSpan(), keyword);


    /// <summary>
    /// Parses a list and reports whether it ended with a terminator.
    /// </summary>
    private static (ICommand? Command, bool TrailingTerminator) ParseListWithTerminatorInfo(
        Tokenizer tokenizer,
        TokenKind? closingKind)
    {
        return ParseListWithTerminatorInfo(tokenizer, closingKind, [], null);
    }

    /// <summary>
    /// Parses a list until a closing token, end keyword, or token kind is reached.
    /// </summary>
    private static (ICommand? Command, bool TrailingTerminator) ParseListWithTerminatorInfo(
        Tokenizer tokenizer,
        TokenKind? closingKind,
        string[] endKeywords,
        TokenKind? endTokenKind) =>
        ParseListWithTerminatorInfo(tokenizer, closingKind, endKeywords, endTokenKind, CommandUnitMetadata.None);

    private enum CommandUnitMetadata { None, HereDocumentLocations, All }

    private static (ICommand? Command, bool TrailingTerminator) ParseListWithTerminatorInfo(
        Tokenizer tokenizer,
        TokenKind? closingKind,
        string[] endKeywords,
        TokenKind? endTokenKind,
        CommandUnitMetadata unitMetadata) =>
        ParseListWithTerminatorInfo(tokenizer, closingKind, endKeywords, endTokenKind, unitMetadata, out _);

    private static (ICommand? Command, bool TrailingTerminator) ParseListWithTerminatorInfo(
        Tokenizer tokenizer,
        TokenKind? closingKind,
        string[] endKeywords,
        TokenKind? endTokenKind,
        CommandUnitMetadata unitMetadata,
        out int lastCommandEnd)
    {
        lastCommandEnd = 0;
        SkipNewlines(tokenizer);
        if (IsEndKeyword(tokenizer, endKeywords) || IsEndTokenKind(tokenizer, endTokenKind))
            return (null, false);

        var left = ParseAndOrList(tokenizer);
        if (left == null)
            return (null, false);

        lastCommandEnd = tokenizer.ConsumedPosition;
        ICommand current = left;
        var unitStart = left;
        var trailingTerminator = false;
        while (true)
        {
            var token = tokenizer.PeekTokenCore();
            if (closingKind != null && token.Kind == closingKind.Value)
                break;

            if (endTokenKind != null && token.Kind == endTokenKind.Value)
                break;

            if (token.Kind is not TokenKind.Newline and not TokenKind.Semicolon)
                break;

            tokenizer.NextTokenCore();
            var endsCommandUnit = unitMetadata != CommandUnitMetadata.None
                && (token.Kind == TokenKind.Newline || tokenizer.PeekTokenCore().Kind == TokenKind.Newline);
            if (endsCommandUnit)
                tokenizer.CompleteCompoundHereDocumentLocations();
            var startsCommandUnit = endsCommandUnit && unitMetadata == CommandUnitMetadata.All;
            if (startsCommandUnit)
                SetUnitDiagnostics(unitStart);
            if (!IsEndTokenKind(tokenizer, endTokenKind))
                SkipNewlines(tokenizer);
            trailingTerminator = true;
            if (IsEndKeyword(tokenizer, endKeywords) || IsEndTokenKind(tokenizer, endTokenKind))
            {
                break;
            }

            var separator = token.Kind == TokenKind.Semicolon && tokenizer.ScanContext.RetainCaptureLookahead
                ? token.Source.Start..tokenizer.PeekTokenCore().Source.Start : default;
            var right = ParseAndOrList(tokenizer);
            if (right == null)
            {
                break;
            }
            lastCommandEnd = tokenizer.ConsumedPosition;

            if (startsCommandUnit)
                unitStart = right;

            current = new ListCommand(current, ListOperator.Sequence, right)
            {
                StartsCommandUnit = startsCommandUnit,
                HasNewlineConnector = token.Kind == TokenKind.Newline,
                SeparatorSource = separator
            };
            trailingTerminator = false;
        }

        if (unitMetadata == CommandUnitMetadata.All)
            SetUnitDiagnostics(unitStart);
        return (current, trailingTerminator);

        void SetUnitDiagnostics(ICommand anchor)
        {
            var diagnostics = new CommandReadDiagnostics(
                tokenizer.ScanContext.TakeHereDocumentWarnings(tokenizer.HereDocuments?.TakeWarnings()),
                tokenizer.ScanContext.TakeCompoundItemDiagnostics()) { FunctionBraceLine = tokenizer.TakeFunctionBraceLine() };
            if (diagnostics.IsEmpty)
                return;
            // These messages precede the whole unit, including commands before a semicolon.
            while (anchor is ListCommand list)
                anchor = list.Left;
            while (anchor is NegatedCommand negated)
                anchor = negated.Inner;
            tokenizer.RecordReadDiagnostics(anchor, diagnostics);
        }
    }

    /// <summary>
    /// Returns true when the next token is one of the end keywords.
    /// </summary>
    private static bool IsEndKeyword(Tokenizer tokenizer, string[] endKeywords)
    {
        if (endKeywords.Length == 0)
            return false;

        var token = tokenizer.PeekTokenCore();
        return endKeywords.Any(keyword => IsKeyword(token, keyword));
    }

    /// <summary>
    /// Skips newlines where the grammar permits them without accepting empty semicolon commands.
    /// </summary>
    private static void SkipNewlines(Tokenizer tokenizer)
    {
        while (tokenizer.PeekTokenCore().Kind == TokenKind.Newline)
            tokenizer.NextTokenCore();
    }

    /// <summary>
    /// Returns true when the next token matches a required end token kind.
    /// </summary>
    private static bool IsEndTokenKind(Tokenizer tokenizer, TokenKind? endTokenKind) =>
        endTokenKind != null && tokenizer.PeekTokenCore().Kind == endTokenKind.Value;
}

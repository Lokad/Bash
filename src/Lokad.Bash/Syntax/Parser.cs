using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;

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

    /// <summary> Returns true when a word is a reserved keyword supported by this parser. </summary>
    internal static bool IsSupportedKeyword(ReadOnlySpan<char> word) =>
        word.SequenceEqual(KeywordIf)
        || word.SequenceEqual(KeywordThen)
        || word.SequenceEqual(KeywordElif)
        || word.SequenceEqual(KeywordElse)
        || word.SequenceEqual(KeywordFi)
        || word.SequenceEqual(KeywordWhile)
        || word.SequenceEqual(KeywordUntil)
        || word.SequenceEqual(KeywordDo)
        || word.SequenceEqual(KeywordDone)
        || word.SequenceEqual(KeywordFor)
        || word.SequenceEqual(KeywordIn)
        || word.SequenceEqual(KeywordFunction)
        || word.SequenceEqual(KeywordCase)
        || word.SequenceEqual(KeywordEsac)
        || word.SequenceEqual("time")
        || word.SequenceEqual("!")
        || word.SequenceEqual("{")
        || word.SequenceEqual("}")
        || word.SequenceEqual("[[")
        || word.SequenceEqual("]]");

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
        if (input.Length > MaximumScriptBytes || Encoding.UTF8.GetByteCount(input) > MaximumScriptBytes)
        {
            error = new SyntaxError(ScriptSizeLimitMessage, 0);
            return false;
        }

        var source = ShellSource.FromText(input);
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

    /// <summary>Parses borrowed source bytes without throwing for invalid syntax or parsing limits.</summary>
    /// <remarks>Retains input storage; do not mutate it while the parsed syntax is in use.</remarks>
    public static bool TryParse(ReadOnlyMemory<byte> input,
        [NotNullWhen(true)] out ParsedScript? script, [NotNullWhen(false)] out SyntaxError? error)
    {
        script = null;
        if (input.Length > MaximumScriptBytes)
        {
            error = new SyntaxError(ScriptSizeLimitMessage, 0);
            return false;
        }
        var tokenizer = new Tokenizer(ShellSource.FromBytes(input));
        SkipNewlines(tokenizer);
        var command = ParseList(tokenizer);
        SkipNewlines(tokenizer);
        var trailing = tokenizer.PeekTokenCore();
        if (trailing.Kind != TokenKind.EndOfInput)
            tokenizer.RecordError("Unexpected token after command list.", trailing.Source.Start.Value);

        error = tokenizer.Error;
        if (error != null)
            return false;
        script = command == null ? new ParsedScript([]) : new ParsedScript([tokenizer.HereDocuments?.Complete(command) ?? command]);
        return true;
    }

    /// <summary>
    /// Lazily returns complete top-level command units or a terminal syntax failure.
    /// Later syntax is checked only when requested; failures retain absolute byte offsets.
    /// </summary>
    internal static IEnumerable<CommandUnitParseResult> TryParseCommandUnits(ReadOnlyMemory<byte> input)
    {
        if (input.Length > MaximumScriptBytes)
        {
            yield return new CommandUnitParseResult.Failed(new SyntaxError(ScriptSizeLimitMessage, 0));
            yield break;
        }

        var tokenizer = new Tokenizer(ShellSource.FromBytes(input));
        while (true)
        {
            SkipNewlines(tokenizer);
            var (command, _) = ParseListWithTerminatorInfo(tokenizer, null, [], TokenKind.Newline);
            var trailing = tokenizer.PeekTokenCore();
            if (trailing.Kind is not TokenKind.Newline and not TokenKind.EndOfInput)
                tokenizer.RecordError("Unexpected token after command list.", trailing.Source.Start.Value);

            if (tokenizer.Error is { } error)
            {
                yield return new CommandUnitParseResult.Failed(error);
                yield break;
            }
            if (command == null)
                yield break;

            var completed = tokenizer.HereDocuments?.Complete(command) ?? command;
            tokenizer.HereDocuments = null;
            if (trailing.Kind == TokenKind.Newline)
                tokenizer.NextTokenCore();
            yield return new CommandUnitParseResult.Parsed(completed);
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
        out ParsedScript? parsed)
    {
        parsed = null;
        closingParenthesisIndex = payloadStart;
        if (payloadStart < 0 || payloadStart > input.Length)
            return ShellScanStatus.NotFound;

        var tokenizer = new Tokenizer(input, payloadStart, context);
        SkipNewlines(tokenizer);
        var (command, _) = ParseListWithTerminatorInfo(tokenizer, TokenKind.CloseParen);
        SkipNewlines(tokenizer);
        if (tokenizer.Error is { } error)
        {
            if (context.ParsingLimitExceeded || error.Offset < input.Length || tokenizer.HasPendingClosingParenthesis)
                return ShellScanStatus.Invalid;

            return ShellScanStatus.Incomplete;
        }

        var closing = tokenizer.PeekTokenCore();
        if (closing.Kind != TokenKind.CloseParen)
            return closing.Kind == TokenKind.EndOfInput ? ShellScanStatus.Incomplete : ShellScanStatus.Invalid;
        tokenizer.NextTokenCore();
        closingParenthesisIndex = closing.Source.Start.Value;
        // An unconsumed body historically fails when the substitution executes, not during lookahead.
        if (tokenizer.HereDocuments is not { HasPending: true })
            parsed = command == null ? new ParsedScript([])
                : new ParsedScript([tokenizer.HereDocuments?.Complete(command) ?? command]);
        return ShellScanStatus.Complete;
    }

    /// <summary>
    /// Parses a list of commands, returning null for empty input.
    /// </summary>
    private static ICommand? ParseList(Tokenizer tokenizer)
    {
        var result = ParseListWithTerminatorInfo(tokenizer, null);
        return result.Command;
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
                tokenizer.RecordError("Expected command after list operator.", tokenizer.Position);
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
            if (TryConsumeNegation(tokenizer))
                negated = !negated;
            else if (IsKeyword(tokenizer.PeekTokenCore(), "time"))
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
        if (prefixed
            && tokenizer.PeekTokenCore().Kind is TokenKind.Newline or TokenKind.Semicolon or TokenKind.EndOfInput)
            return Wrap(new SimpleCommand([], [], [], SimpleCommand.NoDeclarationAssignments));

        var first = ParseCommandAtom(tokenizer, afterPipe: false);
        if (first == null)
        {
            if (negated)
            {
                tokenizer.RecordError("Expected command after !.", tokenizer.Position);
                return null;
            }

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
                tokenizer.RecordError("Expected command after pipeline operator.", tokenizer.Position);
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
            return Time(body);

            ICommand Time(ICommand inner) => inner switch
            {
                NegatedCommand negative => negative with { Inner = Time(negative.Inner) },
                RedirectedCommand { Body: SubshellCommand subshell } redirected =>
                    redirected with { Body = subshell with { Body = Time(subshell.Body) } },
                SubshellCommand subshell => subshell with { Body = Time(subshell.Body) },
                // Bash propagates timing flags into a subshell's sole command. Merge them
                // with an existing timer, but keep separate timers inside lists and groups.
                TimedCommand existing => existing with { Portable = existing.Portable || portable },
                _ => new TimedCommand(inner, portable)
            };
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
            ICommand? command;
            switch (token.Kind)
            {
                case TokenKind.ArithmeticCommand:
                    tokenizer.NextTokenCore();
                    command = new ArithmeticCommand(token.CommandExpression);
                    break;
                case TokenKind.Word when IsKeyword(token, "[["):
                    // Reserved words introduce syntax only at a command boundary.
                    if (!tokenizer.TryReadConditionalExpression(tokenizer.NextTokenCore(), out var expression))
                        return null;
                    if (!ConditionalParser.TryParse(expression, out var condition, out var conditionalError))
                    {
                        tokenizer.RecordError(conditionalError.Message, token.Source.End.Value + conditionalError.Offset);
                        return null;
                    }
                    command = new ConditionalCommand(expression, condition);
                    break;
                case TokenKind.OpenParen:
                    command = ParseSubshell(tokenizer);
                    break;
                case TokenKind.OpenBrace:
                    command = ParseGroup(tokenizer);
                    break;
                case TokenKind.CloseParen:
                case TokenKind.CloseBrace:
                    return null;
                default:
                    if (IsKeyword(token, KeywordFunction))
                    {
                        command = ParseFunctionDefinition(tokenizer);
                        break;
                    }

                    if (IsKeyword(token, KeywordCase))
                    {
                        command = ParseCase(tokenizer);
                        break;
                    }

                    if (IsKeyword(token, KeywordIf))
                    {
                        command = ParseIf(tokenizer);
                        break;
                    }

                    if (IsKeyword(token, KeywordWhile))
                    {
                        command = ParseWhile(tokenizer);
                        break;
                    }

                    if (IsKeyword(token, KeywordUntil))
                    {
                        command = ParseUntil(tokenizer);
                        break;
                    }

                    if (IsKeyword(token, KeywordFor))
                    {
                        command = ParseFor(tokenizer);
                        break;
                    }

                    if (TryParseNameFunctionDefinition(tokenizer, out var functionDefinition))
                    {
                        command = functionDefinition;
                        break;
                    }

                    if (token.TryGetWordToken(out var reservedWord)
                        && TryGetPlainUnquotedText(reservedWord.Word, out var reservedText)
                        && IsSupportedKeyword(reservedText.AsSpan())
                        && !(afterPipe && reservedText == "time"))
                    {
                        tokenizer.RecordError("Unexpected reserved word.", tokenizer.Position);
                        return null;
                    }

                    return ParseSimpleCommand(tokenizer);
            }

            if (command == null)
            {
                return null;
            }

            if (command is SimpleCommand)
            {
                return command;
            }

            var redirections = ReadTrailingRedirections(tokenizer);
            if (tokenizer.Error != null)
                return null;
            return redirections.Count > 0 ? new RedirectedCommand(command, redirections) : command;
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
            tokenizer.RecordError("Subshell requires a list body.", tokenizer.Position);
            return null;
        }

        var close = tokenizer.PeekTokenCore();
        if (close.Kind != TokenKind.CloseParen)
        {
            tokenizer.RecordError("Expected ) to close a subshell.", tokenizer.Position);
            return null;
        }

        tokenizer.NextTokenCore();
        return new SubshellCommand(result.Command);
    }

    /// <summary>
    /// Parses a grouped command enclosed in braces.
    /// </summary>
    private static ICommand? ParseGroup(Tokenizer tokenizer)
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
            tokenizer.RecordError("Group requires a list body.", tokenizer.Position);
            return null;
        }

        if (!result.TrailingTerminator)
        {
            tokenizer.RecordError("Group list must end with a terminator before }.", tokenizer.Position);
            return null;
        }

        var close = tokenizer.PeekTokenCore();
        if (close.Kind != TokenKind.CloseBrace)
        {
            tokenizer.RecordError("Expected } to close a group.", tokenizer.Position);
            return null;
        }

        tokenizer.NextTokenCore();
        return new GroupCommand(result.Command);
    }

    /// <summary>
    /// Parses an if/elif/else/fi command structure.
    /// </summary>
    private static ICommand? ParseIf(Tokenizer tokenizer)
    {
        if (!TryConsumeKeyword(tokenizer, KeywordIf, "if"))
            return null;
        var condition = ParseListUntilKeyword(tokenizer, KeywordThen, requireTerminator: true);
        if (condition == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordThen, "then"))
            return null;

        var thenBody = ParseListUntilKeyword(
            tokenizer,
            KeywordElif,
            KeywordElse,
            KeywordFi,
            requireTerminator: true);
        if (thenBody == null)
            return null;

        var elifClauses = new List<ElifClause>();
        while (IsKeyword(tokenizer.PeekTokenCore(), KeywordElif))
        {
            if (!TryConsumeKeyword(tokenizer, KeywordElif, "elif"))
                return null;
            var elifCondition = ParseListUntilKeyword(tokenizer, KeywordThen, requireTerminator: true);
            if (elifCondition == null)
                return null;
            if (!TryConsumeKeyword(tokenizer, KeywordThen, "then"))
                return null;
            var elifBody = ParseListUntilKeyword(
                tokenizer,
                KeywordElif,
                KeywordElse,
                KeywordFi,
                requireTerminator: true);
            if (elifBody == null)
                return null;
            elifClauses.Add(new ElifClause(elifCondition, elifBody));
        }

        ICommand? elseBody = null;
        if (IsKeyword(tokenizer.PeekTokenCore(), KeywordElse))
        {
            if (!TryConsumeKeyword(tokenizer, KeywordElse, "else"))
                return null;
            elseBody = ParseListUntilKeyword(tokenizer, KeywordFi, requireTerminator: true);
            if (elseBody == null)
                return null;
        }

        if (!TryConsumeKeyword(tokenizer, KeywordFi, "fi"))
            return null;
        return new IfCommand(condition, thenBody, elifClauses, elseBody);
    }

    /// <summary>
    /// Parses a while loop command.
    /// </summary>
    private static ICommand? ParseWhile(Tokenizer tokenizer)
    {
        if (!TryConsumeKeyword(tokenizer, KeywordWhile, "while"))
            return null;
        var condition = ParseListUntilKeyword(tokenizer, KeywordDo, requireTerminator: true);
        if (condition == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDo, "do"))
            return null;
        var body = ParseListUntilKeyword(tokenizer, KeywordDone, requireTerminator: true);
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
        if (!TryConsumeKeyword(tokenizer, KeywordUntil, "until"))
            return null;
        var condition = ParseListUntilKeyword(tokenizer, KeywordDo, requireTerminator: true);
        if (condition == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDo, "do"))
            return null;
        var body = ParseListUntilKeyword(tokenizer, KeywordDone, requireTerminator: true);
        if (body == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDone, "done"))
            return null;
        return new UntilCommand(condition, body);
    }

    /// <summary>
    /// Parses a for-in or arithmetic for loop command.
    /// </summary>
    private static ICommand? ParseFor(Tokenizer tokenizer)
    {
        if (!TryConsumeKeyword(tokenizer, KeywordFor, "for"))
            return null;

        var next = tokenizer.PeekTokenCore();
        if (next.Kind == TokenKind.ArithmeticCommand)
        {
            tokenizer.NextTokenCore();
            if (tokenizer.PeekTokenCore().Kind == TokenKind.Semicolon)
                tokenizer.NextTokenCore();
            SkipNewlines(tokenizer);
            if (!TryConsumeKeyword(tokenizer, KeywordDo, "do"))
                return null;
            var arithmeticBody = ParseListUntilKeyword(tokenizer, KeywordDone, requireTerminator: true);
            if (arithmeticBody == null)
                return null;
            if (!TryConsumeKeyword(tokenizer, KeywordDone, "done"))
                return null;
            return new ArithmeticForCommand(next.CommandExpression, arithmeticBody);
        }

        var nameToken = tokenizer.NextTokenCore();
        if (!nameToken.TryGetWordToken(out var nameTokenWord))
        {
            tokenizer.RecordError("Expected name after for.", tokenizer.Position);
            return null;
        }

        if (!TryGetPlainUnquotedText(nameTokenWord.Word, out var name))
        {
            tokenizer.RecordError("Expected simple name after for.", tokenizer.Position);
            return null;
        }

        if (!VariableNames.IsValidVariableName(name.AsSpan()))
        {
            tokenizer.RecordError("Invalid for loop variable name.", tokenizer.Position);
            return null;
        }

        if (!TryConsumeKeyword(tokenizer, KeywordIn, "in"))

            return null;

        var items = new List<Word>();
        while (true)
        {
            var token = tokenizer.PeekTokenCore();
            if (token.Kind is TokenKind.Newline or TokenKind.Semicolon)
            {
                tokenizer.NextTokenCore();
                break;
            }

            if (token.TryGetWordToken(out var itemToken))
            {
                items.Add(itemToken.Word);
                tokenizer.NextTokenCore();
                continue;
            }

            tokenizer.RecordError("Expected word list or terminator after in.", tokenizer.Position);
            return null;
        }

        SkipNewlines(tokenizer);
        if (!TryConsumeKeyword(tokenizer, KeywordDo, "do"))
            return null;
        var body = ParseListUntilKeyword(tokenizer, KeywordDone, requireTerminator: true);
        if (body == null)
            return null;
        if (!TryConsumeKeyword(tokenizer, KeywordDone, "done"))
            return null;
        return new ForCommand(name, items, body);
    }

    /// <summary>
    /// Parses a function definition introduced by the function keyword.
    /// </summary>
    private static ICommand? ParseFunctionDefinition(Tokenizer tokenizer)
    {
        if (!TryConsumeKeyword(tokenizer, KeywordFunction, "function"))
            return null;

        var nameToken = tokenizer.NextTokenCore();
        if (!nameToken.TryGetWordToken(out var nameTokenWord))
        {
            tokenizer.RecordError("Expected function name.", tokenizer.Position);
            return null;
        }

        if (!tokenizer.TryReadFunctionName(nameTokenWord, out var name))
            return null;

        var next = tokenizer.PeekTokenCore();
        if (next.Kind == TokenKind.OpenParen)
        {
            tokenizer.NextTokenCore();
            var close = tokenizer.NextTokenCore();
            if (close.Kind != TokenKind.CloseParen)
            {
                tokenizer.RecordError("Expected ) after function name.", tokenizer.Position);
                return null;
            }
        }

        var body = ParseCommandAtom(tokenizer, afterPipe: false);
        if (body == null)
        {
            tokenizer.RecordError("Expected function body.", tokenizer.Position);
            return null;
        }

        return new FunctionDefinition(name, body);
    }

    /// <summary>
    /// Parses a case/esac command with pattern clauses.
    /// </summary>
    private static ICommand? ParseCase(Tokenizer tokenizer)
    {
        if (!TryConsumeKeyword(tokenizer, KeywordCase, "case"))
            return null;

        var subjectToken = tokenizer.NextTokenCore();
        if (!subjectToken.TryGetWordToken(out var subjectWord))
        {
            tokenizer.RecordError("Expected case subject word.", tokenizer.Position);
            return null;
        }

        var subject = subjectWord.Word;
        if (!TryConsumeKeyword(tokenizer, KeywordIn, "in"))
            return null;
        SkipNewlines(tokenizer);

        var items = new List<CaseItem>();
        while (!IsKeyword(tokenizer.PeekTokenCore(), KeywordEsac))
        {
            SkipNewlines(tokenizer);
            if (IsKeyword(tokenizer.PeekTokenCore(), KeywordEsac))
                break;

            var patterns = new List<Word>();
            while (true)
            {
                var patternToken = tokenizer.NextTokenCore();
                if (!patternToken.TryGetWordToken(out var patternWord))
                {
                    tokenizer.RecordError("Expected case pattern.", tokenizer.Position);
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

                tokenizer.RecordError("Expected | or ) after case pattern.", tokenizer.Position);
                return null;
            }

            var body = ParseListWithTerminatorInfo(tokenizer, null, [KeywordEsac], TokenKind.CaseTerminator).Command;
            var endToken = tokenizer.PeekTokenCore();
            if (endToken.Kind == TokenKind.CaseTerminator)
                tokenizer.NextTokenCore();
            else if (!IsKeyword(endToken, KeywordEsac))
            {
                tokenizer.RecordError("Expected case item terminator.", tokenizer.Position);
                return null;
            }

            // An empty matching branch succeeds without executing a command.
            items.Add(new CaseItem(patterns, body ?? new SimpleCommand([], [], [], SimpleCommand.NoDeclarationAssignments)));
            SkipNewlines(tokenizer);
        }

        if (!TryConsumeKeyword(tokenizer, KeywordEsac, "esac"))

            return null;
        return new CaseCommand(subject, items);
    }

    /// <summary>
    /// Attempts to parse a function definition introduced by name () syntax.
    /// </summary>
    private static bool TryParseNameFunctionDefinition(Tokenizer tokenizer, out ICommand? functionDefinition)
    {
        functionDefinition = null;
        var first = tokenizer.PeekTokenCore();
        if (!first.TryGetWordToken(out var firstWord))
            return false;
        if (!tokenizer.IsFollowedByFunctionParentheses(firstWord))
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
            if (!tokenizer.TryReadFunctionName(firstWord, out var name))
                return false;
            if (!name.Syntax.HasFlag(FunctionNameSyntax.KeywordOnly))
            {
                var body = ParseCommandAtom(tokenizer, afterPipe: false);
                if (body == null)
                {
                    tokenizer.RecordError("Expected function body.", tokenizer.Position);
                    return false;
                }

                functionDefinition = new FunctionDefinition(name, body);
                return true;
            }
        }

        tokenizer.PushBack(token3);
        tokenizer.PushBack(token2);
        tokenizer.PushBack(token1);
        return false;
    }

    /// <summary>
    /// Parses a simple command with assignments, words, and redirections.
    /// </summary>
    private static SimpleCommand? ParseSimpleCommand(Tokenizer tokenizer)
    {
        static bool TrySplitScalarAssignment(Word word, out AssignmentWord assignment)
        {
            assignment = default;

            var nameBuilder = ByteMemoryBuilder.CreateUnaccounted(0);
            for (var partIndex = 0; partIndex < word.Parts.Count; partIndex++)
            {
                var part = word.Parts[partIndex];
                if (part.Kind != WordPartKind.Unquoted)
                    return false;

                var source = ShellSource.FromBytes(part.GetUnexpandedValue().Bytes);
                var equals = source.AsSpan().IndexOf((byte)'=');
                if (equals < 0)
                {
                    nameBuilder.Append(source.Bytes);
                    continue;
                }

                nameBuilder.Append(source.Bytes[..equals]);
                var operation = AssignmentOperator.Replace;
                if (nameBuilder.Length > 0 && nameBuilder.ToMemory().Span[^1] == (byte)'+')
                {
                    nameBuilder.Truncate(nameBuilder.Length - 1);
                    operation = AssignmentOperator.Append;
                }
                if (!ShellValue.FromBytes(nameBuilder.ToMemory()).TryGetUtf8Text(out var name)
                    || !VariableNames.IsValidVariableName(name.AsSpan()))
                    return false;

                var valueParts = new List<WordPart>();
                if (equals + 1 < source.Length)
                    valueParts.Add(WordPart.FromSource(WordPartKind.Unquoted, source.Slice(equals + 1)));
                for (var j = partIndex + 1; j < word.Parts.Count; j++)
                    valueParts.Add(word.Parts[j]);

                assignment = new AssignmentWord(ShellSource.FromBytes(nameBuilder.ToMemory()), new Word(valueParts), operation);
                return true;
            }

            return false;
        }

        // <summary>
        // Reads compound array assignment values enclosed in parentheses.
        // </summary>
        static IReadOnlyList<CompoundArrayItem> ReadCompoundArrayValues(Tokenizer tokenizer)
        {
            var values = new List<CompoundArrayItem>();

            while (true)
            {
                var token = tokenizer.NextTokenCore();
                switch (token.Kind)
                {
                    case TokenKind.CloseParen:
                        return values;
                    case TokenKind.Newline:
                        continue;
                    case TokenKind.Word:
                        if (!token.TryGetWordToken(out var wordToken))
                        {
                            tokenizer.RecordError("Expected word in array assignment.", tokenizer.Position);
                            return values;
                        }

                        if (!tokenizer.TryReadCompoundArrayItem(wordToken, out var item, out _))
                            return values;
                        values.Add(item);
                        continue;
                    default:
                        tokenizer.RecordError("Unexpected token in array assignment.", tokenizer.Position);
                        return values;
                }
            }
        }

        var assignments = new List<AssignmentWord>();
        var words = new List<Word>();
        Dictionary<Word, AssignmentWord>? declarationAssignments = null;
        var redirections = new List<Redirection>();
        var allowAssignments = true;
        var allowDeclarationAssignments = false;
        var sawAny = false;

        while (true)
        {
            if (tokenizer.Error != null)
                return null;
            var peek = tokenizer.PeekTokenCore();
            if (tokenizer.Error != null)
                return null;
            switch (peek.Kind)
            {
                case TokenKind.EndOfInput:
                    return sawAny
                        ? CreateCommand()
                        : null;
                case TokenKind.Newline:
                case TokenKind.Semicolon:
                    if (sawAny)
                    {
                        return CreateCommand();
                    }

                    if (peek.Kind == TokenKind.Semicolon)
                    {
                        tokenizer.RecordError("Unexpected command terminator.", peek.Source.Start.Value);
                        return null;
                    }

                    tokenizer.NextTokenCore();
                    continue;
                case TokenKind.ListOperator:
                    if (sawAny)
                        return CreateCommand();
                    tokenizer.RecordError("Unexpected list operator.", tokenizer.Position);
                    return null;
                case TokenKind.PipelineOperator:
                    if (sawAny)
                        return CreateCommand();
                    tokenizer.RecordError("Unexpected pipeline operator.", tokenizer.Position);
                    return null;
                case TokenKind.CloseParen:
                case TokenKind.CloseBrace:
                    return sawAny
                        ? CreateCommand()
                        : null;
                case TokenKind.CaseTerminator:
                    return sawAny
                        ? CreateCommand()
                        : null;
            }

            var token = tokenizer.NextTokenCore();
            switch (token.Kind)
            {
                case TokenKind.Redirection:
                    sawAny = true;
                    ReadRedirection(tokenizer, token, null, redirections);
                    continue;
                case TokenKind.Word:
                    sawAny = true;
                    if (!token.TryGetWordToken(out var wordToken))
                    {
                        tokenizer.RecordError("Expected a word token.", tokenizer.Position);
                        return null;
                    }

                    if (TryGetFileDescriptor(tokenizer, wordToken, out var fileDescriptor, out var redirectionToken))
                    {
                        ReadRedirection(tokenizer, redirectionToken, fileDescriptor, redirections);
                        continue;
                    }

                    if (TryGetAllocationVariable(tokenizer, wordToken, out var variableName, out var allocationToken))
                    {
                        ReadAllocationRedirection(tokenizer, allocationToken, variableName, redirections);
                        continue;
                    }

                    var assignment = default(AssignmentWord);
                    var assignmentPosition = allowAssignments || allowDeclarationAssignments;
                    var arrayAssignment = assignmentPosition && tokenizer.TryReadArrayAssignment(wordToken, out assignment);
                    if (arrayAssignment)
                        token = token with { Source = token.Source.Start..tokenizer.Position };
                    if (assignmentPosition && (arrayAssignment || TrySplitScalarAssignment(wordToken.Word, out assignment)))
                    {
                        if (assignment.Value.Parts.Count == 0
                            && tokenizer.PeekTokenCore().Kind == TokenKind.OpenParen)
                        {
                            var opening = tokenizer.NextTokenCore();
                            if (!tokenizer.AreAdjacent(token, opening))
                            {
                                tokenizer.RecordError("An array assignment requires an adjacent parenthesis.", opening.Source.Start.Value);
                                return null;
                            }

                            var arrayValues = ReadCompoundArrayValues(tokenizer);
                            if (tokenizer.Error != null)
                                return null;
                            if (tokenizer.IsAtWordBoundary())
                            {
                                AddAssignment(new AssignmentWord(assignment.Target, arrayValues, assignment.Operator), wordToken.Word);
                                continue;
                            }

                            var suffix = tokenizer.NextTokenCore();
                            if (!suffix.TryGetWordToken(out var suffixWord) || !tokenizer.IsAtWordBoundary())
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

                    if (words.Count == 0 && TryGetPlainUnquotedText(wordToken.Word, out var commandName))
                    {
                        allowDeclarationAssignments = BuiltinClassification.TryResolve(commandName.AsSpan(), out var kind)
                            && kind is BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local;
                    }
                    allowAssignments = false;
                    words.Add(wordToken.Word);
                    continue;
                default:
                    tokenizer.RecordError("Unexpected token while parsing a simple command.", tokenizer.Position);
                    return null;
            }
        }

        SimpleCommand CreateCommand() =>
            new(assignments, words, redirections, declarationAssignments ?? SimpleCommand.NoDeclarationAssignments);

        void AddAssignment(AssignmentWord assignment, Word word)
        {
            if (allowAssignments)
            {
                assignments.Add(assignment);
                return;
            }

            // Word identity survives command-field forwarding; numeric argument indices do not.
            words.Add(word);
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

        if (!TryGetPlainUnquotedText(token.Word, out var wordText))
            return false;

        if (!IsAllDigits(wordText.AsSpan()))
            return false;

        var peek = tokenizer.PeekTokenCore();
        // Only an adjacent number is a descriptor; whitespace leaves it as an argument.
        if (peek.Kind != TokenKind.Redirection || !tokenizer.AreAdjacent(token.Token, peek))
            return false;

        redirectionToken = tokenizer.NextTokenCore();
        if (!Integers.TryParseDescriptor(wordText.AsSpan(), out var descriptor))
        {
            tokenizer.RecordError("File descriptor exceeds the Int32 limit.", token.Token.Source.Start.Value);
            return false;
        }
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

        if (!TryGetPlainUnquotedText(token.Word, out var text))
            return false;

        var span = text.AsSpan();
        if (span.Length < 3 || span[0] != '{' || span[^1] != '}')
            return false;

        var inner = text.Substring(1, span.Length - 2);
        if (!VariableNames.IsValidVariableName(inner.AsSpan()))
            return false;

        var peek = tokenizer.PeekTokenCore();
        if (peek.Kind != TokenKind.Redirection)
            return false;

        if (peek.RedirectionKind != RedirectionKind.Output && peek.RedirectionKind != RedirectionKind.Input)
            return false;

        redirectionToken = tokenizer.NextTokenCore();
        variableName = inner;
        return true;
    }

    private static void ReadRedirection(
        Tokenizer tokenizer,
        Token token,
        int? fileDescriptor,
        List<Redirection> redirections)
    {
        var target = tokenizer.NextTokenCore();
        if (!target.TryGetWordToken(out var targetWord))
        {
            tokenizer.RecordError("Expected a word after redirection operator.", tokenizer.Position);
            return;
        }

        if (token.RedirectionKind == null)
        {
            tokenizer.RecordError("Redirection token missing kind.", tokenizer.Position);
            return;
        }

        var kind = token.RedirectionKind.Value;
        if (kind is RedirectionKind.HereDocument or RedirectionKind.HereDocumentStripTabs)
        {
            var redirection = new Redirection(kind, fileDescriptor, targetWord.Word, string.Empty, hereDocumentDelimiter: null);
            redirections.Add(redirection);
            (tokenizer.HereDocuments ??= new HereDocumentReader()).Enqueue(redirection);
            return;
        }

        if (kind is RedirectionKind.DuplicateOutput or RedirectionKind.DuplicateInput
            && TryGetPlainUnquotedText(targetWord.Word, out var text)
            && text is "-")
        {
            kind = kind == RedirectionKind.DuplicateOutput
                ? RedirectionKind.CloseOutput
                : RedirectionKind.CloseInput;
        }

        redirections.Add(new Redirection(kind, fileDescriptor, targetWord.Word, string.Empty, hereDocumentDelimiter: null));
    }

    private static void ReadAllocationRedirection(
        Tokenizer tokenizer,
        Token token,
        string variableName,
        List<Redirection> redirections)
    {
        var target = tokenizer.NextTokenCore();
        if (!target.TryGetWordToken(out var targetWord))
        {
            tokenizer.RecordError("Expected a word after allocation redirection operator.", tokenizer.Position);
            return;
        }

        var kind = token.RedirectionKind == RedirectionKind.Output
            ? RedirectionKind.AllocateOutput
            : RedirectionKind.AllocateInput;

        redirections.Add(new Redirection(kind, null, targetWord.Word, variableName, hereDocumentDelimiter: null));
    }

    private static IReadOnlyList<Redirection> ReadTrailingRedirections(Tokenizer tokenizer)
    {
        var redirections = new List<Redirection>();

        while (true)
        {
            var peek = tokenizer.PeekTokenCore();
            switch (peek.Kind)
            {
                case TokenKind.Redirection:
                    tokenizer.NextTokenCore();
                    ReadRedirection(tokenizer, peek, null, redirections);
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
                        ReadRedirection(tokenizer, redirectionToken, fileDescriptor, redirections);
                        continue;
                    }

                    if (TryGetAllocationVariable(tokenizer, wordToken, out var variableName, out var allocationToken))
                    {
                        ReadAllocationRedirection(tokenizer, allocationToken, variableName, redirections);
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
    /// Extracts text when a word is a single unquoted part.
    /// </summary>
    private static bool TryGetPlainUnquotedText(Word word, out string text)
    {
        if (word.Parts is [{ Kind: WordPartKind.Unquoted }])
        {
            return word.Parts[0].GetUnexpandedValue().TryGetUtf8Text(out text);
        }

        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true when all bytes are ASCII digits.
    /// </summary>
    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        if (span.Length == 0)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Consumes a leading ! token for pipeline negation.
    /// </summary>
    private static bool TryConsumeNegation(Tokenizer tokenizer)
    {
        var token = tokenizer.PeekTokenCore();
        if (token.Kind != TokenKind.Word)
            return false;

        if (!token.TryGetWordToken(out var wordToken))
            return false;

        if (!TryGetPlainUnquotedText(wordToken.Word, out var text))
            return false;

        var span = text.AsSpan();
        if (span.Length == 1 && span[0] == '!')
        {
            tokenizer.NextTokenCore();
            return true;
        }

        return false;
    }

    private static ICommand? ParseListUntilKeyword(
        Tokenizer tokenizer,
        string keyword,
        bool requireTerminator)
    {
        return ParseListUntilKeyword(tokenizer, [keyword], requireTerminator);
    }

    private static ICommand? ParseListUntilKeyword(
        Tokenizer tokenizer,
        string keyword1,
        string keyword2,
        string keyword3,
        bool requireTerminator)
    {
        return ParseListUntilKeyword(tokenizer, [keyword1, keyword2, keyword3], requireTerminator);
    }

    private static ICommand? ParseListUntilKeyword(
        Tokenizer tokenizer,
        string[] keywords,
        bool requireTerminator)
    {
        var result = ParseListWithTerminatorInfo(tokenizer, null, keywords, null);
        if (result.Command == null)
        {
            tokenizer.RecordError("Expected list before reserved word.", tokenizer.Position);
            return null;
        }

        if (requireTerminator && !result.TrailingTerminator)
        {
            tokenizer.RecordError("Expected terminator before reserved word.", tokenizer.Position);
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
            tokenizer.RecordError($"Expected {name} keyword.", tokenizer.Position);
            return false;
        }

        tokenizer.NextTokenCore();
        return tokenizer.Error == null;
    }

    /// <summary>
    /// Returns true when a token matches a specific reserved keyword.
    /// </summary>
    private static bool IsKeyword(Token token, string keyword)
    {
        if (token.Kind != TokenKind.Word)
            return false;

        if (!token.TryGetWordToken(out var wordToken))
            return false;

        if (!TryGetPlainUnquotedText(wordToken.Word, out var text))
            return false;

        return text.AsSpan().SequenceEqual(keyword.AsSpan());
    }


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
        TokenKind? endTokenKind)
    {
        SkipNewlines(tokenizer);
        if (IsEndKeyword(tokenizer, endKeywords) || IsEndTokenKind(tokenizer, endTokenKind))
            return (null, false);

        var left = ParseAndOrList(tokenizer);
        if (left == null)
            return (null, false);

        ICommand current = left;
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
            if (!IsEndTokenKind(tokenizer, endTokenKind))
                SkipNewlines(tokenizer);
            trailingTerminator = true;
            if (IsEndKeyword(tokenizer, endKeywords) || IsEndTokenKind(tokenizer, endTokenKind))
            {
                break;
            }

            var right = ParseAndOrList(tokenizer);
            if (right == null)
            {
                break;
            }

            current = new ListCommand(current, ListOperator.Sequence, right);
            trailingTerminator = false;
        }

        return (current, trailingTerminator);
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

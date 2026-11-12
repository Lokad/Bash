using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Lokad.Bash.Helpers;

using Lokad.Bash.Files;
using static Lokad.Bash.Evaluation.ShellPredicates;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Parses and evaluates [[ expression ]] arguments into a boolean result.
/// </summary>
/// <remarks>
/// Supports the conditional expression primaries documented in docs/COMPATIBILITY.md.
/// </remarks>
public sealed class ConditionalEvaluation
{
    /// <summary> Stores the conditional expression to tokenize and parse. </summary>
    private readonly string _expression;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _errexitEnabled;

    /// <summary> Stores the parsed expression tree after parsing succeeds; null when parsing fails. </summary>
    private TestNode? _root;

    /// <summary>
    /// Stores file test requests collected during parsing.
    /// </summary>
    private readonly List<UnresolvedFileTest> _fileTests = [];

    /// <summary>
    /// Initializes the evaluation with the raw expression to tokenize and parse.
    /// </summary>
    private ConditionalEvaluation(string expression, CancellationToken cancellationToken, bool errexitEnabled)
    {
        _expression = expression;
        _cancellationToken = cancellationToken;
        _errexitEnabled = errexitEnabled;
    }

    /// <summary>
    /// Gets whether parsing failed.
    /// </summary>
    public bool HasParseError { get; private set; }

    /// <summary>
    /// Parses a conditional expression into an evaluatable instance.
    /// </summary>
    /// <param name="expression">The conditional expression.</param>
    /// <returns>The parsed <see cref="ConditionalEvaluation"/>.</returns>
    public static ConditionalEvaluation Parse(string expression) => Parse(expression, CancellationToken.None, false);

    internal static ConditionalEvaluation Parse(string expression, CancellationToken cancellationToken, bool errexitEnabled)
    {
        var evaluation = new ConditionalEvaluation(expression, cancellationToken, errexitEnabled);
        evaluation.ParseExpression();
        return evaluation;
    }

    /// <summary>
    /// Evaluates non-filesystem predicates and collects filesystem tests.
    /// </summary>
    /// <param name="result">The result when no filesystem tests are required.</param>
    /// <returns>The filesystem tests to evaluate.</returns>
    internal IReadOnlyList<UnresolvedFileTest> CollectFileTests(out bool? result)
    {
        result = null;
        if (HasParseError || _root == null)
            return [];

        if (_fileTests.Count == 0)
        {
            result = _root.Evaluate([]);
            return [];
        }

        return _fileTests;
    }

    /// <summary>
    /// Completes evaluation using filesystem predicate results.
    /// </summary>
    /// <param name="fileResults">The filesystem predicate results.</param>
    /// <returns>The final test result.</returns>
    public bool CompleteFileTests(IReadOnlyList<bool> fileResults)
    {
        if (HasParseError || _root == null)
            return false;

        return _root.Evaluate(fileResults);
    }

    /// <summary>
    /// Parses the raw expression into the internal evaluation tree and records parse failures.
    /// </summary>
    private void ParseExpression()
    {
        var tokens = Tokenize(_expression, _cancellationToken, out var hasError);
        if (hasError)
        {
            HasParseError = true;
            _root = null;
            return;
        }

        if (tokens.Count == 0)
        {
            _root = new ConstantNode(false);
            return;
        }

        var parser = new Parser(tokens, _cancellationToken, _errexitEnabled);
        _root = parser.ParseExpression(_fileTests);
        if (parser.HasError || !parser.AtEnd)
        {
            HasParseError = true;
            _root = null;
        }
    }

    /// <summary>
    /// Tokenizes the raw expression into conditional tokens, handling quoting and operators.
    /// </summary>
    /// <param name="expression">The expression to tokenize.</param>
    /// <param name="hasError">True when unterminated quotes or invalid syntax is detected.</param>
    /// <param name="cancellationToken">Cancels scanning the expression.</param>
    /// <returns>The list of parsed tokens.</returns>
    private static IReadOnlyList<ConditionalToken> Tokenize(
        string expression,
        CancellationToken cancellationToken,
        out bool hasError)
    {
        var tokens = new List<ConditionalToken>();
        var buffer = new StringBuilder();
        var mask = new StringBuilder();
        var tokenActive = false;
        var span = expression.AsSpan();
        var inSingle = false;
        var inDouble = false;
        hasError = false;

        for (var index = 0; index < span.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = span[index];
            if (inSingle)
            {
                if (value == '\'')
                {
                    inSingle = false;
                    continue;
                }

                buffer.Append(value);
                mask.Append('\x1');
                continue;
            }

            if (inDouble)
            {
                if (value == '"')
                {
                    inDouble = false;
                    continue;
                }

                if (value == '\\' && index + 1 < span.Length)
                {
                    index++;
                    buffer.Append(span[index]);
                    mask.Append('\x1');
                    continue;
                }

                buffer.Append(value);
                mask.Append('\x1');
                continue;
            }

            if (Strings.IsWhitespace(value))
            {
                Flush();
                continue;
            }

            if (value == '\'')
            {
                tokenActive = true;
                inSingle = true;
                continue;
            }

            if (value == '"')
            {
                tokenActive = true;
                inDouble = true;
                continue;
            }

            if (value is '(' or ')')
            {
                Flush();
                tokens.Add(new ConditionalToken(value.ToString(), string.Empty));
                continue;
            }

            if (value == '&' && index + 1 < span.Length && span[index + 1] == '&')
            {
                Flush();
                tokens.Add(new ConditionalToken("&&", string.Empty));
                index++;
                continue;
            }

            if (value == '|' && index + 1 < span.Length && span[index + 1] == '|')
            {
                Flush();
                tokens.Add(new ConditionalToken("||", string.Empty));
                index++;
                continue;
            }

            if (value == '!' && index + 1 < span.Length && span[index + 1] == '=')
            {
                Flush();
                tokens.Add(new ConditionalToken("!=", string.Empty));
                index++;
                continue;
            }

            if (value == '!')
            {
                Flush();
                tokens.Add(new ConditionalToken("!", string.Empty));
                continue;
            }

            if (value == '=' && index + 1 < span.Length)
            {
                var next = span[index + 1];
                if (next == '=')
                {
                    Flush();
                    tokens.Add(new ConditionalToken("==", string.Empty));
                    index++;
                    continue;
                }

                if (next == '~')
                {
                    Flush();
                    tokens.Add(new ConditionalToken("=~", string.Empty));
                    index++;
                    continue;
                }
            }

            if (value == '=')
            {
                Flush();
                tokens.Add(new ConditionalToken("=", string.Empty));
                continue;
            }

            if (value is '<' or '>')
            {
                Flush();
                tokens.Add(new ConditionalToken(value.ToString(), string.Empty));
                continue;
            }

            tokenActive = true;
            buffer.Append(value);
            mask.Append('\0');
        }

        if (inSingle || inDouble)
        {
            hasError = true;
            return [];
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (buffer.Length == 0)
            {
                if (!tokenActive)
                {
                    return;
                }

                tokens.Add(new ConditionalToken(string.Empty, string.Empty));
                tokenActive = false;
                return;
            }

            tokens.Add(new ConditionalToken(buffer.ToString(), mask.ToString()));
            buffer = new StringBuilder();
            mask = new StringBuilder();
            tokenActive = false;
        }
    }

    /// <summary> Parses the token stream into a boolean expression tree. </summary>
    private sealed class Parser(IReadOnlyList<ConditionalToken> tokens, CancellationToken cancellationToken, bool errexitEnabled)
    {
        /// <summary> Stores the token stream produced by tokenization. </summary>
        private readonly IReadOnlyList<ConditionalToken> _tokens = tokens;

        /// <summary>
        /// Tracks the current token index while parsing; increments as tokens are consumed.
        /// </summary>
        private int _index;

        public bool HasError { get; private set; }

        public bool AtEnd => _index >= _tokens.Count;

        /// <summary>
        /// Parses the full expression and appends any file-test requests encountered.
        /// </summary>
        public TestNode ParseExpression(List<UnresolvedFileTest> fileTests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ParseOr(fileTests);
        }

        /// <summary>
        /// Parses a logical OR expression, handling left-associative chaining.
        /// </summary>
        private TestNode ParseOr(List<UnresolvedFileTest> fileTests)
        {
            var left = ParseAnd(fileTests);
            while (TryConsumeOperator(IsOrOperator))
            {
                var right = ParseAnd(fileTests);
                left = new BinaryNode(BinaryOperator.Or, left, right);
            }

            return left;
        }

        /// <summary>
        /// Parses a logical AND expression, handling left-associative chaining.
        /// </summary>
        private TestNode ParseAnd(List<UnresolvedFileTest> fileTests)
        {
            var left = ParseUnary(fileTests);
            while (TryConsumeOperator(IsAndOperator))
            {
                var right = ParseUnary(fileTests);
                left = new BinaryNode(BinaryOperator.And, left, right);
            }

            return left;
        }

        /// <summary>
        /// Parses a unary expression, handling negation or delegating to primaries.
        /// </summary>
        private TestNode ParseUnary(List<UnresolvedFileTest> fileTests)
        {
            if (TryConsumeOperator(IsNotOperator))
            {
                var inner = ParseUnary(fileTests);
                return new UnaryNode(UnaryOperator.Not, inner);
            }

            return ParsePrimary(fileTests);
        }

        /// <summary>
        /// Parses a primary expression, including parentheses and predicate forms.
        /// </summary>
        private TestNode ParsePrimary(List<UnresolvedFileTest> fileTests)
        {
            if (AtEnd)
            {
                HasError = true;
                return new ConstantNode(false);
            }

            var token = PeekSpan();
            if (IsOpenParenthesis(token))
            {
                _index++;
                var inner = ParseExpression(fileTests);
                if (!TryConsumeOperator(IsCloseParenthesis))
                {
                    HasError = true;
                    return new ConstantNode(false);
                }

                return inner;
            }

            if (TryGetUnaryPredicate(token, out var unaryPredicate))
            {
                _index++;
                if (AtEnd)
                {
                    HasError = true;
                    return new ConstantNode(false);
                }

                var operand = NextToken();
                if (unaryPredicate.IsFilePredicate)
                {
                    return CreateFileTestNode(fileTests, new UnresolvedFileTest(unaryPredicate.FileKind, operand.Content, null));
                }

                return new ConstantNode(EvaluateUnary(unaryPredicate.Operator, operand.Content, errexitEnabled));
            }

            var left = NextToken();
            if (AtEnd)
            {
                return new ConstantNode(!string.IsNullOrEmpty(left.Content));
            }

            var op = PeekSpan();
            if (TryGetBinaryPredicate(op, out var binaryPredicate))
            {
                _index++;
                if (AtEnd)
                {
                    HasError = true;
                    return new ConstantNode(false);
                }

                var right = NextToken();
                if (binaryPredicate.IsFilePredicate)
                {
                    return CreateFileTestNode(
                        fileTests,
                        new UnresolvedFileTest(binaryPredicate.FileKind, left.Content, right.Content));
                }

                var evaluation = EvaluateBinary(binaryPredicate.Operator, left, right, cancellationToken);
                if (evaluation.HasError)
                {
                    HasError = true;
                    return new ConstantNode(false);
                }

                return new ConstantNode(evaluation.Result);
            }

            return new ConstantNode(!string.IsNullOrEmpty(left.Content));
        }

        /// <summary>
        /// Adds a file-test request to the shared list and returns a node that will read its result by index.
        /// </summary>
        /// <remarks>
        /// Evaluation happens in two phases. First, parsing builds the boolean expression tree and collects
        /// <see cref="UnresolvedFileTest"/> entries for any filesystem predicates encountered. Second,
        /// <see cref="ConditionalEvaluation.CollectFileTests"/> exposes the collected requests so the caller
        /// (see <see cref="Evaluation.EvaluateConditionalCommandAsync"/>) can execute a single file-test
        /// operation against the host. The operation returns a compact list of booleans, which is then fed
        /// back into the same tree via <see cref="ConditionalEvaluation.CompleteFileTests"/>.
        ///
        /// The tree cannot execute filesystem predicates itself because evaluation is operation-driven: only
        /// the engine/host can perform file I/O, and it does so in a batch via a FileTests operation. Keeping
        /// a separate list of requests lets the parser preserve the logical structure while deferring I/O to
        /// the operation pipeline, and the indexed <see cref="FileTestNode"/> provides the stable mapping
        /// from each predicate occurrence to its result slot.
        /// </remarks>
        private static TestNode CreateFileTestNode(List<UnresolvedFileTest> fileTests, UnresolvedFileTest request)
        {
            var index = fileTests.Count;
            fileTests.Add(request);
            return new FileTestNode(index);
        }

        /// <summary>
        /// Consumes the next token if it matches the provided operator predicate.
        /// </summary>
        private bool TryConsumeOperator(Func<ReadOnlySpan<char>, bool> match)
        {
            if (AtEnd)
            {
                return false;
            }

            var span = PeekSpan();
            if (!match(span))
            {
                return false;
            }

            _index++;
            return true;
        }

        /// <summary>
        /// Returns the next token and advances the parser index, returning an empty token at end.
        /// </summary>
        private ConditionalToken NextToken()
        {
            return _tokens[_index++];
        }

        /// <summary>
        /// Returns the raw span for the next token without consuming it.
        /// </summary>
        private ReadOnlySpan<char> PeekSpan()
        {
            return _tokens[_index].Content.AsSpan();
        }

        /// <summary> Returns true when the token represents the logical NOT operator. </summary>
        private static bool IsNotOperator(ReadOnlySpan<char> token) => token is ['!'];

        /// <summary> Returns true when the token represents the logical AND operator. </summary>
        private static bool IsAndOperator(ReadOnlySpan<char> token) =>
            token is ['-', 'a'] or ['&', '&'];

        /// <summary> Returns true when the token represents the logical OR operator. </summary>
        private static bool IsOrOperator(ReadOnlySpan<char> token) =>
            token is ['-', 'o'] or ['|', '|'];

        /// <summary> Returns true when the token is an opening parenthesis. </summary>
        private static bool IsOpenParenthesis(ReadOnlySpan<char> token) => token is ['('];

        /// <summary> Returns true when the token is a closing parenthesis. </summary>
        private static bool IsCloseParenthesis(ReadOnlySpan<char> token) => token is [')'];


    }

    /// <summary>
    /// Evaluates a unary predicate that does not require filesystem access.
    /// </summary>
    private static bool EvaluateUnary(UnaryPredicateKind kind, string operand, bool errexitEnabled)
    {
        return kind switch
        {
            UnaryPredicateKind.StringLengthNonZero => !string.IsNullOrEmpty(operand),
            UnaryPredicateKind.StringLengthZero => string.IsNullOrEmpty(operand),
            UnaryPredicateKind.OptionEnabled => IsOptionEnabled(Utf8Text.Encode(operand).Span, errexitEnabled),
            _ => false
        };
    }

    /// <summary>
    /// Evaluates a binary predicate, returning both the result and parse error state.
    /// </summary>
    private static BinaryEvaluation EvaluateBinary(
        BinaryPredicateKind predicate,
        ConditionalToken left,
        ConditionalToken right, CancellationToken cancellationToken)
    {
        switch (predicate)
        {
            case BinaryPredicateKind.NumericEqual:
            {
                if (!Integers.TryParseNumber(left.Content.AsSpan(), out var leftValue)
                    || !Integers.TryParseNumber(right.Content.AsSpan(), out var rightValue))
                {
                    return new BinaryEvaluation(false);
                }

                return new BinaryEvaluation(leftValue == rightValue);
            }
            case BinaryPredicateKind.NumericNotEqual:
            {
                if (!Integers.TryParseNumber(left.Content.AsSpan(), out var leftValue)
                    || !Integers.TryParseNumber(right.Content.AsSpan(), out var rightValue))
                {
                    return new BinaryEvaluation(false);
                }

                return new BinaryEvaluation(leftValue != rightValue);
            }
            case BinaryPredicateKind.NumericLess:
            {
                if (!Integers.TryParseNumber(left.Content.AsSpan(), out var leftValue)
                    || !Integers.TryParseNumber(right.Content.AsSpan(), out var rightValue))
                {
                    return new BinaryEvaluation(false);
                }

                return new BinaryEvaluation(leftValue < rightValue);
            }
            case BinaryPredicateKind.NumericLessOrEqual:
            {
                if (!Integers.TryParseNumber(left.Content.AsSpan(), out var leftValue)
                    || !Integers.TryParseNumber(right.Content.AsSpan(), out var rightValue))
                {
                    return new BinaryEvaluation(false);
                }

                return new BinaryEvaluation(leftValue <= rightValue);
            }
            case BinaryPredicateKind.NumericGreater:
            {
                if (!Integers.TryParseNumber(left.Content.AsSpan(), out var leftValue)
                    || !Integers.TryParseNumber(right.Content.AsSpan(), out var rightValue))
                {
                    return new BinaryEvaluation(false);
                }

                return new BinaryEvaluation(leftValue > rightValue);
            }
            case BinaryPredicateKind.NumericGreaterOrEqual:
            {
                if (!Integers.TryParseNumber(left.Content.AsSpan(), out var leftValue)
                    || !Integers.TryParseNumber(right.Content.AsSpan(), out var rightValue))
                {
                    return new BinaryEvaluation(false);
                }

                return new BinaryEvaluation(leftValue >= rightValue);
            }
            case BinaryPredicateKind.StringEqual:
                return new BinaryEvaluation(
                    MatchPattern(left.Content.AsSpan(), right.Content.AsSpan(), right.LiteralMask.AsSpan(), cancellationToken));
            case BinaryPredicateKind.StringNotEqual:
                return new BinaryEvaluation(
                    !MatchPattern(left.Content.AsSpan(), right.Content.AsSpan(), right.LiteralMask.AsSpan(), cancellationToken));
            case BinaryPredicateKind.StringLess:
                return new BinaryEvaluation(string.CompareOrdinal(left.Content, right.Content) < 0);
            case BinaryPredicateKind.StringGreater:
                return new BinaryEvaluation(string.CompareOrdinal(left.Content, right.Content) > 0);
            case BinaryPredicateKind.RegexMatch:
                if (!TryMatchRegex(left.Content, right.Content, right.LiteralMask, out var matched))
                {
                    return new BinaryEvaluation(false, hasError: true);
                }

                return new BinaryEvaluation(matched);
            default:
                return new BinaryEvaluation(false);
        }
    }

    /// <summary>
    /// Matches a pattern against a value, honoring literal mask and wildcards.
    /// </summary>
    private static bool MatchPattern(
        ReadOnlySpan<char> input,
        ReadOnlySpan<char> pattern,
        ReadOnlySpan<char> literalMask, CancellationToken cancellationToken)
    {
        var inputIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var starInputIndex = 0;

        while (inputIndex < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (patternIndex < pattern.Length)
            {
                if (!IsLiteral(literalMask, patternIndex) && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex;
                    starInputIndex = inputIndex;
                    patternIndex++;
                    continue;
                }

                if (!IsLiteral(literalMask, patternIndex) && pattern[patternIndex] == '?')
                {
                    inputIndex++;
                    patternIndex++;
                    continue;
                }

                if (!IsLiteral(literalMask, patternIndex) && pattern[patternIndex] == '[')
                {
                    if (TryMatchBracketClass(
                            input[inputIndex],
                            pattern,
                            literalMask,
                            patternIndex,
                            cancellationToken,
                            out var nextIndex,
                            out var matched))
                    {
                        if (matched)
                        {
                            inputIndex++;
                            patternIndex = nextIndex;
                            continue;
                        }
                    }
                    else if (pattern[patternIndex] == input[inputIndex])
                    {
                        inputIndex++;
                        patternIndex++;
                        continue;
                    }
                }

                if (pattern[patternIndex] == input[inputIndex])
                {
                    inputIndex++;
                    patternIndex++;
                    continue;
                }
            }

            if (starIndex != -1)
            {
                patternIndex = starIndex + 1;
                starInputIndex++;
                inputIndex = starInputIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length
               && !IsLiteral(literalMask, patternIndex)
               && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    /// <summary>
    /// Attempts to match a bracket character class within a glob pattern.
    /// </summary>
    private static bool TryMatchBracketClass(
        char value,
        ReadOnlySpan<char> pattern,
        ReadOnlySpan<char> literalMask,
        int patternIndex,
        CancellationToken cancellationToken,
        out int nextIndex,
        out bool matched)
    {
        matched = false;
        nextIndex = patternIndex;
        var index = patternIndex + 1;
        if (index >= pattern.Length)
        {
            return false;
        }

        var negate = false;
        if ((pattern[index] == '!' || pattern[index] == '^') && !IsLiteral(literalMask, index))
        {
            negate = true;
            index++;
        }

        var hasEntry = false;
        var hasPrev = false;
        char prev = '\0';
        for (; index < pattern.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pattern[index] == ']' && !IsLiteral(literalMask, index))
            {
                nextIndex = index + 1;
                matched = negate ? !matched : matched;
                return hasEntry;
            }

            var current = pattern[index];
            var currentLiteral = IsLiteral(literalMask, index);

            if (!currentLiteral
                && current == '-'
                && hasPrev
                && index + 1 < pattern.Length
                && pattern[index + 1] != ']'
                && !IsLiteral(literalMask, index + 1))
            {
                var next = pattern[index + 1];
                if (value >= prev && value <= next)
                {
                    matched = true;
                }

                hasEntry = true;
                hasPrev = false;
                index++;
                continue;
            }

            if (value == current)
            {
                matched = true;
            }

            hasEntry = true;
            prev = current;
            hasPrev = true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when the mask marks the byte at the index as literal.
    /// </summary>
    private static bool IsLiteral(ReadOnlySpan<char> literalMask, int index)
    {
        return index < literalMask.Length && literalMask[index] != '\0';
    }

    /// <summary>
    /// Attempts a regex match using a pattern that may include escaped literals.
    /// </summary>
    private static bool TryMatchRegex(
        string input,
        string pattern,
        string literalMask,
        out bool matched)
    {
        matched = false;
        var patternSpan = pattern.AsSpan();
        var maskSpan = literalMask.AsSpan();
        var builder = new StringBuilder(patternSpan.Length);
        for (var index = 0; index < patternSpan.Length; index++)
        {
            var ch = patternSpan[index];
            if (IsLiteral(maskSpan, index))
            {
                AppendRegexLiteral(builder, ch);
            }
            else
            {
                builder.Append(ch);
            }
        }

        try
        {
            matched = Regex.IsMatch(
                input,
                builder.ToString(),
                RegexOptions.None,
                TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Appends a regex-escaped literal character to the builder.
    /// </summary>
    private static void AppendRegexLiteral(StringBuilder builder, char value)
    {
        if (IsRegexMeta(value))
        {
            builder.Append('\\');
        }

        builder.Append(value);
    }

    /// <summary> Returns true when the character is a regex metacharacter. </summary>
    private static bool IsRegexMeta(char value)
    {
        return value is '.' or '^' or '$' or '|' or '?' or '*' or '+' or '(' or ')'
            or '[' or ']' or '{' or '}' or '\\';
    }

    /// <summary> Represents a node in the conditional expression evaluation tree. </summary>
    private abstract class TestNode
    {
        /// <summary>
        /// Evaluates the node using the supplied file test results.
        /// </summary>
        public abstract bool Evaluate(IReadOnlyList<bool> fileResults);
    }

    /// <summary> Represents a constant boolean value in the expression tree. </summary>
    private sealed class ConstantNode(bool value) : TestNode
    {
        /// <summary> Stores the constant value returned by this node. </summary>
        private readonly bool _value = value;

        /// <inheritdoc />
        public override bool Evaluate(IReadOnlyList<bool> fileResults) => _value;
    }

    /// <summary> Represents a resolved file test that evaluates using the provided results list. </summary>
    private sealed class FileTestNode(int index) : TestNode
    {
        /// <summary> Stores the index into the file test result list for this node. </summary>
        private readonly int _index = index;

        /// <inheritdoc />
        public override bool Evaluate(IReadOnlyList<bool> fileResults) => fileResults[_index];
    }

    /// <summary> Represents a unary logical operation in the expression tree. </summary>
    private sealed class UnaryNode(UnaryOperator @operator, TestNode inner) : TestNode
    {
        private UnaryOperator Operator { get; } = @operator;

        private TestNode Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

        /// <inheritdoc />
        public override bool Evaluate(IReadOnlyList<bool> fileResults)
        {
            var value = Inner.Evaluate(fileResults);
            return Operator switch
            {
                UnaryOperator.Not => !value,
                _ => value
            };
        }
    }

    /// <summary> Represents a binary logical operation in the expression tree. </summary>
    private sealed class BinaryNode(BinaryOperator @operator, TestNode left, TestNode right) : TestNode
    {
        private BinaryOperator Operator { get; } = @operator;

        private TestNode Left { get; } = left ?? throw new ArgumentNullException(nameof(left));

        private TestNode Right { get; } = right ?? throw new ArgumentNullException(nameof(right));

        /// <inheritdoc />
        public override bool Evaluate(IReadOnlyList<bool> fileResults)
        {
            return Operator switch
            {
                BinaryOperator.And => Left.Evaluate(fileResults) && Right.Evaluate(fileResults),
                BinaryOperator.Or => Left.Evaluate(fileResults) || Right.Evaluate(fileResults),
                _ => false
            };
        }
    }

    /// <summary> Represents a token produced by conditional expression tokenization. </summary>
    private readonly struct ConditionalToken(string content, string literalMask)
    {
        public string Content { get; } = content;
        public string LiteralMask { get; } = literalMask;
    }

    /// <summary> Represents the parsed binary predicate evaluation details. </summary>
    private readonly struct BinaryEvaluation(bool result, bool hasError = false)
    {
        public bool Result { get; } = result;
        public bool HasError { get; } = hasError;
    }

    /// <summary> Enumerates unary logical operators in conditional expressions. </summary>
    private enum UnaryOperator
    {
        Not
    }

    /// <summary> Enumerates binary logical operators in conditional expressions. </summary>
    private enum BinaryOperator
    {
        And,
        Or
    }
}

using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;

using Lokad.Bash.Files;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Parses and evaluates test/[ arguments into a boolean result.
/// </summary>
/// <remarks>
/// Supports the conditional expression primaries documented in docs/COMPATIBILITY.md.
/// </remarks>
public sealed class TestEvaluation
{
    /// <summary> Stores the test arguments to parse. </summary>
    private readonly IReadOnlyList<string> _arguments;
    /// <summary> Stores the parsed expression tree after parsing succeeds; null when parsing fails. </summary>
    private TestNode? _root;
    /// <summary>
    /// Stores the expression tree with file predicates replaced; set during collection and reused for completion.
    /// </summary>
    private TestNode? _rootWithFiles;

    /// <summary>
    /// Initializes a new instance with the provided test arguments.
    /// </summary>
    private TestEvaluation(IReadOnlyList<string> arguments)
    {
        _arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    }

    /// <summary>
    /// Gets whether parsing failed.
    /// </summary>
    public bool HasParseError { get; private set; }

    /// <summary>
    /// Parses a test argument list into an evaluatable instance.
    /// </summary>
    /// <param name="arguments">The arguments to test.</param>
    /// <returns>The parsed <see cref="TestEvaluation"/>.</returns>
    public static TestEvaluation Parse(IReadOnlyList<string> arguments)
    {
        var evaluation = new TestEvaluation(arguments);
        evaluation.ParseArguments();
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
        {
            return [];
        }

        var fileTests = new List<UnresolvedFileTest>();
        _rootWithFiles = ReplaceFileTests(_root, fileTests);
        if (fileTests.Count == 0)
        {
            result = _rootWithFiles.Evaluate([]);
            return [];
        }

        return fileTests;
    }

    /// <summary>
    /// Completes evaluation using filesystem predicate results.
    /// </summary>
    /// <param name="fileResults">The filesystem predicate results.</param>
    /// <returns>The final test result.</returns>
    public bool CompleteFileTests(IReadOnlyList<bool> fileResults)
    {
        if (HasParseError || _rootWithFiles == null)
        {
            return false;
        }

        if (fileResults == null)
        {
            throw new ArgumentNullException(nameof(fileResults));
        }

        return _rootWithFiles.Evaluate(fileResults);
    }

    /// <summary>
    /// Parses arguments into an expression tree and records parse errors.
    /// </summary>
    private void ParseArguments()
    {
        if (_arguments.Count == 0)
        {
            _root = new ConstantNode(false);
            return;
        }

        var parser = new Parser(_arguments);
        _root = parser.ParseExpression();
        if (parser.HasError || !parser.AtEnd)
        {
            HasParseError = true;
            _root = null;
        }
    }

    /// <summary>
    /// Replaces file predicate nodes with indexed file test nodes for deferred evaluation.
    /// </summary>
    private static TestNode ReplaceFileTests(TestNode node, List<UnresolvedFileTest> fileTests)
    {
        return node switch
        {
            FilePredicateNode filePredicate => new FileTestNode(
                fileTests.Count,
                AddFileTest(fileTests, filePredicate)),
            FileBinaryPredicateNode fileBinaryPredicate => new FileTestNode(
                fileTests.Count,
                AddFileTest(fileTests, fileBinaryPredicate)),
            UnaryNode unary => new UnaryNode(
                unary.Operator,
                ReplaceFileTests(unary.Inner, fileTests)),
            BinaryNode binary => new BinaryNode(
                binary.Operator,
                ReplaceFileTests(binary.Left, fileTests),
                ReplaceFileTests(binary.Right, fileTests)),
            _ => node
        };
    }

    /// <summary>
    /// Appends a unary file test request and returns it for node replacement.
    /// </summary>
    private static UnresolvedFileTest AddFileTest(List<UnresolvedFileTest> fileTests, FilePredicateNode predicate)
    {
        var request = new UnresolvedFileTest(predicate.Kind, predicate.Operand, null);
        fileTests.Add(request);
        return request;
    }

    /// <summary>
    /// Appends a binary file test request and returns it for node replacement.
    /// </summary>
    private static UnresolvedFileTest AddFileTest(List<UnresolvedFileTest> fileTests, FileBinaryPredicateNode predicate)
    {
        var request = new UnresolvedFileTest(predicate.Kind, predicate.Left, predicate.Right);
        fileTests.Add(request);
        return request;
    }

    /// <summary> Parses test arguments into a boolean expression tree. </summary>
    private sealed class Parser
    {
        /// <summary> Stores the token list derived from the argument array. </summary>
        private readonly IReadOnlyList<string> _tokens;
        /// <summary>
        /// Tracks the current token index while parsing; increments as tokens are consumed.
        /// </summary>
        private int _index;

        public Parser(IReadOnlyList<string> tokens)
        {
            _tokens = tokens;
        }

        public bool HasError { get; private set; }

        public bool AtEnd => _index >= _tokens.Count;

        /// <summary>
        /// Parses the full expression with lowest-precedence OR handling.
        /// </summary>
        public TestNode ParseExpression()
        {
            return ParseOr();
        }

        /// <summary>
        /// Parses a chain of OR expressions.
        /// </summary>
        private TestNode ParseOr()
        {
            var left = ParseAnd();
            while (TryConsumeOperator(IsOrOperator))
            {
                var right = ParseAnd();
                left = new BinaryNode(BinaryOperator.Or, left, right);
            }

            return left;
        }

        /// <summary>
        /// Parses a chain of AND expressions.
        /// </summary>
        private TestNode ParseAnd()
        {
            var left = ParseUnary();
            while (TryConsumeOperator(IsAndOperator))
            {
                var right = ParseUnary();
                left = new BinaryNode(BinaryOperator.And, left, right);
            }

            return left;
        }

        /// <summary>
        /// Parses unary operators or delegates to primary parsing.
        /// </summary>
        private TestNode ParseUnary()
        {
            if (TryConsumeOperator(IsNotOperator))
            {
                var inner = ParseUnary();
                return new UnaryNode(UnaryOperator.Not, inner);
            }

            return ParsePrimary();
        }

        /// <summary>
        /// Parses parentheses, predicates, or literal operands.
        /// </summary>
        private TestNode ParsePrimary()
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
                var inner = ParseExpression();
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
                return unaryPredicate.IsFilePredicate
                    ? new FilePredicateNode(unaryPredicate.FileKind, operand)
                    : new ConstantNode(EvaluateUnary(unaryPredicate.Operator, operand));
            }

            var left = NextToken();
            if (AtEnd)
            {
                return new ConstantNode(!string.IsNullOrEmpty(left));
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
                return binaryPredicate.IsFilePredicate
                    ? new FileBinaryPredicateNode(binaryPredicate.FileKind, left, right)
                    : new ConstantNode(EvaluateBinary(binaryPredicate.Operator, left, right));
            }

            return new ConstantNode(!string.IsNullOrEmpty(left));
        }

        /// <summary>
        /// Consumes the current token when it matches a predicate operator.
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
        /// Returns the next token and advances the parser index.
        /// </summary>
        private string NextToken()
        {
            return _tokens[_index++];
        }

        /// <summary>
        /// Returns the current token span without advancing the parser.
        /// </summary>
        private ReadOnlySpan<char> PeekSpan()
        {
            return _tokens[_index].AsSpan();
        }

        /// <summary>
        /// Returns true when the token is the logical NOT operator.
        /// </summary>
        private static bool IsNotOperator(ReadOnlySpan<char> token)
        {
            return token is ['!'];
        }

        /// <summary>
        /// Returns true when the token is the logical AND operator.
        /// </summary>
        private static bool IsAndOperator(ReadOnlySpan<char> token)
        {
            return token is ['-', 'a'];
        }

        /// <summary>
        /// Returns true when the token is the logical OR operator.
        /// </summary>
        private static bool IsOrOperator(ReadOnlySpan<char> token)
        {
            return token is ['-', 'o'];
        }

        /// <summary>
        /// Returns true when the token is an opening parenthesis.
        /// </summary>
        private static bool IsOpenParenthesis(ReadOnlySpan<char> token)
        {
            return token is ['('];
        }

        /// <summary>
        /// Returns true when the token is a closing parenthesis.
        /// </summary>
        private static bool IsCloseParenthesis(ReadOnlySpan<char> token)
        {
            return token is [')'];
        }

        /// <summary>
        /// Attempts to map a token to a unary predicate or file predicate.
        /// </summary>
        private static bool TryGetUnaryPredicate(ReadOnlySpan<char> token, out UnaryPredicate predicate)
        {
            if (token is ['-', 'n'])
            {
                predicate = new UnaryPredicate(UnaryPredicateKind.StringLengthNonZero, false, default);
                return true;
            }

            if (token is ['-', 'z'])
            {
                predicate = new UnaryPredicate(UnaryPredicateKind.StringLengthZero, false, default);
                return true;
            }

            if (TryGetFilePredicate(token, out var fileKind))
            {
                predicate = new UnaryPredicate(UnaryPredicateKind.FilePredicate, true, fileKind);
                return true;
            }

            predicate = default;
            return false;
        }

        /// <summary>
        /// Attempts to map a token to a binary predicate or file predicate.
        /// </summary>
        private static bool TryGetBinaryPredicate(ReadOnlySpan<char> token, out BinaryPredicate predicate)
        {
            if (token is ['-', 'e', 'q'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.NumericEqual, false, default);
                return true;
            }

            if (token is ['-', 'n', 'e'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.NumericNotEqual, false, default);
                return true;
            }

            if (token is ['-', 'l', 't'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.NumericLess, false, default);
                return true;
            }

            if (token is ['-', 'l', 'e'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.NumericLessOrEqual, false, default);
                return true;
            }

            if (token is ['-', 'g', 't'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.NumericGreater, false, default);
                return true;
            }

            if (token is ['-', 'g', 'e'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.NumericGreaterOrEqual, false, default);
                return true;
            }

            if (token is ['='])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.StringEqual, false, default);
                return true;
            }

            if (token is ['=', '='])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.StringEqual, false, default);
                return true;
            }

            if (token is ['!', '='])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.StringNotEqual, false, default);
                return true;
            }

            if (token is ['<'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.StringLess, false, default);
                return true;
            }

            if (token is ['>'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.StringGreater, false, default);
                return true;
            }

            if (token is ['-', 'e', 'f'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.FilePredicate, true, FilePredicateKind.SameFile);
                return true;
            }

            if (token is ['-', 'n', 't'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.FilePredicate, true, FilePredicateKind.NewerThan);
                return true;
            }

            if (token is ['-', 'o', 't'])
            {
                predicate = new BinaryPredicate(BinaryPredicateKind.FilePredicate, true, FilePredicateKind.OlderThan);
                return true;
            }

            predicate = default;
            return false;
        }

        /// <summary>
        /// Maps a file-test token to its corresponding file test kind.
        /// </summary>
        private static bool TryGetFilePredicate(ReadOnlySpan<char> token, out FilePredicateKind fileKind)
        {
            if (token is ['-', 'a'] || token is ['-', 'e'])
            {
                fileKind = FilePredicateKind.Exists;
                return true;
            }

            if (token is ['-', 'b'])
            {
                fileKind = FilePredicateKind.BlockDevice;
                return true;
            }

            if (token is ['-', 'c'])
            {
                fileKind = FilePredicateKind.CharacterDevice;
                return true;
            }

            if (token is ['-', 'd'])
            {
                fileKind = FilePredicateKind.Directory;
                return true;
            }

            if (token is ['-', 'f'])
            {
                fileKind = FilePredicateKind.File;
                return true;
            }

            if (token is ['-', 'g'])
            {
                fileKind = FilePredicateKind.SetGroupId;
                return true;
            }

            if (token is ['-', 'h'] || token is ['-', 'L'])
            {
                fileKind = FilePredicateKind.SymbolicLink;
                return true;
            }

            if (token is ['-', 'k'])
            {
                fileKind = FilePredicateKind.Sticky;
                return true;
            }

            if (token is ['-', 'p'])
            {
                fileKind = FilePredicateKind.Fifo;
                return true;
            }

            if (token is ['-', 'r'])
            {
                fileKind = FilePredicateKind.Readable;
                return true;
            }

            if (token is ['-', 's'])
            {
                fileKind = FilePredicateKind.NonEmpty;
                return true;
            }

            if (token is ['-', 't'])
            {
                fileKind = FilePredicateKind.Terminal;
                return true;
            }

            if (token is ['-', 'u'])
            {
                fileKind = FilePredicateKind.SetUserId;
                return true;
            }

            if (token is ['-', 'w'])
            {
                fileKind = FilePredicateKind.Writable;
                return true;
            }

            if (token is ['-', 'x'])
            {
                fileKind = FilePredicateKind.Executable;
                return true;
            }

            if (token is ['-', 'G'])
            {
                fileKind = FilePredicateKind.OwnedByEffectiveGroup;
                return true;
            }

            if (token is ['-', 'N'])
            {
                fileKind = FilePredicateKind.ModifiedSinceAccess;
                return true;
            }

            if (token is ['-', 'O'])
            {
                fileKind = FilePredicateKind.OwnedByEffectiveUser;
                return true;
            }

            if (token is ['-', 'S'])
            {
                fileKind = FilePredicateKind.Socket;
                return true;
            }

            fileKind = default;
            return false;
        }
    }

    /// <summary>
    /// Evaluates non-filesystem unary predicates directly.
    /// </summary>
    private static bool EvaluateUnary(UnaryPredicateKind kind, string operand)
    {
        return kind switch
        {
            UnaryPredicateKind.StringLengthNonZero => !string.IsNullOrEmpty(operand),
            UnaryPredicateKind.StringLengthZero => string.IsNullOrEmpty(operand),
            _ => false
        };
    }

    /// <summary>
    /// Evaluates non-filesystem binary predicates directly.
    /// </summary>
    private static bool EvaluateBinary(BinaryPredicateKind predicate, string left, string right)
    {
        return predicate switch
        {
            BinaryPredicateKind.NumericEqual => Integers.TryParseNumber(left.AsSpan(), out var l)
                && Integers.TryParseNumber(right.AsSpan(), out var r)
                && l == r,
            BinaryPredicateKind.NumericNotEqual => Integers.TryParseNumber(left.AsSpan(), out var l)
                && Integers.TryParseNumber(right.AsSpan(), out var r)
                && l != r,
            BinaryPredicateKind.NumericLess => Integers.TryParseNumber(left.AsSpan(), out var l)
                && Integers.TryParseNumber(right.AsSpan(), out var r)
                && l < r,
            BinaryPredicateKind.NumericLessOrEqual => Integers.TryParseNumber(left.AsSpan(), out var l)
                && Integers.TryParseNumber(right.AsSpan(), out var r)
                && l <= r,
            BinaryPredicateKind.NumericGreater => Integers.TryParseNumber(left.AsSpan(), out var l)
                && Integers.TryParseNumber(right.AsSpan(), out var r)
                && l > r,
            BinaryPredicateKind.NumericGreaterOrEqual => Integers.TryParseNumber(left.AsSpan(), out var l)
                && Integers.TryParseNumber(right.AsSpan(), out var r)
                && l >= r,
            BinaryPredicateKind.StringEqual => string.Equals(left, right, StringComparison.Ordinal),
            BinaryPredicateKind.StringNotEqual => !string.Equals(left, right, StringComparison.Ordinal),
            BinaryPredicateKind.StringLess => string.CompareOrdinal(left, right) < 0,
            BinaryPredicateKind.StringGreater => string.CompareOrdinal(left, right) > 0,
            _ => false
        };
    }

    /// <summary> Represents a node in the test expression evaluation tree. </summary>
    private abstract class TestNode
    {
        public abstract bool Evaluate(IReadOnlyList<bool> fileResults);
    }

    /// <summary> Represents a constant boolean value in the expression tree. </summary>
    private sealed class ConstantNode : TestNode
    {
        /// <summary> Stores the constant value returned by this node. </summary>
        private readonly bool _value;

        public ConstantNode(bool value)
        {
            _value = value;
        }

        public override bool Evaluate(IReadOnlyList<bool> fileResults)
        {
            return _value;
        }
    }

    /// <summary> Represents a filesystem predicate that must be evaluated externally. </summary>
    private sealed class FilePredicateNode : TestNode
    {
        public FilePredicateNode(FilePredicateKind kind, string operand)
        {
            Kind = kind;
            Operand = operand;
        }

        public FilePredicateKind Kind { get; }

        public string Operand { get; }

        public override bool Evaluate(IReadOnlyList<bool> fileResults)
        {
            return false;
        }
    }

    /// <summary> Represents a binary filesystem predicate that must be evaluated externally. </summary>
    private sealed class FileBinaryPredicateNode : TestNode
    {
        public FileBinaryPredicateNode(FilePredicateKind kind, string left, string right)
        {
            Kind = kind;
            Left = left;
            Right = right;
        }

        public FilePredicateKind Kind { get; }

        public string Left { get; }

        public string Right { get; }

        public override bool Evaluate(IReadOnlyList<bool> fileResults)
        {
            return false;
        }
    }

    /// <summary> Represents a resolved file test that evaluates using the provided results list. </summary>
    private sealed class FileTestNode : TestNode
    {
        /// <summary> Stores the index into the file test result list for this node. </summary>
        private readonly int _index;

        public FileTestNode(int index, UnresolvedFileTest request)
        {
            _index = index;
            Request = request;
        }

        public UnresolvedFileTest Request { get; }

        public override bool Evaluate(IReadOnlyList<bool> fileResults)
        {
            return fileResults[_index];
        }
    }

    /// <summary> Represents a unary logical operation in the expression tree. </summary>
    private sealed class UnaryNode : TestNode
    {
        public UnaryNode(UnaryOperator @operator, TestNode inner)
        {
            Operator = @operator;
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public UnaryOperator Operator { get; }

        public TestNode Inner { get; }

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
    private sealed class BinaryNode : TestNode
    {
        public BinaryNode(BinaryOperator @operator, TestNode left, TestNode right)
        {
            Operator = @operator;
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
        }

        public BinaryOperator Operator { get; }

        public TestNode Left { get; }

        public TestNode Right { get; }

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

    /// <summary> Captures a parsed unary predicate and whether it is a file test. </summary>
    private readonly struct UnaryPredicate
    {
        public UnaryPredicate(UnaryPredicateKind @operator, bool isFilePredicate, FilePredicateKind fileKind)
        {
            Operator = @operator;
            IsFilePredicate = isFilePredicate;
            FileKind = fileKind;
        }

        public UnaryPredicateKind Operator { get; }

        public bool IsFilePredicate { get; }

        public FilePredicateKind FileKind { get; }
    }

    /// <summary> Captures a parsed binary predicate and whether it is a file test. </summary>
    private readonly struct BinaryPredicate
    {
        public BinaryPredicate(BinaryPredicateKind @operator, bool isFilePredicate, FilePredicateKind fileKind)
        {
            Operator = @operator;
            IsFilePredicate = isFilePredicate;
            FileKind = fileKind;
        }

        public BinaryPredicateKind Operator { get; }

        public bool IsFilePredicate { get; }

        public FilePredicateKind FileKind { get; }
    }

    /// <summary> Enumerates unary predicate kinds recognized in test expressions. </summary>
    private enum UnaryPredicateKind
    {
        StringLengthNonZero,
        StringLengthZero,
        FilePredicate
    }

    /// <summary> Enumerates binary predicate kinds recognized in test expressions. </summary>
    private enum BinaryPredicateKind
    {
        NumericEqual,
        NumericNotEqual,
        NumericLess,
        NumericLessOrEqual,
        NumericGreater,
        NumericGreaterOrEqual,
        StringEqual,
        StringNotEqual,
        StringLess,
        StringGreater,
        FilePredicate
    }

    /// <summary> Enumerates unary logical operators in test expressions. </summary>
    private enum UnaryOperator
    {
        Not
    }

    /// <summary> Enumerates binary logical operators in test expressions. </summary>
    private enum BinaryOperator
    {
        And,
        Or
    }
}

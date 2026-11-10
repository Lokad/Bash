using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticEvaluationTests
{
    [Fact]
    public void Evaluate_ParsesLiteral()
    {
        Assert.Equal(42, Eval("42"));
    }

    [Fact]
    public void Evaluate_Utf8UsesSigned64BitValues()
    {
        Assert.Equal(long.MinValue, ArithmeticEvaluation.Evaluate("9223372036854775807 + 1"u8));
    }

    [Fact]
    public void Evaluate_TrimsWhitespace()
    {
        Assert.Equal(7, Eval(" \t 7 \n "));
    }

    [Fact]
    public void Evaluate_AdditionAndSubtraction()
    {
        Assert.Equal(0, Eval("1+2-3"));
    }

    [Fact]
    public void Evaluate_MultiplicationPrecedence()
    {
        Assert.Equal(14, Eval("2+3*4"));
    }

    [Fact]
    public void Evaluate_ParenthesesOverridePrecedence()
    {
        Assert.Equal(20, Eval("(2+3)*4"));
    }

    [Fact]
    public void Evaluate_UnaryOperators()
    {
        Assert.Equal(-3, Eval("-5+2"));
        Assert.Equal(5, Eval("+5"));
    }

    [Theory]
    [InlineData("0++1", 1)]
    [InlineData("0--1", 1)]
    [InlineData("(3)++1", 4)]
    [InlineData("(3)--1", 4)]
    public void Evaluate_AdjacentBinaryAndUnarySigns(string expression, long expected)
    {
        Assert.Equal(expected, Eval(expression));
    }

    [Fact]
    public void Evaluate_AdjacentSignsAfterPostfixMutation()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["x"] = 3
        };

        Assert.Equal(4, Eval("x++++1", variables));
        Assert.Equal(4, variables["x"]);

        variables["x"] = 3;
        Assert.Equal(4, Eval("x----1", variables));
        Assert.Equal(2, variables["x"]);
    }

    [Fact]
    public void Evaluate_DivisionAndModulo()
    {
        Assert.Equal(4, Eval("9/2"));
        Assert.Equal(1, Eval("9%2"));
    }

    [Fact]
    public void Evaluate_ShiftOperators()
    {
        Assert.Equal(8, Eval("1 << 3"));
        Assert.Equal(4, Eval("16 >> 2"));
        Assert.Equal(8, Eval("1 << 2 + 1"));
    }

    [Theory]
    [InlineData("9223372036854775807 + 1", long.MinValue)]
    [InlineData("-9223372036854775808 - 1", long.MaxValue)]
    [InlineData("3037000500 * 3037000500", -9223372036709301616L)]
    [InlineData("-9223372036854775808 / -1", long.MinValue)]
    [InlineData("-9223372036854775808 % -1", 0L)]
    [InlineData("2 ** 63", long.MinValue)]
    [InlineData("2 << 62", long.MinValue)]
    [InlineData("1 << 64", 1L)]
    [InlineData("1 << -1", long.MinValue)]
    public void Evaluate_WrapsSigned64BitOperationsLikeBash(string expression, long expected)
    {
        Assert.Equal(expected, Eval(expression));
    }

    [Fact]
    public void Evaluate_BitwiseOperators()
    {
        Assert.Equal(2, Eval("6 & 3"));
        Assert.Equal(4, Eval("7 ^ 3"));
        Assert.Equal(5, Eval("4 | 1"));
        Assert.Equal(1, Eval("1 | 2 & 0"));
    }

    [Fact]
    public void Evaluate_LogicalOperators()
    {
        Assert.Equal(1, Eval("2 && 3"));
        Assert.Equal(1, Eval("0 || 5"));
        Assert.Equal(0, Eval("0 && 1 / 0"));
        Assert.Equal(1, Eval("1 || 1 / 0"));
    }

    [Fact]
    public void Evaluate_UnaryLogicalAndBitwiseOperators()
    {
        Assert.Equal(1, Eval("!0"));
        Assert.Equal(0, Eval("!5"));
        Assert.Equal(-1, Eval("~0"));
    }

    [Fact]
    public void Evaluate_Exponentiation()
    {
        Assert.Equal(8, Eval("2 ** 3"));
        Assert.Equal(512, Eval("2 ** 3 ** 2"));
        Assert.Equal(1, Eval("1 ** 2147483647"));
        Assert.Equal(-1, Eval("(-1) ** 2147483647"));
        Assert.Equal(0, Eval("2 ** 2147483647"));
    }

    [Fact]
    public void Evaluate_AssignmentsAndIncrements()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal);

        Assert.Equal(3, Eval("x = 1 + 2", variables));
        Assert.Equal(3, variables["x"]);

        Assert.Equal(12, Eval("x <<= 2", variables));
        Assert.Equal(12, variables["x"]);

        Assert.Equal(12, Eval("x++", variables));
        Assert.Equal(13, variables["x"]);

        Assert.Equal(14, Eval("++x", variables));
        Assert.Equal(14, variables["x"]);
    }

    [Fact]
    public void Evaluate_IncrementsWrapAtSigned64BitBoundaries()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["x"] = long.MaxValue
        };

        Assert.Equal(long.MaxValue, Eval("x++", variables));
        Assert.Equal(long.MinValue, variables["x"]);
        Assert.Equal(long.MaxValue, Eval("--x", variables));
        Assert.Equal(long.MaxValue, variables["x"]);
    }

    [Theory]
    [InlineData("x *= 3", 6)]
    [InlineData("x /= 2", 1)]
    [InlineData("x %= 2", 0)]
    [InlineData("x += 3", 5)]
    [InlineData("x -= 3", -1)]
    [InlineData("x <<= 2", 8)]
    [InlineData("x >>= 1", 1)]
    [InlineData("x &= 3", 2)]
    [InlineData("x ^= 3", 1)]
    [InlineData("x |= 1", 3)]
    public void Evaluate_CompoundAssignments(string expression, long expected)
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["x"] = 2
        };

        Assert.Equal(expected, Eval(expression, variables));
        Assert.Equal(expected, variables["x"]);
    }

    [Fact]
    public void Evaluate_CompoundAssignmentReadsLeftOperandBeforeRightOperand()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal);

        Assert.Equal(6, Eval("x = 1, x += (x = 5), x", variables));
        Assert.Equal(6, variables["x"]);
    }

    [Fact]
    public void Evaluate_AssignmentStateRemainsVisibleWithoutCommitCallback()
    {
        Assert.Equal(3, Eval("x = 1, x += 2, x"));
        Assert.Equal(1, Eval("x = 1, x"));
        Assert.Equal(
            1,
            ArithmeticEvaluation.Evaluate("x = 1, x".AsSpan(), static _ => 9));
    }

    [Fact]
    public void Evaluate_InvalidExpressionDoesNotAssign()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["x"] = 10
        };

        Assert.Equal(0, Eval("x++,", variables));
        Assert.Equal(10, variables["x"]);

        Assert.Equal(0, Eval("y = 1, 1 / 0", variables));
        Assert.False(variables.ContainsKey("y"));
    }

    [Fact]
    public void Evaluate_ConditionalAndCommaOperators()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal);

        Assert.Equal(7, Eval("1 ? 7 : 1 / 0", variables));
        Assert.Equal(9, Eval("0 ? 1 / 0 : 9", variables));
        Assert.Equal(3, Eval("a = 1, a += 2", variables));
        Assert.Equal(3, variables["a"]);
    }

    [Fact]
    public void Evaluate_ConditionalFalseArmAssignmentRequiresParentheses()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal);

        Assert.Equal(0, Eval("0 ? 1 : x = 4", variables));
        Assert.False(variables.ContainsKey("x"));

        Assert.Equal(4, Eval("0 ? 1 : (x = 4)", variables));
        Assert.Equal(4, variables["x"]);
    }

    [Fact]
    public void Evaluate_DivisionByZeroReturnsZero()
    {
        Assert.Equal(0, Eval("1/0"));
    }

    [Fact]
    public void Evaluate_InvalidExpressionReturnsZero()
    {
        Assert.Equal(0, Eval("1+"));
        Assert.Equal(0, Eval("abc"));
        Assert.Equal(0, Eval("(1+2"));
    }

    [Theory]
    [InlineData("2147483648", 2147483648L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9223372036854775808", long.MinValue)]
    [InlineData("18446744073709551615", -1L)]
    [InlineData("18446744073709551616", 0L)]
    [InlineData("18446744073709551617", 1L)]
    [InlineData("-18446744073709551617", -1L)]
    [InlineData("999999999999999999999999999999999999", -5527149226598858753L)]
    public void Evaluate_WrapsDecimalLiterals(string expression, long expected)
    {
        Assert.Equal(expected, Eval(expression));
        Assert.True(ArithmeticEvaluation.TryParseDecimal(expression.AsSpan(), out var textValue));
        Assert.True(ArithmeticEvaluation.TryParseDecimal(ShellValue.FromText(expression).Bytes.Span, out var byteValue));
        Assert.Equal(expected, textValue);
        Assert.Equal(expected, byteValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(" 1")]
    [InlineData("1x")]
    [InlineData("é")]
    public void DecimalParsingRejectsNonAsciiNumbers(string text)
    {
        Assert.False(ArithmeticEvaluation.TryParseDecimal(text.AsSpan(), out _));
        Assert.False(ArithmeticEvaluation.TryParseDecimal(ShellValue.FromText(text).Bytes.Span, out _));
    }

    [Fact]
    public void LiteralChainsDoNotAllocatePerOperandContinuations()
    {
        var shortChain = ArithmeticOperand.FromValue(ShellValue.FromText(string.Join('+', Enumerable.Repeat("1", 100))));
        var longChain = ArithmeticOperand.FromValue(ShellValue.FromText(string.Join('+', Enumerable.Repeat("1", 1000))));
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(100, Evaluate(shortChain));
            Assert.Equal(1000, Evaluate(longChain));
        }

        var shortAllocation = Measure(shortChain);
        var longAllocation = Measure(longChain);
        // Prepared input excludes text-copy costs; leave room for runtime overhead, not per-operand frames.
        Assert.True(longAllocation < shortAllocation + 32 * 1024,
            $"Allocated {shortAllocation} / {longAllocation} bytes for short / long literal chains.");

        static long Measure(ArithmeticOperand expression)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 16; index++)
                Evaluate(expression);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        static long Evaluate(ArithmeticOperand expression)
        {
            var result = ArithmeticEvaluation.EvaluateAsync<string>(expression,
                static (_, _) => throw new InvalidOperationException("Literal arithmetic must not bind variables."),
                static (_, _, _) => throw new InvalidOperationException("Literal arithmetic must not assign variables."));
            Assert.True(result.IsCompletedSuccessfully);
            return result.Result.Value;
        }
    }

    [Fact]
    public void StoredVariableReadsDoNotAllocateDecodedCopies()
    {
        var bytes = new byte[64 * 1024];
        bytes.AsSpan().Fill((byte)'0');
        bytes[^1] = (byte)'1';
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("p", ShellValue.FromBytes(bytes)));
        var target = new WordExpansion.ResolvedVariableReference.Scalar("p");
        // Warm a full batch before measuring to exclude first-use overhead.
        for (var index = 0; index < 16; index++)
            Assert.Equal(1, target.ReadArithmetic(variables));

        var before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        for (var index = 0; index < 16; index++)
            sum += target.ReadArithmetic(variables);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(16, sum);
        // Allow runtime overhead while staying below a single 128 KiB decoded copy.
        Assert.True(allocated < 32 * 1024, $"Allocated {allocated} bytes while reading stored numbers.");
        Assert.True(variables.TrySetScalar("p", ShellValue.FromBytes(new byte[] { 0xff })));
        Assert.Equal(0, target.ReadArithmetic(variables));
    }

    [Theory]
    [InlineData("1 << 4294967296", 1L)]
    [InlineData("1 >> -1", 0L)]
    [InlineData("-9223372036854775808 >> 63", -1L)]
    [InlineData("0 ** 0", 1L)]
    [InlineData("(-1) ** 9223372036854775807", -1L)]
    [InlineData("x=9223372036854775807, x+=1", long.MinValue)]
    [InlineData("x=-9223372036854775808, x/=-1", long.MinValue)]
    public void Evaluate_UsesFullWidthOperands(string expression, long expected)
    {
        Assert.Equal(expected, Eval(expression));
    }

    [Fact]
    public void Evaluate_RejectsExcessiveAssignmentDepth()
    {
        var expression = string.Concat(Enumerable.Repeat("value=", 1_000)) + "1";

        Assert.Equal(0, Eval(expression));
    }

    [Theory]
    [InlineData("++1", 1L, 3L)]
    [InlineData("--1", 1L, 3L)]
    [InlineData("++++1", 1L, 3L)]
    [InlineData("----1", 1L, 3L)]
    [InlineData("++(x)", 3L, 3L)]
    [InlineData("--(x)", 3L, 3L)]
    [InlineData("++ x", 4L, 4L)]
    [InlineData("-- x", 2L, 2L)]
    [InlineData("++(x=5)", 5L, 5L)]
    [InlineData("0++++x", 4L, 4L)]
    [InlineData("0----x", 2L, 2L)]
    [InlineData("++x + x++", 8L, 5L)]
    public void Evaluate_DistinguishesUnarySignsFromPrefixMutation(string expression, long expected, long assigned)
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal) { ["x"] = 3 };

        Assert.Equal(expected, Eval(expression, variables));
        Assert.Equal(assigned, variables["x"]);
    }

    [Theory]
    [InlineData(" ", 5L)]
    [InlineData("\t", 5L)]
    [InlineData("\n", 5L)]
    [InlineData("\r", 0L)]
    [InlineData("\v", 0L)]
    [InlineData("\f", 0L)]
    [InlineData("\u00a0", 0L)]
    public void Evaluate_OnlySkipsArithmeticBlanks(string separator, long expected)
    {
        Assert.Equal(expected, Eval("2" + separator + "+3"));
    }

    [Theory]
    [InlineData("++x++")]
    [InlineData("--x--")]
    [InlineData("x++--")]
    [InlineData("(x)++")]
    [InlineData("x++++")]
    [InlineData("1+=2")]
    public void Evaluate_InvalidMutationDoesNotCommitHelperAssignments(string expression)
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal) { ["x"] = 3 };

        Assert.Equal(0, Eval(expression, variables));
        Assert.Equal(3, variables["x"]);
    }

    [Theory]
    [InlineData("value=", "")]
    [InlineData("(", ")")]
    [InlineData("+", "")]
    [InlineData("1**", "")]
    [InlineData("0?1:", "")]
    [InlineData("1?", ":0")]
    public void Evaluate_BoundsEveryRecursiveGrammarEdge(string prefix, string suffix)
    {
        var depth = ShellSourceCursor.MaximumNestedExpansionDepth;
        Assert.Equal(1, Eval(Build(depth)));
        Assert.Equal(0, Eval(Build(depth + 1)));

        string Build(int count) =>
            string.Concat(Enumerable.Repeat(prefix, count)) + "1" + string.Concat(Enumerable.Repeat(suffix, count));
    }

    [Fact]
    public void Evaluate_RejectsDeepUnselectedArmsWithoutCommittingHelperAssignments()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal) { ["x"] = 3 };
        var deep = new string('+', ShellSourceCursor.MaximumNestedExpansionDepth + 1) + "1";

        Assert.Equal(0, Eval("x=20, 1 || " + deep, variables));
        Assert.Equal(3, variables["x"]);
    }

    [Fact]
    public void Evaluate_ReleasesDepthBetweenFlatOperands()
    {
        Assert.Equal(1000, Eval(string.Join("+", Enumerable.Repeat("(1)", 1000))));
        Assert.Equal(1, Eval(string.Concat(Enumerable.Repeat("value=1,", 1000)) + "value"));
    }

    [Fact]
    public void Evaluate_ArrayElementIsOneTypedAssignmentTarget()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["arr[index[0]]"] = 5
        };

        Assert.Equal(7, Eval("arr[index[0]] += 2", variables));
        Assert.Equal(7, variables["arr[index[0]]"]);
    }

    [Fact]
    public void Evaluate_ArraySubscriptSkipsNestedExpansionBrackets()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["map[$(printf ']')]"] = 4
        };

        Assert.Equal(5, Eval("map[$(printf ']')] + 1", variables));
    }

    [Fact]
    public void Evaluate_ArraySubscriptSkipsDoubleQuotedNestedExpansionBrackets()
    {
        const string Target = """map["$(ignored="]"; printf key)"]""";
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [Target] = 4
        };

        Assert.Equal(5, Eval(Target + " + 1", variables));
    }

    [Fact]
    public void Evaluate_FailedNestedHelperDiscardsItsStagedAssignments()
    {
        var committed = new List<KeyValuePair<string, long>>();
        var success = ArithmeticEvaluation.TryEvaluate(
            ShellValue.FromText("array[(index=1, 1/0)]"), Resolve,
            (_, _) => throw new InvalidOperationException("Unexpected outer assignment"), out var value, out var error);

        Assert.True(success);
        Assert.Equal(0, value);
        Assert.Null(error);
        Assert.Empty(committed);

        long Resolve(string reference)
        {
            Assert.Equal("array[(index=1, 1/0)]", reference);
            return ArithmeticEvaluation.Evaluate("(index=1, 1/0)".AsSpan(), static _ => 0,
                (name, assigned) => committed.Add(KeyValuePair.Create(name, assigned)));
        }
    }

    [Fact]
    public void Evaluate_TextCallbacksRejectNonUtf8TargetsWithoutDecodingOrInvocation()
    {
        var expression = ShellValue.FromBytes(new byte[] { (byte)'a', (byte)'[', 0xff, (byte)']' });

        Assert.Equal(0, ArithmeticEvaluation.Evaluate(expression.Bytes.Span));
        Assert.False(ArithmeticEvaluation.TryEvaluate(
            expression,
            _ => throw new InvalidOperationException("Unrepresentable read target"),
            (_, _) => throw new InvalidOperationException("Unrepresentable write target"),
            out var value, out var error));
        Assert.Equal(0, value);
        Assert.Equal(new ArithmeticError(ArithmeticErrorKind.InvalidTarget, expression), error);
    }

    private static long Eval(string expression)
    {
        return ArithmeticEvaluation.Evaluate(expression.AsSpan());
    }

    private static long Eval(string expression, Dictionary<string, long> variables)
    {
        return ArithmeticEvaluation.Evaluate(
            expression.AsSpan(),
            name => variables.GetValueOrDefault(name),
            (name, value) => variables[name] = value);
    }
}

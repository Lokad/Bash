using System.Collections.Generic;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ConditionalEvaluationTests
{
    [Fact]
    public void ConditionalEvaluation_EmptyExpressionIsFalse()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8(""));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.False(result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_SingleTokenNonEmptyIsTrue()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("value"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.True(result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_ParseError_UnterminatedQuote()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("\"unterminated"));

        Assert.True(evaluation.HasParseError);
    }

    [Fact]
    public void ConditionalEvaluation_ParseError_UnbalancedParenthesis()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("( -n a"));

        Assert.True(evaluation.HasParseError);
    }

    [Fact]
    public void ConditionalEvaluation_ParseError_MissingOperand()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("-n"));

        Assert.True(evaluation.HasParseError);
    }

    [Fact]
    public void ConditionalEvaluation_RespectsOperatorPrecedence()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("-n a || -n \"\" && -n b"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.True(result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_ParenthesesOverridePrecedence()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("( -n \"\" || -n a ) && -n \"\""));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.False(result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_NotOperatorNegates()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("! -n a"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.False(result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_SupportsPackedOperators()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("a==a"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.True(result.Value);
    }

    [Theory]
    [MemberData(nameof(PatternMatchData))]
    public void ConditionalEvaluation_EvaluatesPatternMatches(
        string left,
        string op,
        string right,
        bool expected)
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8($"{left} {op} {right}"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [MemberData(nameof(RegexMatchData))]
    public void ConditionalEvaluation_EvaluatesRegexMatches(
        string left,
        string op,
        string right,
        bool expected)
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8($"{left} {op} {right}"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_InvalidRegexIsParseError()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("foo =~ *"));

        Assert.True(evaluation.HasParseError);
    }

    [Theory]
    [MemberData(nameof(NumericComparisonData))]
    public void ConditionalEvaluation_EvaluatesNumericComparisons(
        string left,
        string op,
        string right,
        bool expected)
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8($"{left} {op} {right}"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_InvalidNumberReturnsFalse()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("a -eq 1"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.False(result.Value);
    }

    [Theory]
    [MemberData(nameof(StringComparisonData))]
    public void ConditionalEvaluation_EvaluatesStringComparisons(
        string left,
        string op,
        string right,
        bool expected)
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8($"{left} {op} {right}"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void ConditionalEvaluation_CollectsTerminalPredicate()
    {
        var evaluation = ConditionalEvaluation.Parse("-t 0");

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Null(result);
        Assert.Single(fileTests);
        Assert.Equal(FilePredicateKind.Terminal, fileTests[0].Kind);
        Assert.Equal("0", fileTests[0].Operand);
    }

    [Fact]
    public void ConditionalEvaluation_CollectsSameFilePredicate()
    {
        var evaluation = ConditionalEvaluation.Parse("left -ef right");

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Null(result);
        Assert.Single(fileTests);
        Assert.Equal(FilePredicateKind.SameFile, fileTests[0].Kind);
        Assert.Equal("left", fileTests[0].Operand);
        Assert.Equal("right", fileTests[0].SecondaryOperand);
    }

    [Fact]
    public void ConditionalEvaluation_CompletesFileTestsWithLogicalOperators()
    {
        var evaluation = ConditionalEvaluation.Parse(Utf8("-f file && -n a"));

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Null(result);
        Assert.Single(fileTests);

        var finalResult = evaluation.CompleteFileTests([true]);
        Assert.True(finalResult);
    }

    public static IEnumerable<object[]> NumericComparisonData()
    {
        yield return ["1", "-eq", "1", true];
        yield return ["1", "-ne", "2", true];
        yield return ["1", "-lt", "2", true];
        yield return ["2", "-le", "2", true];
        yield return ["3", "-gt", "2", true];
        yield return ["3", "-ge", "3", true];
        yield return ["-2", "-lt", "0", true];
        yield return ["-1", "-gt", "1", false];
    }

    public static IEnumerable<object[]> StringComparisonData()
    {
        yield return ["a", "=", "a", true];
        yield return ["a", "==", "a", true];
        yield return ["a", "!=", "b", true];
        yield return ["a", "<", "b", true];
        yield return ["b", ">", "a", true];
    }

    public static IEnumerable<object[]> PatternMatchData()
    {
        yield return ["foo", "==", "f*", true];
        yield return ["foo", "==", "f?o", true];
        yield return ["foo", "==", "f[oa]o", true];
        yield return ["foo", "!=", "f*", false];
        yield return ["bar", "!=", "f*", true];
    }

    public static IEnumerable<object[]> RegexMatchData()
    {
        yield return ["foo", "=~", "^f.*o$", true];
        yield return ["foo", "=~", "^bar", false];
    }

    [Theory]
    [InlineData("-a", null)]
    [InlineData("-e", null)]
    [InlineData("-d", FilePathKind.Directory)]
    [InlineData("-f", FilePathKind.File)]
    [InlineData("-h", FilePathKind.SymbolicLink)]
    [InlineData("-L", FilePathKind.SymbolicLink)]
    public void ConditionalEvaluation_CollectsPathKindPredicates(string op, FilePathKind? requiredKind)
    {
        var evaluation = ConditionalEvaluation.Parse($"{op} path");
        var test = Assert.Single(evaluation.CollectFileTests(out var result));
        Assert.Null(result);
        Assert.False(test.Kind.IsTerminal);
        Assert.Equal(requiredKind, test.Kind.RequiredKind);
        Assert.Equal("path", test.Operand);
    }

    /// <summary>
    /// Passes through a string value for test inputs.
    /// </summary>
    private static string Utf8(string value) => value;
}

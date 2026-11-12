using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ConditionalEvaluationTestsEndToEnd
{
    [Fact]
    public async Task ConditionalEvaluation_EmptyExpressionIsASyntaxError()
    {
        var result = await RunConditionAsync(string.Empty);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("syntax error", result.StdErr);
    }

    [Fact]
    public async Task ConditionalEvaluation_SingleTokenNonEmptyIsTrue()
    {
        var result = await RunConditionAsync("value");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_ParseError_UnterminatedQuote()
    {
        var result = await RunConditionAsync("\"unterminated");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_ParseError_UnbalancedParenthesis()
    {
        var result = await RunConditionAsync("( -n a");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_ParseError_MissingOperand()
    {
        var result = await RunConditionAsync("-n");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_RespectsOperatorPrecedence()
    {
        var result = await RunConditionAsync("-n a || -n \"\" && -n b");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_ParenthesesOverridePrecedence()
    {
        var result = await RunConditionAsync("( -n \"\" || -n a ) && -n \"\"");
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_NotOperatorNegates()
    {
        var result = await RunConditionAsync("! -n a");
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_SupportsPackedOperators()
    {
        var result = await RunConditionAsync("a==a");
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [MemberData(nameof(PatternMatchData))]
    public async Task ConditionalEvaluation_EvaluatesPatternMatches(
        string left,
        string op,
        string right,
        bool expected)
    {
        var result = await RunConditionAsync($"{left} {op} {right}");
        Assert.Equal(expected ? 0 : 1, result.ExitCode);
    }

    [Theory]
    [MemberData(nameof(RegexMatchData))]
    public async Task ConditionalEvaluation_EvaluatesRegexMatches(
        string left,
        string op,
        string right,
        bool expected)
    {
        var result = await RunConditionAsync($"{left} {op} {right}");
        Assert.Equal(expected ? 0 : 1, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_InvalidRegexIsParseError()
    {
        var result = await RunConditionAsync("foo =~ *");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [MemberData(nameof(NumericComparisonData))]
    public async Task ConditionalEvaluation_EvaluatesNumericComparisons(
        string left,
        string op,
        string right,
        bool expected)
    {
        var result = await RunConditionAsync($"{left} {op} {right}");
        Assert.Equal(expected ? 0 : 1, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_InvalidNumberReturnsFalse()
    {
        var result = await RunConditionAsync("a -eq 1");
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [MemberData(nameof(StringComparisonData))]
    public async Task ConditionalEvaluation_EvaluatesStringComparisons(
        string left,
        string op,
        string right,
        bool expected)
    {
        var result = await RunConditionAsync($"{left} {op} {right}");
        Assert.Equal(expected ? 0 : 1, result.ExitCode);
    }

    [Theory]
    [MemberData(nameof(FilePredicateData))]
    public async Task ConditionalEvaluation_CollectsUnaryFilePredicates(string op)
    {
        var tree = EndToEndHarness.BuildTree(["/dir"], [("/file", "content\n")]);
        var path = "/file";
        var result = await EndToEndHarness.RunScriptAsync($"[[ {op} {path} ]]", tree);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task ConditionalEvaluation_RecognizesDistinctFiles()
    {
        var now = DateTimeOffset.UtcNow;
        var tree = EndToEndHarness.BuildTree(
            [("/dir", now)],
            [("/left", "one\n", now), ("/right", "two\n", now)]);
        var result = await EndToEndHarness.RunScriptAsync("[[ /left -ef /right ]]", tree);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("old", "-ot", "new", 0)]
    [InlineData("new", "-nt", "old", 0)]
    [InlineData("old", "-nt", "new", 1)]
    [InlineData("new", "-ot", "old", 1)]
    [InlineData("new", "-nt", "same", 1)]
    [InlineData("new", "-ot", "same", 1)]
    [InlineData("new", "-nt", "new", 1)]
    [InlineData("new", "-ot", "new", 1)]
    [InlineData("new", "-nt", "missing", 0)]
    [InlineData("missing", "-ot", "new", 0)]
    [InlineData("new", "-ot", "missing", 1)]
    [InlineData("missing", "-nt", "new", 1)]
    [InlineData("missing", "-nt", "also-missing", 1)]
    [InlineData("missing", "-ot", "also-missing", 1)]
    [InlineData("dir", "-ot", "new", 0)]
    [InlineData("new", "-nt", "dir", 0)]
    [InlineData("new", "-nt", "", 0)]
    [InlineData("", "-ot", "new", 0)]
    [InlineData("new", "-ot", "", 1)]
    [InlineData("", "-nt", "new", 1)]
    [InlineData("", "-nt", "missing", 1)]
    [InlineData("missing", "-ot", "", 1)]
    [InlineData("", "-nt", "", 1)]
    [InlineData("", "-ot", "", 1)]
    [InlineData("new/", "-nt", "missing", 1)]
    [InlineData("missing", "-ot", "new/", 1)]
    [InlineData("new", "-nt", "old/", 0)]
    [InlineData("old/", "-ot", "new", 0)]
    [InlineData("dir/", "-ot", "new", 0)]
    [InlineData("new", "-nt", "dir/", 0)]
    [InlineData("missing-parent/../new", "-nt", "missing", 1)]
    [InlineData("missing", "-ot", "missing-parent/../new", 1)]
    [InlineData("new", "-nt", "missing-parent/../old", 0)]
    [InlineData("missing-parent/../old", "-ot", "new", 0)]
    [InlineData("old/../new", "-nt", "missing", 1)]
    [InlineData("missing", "-ot", "old/../new", 1)]
    [InlineData("dir/../new", "-nt", "old", 0)]
    [InlineData("old", "-ot", "dir/../new", 0)]
    [InlineData("../new", "-nt", "old", 0)]
    [InlineData("old", "-ot", "../../dir/../new", 0)]
    [InlineData("missing-parent/../../new", "-nt", "missing", 1)]
    [InlineData("missing", "-ot", "../old/../new", 1)]
    public async Task FileTimeComparisons_UseModificationTimesAndMissingOperandRules(
        string left, string op, string right, int expected)
    {
        var oldTime = DateTimeOffset.UnixEpoch;
        var newTime = oldTime.AddSeconds(1);
        var tree = EndToEndHarness.BuildTree(
            [("/dir", oldTime)],
            [("/old", "one\n", oldTime), ("/new", "two\n", newTime), ("/same", "three\n", newTime)]);
        var leftOperand = left.Length == 0 ? "''" : $"/{left}";
        var rightOperand = right.Length == 0 ? "''" : $"/{right}";
        foreach (var script in new[]
        {
            $"test {leftOperand} {op} {rightOperand}",
            $"[ {leftOperand} {op} {rightOperand} ]",
            $"[[ {leftOperand} {op} {rightOperand} ]]"
        })
        {
            var result = await EndToEndHarness.RunScriptAsync(script, tree);
            Assert.Equal(expected, result.ExitCode);
            Assert.Empty(result.StdOut);
            Assert.Empty(result.StdErr);
        }
    }

    [Fact]
    public async Task ConditionalEvaluation_CompletesFileTestsWithLogicalOperators()
    {
        var tree = EndToEndHarness.BuildTree(["/dir"], [("/file", "content\n")]);
        var result = await EndToEndHarness.RunScriptAsync("[[ -f /file && -n a ]]", tree);
        Assert.Equal(0, result.ExitCode);
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
    [InlineData("test -a /dir && [[ -e /file ]]", 0)]
    [InlineData("test -f /file && [[ -d /dir ]]", 0)]
    [InlineData("test -d /file || [[ -f /dir ]]", 1)]
    [InlineData("test -e /missing || [[ -a /missing ]]", 1)]
    [InlineData("[[ ! -b /file && ! -c /dir && ! -p /missing && ! -S /file ]]", 0)]
    public async Task PathKinds_DistinguishFilesDirectoriesAndMissingPaths(string script, int exitCode)
    {
        var tree = EndToEndHarness.BuildTree(["/dir"], [("/file", "content\n")]);
        var result = await EndToEndHarness.RunScriptAsync(script, tree);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("file", 0)]
    [InlineData("empty", 1)]
    [InlineData("missing", 1)]
    [InlineData("dir", 1)]
    [InlineData("''", 1)]
    [InlineData("file/", 1)]
    [InlineData("file/.", 1)]
    [InlineData("file/../file", 1)]
    [InlineData("missing/../file", 1)]
    [InlineData("dir/../file", 0)]
    [InlineData("dir//../file", 0)]
    [InlineData("/../../file", 0)]
    [InlineData("dir/../../file", 0)]
    public async Task NonEmptyPredicates_PreserveSizeAndTraversalSemantics(string operand, int exitCode)
    {
        foreach (var format in new[] { "test -s {0}", "[ -s {0} ]", "[[ -s {0} ]]" })
        {
            var tree = EndToEndHarness.BuildTree(["/dir"], [("/file", "content"), ("/empty", "")]);
            var result = await EndToEndHarness.RunScriptAsync(string.Format(format, operand), tree);
            Assert.Equal(exitCode, result.ExitCode);
            Assert.Empty(result.StdOut);
            Assert.Empty(result.StdErr);
        }
    }

    public static IEnumerable<object[]> FilePredicateData()
    {
        yield return ["-h"];
        yield return ["-L"];
        yield return ["-t"];
    }

    private static async Task<EndToEndHarness.ExecutionResult> RunConditionAsync(string expression)
    {
        var tree = EndToEndHarness.BuildTree(["/dir"], [("/file", "content\n")]);
        var script = string.IsNullOrEmpty(expression) ? "[[ ]]" : $"[[ {expression} ]]";
        var result = await tree.RunAsync(script, CancellationToken.None);
        return new(result.ExitCode, tree.StandardOutput.TextContent, tree.StandardError.TextContent, tree);
    }
}

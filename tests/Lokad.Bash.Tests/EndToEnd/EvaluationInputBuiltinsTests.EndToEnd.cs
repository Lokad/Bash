using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationInputBuiltinsTestsEndToEnd
{
    [Theory]
    [InlineData("a=(outer tail); a=temp printf -v \"a[0]\" %s body; printf \"%s:%s\" \"${a[0]}\" \"${a[1]}\"", "body:tail")]
    [InlineData("a=(outer tail); a=temp printf -v \"a[2]\" %s body; printf \"%s:%s:%s\" \"${a[0]}\" \"${a[1]}\" \"${a[2]}\"", "outer:tail:body")]
    [InlineData("a=(outer tail); a=temp printf -v a %s body; printf \"%s:%s\" \"${a[0]}\" \"${a[1]}\"", "body:tail")]
    [InlineData("a=(outer tail); a=temp printf \"%nX%n\" a a; printf \"%s:%s\" \"${a[0]}\" \"${a[1]}\"", "X1:tail")]
    [InlineData("x=outer; declare -n ref=x; x=temp printf -v ref %s body; printf \"%s\" \"$x\"", "outer")]
    [InlineData("x=outer; declare -n ref=x; ref=temp printf -v ref %s body; printf \"%s\" \"$x\"", "outer")]
    [InlineData("x=outer; declare -n ref=x; x=temp printf \"%nX%n\" x ref; printf \"%s\" \"$x\"", "X0")]
    [InlineData("x=outer; f() { declare -n ref=x; x=temp printf -v ref %s body; }; f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=outer; f() { declare -n ref=x; x=temp printf -v ref %s body; }; x=prefix f; printf \"%s\" \"$x\"", "outer")]
    [InlineData("x=outer; f() { declare -n ref=x; g() { x=temp printf -v ref %s body; }; g; }; f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=outer; f() { declare -n ref=x; result=$(x=temp printf -v ref %s body; printf \"%s\" \"$x\"); printf \"%s/%s\" \"$result\" \"$x\"; }; f", "body/outer")]
    [InlineData("x=outer; x=temp printf -v x %s body; sh -c 'printf \"%s\" \"${x-unset}\"'", "unset")]
    [InlineData("declare -n ref; ref=temp printf -v ref %s body; printf \"<%s>\" \"${ref-unset}\"", "<unset>")]
    public async Task Evaluation_PrintfPrefix_PreservesCallerOutputsAndNamerefScope(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_ReadBuiltin_PreservesPreparationAndInputAssignments()
    {
        var script = @"unset prepared; X=${prepared:=temporary} read value; echo ${prepared-unset}/$value";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "inner\n");

        Assert.Equal("temporary/inner\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_AssignmentPrefixProtectsTargetArray()
    {
        var script = @"arr=(outer); arr=(temp) mapfile arr; echo ${arr[0]}";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "inner\n");

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_AssignmentPrefixProtectsTargetVariable()
    {
        var script = @"value=outer; value=temp read value; echo $value";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "inner\n");

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Theory]
    [InlineData("printf '<%s>' a b c", "<a><b><c>")]
    [InlineData("printf '%s\\n' a b", "a\nb\n")]
    [InlineData("printf '%b' 'a\\nb'", "a\nb")]
    [InlineData("printf '%03d\\n' 7", "007\n")]
    [InlineData("printf '<%s><%s>' a", "<a><>")]
    public async Task Evaluation_PrintfBuiltin_MatchesBashFormatting(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_AssignsOutputWithDashV()
    {
        var script = "printf -v result '%03d:%q' 7 'a b'; printf '<%s>\\n' \"$result\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<007:a\\ b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_DashVSupportsArrayElements()
    {
        var result = await EvaluationTestHelper.RunAsync("printf -v 'result[2]' '%s' value; printf '<%s>' \"${result[2]}\"");

        Assert.Equal("<value>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_QuotedOutputCanBeEvaluatedAsShellInput()
    {
        var script = "printf -v quoted '%q' 'a b'; eval \"value=$quoted\"; printf '<%s>' \"$value\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<a b>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_DoubleDashAllowsDashPrefixedFormat()
    {
        var result = await EvaluationTestHelper.RunAsync("printf -- '-%s' value");

        Assert.Equal("-value", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_InvalidOptionReturnsUsageError()
    {
        var result = await EvaluationTestHelper.RunAsync("printf -x");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Contains("invalid option", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("usage: printf", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_AssignsWrittenCountWithPercentN()
    {
        var result = await EvaluationTestHelper.RunAsync("printf 'abc%n' count; printf '%s\\n' \"$count\"");

        Assert.Equal("abc3\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_ReportsConversionErrorsAndContinues()
    {
        var script = "printf '%d\\n' nope; status=$?; printf 'status:%d\\n' \"$status\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0\nstatus:1\n", result.StdOut);
        Assert.Contains("printf: nope: invalid number\n", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_DashVPreservesNonUtf8Output()
    {
        var script = "printf -v result '\\xff'; status=$?; printf 'status:%s value:<%s>\\n' \"$status\" \"$result\"";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(Convert.FromHexString("7374617475733A302076616C75653A3CFF3E0A"),
            result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_ReadonlyPercentNTargetDoesNotFailBuiltin()
    {
        var script = "readonly count; printf 'a%n-b%s-c\\n' count z; printf 'status:%s\\n' \"$?\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a-bz-c\nstatus:0\n", result.StdOut);
        Assert.Contains("count: readonly variable\n", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_WritesDiagnosticsBeforeOutputWhenDescriptorsAreMerged()
    {
        var result = await EvaluationTestHelper.RunAsync("printf 'before:%d:after\\n' nope 2>&1");

        Assert.Equal("printf: nope: invalid number\nbefore:0:after\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_EmitsWriteOperation()
    {
        // This test verifies that printf writes formatted output without adding a newline.
        // The script "printf %s a" must output "a" with no STDERR and exit code 0.
        // If printf appends a newline or ignores arguments, STDOUT will differ.
        var script = @"printf %s a";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_AllowsOnlyFormat()
    {
        // This test verifies that printf succeeds when only a format is provided.
        // The script "printf %s" must emit no output and return exit code 0.
        // If a missing argument is treated as an error, the exit code would be non-zero.
        var script = @"printf %s";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_EmitsReadLineOperation()
    {
        // This test verifies that "read" consumes a line from STDIN.
        // With default input "value\n", the script "read name; echo $name" must output "value\n".
        // If read does not consume input, the output would be empty.
        var script = @"read name; echo $name";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_AssignsVariables()
    {
        // This test verifies that "read" assigns multiple variables from a single line.
        // With input "alpha beta\n", the script must output "alpha,beta\n".
        // If splitting fails, one of the variables would be empty.
        var script = @"read first second; echo $first,$second";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "alpha beta\n");

        Assert.Equal("alpha,beta\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_UsesIfsAndPreservesFinalRemainder()
    {
        var script = @"IFS=, read first rest <<< ""x,y,z""; printf '%s|%s\n' ""$first"" ""$rest""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("x|y,z\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("x,y,z\n", "x|y,z\n")]
    [InlineData("x,,z\n", "x|,z\n")]
    [InlineData("x,y,\n", "x|y\n")]
    [InlineData("x,,\n", "x|\n")]
    [InlineData(",x,y,\n", "|x,y,\n")]
    public async Task Evaluation_ReadBuiltin_PreservesNonWhitespaceIfsFields(
        string standardInput,
        string expected)
    {
        var script = @"IFS=, read first rest; printf '%s|%s\n' ""$first"" ""$rest""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("IFS=' ' read first rest", " x  y   z \n", "x|y   z\n")]
    [InlineData("IFS=' ,' read first rest", "  x ,  y , z  \n", "x|y , z\n")]
    [InlineData("IFS=' ,' read first rest", "a,b,c\\ \n", "a|b,c\n")]
    [InlineData("IFS= read first rest", " x  y \n", " x  y |\n")]
    public async Task Evaluation_ReadBuiltin_HandlesWhitespaceAndEmptyIfs(
        string readCommand,
        string standardInput,
        string expected)
    {
        var script = $@"{readCommand}; printf '%s|%s\n' ""$first"" ""$rest""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(@"read value <<< ""a\b""", "ab\n")]
    [InlineData(@"read -r value <<< ""a\b""", "a\\b\n")]
    public async Task Evaluation_ReadBuiltin_RawOptionControlsBackslashEscaping(
        string readCommand,
        string expected)
    {
        var script = $@"{readCommand}; printf '%s\n' ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_EscapedIfsCharacterRemainsLiteral()
    {
        var script = @"IFS=, read first rest; printf '%s|%s\n' ""$first"" ""$rest""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "x\\,y,z\n");

        Assert.Equal("x,y|z\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_BackslashNewlineContinuesInput()
    {
        var script = @"read value; printf '%s\n' ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "a\\\nb\n");

        Assert.Equal("ab\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a\\", "a")]
    [InlineData("a\\\nb", "ab")]
    public async Task Evaluation_ReadBuiltin_UnterminatedInputAssignsAndReturnsFailure(
        string standardInput,
        string expectedValue)
    {
        var script = @"read value; status=$?; printf '%s|<%s>\n' ""$status"" ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput);

        Assert.Equal($"1|<{expectedValue}>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_PreservesCarriageReturnAsData()
    {
        var script = @"read value; printf '<%s>\n' ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "a\r\n");

        Assert.Equal("<a\r>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_ContinuationOnlyInputAssignsEmptyValue()
    {
        var script = @"value=old; IFS= read value; status=$?; printf '%s|<%s>\n' ""$status"" ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "\\\n");

        Assert.Equal("1|<>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_RawBackslashNewlineRemainsTwoLines()
    {
        var script = @"read -r first; read second; printf '%s|%s\n' ""$first"" ""$second""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "a\\\nb\n");

        Assert.Equal("a\\|b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_DefaultReplyPreservesInput()
    {
        var script = @"read; printf '<%s>\n' ""$REPLY""";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "  a   b  \n");

        Assert.Equal("<  a   b  >\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_NonZeroExitCodeDoesNotAssign()
    {
        // This test verifies that a failed read does not assign the target variable.
        // The script performs two reads with only one line of input; the second read must fail,
        // leave "value" unset, output "unset\n", and exit with code 1.
        // If the failed read assigns anyway, the output would differ.
        var script = @"read first; read value; status=$?; echo ${value:-unset}; exit $status";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_EmptyEofClearsTarget()
    {
        var script = @"value=old; read value; status=$?; printf '%s|<%s>\n' ""$status"" ""$value""; exit $status";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: string.Empty);

        Assert.Equal("1|<>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_EmitsReadLineOperation()
    {
        // This test verifies that mapfile reads a line into an array.
        // With input "line\n", the script must output "line\n".
        // If mapfile fails to assign, STDOUT would be empty.
        var script = @"mapfile arr; echo ${arr[0]}";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "line\n");

        Assert.Equal("line\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_AssignmentPrefixDoesNotHideTargetArray()
    {
        var script = @"IFS= mapfile arr < /input; echo ${arr[0]}";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: string.Empty);

        Assert.Equal("input\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadarrayBuiltin_EmitsReadLineOperation()
    {
        // This test verifies that readarray behaves like mapfile.
        // With input "line\n", the script must output "line\n".
        // If readarray fails to assign, STDOUT would be empty.
        var script = @"readarray arr; echo ${arr[0]}";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: "line\n");

        Assert.Equal("line\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

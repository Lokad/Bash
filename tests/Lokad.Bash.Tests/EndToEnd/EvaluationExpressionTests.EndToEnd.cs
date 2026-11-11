using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationExpressionTestsEndToEnd
{
    [Theory]
    [InlineData("a=(outer tail); a=1 let 'a=2'; printf '%s:%s' \"${a[0]}\" \"${a[1]}\"", "2:tail")]
    [InlineData("declare -A a; a[key]=tail; a=1 let 'a=2'; printf '%s:%s' \"${a[0]}\" \"${a[key]}\"", "2:tail")]
    [InlineData("x=outer; x=1 let 'x=2'; sh -c 'printf \"%s\" \"${x-unset}\"'", "unset")]
    [InlineData("export x=outer; x=1 let 'x=2'; sh -c 'printf \"%s\" \"$x\"'", "2")]
    [InlineData("x=outer; x=1 let 'x=x+1' 'x=x+1'; printf '%s' \"$x\"", "3")]
    [InlineData("x=outer; declare -n ref=x; x=1 let 'ref=2'; printf '%s' \"$x\"", "outer")]
    [InlineData("x=outer; f() { declare -n ref=x; x=1 let 'ref=2'; }; f; printf '%s' \"$x\"", "2")]
    public async Task Evaluation_LetPrefix_PreservesCallerBindingAndSubsequentReads(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_PromotesAssignedPrefixVariable()
    {
        var script = @"x=outer; x=1 let 'x=x+1'; echo $x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_AssignmentOnlyCommand_AssignsVariable()
    {
        // This test verifies that an assignment-only command updates shell variables.
        // The script assigns FOO=bar and echoes it, so STDOUT must be "bar\n" with no STDERR.
        // If assignment-only commands are ignored, the echo would be empty.
        var script = @"FOO=bar; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentOnlyCommand_ReadonlyReturnsFailure()
    {
        // Assignment-only readonly errors report a diagnostic and terminate the script.
        var script = @"readonly FOO=bar; FOO=baz";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("FOO: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("reference=new", "target: readonly variable\n")]
    [InlineData("value=${MISSING:?first}", "MISSING: first\n")]
    [InlineData("values=(first ${MISSING:?first})", "MISSING: first\n")]
    public async Task FatalAssignment_UsesEnclosingStderrAndSkipsItsRedirections(string assignment, string diagnostic)
    {
        var tree = EndToEndHarness.BuildTree([("/victim", "original")]);
        var result = await EndToEndHarness.RunScriptAsync(
            "readonly target=old; declare -n reference=target; "
            + "{ " + assignment + " 2>/not-active > /victim; } 2>/errors; printf should-not-run", tree);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(diagnostic, await EndToEndHarness.ReadFileAsync(result.Host, "/errors"));
        Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/not-active"));
        Assert.Equal("original", await EndToEndHarness.ReadFileAsync(result.Host, "/victim"));
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_ReturnsSuccessForNonZero()
    {
        // This test verifies that "let" returns success for a non-zero expression result.
        // The script "let 1" must emit no output and exit with code 0.
        // If the result is misinterpreted, the exit code would be 1.
        var script = @"let 1";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_ReturnsFailureForZero()
    {
        // This test verifies that "let" returns failure for a zero expression result.
        // The script "let 0" must emit no output and exit with code 1.
        // If the result is treated as success, the exit code would be 0.
        var script = @"let 0";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_UsesLastArgumentResult()
    {
        // This test verifies that "let" uses the last expression's value for its exit status.
        // The script "let 1 0" must exit with code 1 and emit no output.
        // If it used the first argument, the exit code would be 0.
        var script = @"let 1 0";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_TestBuiltin_EvaluatesStringPredicate()
    {
        // This test verifies that "test" succeeds for a non-empty string.
        // The script "test foo" must emit no output and return exit code 0.
        // If the predicate is inverted, the exit code would be 1.
        var script = @"test foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_TestBuiltin_FalseReturnsFailure()
    {
        // This test verifies that "test" fails for an empty string.
        // The script "test \"\"" must emit no output and return exit code 1.
        // If empty strings are treated as true, the exit code would be 0.
        var script = @"test """"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BracketBuiltin_StripsClosingBracket()
    {
        // This test verifies that "[" treats the closing "]" as syntax, not an argument.
        // The script "[ foo ]" must succeed with no output and exit code 0.
        // If the closing bracket is treated as a literal argument, parsing would fail.
        var script = @"[ foo ]";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BracketBuiltin_MissingClosingBracketReturnsUsageError()
    {
        // This test verifies that "[" requires a closing "]" and returns a usage error when missing.
        // The script "[ foo" must emit no output and return exit code 2.
        // If the missing bracket is ignored, the exit code would be 0 or 1.
        var script = @"[ foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BracketBuiltin_ExpandsOperandsBeforeFatalClosingWord()
    {
        var script = @"[ ""$(echo first >&2)"" = x ""${MISSING:?boom}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.StartsWith("first\n", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }
}

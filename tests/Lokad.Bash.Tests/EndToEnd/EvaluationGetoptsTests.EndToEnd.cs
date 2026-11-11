using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationGetoptsTestsEndToEnd
{
    [Theory]
    [InlineData("a=(outer tail); a=temp getopts a a -a; printf '%s:%s' \"${a[0]}\" \"${a[1]}\"", "a:tail")]
    [InlineData("opt=outer; opt=temp getopts a opt -a; sh -c 'printf \"%s\" \"${opt-unset}\"'", "unset")]
    [InlineData("export opt=outer; opt=temp getopts a opt -a; sh -c 'printf \"%s\" \"$opt\"'", "a")]
    [InlineData("x=outer; declare -n ref=x; x=temp getopts a ref -a; printf '%s' \"$x\"", "outer")]
    [InlineData("x=outer; f() { declare -n ref=x; x=temp getopts a ref -a; }; f; printf '%s' \"$x\"", "a")]
    public async Task Evaluation_GetoptsPrefix_PreservesCallerBinding(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_PromotesPrefixedOutputVariables()
    {
        var script = @"opt=outer; OPTIND=1; opt=temp OPTIND=1 getopts a opt -a; echo $opt/$OPTIND";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a/2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_GetoptsBuiltin_ParsesOptionsAndUpdatesOptind()
    {
        // This test verifies that getopts parses options and increments OPTIND across calls.
        // The script parses "-a -b" twice and echoes the option and OPTIND after each call.
        // A correct run must emit "a 2\nb 3\n", with no STDERR and exit code 0.
        // If OPTIND is not updated, the second line would repeat "a 2".
        var script = @"getopts ab opt -a -b; echo $opt $OPTIND; getopts ab opt -a -b; echo $opt $OPTIND";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a 2\nb 3\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_NoOptionsReturnsFailure()
    {
        // This test verifies that getopts returns failure when no options are available.
        // The script "getopts ab opt" has no arguments, so it must emit no output and exit with 1.
        // If it succeeds incorrectly, the exit code would be 0.
        var script = @"getopts ab opt";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_MissingArgumentsReturnsFailure()
    {
        // This test verifies that getopts fails when required arguments are missing.
        // The script "getopts" must emit no output and return exit code 1.
        // If missing arguments are not validated, the exit code would be 0.
        var script = @"getopts";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_FatalPartialOperandSkipsAssignmentPrefix()
    {
        var script = @"X=$(echo assignment-side-effect >&2) getopts ""${MISSING:?word failed}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.DoesNotContain("assignment-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }
}

using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationLoopTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_WhileCommand_ExecutesSingleIteration()
    {
        // This test verifies that a while-loop executes its body when the condition succeeds.
        // The script sets i=0 and loops while i==0, echoing once and then setting i=1 to stop.
        // A correct implementation must emit "once\n", leave STDERR empty, and exit with code 0.
        // If the loop never executes or never terminates, output and exit code would differ.
        var script = @"i=0; while [ $i -eq 0 ]; do echo once; i=1; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("once\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_BreakExitsLoop()
    {
        // This test verifies that "break" exits a while-loop immediately.
        // The script echoes "first", breaks, then attempts to echo "after" inside the loop and
        // "tail" after the loop; only "first" and "tail" should appear.
        // If break is ignored, STDOUT would include "after\n".
        var script = @"while true; do echo first; break; echo after; done; echo tail";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("first\ntail\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_ContinueSkipsBody()
    {
        // This test verifies that "continue" skips the remainder of the loop body.
        // The script increments i, continues on the first iteration, and echoes "after" only on
        // the second iteration; a correct run must emit "after\n" once with exit code 0.
        // If continue is ignored, STDOUT would include an extra line.
        var script = @"i=0; while [ $i -lt 2 ]; do i=$((i+1)); if [ $i -eq 1 ]; then continue; fi; echo after; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("after\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_BreakInvalidArgumentReturnsFailure()
    {
        // This test verifies that "break" with an invalid argument inside a loop returns a usage
        // error exit code and stops the loop.
        // The script uses "break nope" so a correct implementation must emit no output and return 2.
        // If the invalid argument is ignored, the exit code would be 0 or 1.
        var script = @"while true; do break nope; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_AllowsZeroIterations()
    {
        // This test verifies that a while-loop with a failing condition runs zero iterations.
        // The script "while false; do echo never; done" must emit no output and exit with code 0.
        // If the body executes incorrectly, STDOUT would contain "never\n".
        var script = @"while false; do echo never; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileRead_WithInputRedirectionConsumesFile()
    {
        var script = @"while IFS= read value; do printf '%s\n' ""$value""; done < /input";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: string.Empty);

        Assert.Equal("input\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UntilCommand_ExecutesSingleIteration()
    {
        // This test verifies that an until-loop runs the body until the condition succeeds.
        // The script starts with i=0 and runs until i==1, echoing once and then setting i=1.
        // A correct implementation must emit "once\n", leave STDERR empty, and exit 0.
        // If until is inverted incorrectly, the body would not execute.
        var script = @"i=0; until [ $i -eq 1 ]; do echo once; i=1; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("once\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a while-loop.
        // The script "! while false; do echo never; done" normally exits 0; negation must return 1.
        // If negation is ignored, the exit code would be 0.
        var script = @"! while false; do echo never; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

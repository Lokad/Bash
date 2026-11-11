using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationExecutionTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_YieldsRunCommandOperation()
    {
        // This test verifies that evaluating a simple command results in executing that command and
        // capturing its output as STDOUT without emitting any STDERR.
        // The script uses "echo run-commands" so a successful execution must produce exactly
        // "run-commands\n" on STDOUT, an empty STDERR, and exit code 0.
        // If evaluation failed to invoke the command, STDOUT would be empty or the exit code would
        // be non-zero; if output were routed to STDERR, the StdErr assert would fail.
        var script = @"echo run-commands";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("run-commands\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CompletesAfterResult()
    {
        // This test verifies that once a single command finishes, the evaluation completes and
        // returns its final result without hanging or producing extra output.
        // The script runs "cat" with the default test input so a successful completion must echo
        // the input on STDOUT, leave STDERR empty, and exit with code 0.
        // If evaluation failed to complete after the command result, it would time out (producing
        // a non-empty STDERR and exit code 124), or it would emit unexpected output.
        var script = @"cat";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

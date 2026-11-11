using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationBuiltinControlTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_BuiltinColon_ReturnsSuccessWithoutOperation()
    {
        // This test verifies that the ":" builtin is a no-op that succeeds without producing output.
        // The script contains only ":" so a correct implementation must leave STDOUT and STDERR empty
        // and return exit code 0.
        // Any execution of unintended commands or an error path would change output or exit code.
        var script = @":";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinTrue_ReturnsSuccessWithoutOperation()
    {
        // This test verifies that the "true" builtin succeeds without emitting output.
        // The script contains only "true" so a correct run yields empty STDOUT/STDERR and exit code 0.
        // If "true" were mis-evaluated or caused output, the assertions would fail.
        var script = @"true";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinFalse_ReturnsFailureWithoutOperation()
    {
        // This test verifies that the "false" builtin fails without emitting output.
        // The script contains only "false" so a correct run yields empty STDOUT/STDERR and exit code 1.
        // If "false" returned success or produced output, the assertions would fail.
        var script = @"false";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExitBuiltin_TerminatesScript()
    {
        // This test verifies that "exit" stops evaluation immediately and prevents later commands.
        // The script echoes "before", exits, and then tries to echo "after".
        // A correct implementation must emit only "before\n", leave STDERR empty, and exit with code 0.
        // If evaluation continues after exit, STDOUT would include "after\n".
        var script = @"echo before; exit; echo after";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("before\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExitBuiltin_UsesProvidedStatus()
    {
        // This test verifies that "exit <status>" terminates evaluation and returns the provided code.
        // The script echoes "before", exits with 7, and then tries to echo "after".
        // A correct run must emit only "before\n", leave STDERR empty, and return exit code 7.
        // If the exit code is ignored or the script continues, the assertions will fail.
        var script = @"echo before; exit 7; echo after";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("before\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(7, result.ExitCode);
    }
}

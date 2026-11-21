using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationBuiltinControlTestsEndToEnd
{
    [Theory]
    [InlineData(": --help", 0)]
    [InlineData("true --help", 0)]
    [InlineData("false --help", 1)]
    [InlineData("command true -- --help", 0)]
    [InlineData("builtin false -z --help", 1)]
    [InlineData("! builtin false --help", 0)]
    public async Task FixedStatusBuiltins_IgnoreOptionLookingOperands(string script, int expectedStatus)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(expectedStatus, result.ExitCode);
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

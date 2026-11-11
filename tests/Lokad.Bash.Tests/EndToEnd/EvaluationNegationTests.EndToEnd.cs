using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationNegationTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_NegatedCommand_InvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a simple command.
        // The script uses "! true" so a correct implementation must produce no output,
        // leave STDERR empty, and return exit code 1.
        // If negation is ignored, the exit code would remain 0.
        var script = @"! true";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

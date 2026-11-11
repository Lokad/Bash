using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationListTestsEndToEnd
{
    [Theory]
    [InlineData("; ", "", 0)]
    [InlineData("\n", "", 0)]
    [InlineData(" && ", "", 0)]
    [InlineData(" || ", "! ", 1)]
    public async Task Evaluation_LongLists_PreserveOrderAndExitStatus(
        string separator,
        string commandPrefix,
        int expectedExitCode)
    {
        const int commandCount = 5_000;
        var script = string.Join(separator, Enumerable.Range(1, commandCount)
            .Select(index => $"{commandPrefix}echo {index}"));

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Concat(Enumerable.Range(1, commandCount).Select(index => $"{index}\n")), result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LongList_ExitSkipsRemainingCommands()
    {
        var commands = Enumerable.Repeat(":", 5_000).ToArray();
        commands[commands.Length / 2] = "exit 7";
        commands[^1] = "echo unexpected";

        var result = await EvaluationTestHelper.RunAsync(string.Join("; ", commands));

        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LongList_MixedOperatorsPreserveShortCircuiting()
    {
        var script = string.Concat(Enumerable.Repeat(
            "true || echo skipped; false && echo skipped || :; ", 2_000))
            + "! { false && echo skipped; }; echo $?";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ListAnd_ExecutesRightOnSuccess()
    {
        // This test verifies that "&&" executes the right-hand command only when the left succeeds.
        // The script uses "true && echo right" so a correct implementation must print "right\n",
        // produce no STDERR, and exit with 0.
        // If the right side is skipped despite a successful left, STDOUT would be empty.
        var script = @"true && echo right";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("right\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ListAnd_SkipsRightOnFailure()
    {
        // This test verifies that "&&" skips the right-hand command when the left fails.
        // The script uses "false && echo right" so a correct implementation must emit nothing
        // on STDOUT/STDERR and return exit code 1 from the failed left command.
        // If the right side executes incorrectly, STDOUT would contain "right\n".
        var script = @"false && echo right";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ListOr_SkipsRightOnSuccess()
    {
        // This test verifies that "||" skips the right-hand command when the left succeeds.
        // The script uses "true || echo right" so a correct implementation must emit no output
        // and return exit code 0.
        // If the right side executes, STDOUT would contain "right\n".
        var script = @"true || echo right";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ListSequence_AlwaysExecutesRight()
    {
        // This test verifies that ";" always executes the right-hand command regardless of the left.
        // The script uses "false; echo right" so the failed left command must not prevent "echo right".
        // A correct implementation prints "right\n" with exit code 0 and no STDERR.
        // If sequencing is broken, STDOUT would be empty or the exit code would remain 1.
        var script = @"false; echo right";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("right\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ListLeftAssociativity_RespectsExitStatus()
    {
        // This test verifies left-to-right evaluation for mixed "&&" and "||".
        // The script uses "false && echo a || echo b", which should skip "echo a" and run "echo b".
        // A correct implementation prints "b\n", yields no STDERR, and exits with 0.
        // If associativity is wrong, "echo b" might not run or the exit code would differ.
        var script = @"false && echo a || echo b";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

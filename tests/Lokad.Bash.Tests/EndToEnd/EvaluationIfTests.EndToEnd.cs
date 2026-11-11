using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationIfTestsEndToEnd
{
    [Theory]
    [InlineData("if false; then :; fi", 0)]
    [InlineData("if false; then :; elif (exit 7); then :; fi", 0)]
    [InlineData("! if false; then :; fi", 1)]
    [InlineData("if true; then false; fi", 1)]
    [InlineData("if false; then :; else false; fi", 1)]
    [InlineData("if exit 7; then :; fi", 7)]
    public async Task Evaluation_IfCommand_PreservesCompletionAndExitFlow(string script, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_IfCommand_ExecutesThenBranch()
    {
        // This test verifies that an "if" executes the then-branch when the condition succeeds.
        // The script uses "if true; then echo yes; else echo no; fi" so a correct run must emit
        // "yes\n", produce no STDERR, and exit with code 0.
        // If the condition is mis-evaluated or branches are swapped, STDOUT would be "no\n".
        var script = @"if true; then echo yes; else echo no; fi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("yes\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_IfCommand_ExecutesElseBranch()
    {
        // This test verifies that an "if" executes the else-branch when the condition fails.
        // The script uses "if false; then echo yes; else echo no; fi" so a correct run must emit
        // "no\n", produce no STDERR, and exit with code 0.
        // If the else branch is skipped, STDOUT would be empty.
        var script = @"if false; then echo yes; else echo no; fi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("no\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_IfCommand_ExecutesElifBranch()
    {
        // This test verifies that an "elif" branch is taken when earlier conditions fail.
        // The script uses "if false; then echo a; elif true; then echo b; else echo c; fi" so a
        // correct run must emit "b\n", no STDERR, and exit 0.
        // If elif is ignored, STDOUT would be "c\n" or empty.
        var script = @"if false; then echo a; elif true; then echo b; else echo c; fi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_IfCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of an if-command.
        // The script uses "! if true; then :; fi" which normally succeeds; negation must flip the
        // exit code to 1 while leaving STDOUT/STDERR empty.
        // If negation is ignored, the exit code would remain 0.
        var script = @"! if true; then :; fi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

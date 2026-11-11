using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationDirectoryStackTestsEndToEnd
{

    [Fact]
    public async Task Evaluation_PushdBuiltin_AssignmentPrefixContributesToDirectoryVariables()
    {
        var script = @"PWD=/temporary OLDPWD=/ignored pushd /target; printf '%s|%s\n' ""$PWD"" ""$OLDPWD""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target /\n/target|/temporary\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PopdBuiltin_AssignmentPrefixContributesToDirectoryVariables()
    {
        var script = @"pushd /target; PWD=/temporary OLDPWD=/ignored popd; printf '%s|%s\n' ""$PWD"" ""$OLDPWD""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target /\n/\n/|/temporary\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_DirsBuiltin_EmitsWriteOperation()
    {
        // This test verifies that "dirs" prints the current directory stack.
        // With the initial stack containing only "/", STDOUT must be "/\n".
        // If the stack is not initialized, the output would be empty.
        var script = @"dirs";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DirsClearOption_ResetsStack()
    {
        // This test verifies that "dirs -c" clears the stack to the current directory.
        // The script pushes /target (printing "/target /"), clears the stack (printing "/target"),
        // then prints it again. The combined output must match those three lines.
        // If -c is ignored, the middle and final lines would still include "/".
        var script = @"pushd /target; dirs -c; dirs";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target /\n/target\n/target\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PushdBuiltin_UpdatesStackAndOutputs()
    {
        // This test verifies that pushd updates the stack and prints it.
        // The script "pushd /target" must output "/target /\n" with no STDERR and exit 0.
        // If pushd fails, output would be empty or the exit code non-zero.
        var script = @"pushd /target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target /\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PopdBuiltin_UpdatesStackAndOutputs()
    {
        // This test verifies that popd restores the previous directory and prints the stack.
        // After pushing /target, popd must print "/\n". The full output must be "/target /\n/\n".
        // If popd fails, the output would not include the restored root.
        var script = @"pushd /target; popd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target /\n/\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PopdBuiltin_EmptyStackReturnsFailure()
    {
        // This test verifies that popd fails when there is no previous directory.
        // The script "popd" must emit no output and exit with code 1.
        // If it succeeds incorrectly, the exit code would be 0.
        var script = @"popd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

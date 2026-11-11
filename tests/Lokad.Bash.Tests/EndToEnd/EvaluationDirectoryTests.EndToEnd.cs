using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationDirectoryTestsEndToEnd
{

    [Fact]
    public async Task Evaluation_CdBuiltin_AssignmentPrefixContributesToDirectoryVariables()
    {
        var script = @"PWD=/temporary OLDPWD=/ignored cd /target; printf '%s|%s\n' ""$PWD"" ""$OLDPWD""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target|/temporary\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_PwdBuiltin_EmitsWriteOperation()
    {
        // This test verifies that "pwd" prints the current directory.
        // The initial directory is "/", so STDOUT must be "/\n" with no STDERR and exit code 0.
        // If the current directory is not tracked correctly, the output would differ.
        var script = @"pwd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PwdBuiltin_UsesExitCode()
    {
        // This test verifies that "pwd" returns a failure exit code when stdout is closed.
        // The script "pwd >&-" closes stdout; a correct implementation must emit no output and
        // return exit code 1.
        // If write failures are ignored, the exit code would be 0.
        var script = @"pwd >&-";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ClosedStdout_IsLexicallyScoped()
    {
        var result = await EvaluationTestHelper.RunAsync("pwd >&-; echo after");

        Assert.Equal("after\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PwdVariable_Initialized()
    {
        // This test verifies that PWD is initialized to the current directory.
        // The script "echo $PWD" must output "/\n" with no STDERR and exit code 0.
        // If PWD is missing, the output would be empty.
        var script = @"echo $PWD";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_UpdatesCurrentDirectory()
    {
        // This test verifies that "cd" updates the working directory.
        // The script "cd /target; pwd" must output "/target\n", with no STDERR and exit code 0.
        // If the directory change fails, the output would remain "/".
        var script = @"cd /target; pwd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_UpdatesPwdVariables()
    {
        // This test verifies that "cd" updates both PWD and OLDPWD.
        // The script "cd /target; echo $PWD $OLDPWD" must output "/target /\n".
        // If OLDPWD is not tracked, the second value would be empty or incorrect.
        var script = @"cd /target; echo $PWD $OLDPWD";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target /\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_FailureDoesNotUpdateDirectory()
    {
        // This test verifies that a failing "cd" does not change the current directory.
        // The script captures the failing cd exit code, prints $PWD, and exits with that code.
        // A correct run must output "/\n" and exit with code 1.
        // If the directory changes or the failure status is lost, the assertions will fail.
        var script = @"cd /missing; status=$?; echo $PWD; exit $status";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_NoArgumentsReturnsFailure()
    {
        // This test verifies that "cd" without arguments returns failure.
        // The script "cd" must emit no output and exit with code 1.
        // If a default directory is assumed, the exit code would be 0.
        var script = @"cd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

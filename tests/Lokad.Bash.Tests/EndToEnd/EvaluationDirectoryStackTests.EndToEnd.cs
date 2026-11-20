using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationDirectoryStackTestsEndToEnd
{
    [Theory]
    [InlineData("pushd /alpha /beta", 1, "pushd: too many arguments\n")]
    [InlineData("pushd -n -x", 2, "pushd: -x: invalid number\npushd: usage: pushd [-n] [+N | -N | dir]\n")]
    [InlineData("popd +1 stray", 2, "popd: stray: invalid argument\npopd: usage: popd [-n] [+N | -N]\n")]
    [InlineData("popd -n --help", 2, "popd: --help: invalid number\npopd: usage: popd [-n] [+N | -N]\n")]
    public async Task InvalidStackOperandsLeavePathsAndBindingsUntouched(string command, int status, string error)
    {
        await using var host = EndToEndHarness.BuildTree(["/alpha", "/beta"], []);
        var lookups = new List<string>();
        host.BeforeGetPathInfo = path => lookups.Add(path.Path);
        var source = $"""
            pushd /alpha >/dev/null
            pushd /beta >/dev/null
            {command}
            printf '%s|%s|%s\n' "$?" "$PWD" "$OLDPWD"
            dirs
            """;

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "/alpha", "/beta" }, lookups);
        Assert.Equal($"{status}|/beta|/alpha\n/beta /alpha /\n", host.StandardOutput.TextContent);
        Assert.Equal(error, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("pushd -", "/alpha\n/alpha /beta /alpha /\n0|/alpha|/beta\n/alpha /beta /alpha /\n", "")]
    [InlineData("pushd -- - 1>&-", "1|/alpha|/beta\n/alpha /alpha /\n", "pushd: write error: Bad file descriptor\n")]
    [InlineData("pushd ''", "/beta /beta /alpha /\n0|/beta|/beta\n/beta /beta /alpha /\n", "")]
    public async Task PushdAppliesNavigationBeforeCompletingTheStackEdit(string command, string expected, string error)
    {
        await using var host = EndToEndHarness.BuildTree(["/alpha", "/beta"], []);
        var source = $"""
            pushd /alpha >/dev/null
            pushd /beta >/dev/null
            {command}
            printf '%s|%s|%s\n' "$?" "$PWD" "$OLDPWD"
            dirs
            """;

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Equal(error, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task PopdHelpRunsBeforeTheEmptyStackCheck()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("popd --help", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("popd: popd [-n] [+N | -N]\n", host.StandardOutput.TextContent, StringComparison.Ordinal);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

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
        var script = @"pushd /target; dirs -c; dirs";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target /\n/target\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DirsClearSucceedsWithClosedOutput()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "pushd /target >/dev/null; dirs -c 1>&-; printf '%s\\n' \"$?\"; dirs");

        Assert.Equal("0\n/target\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("unset omitted", "dirs $omitted -c", "", 0, "/target")]
    [InlineData("set --", "dirs \"$@\" -c", "", 0, "/target")]
    [InlineData("unset omitted; set -- command dirs", "\"$@\" $omitted -c", " 1>&-", 0, "/target")]
    [InlineData("omitted=", "dirs \"$omitted\" -c", "", 1, "/target /")]
    public async Task DirsClearUsesTheFirstProducedArgument(
        string setup, string invocation, string redirection, int status, string stack)
    {
        await using var host = new TestHost();
        host.AddDirectory("/target");
        var source = $"""
            pushd /target >/dev/null
            {setup}
            {invocation} $(printf reached >effect){redirection}
            printf 'status:%s:%s\n' "$?" "$_"
            dirs
            """;

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"status:{status}:-c\n{stack}\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("reached", host.ReadFile("/target/effect"));
        Assert.Equal(0, host.OpenFileCount);
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
        // Missing stack entries fail with a diagnostic and no stdout.
        var script = @"popd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("popd: directory stack empty\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

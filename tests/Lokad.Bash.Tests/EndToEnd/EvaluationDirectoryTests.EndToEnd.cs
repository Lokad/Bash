using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationDirectoryTestsEndToEnd
{
    [Theory]
    [InlineData("cd \\\n./missing", 3)]
    [InlineData("tag=value \\\ncd \\\n./missing", 2)]
    [InlineData("cd >\\\nreport \\\n./missing", 3)]
    [InlineData("cd 1>\\\nreport \\\n./missing", 2)]
    public async Task DirectoryLookupDiagnosticUsesTheReachedTokenLine(string command, int line)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(":\n" + command, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/", result.CurrentDirectory.Path);
        Assert.Equal($"line {line}: cd: ./missing: No such file or directory\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("source /body", "/body: line 3")]
    [InlineData("eval ':\ncd ./missing'", "line 4")]
    public async Task DirectoryLookupDiagnosticKeepsTheLoadedSourceLocation(string command, string location)
    {
        await using var host = new TestHost();
        host.AddFile("/body", ":\ncd \\\n./missing\n");
        var result = await host.RunAsync(":\n" + command, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"{location}: cd: ./missing: No such file or directory\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("CDPATH=missing:search cd target", "/search/target", "/search/target\n")]
    [InlineData("CDPATH=:search cd target", "/target", "")]
    [InlineData("CDPATH=.:search cd target", "/target", "/target\n")]
    [InlineData("CDPATH=search cd ./target", "/target", "")]
    [InlineData("HOME=target CDPATH=search cd", "/target", "")]
    [InlineData("HOME=/search CDPATH='~' cd target", "/search/target", "/search/target\n")]
    [InlineData("CDPATH=search cd ''", "/search", "/search\n")]
    public async Task CdSearchUpdatesDirectoryVariablesAndAnnouncesNonemptyComponents(
        string command, string directory, string announcement)
    {
        await using var host = EndToEndHarness.BuildTree(["/target", "/search", "/search/target"], []);
        var result = await host.RunAsync("unset CDPATH; " + command
            + " >announcement; printf '<%s><%s><%s><%s>' \"$?\" \"$PWD\" \"$OLDPWD\" \"${CDPATH-unset}\"",
            CancellationToken.None);

        Assert.Equal(directory, result.CurrentDirectory.Path);
        Assert.Equal($"<0><{directory}></><unset>", host.StandardOutput.TextContent);
        Assert.Equal(announcement, host.ReadFile("/announcement"));
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("absent", "No such file or directory")]
    [InlineData("plain", "Not a directory")]
    public async Task CdChecksComponentsBeforeRemovingThem(string component, string cause)
    {
        await using var host = EndToEndHarness.BuildTree(["/start", "/start/finish"], []);
        host.AddFile("/start/plain", "sample");
        var result = await host.RunAsync($"""
            PWD=/
            cd /start
            cd {component}/../finish 2>/reason
            status=$?
            printf '<%s><%s>' "$PWD" "$OLDPWD"
            exit "$status"
            """, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/start", result.CurrentDirectory.Path);
        Assert.Equal("</start></>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal($"line 3: cd: {component}/../finish: {cause}\n", host.ReadFile("/reason"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CdPathSkipsCandidatesWithMissingComponentsBeforeParentSelectors()
    {
        await using var host = EndToEndHarness.BuildTree(
            ["/first", "/first/finish", "/second", "/second/step", "/second/finish"], []);
        var lookups = new List<string>();
        host.BeforeGetPathInfo = path => lookups.Add(path.Path);
        var result = await host.RunAsync("CDPATH=first:second cd step/../finish", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/second/finish", result.CurrentDirectory.Path);
        Assert.Equal(["/first/step", "/second/step", "/second/finish"], lookups);
        Assert.Equal("/second/finish\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CdClampsRepeatedParentComponentsAtRoot()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("cd ../..", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/", result.CurrentDirectory.Path);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task PopdKeepsDeferredEntryWhenAComponentBeforeAParentSelectorIsMissing()
    {
        await using var host = EndToEndHarness.BuildTree(["/finish"], []);
        var result = await host.RunAsync("pushd -n absent/../finish >/dev/null; popd; status=$?; dirs -l; exit \"$status\"",
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/", result.CurrentDirectory.Path);
        Assert.Equal("/ absent/../finish\n", host.StandardOutput.TextContent);
        Assert.Equal("line 1: popd: absent/../finish: No such file or directory\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CdPathAnnouncementFailureKeepsTheSelectedDirectory()
    {
        await using var host = EndToEndHarness.BuildTree(["/search", "/search/target"], []);
        var result = await host.RunAsync("CDPATH=search cd target >&-; status=$?; pwd; exit \"$status\"",
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/search/target\n", host.StandardOutput.TextContent);
        Assert.Equal("line 1: cd: write error: Bad file descriptor\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("pushd target", "/search/target\n/search/target /\n")]
    [InlineData("pushd -n target >/dev/null; popd", "/search/target\n/search/target\n")]
    public async Task StackNavigationSearchesForReachedRelativeOperands(string command, string expected)
    {
        await using var host = EndToEndHarness.BuildTree(["/target", "/search", "/search/target"], []);
        var result = await host.RunAsync("CDPATH=search; " + command, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/search/target", result.CurrentDirectory.Path);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Fact]
    public async Task CdHelpUsesTheRedirectedOutputAndPreservesDirectoryState()
    {
        await using var host = EndToEndHarness.BuildTree(["/target"], []);
        var result = await host.RunAsync("OLDPWD=marker; cd --help target >documentation; "
            + "printf '<%s><%s><%s>' \"$?\" \"$PWD\" \"$OLDPWD\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2></><marker>", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(
            "cd: cd [--] [dir]\n    Change the working directory to dir, or to HOME when omitted. A lone '-' selects and prints OLDPWD.\n",
            host.ReadFile("/documentation"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task PwdHelpAfterBundledOptionsKeepsExpansionEffectsWhenStdoutIsClosed()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "option=--help; command pwd -LP \"$option\" \"$(printf x >effect; printf operand)\" >&-; "
            + "printf '<%s><%s>' \"$?\" \"$_\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2><operand>", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal("x", host.ReadFile("/effect"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task InvalidPwdFlagPreservesItsByteAndPrecedesHelp()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("pwd $'-L\\377' --help", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        byte[] expected = [.. "line 1: pwd: -"u8, 0xff, .. ": invalid option\npwd: usage: pwd [-LP]\n"u8];
        Assert.Equal(expected, host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("-- --help")]
    [InlineData("operand --help")]
    public async Task PwdStopsScanningOptionsAtTheOperandBoundary(string arguments)
    {
        var result = await EvaluationTestHelper.RunAsync("pwd " + arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

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
    public async Task Evaluation_PwdBuiltin_PrintsTheCurrentDirectory()
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
        // The failure goes to stderr while stdout is closed.
        var script = @"pwd >&-";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: pwd: write error: Bad file descriptor\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ClosedStdout_IsLexicallyScoped()
    {
        var result = await EvaluationTestHelper.RunAsync("pwd >&-; echo after");

        Assert.Equal("after\n", result.StdOut);
        Assert.Equal("line 1: pwd: write error: Bad file descriptor\n", result.StdErr);
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
        Assert.Equal("line 1: cd: /missing: No such file or directory\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_NoArgumentsReturnsFailure()
    {
        var script = @"unset HOME; cd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: cd: HOME not set\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("HOME=target cd", "/target", "", "", 0)]
    [InlineData("unset x; HOME=target cd -- $x", "/target", "", "", 0)]
    [InlineData("HOME=target cd \"\"", "/", "", "", 0)]
    [InlineData("HOME= cd", "/", "", "", 0)]
    [InlineData("OLDPWD=target; cd -", "/target", "target\n", "", 0)]
    [InlineData("OLDPWD=/target; cd -- -", "/target", "/target\n", "", 0)]
    [InlineData("OLDPWD=; cd -", "/", "\n", "", 0)]
    [InlineData("OLDPWD=target; cd ./-", "/-", "", "", 0)]
    [InlineData("cd -- -x", "/-x", "", "", 0)]
    [InlineData("x='one space'; cd \"$x\"", "/one space", "", "", 0)]
    [InlineData("cd first-*", "/first-hit", "", "", 0)]
    [InlineData("x='one space'; cd $x", "/", "", "line 1: cd: too many arguments\n", 1)]
    [InlineData("IFS=:; x=multi-a:multi-b; cd $x", "/", "", "line 1: cd: too many arguments\n", 1)]
    [InlineData("cd multi-*", "/", "", "line 1: cd: too many arguments\n", 1)]
    [InlineData("cd -x", "/", "", "line 1: cd: -x: invalid option\n", 2)]
    [InlineData("unset OLDPWD; cd -", "/", "", "line 1: cd: OLDPWD not set\n", 1)]
    [InlineData("OLDPWD=target; cd - 1>&-", "/target", "", "line 1: cd: write error: Bad file descriptor\n", 1)]
    public async Task CdUsesExpandedOperandsAndReportsItsSelectedDestination(
        string command, string directory, string output, string error, int status)
    {
        await using var host = EndToEndHarness.BuildTree(
            ["/target", "/-", "/-x", "/one space", "/first-hit", "/multi-a", "/multi-b"], []);
        var result = await host.RunAsync(command + "; status=$?; pwd; exit \"$status\"", CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output + directory + "\n", host.StandardOutput.TextContent);
        Assert.Equal(error, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }
}

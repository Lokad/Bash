using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tree;
using Eval = Lokad.Bash.Evaluation.Evaluation;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationDirectoryStackTestsEndToEnd
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryStackReadsLivePathsAndRetainsExpandedValues(bool prepared)
    {
        const string script = """
            pushd -n alpha >/dev/null
            pushd -n beta >/dev/null
            PWD=/shadow
            printf '<%s>\n' "$DIRSTACK" "${DIRSTACK[-1]}" "${!DIRSTACK[@]}"
            DIRSTACK[1]=
            printf '<%s>\n' "${DIRSTACK[@]}" "${DIRSTACK[1]:=gamma}" "${DIRSTACK[1]}"
            for entry in "${DIRSTACK[@]}"; do printf '<%s>\n' "$entry"; dirs -c; done
            declare -p DIRSTACK
            """;
        await AssertDirectoryStackAsync(script, prepared,
            "</>\n<alpha>\n<0>\n<1>\n<2>\n</>\n<>\n<alpha>\n<>\n<gamma>\n"
            + "</>\n<gamma>\n<alpha>\ndeclare -a DIRSTACK=([0]=\"/\")\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryStackWritesThroughTheSelectedBindingAndDrivesNavigation(bool prepared)
    {
        const string script = """
            pushd -n alpha >/dev/null
            pushd -n beta >/dev/null
            declare -n target='DIRSTACK[1]'
            target=gamma
            DIRSTACK[0]=ignored
            DIRSTACK[9]=ignored
            unset 'DIRSTACK[1]'
            popd
            printf '<%s>\n' "$PWD" "${DIRSTACK[@]}"
            """;
        await AssertDirectoryStackAsync(script, prepared, "/gamma alpha\n</gamma>\n</gamma>\n<alpha>\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryStackFollowsLocalInheritanceAndChildOwnership(bool prepared)
    {
        const string script = """
            pushd -n alpha >/dev/null
            pushd -n beta >/dev/null
            show() { printf '<%s>\n' "${DIRSTACK[1]-unset}"; }
            f() {
                local -I DIRSTACK
                ( DIRSTACK[1]=subshell; show )
                result=$(DIRSTACK[1]=capture; show); printf '%s\n' "$result"
                { DIRSTACK[1]=pipeline; show; } | while IFS= read -r line; do printf '%s\n' "$line"; done
                DIRSTACK[1]=gamma
            }
            f
            DIRSTACK=prefix show
            show
            unset DIRSTACK
            DIRSTACK[1]=ordinary
            show; dirs -l
            """;
        await AssertDirectoryStackAsync(script, prepared,
            "<subshell>\n<capture>\n<pipeline>\n<unset>\n<gamma>\n<ordinary>\n/ gamma alpha\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryStackDistinguishesBulkInputAndIsolatedRedirections(bool prepared)
    {
        const string script = """
            pushd -n alpha >/dev/null
            pushd -n beta >/dev/null
            < "./file$((DIRSTACK[1]=2))"
            dirs -l
            : > "out$((DIRSTACK[1]=2))"
            dirs -l
            visit() { printf '<%s:%s>\n' "$1" "${DIRSTACK[1]}"; }
            mapfile -t -O 1 -c 1 -C visit DIRSTACK <<< $'first\nsecond'
            dirs -l
            mapfile -t DIRSTACK <<< $'ignored\nnext\nlast\nignored'
            dirs -l
            """;
        await AssertDirectoryStackAsync(script, prepared,
            "/ beta alpha\n/ 2 alpha\n<1:2>\n<2:first>\n/ first second\n/ next last\n");
    }

    private static async Task AssertDirectoryStackAsync(string script, bool prepared, string expected)
    {
        var governor = new MemoryGovernor(TestMemoryBudget.DefaultBytes);
        using var owner = governor.CreateScope();
        await using var host = EndToEndHarness.BuildTree(["/alpha", "/beta", "/gamma"], [("/file2", "data\n")]);
        host.MemoryBudget = owner;
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("pushd /alpha /beta", 1, "line 3: pushd: too many arguments\n")]
    [InlineData("pushd -n -x", 2, "line 3: pushd: -x: invalid number\npushd: usage: pushd [-n] [+N | -N | dir]\n")]
    [InlineData("pushd +7 +1", 1, "line 3: pushd: +7: directory stack index out of range\n")]
    [InlineData("pushd +07 -q", 1, "line 3: pushd: +07: directory stack index out of range\n")]
    [InlineData("pushd -7 -n +1", 1, "line 3: pushd: -7: directory stack index out of range\n")]
    [InlineData("popd +1 stray", 2, "line 3: popd: stray: invalid argument\npopd: usage: popd [-n] [+N | -N]\n")]
    [InlineData("popd -n --help", 2, "line 3: popd: --help: invalid number\npopd: usage: popd [-n] [+N | -N]\n")]
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
    [InlineData("pushd -- - 1>&-", "1|/alpha|/beta\n/alpha /alpha /\n", "line 3: pushd: write error: Bad file descriptor\n")]
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

    [Theory]
    [InlineData("popd '' +1", "/beta /alpha\n0|/beta|/alpha\n/beta /alpha\n", false)]
    [InlineData("popd +1 ''", "/beta /\n0|/beta|/alpha\n/beta /\n", false)]
    [InlineData("popd +7 +1", "/beta /\n0|/beta|/alpha\n/beta /\n", false)]
    [InlineData("popd -- ''", "/alpha /\n0|/alpha|/beta\n/alpha /\n", true)]
    public async Task PopdEmptyFieldSelectsFromTheBottomAndEndsScanning(string command, string output, bool navigates)
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
        Assert.Equal(navigates ? new[] { "/alpha", "/beta", "/alpha" } : new[] { "/alpha", "/beta" }, lookups);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("cd -", "/base/missing", "line 3: cd: ../missing: No such file or directory\n", "/base/alpha")]
    [InlineData("pushd -- +1", "/base/alpha/+1", "line 3: pushd: +1: No such file or directory\n", "/base/alpha")]
    [InlineData("pushd -n ../record >/dev/null; popd", "/base/record", "line 3: popd: ../record: Not a directory\n", "/base/alpha ../record")]
    public async Task DirectoryLookupReportsTheSelectedSpelling(string command, string lookup, string diagnostic, string stack)
    {
        await using var host = EndToEndHarness.BuildTree(["/base/alpha"], [("/base/record", "record")]);
        var lookups = new List<string>();
        host.BeforeGetPathInfo = path => lookups.Add(path.Path);
        var source = $"""
            cd /base/alpha
            OLDPWD=../missing
            {command}
            printf '%s|%s|%s\n' "$?" "$PWD" "$OLDPWD"
            dirs -l
            """;

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "/base/alpha", lookup }, lookups);
        Assert.Equal($"1|/base/alpha|../missing\n{stack}\n", host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.TextContent);
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
    public async Task StackEditsPreserveOrderAcrossRotationTildeLookupAndSubshells()
    {
        await using var host = EndToEndHarness.BuildTree(["/root", "/root/alpha", "/root/beta"], []);
        const string Script = """
            cd /root
            pushd -n /root/alpha >/dev/null
            pushd -n /root/beta >/dev/null
            (popd -n +1 >/dev/null; printf 'child:%s:%s:%s\n' ~+0 ~+1 ~-0)
            printf 'parent:%s:%s:%s\n' ~+0 ~+1 ~-0
            pushd +1 >/dev/null
            printf 'rotated:%s:%s:%s\n' ~+0 ~+1 ~-0
            popd -n -0 >/dev/null
            dirs
            """;

        var result = await host.RunAsync(Script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("child:/root:/root/alpha:/root/alpha\n"
            + "parent:/root:/root/beta:/root/alpha\n"
            + "rotated:/root/beta:/root/alpha:/root\n"
            + "/root/beta /root/alpha\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
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
    public async Task Evaluation_DirsBuiltin_PrintsTheInitialStack()
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
    [InlineData("omitted=", "dirs \"$omitted\" -c", "", 2, "/target /")]
    public async Task DirsClearDistinguishesOmittedAndEmptyFields(
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
        if (status == 2)
            Assert.Equal("line 3: dirs: : invalid option\ndirs: usage: dirs [-clpv] [+N] [-N]\n", host.StandardError.TextContent);
        else
            Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("reached", host.ReadFile("/target/effect"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("-p", "~/leaf\n/targeted\n~\n/\n")]
    [InlineData("-v -p", " 0  ~/leaf\n 1  /targeted\n 2  ~\n 3  /\n")]
    [InlineData("-p -v", " 0  ~/leaf\n 1  /targeted\n 2  ~\n 3  /\n")]
    [InlineData("-l", "/target/leaf /targeted /target /\n")]
    [InlineData("-v -1", " 2  ~\n")]
    [InlineData("+99 +1 -- -l", "/targeted\n")]
    public async Task DirsDisplaysSelectedEntriesWithoutChangingTheStack(string operands, string expected)
    {
        await using var host = EndToEndHarness.BuildTree(["/target", "/target/leaf", "/targeted"], []);
        var result = await host.RunAsync($"""
            pushd /target >/dev/null
            pushd /targeted >/dev/null
            pushd /target/leaf >/dev/null
            HOME=/target command dirs {operands}
            printf 'status:%s cwd:%s\n' "$?" "$PWD"
            dirs -l
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected + "status:0 cwd:/target/leaf\n/target/leaf /targeted /target /\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task DirsClearValidatesAllOperandsBeforeClearing()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            pushd /target >/dev/null
            dirs -c -z
            printf 'rejected:%s\n' "$?"
            dirs -l
            dirs -c +99 1>&-
            printf 'cleared:%s\n' "$?"
            dirs -l
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("rejected:2\n/target /\ncleared:0\n/target\n", result.StdOut);
        Assert.Equal("line 2: dirs: -z: invalid number\ndirs: usage: dirs [-clpv] [+N] [-N]\n", result.StdErr);
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
        Assert.Equal("line 1: popd: directory stack empty\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

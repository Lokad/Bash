namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ChildShellLaunchTests
{
    [Fact]
    public async Task FileLaunchUsesInvocationDirectoryAndPreservesSourceAndArguments()
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "printf wrong\n");
        host.AddFile("/sub/unit", [.. "printf '<%s>' \"$0\" \"$#\" \"$@\" 'L"u8, 0xff, .. "R'\n"u8]);

        var result = await host.RunAsync("cd sub; PWD=/wrong bash ./unit '' $'a\\200b'", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        byte[] expected = [.. "<./unit><2><><a"u8, 0x80, .. "b><L"u8, 0xff, .. "R>"u8];
        Assert.Equal(expected, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CommandStringLaunchPreservesRawSource()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("bash -c $'printf %s L\\377R'", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { (byte)'L', 0xff, (byte)'R' }, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task SourceCallsProjectTheDefinitionPathAndCallLineThroughAFork()
    {
        await using var host = new TestHost();
        host.AddFile("/unit", """
            outer() {
                source ./defs
                printf '%s:%s\n' "${BASH_SOURCE[*]}" "${BASH_LINENO[*]}"
            }
            outer
            printf '%s:%s\n' "${BASH_SOURCE[*]}" "${BASH_LINENO[*]}"
            """);
        host.AddFile("/defs", """
            inner() { (printf '%s:%s\n' "${BASH_SOURCE[*]}" "${BASH_LINENO[*]}"); }
            inner
            """);

        var result = await host.RunAsync("bash ./unit", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("./defs ./defs ./unit ./unit:2 2 5 0\n./unit ./unit:5 0\n./unit:0\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("BASH_SOURCE", "./fixture")]
    [InlineData("BASH_LINENO", "0")]
    public async Task CallArraysKeepTheirCellsAcrossAssignmentAndUnset(string name, string first)
    {
        await using var host = new TestHost();
        host.AddFile("/fixture", name + "[0]=value; printf '%s\\n' \"$?\"\n"
            + name + "=(value); printf '%s\\n' \"$?\"\n"
            + "unset '" + name + "[0]'; printf '%s\\n' \"$?\" \"$" + name + "\"");

        var result = await host.RunAsync("bash ./fixture", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"0\n1\n1\n{first}\n", host.StandardOutput.TextContent);
        Assert.Equal($"./fixture: line 3: unset: {name}: cannot unset\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("SHLVL=7 exec bash -c 'printf \"%s\\n\" \"$SHLVL\"'", "7\n")]
    [InlineData("(SHLVL=7 exec bash -c 'printf \"%s\\n\" \"$SHLVL\"')", "8\n")]
    [InlineData("SHLVL=7 exec -c bash -c 'printf \"%s\\n\" \"$SHLVL\"'", "1\n")]
    [InlineData("bash -c 'SHLVL=7 command bash -c \"echo \\$SHLVL\"' fixture", "2\n")]
    [InlineData("unset SHLVL; OTHER=7; declare -n SHLVL=OTHER; exec bash -c 'printf \"%s:%s\\n\" \"$SHLVL\" \"$OTHER\"'", "1:6\n")]
    [InlineData("f(){ bash -c 'printf \"%s\\n\" \"$SHLVL\"'; }; row=$(f); printf '%s\\n' \"$row\"", "1\n")]
    public async Task ReplacementProjectsTheReachedLaunchLevel(string source, string expected)
    {
        await using var host = new TestHost();

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("readonly SHLVL=7; exec bash -c 'printf \"%s\\n\" \"$SHLVL\"' 2>&1",
        "line 1: SHLVL: readonly variable\n8\n", "")]
    [InlineData("SHLVL=1001; exec bash -c 'printf \"%s\\n\" \"$SHLVL\"' 2>&1",
        "line 1: warning: shell level (1000) too high, resetting to 1\n2\n", "")]
    [InlineData("trap 'printf \"%s\\n\" \"$SHLVL\"' EXIT; SHLVL=1001; exec missing",
        "1001\n", "line 1: exec: missing: not found\n")]
    public async Task ReplacementDiagnosticsFollowLookupAndPrecedeChildOutput(string source, string output, string diagnostic)
    {
        await using var host = new TestHost();

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(diagnostic.Length == 0 ? 0 : 127, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("", "level-7", "7:7\n")]
    [InlineData("SHLVL=12 ", "level-12", "7:12\n")]
    public async Task DirectSubshellRedirectionCanChangeItsBindingAfterTheChildEnvironmentIsCaptured(
        string prefix, string target, string content)
    {
        var source = "SHLVL=7; (" + prefix
            + """bash -c 'printf "%s:%s\n" "$1" "$SHLVL"' fixture "$SHLVL" >"level-$((SHLVL+=1))"); printf '%s\n' "$SHLVL" """;
        await using var host = new TestHost();

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("7\n", host.StandardOutput.TextContent);
        Assert.Equal(content, host.ReadFile("/" + target));
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(">report 2>&1", "2\n", "")]
    [InlineData("2>&-", "", "2\n")]
    public async Task DirectSubshellWarningUsesStderrBeforeTheLeafRedirection(
        string redirection, string report, string output)
    {
        var source = """SHLVL=1001; (bash -c 'printf "%s\n" "$SHLVL"' fixture """ + redirection + "); :";
        await using var host = new TestHost();

        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("line 1: warning: shell level (1000) too high, resetting to 1\n", host.StandardError.TextContent);
        Assert.Equal(output, host.StandardOutput.TextContent);
        if (report.Length != 0)
            Assert.Equal(report, host.ReadFile("/report"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("tail\\", "[tail]", "[tail\\]")]
    [InlineData("\"tail\"\\", "[tail]", "[tail\\]")]
    [InlineData("tail\\\\", "[tail\\]", "[tail\\]")]
    [InlineData("ta\\\nil", "[tail]", "[tail]")]
    public async Task FileAndCommandStringUseTheirOwnContinuationRules(string operand, string fileOutput, string commandOutput)
    {
        var source = "printf \"[%s]\" " + operand;
        await using var host = new TestHost();
        host.AddFile("/unit", source);

        var result = await host.RunAsync($"bash ./unit; printf '|'; bash -c '{source}'", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(fileOutput + "|" + commandOutput, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("bash ./broken 2>errors", "./broken: line 3: ")]
    [InlineData("bash -c 'printf earlier >effect\nif\n' launcher 2>errors", "launcher: -c: line 3: ")]
    public async Task ChildReportsLateSyntaxThroughItsDescriptorAfterEarlierEffects(string invocation, string diagnosticPrefix)
    {
        await using var host = new TestHost();
        host.AddFile("/sub/broken", "printf earlier >effect\nif\n");

        var result = await host.RunAsync("cd sub; " + invocation, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("earlier", host.ReadFile("/sub/effect"));
        Assert.Equal(diagnosticPrefix + "syntax error: unexpected end of file\n", host.ReadFile("/sub/errors"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task MissingChildScriptKeepsTheLookupFailureAndReleasesRedirection()
    {
        await using var host = new TestHost();
        host.AddDirectory("/sub");

        var result = await host.RunAsync("cd sub; bash ./missing 2>errors", CancellationToken.None);

        Assert.Equal(127, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("bash: ./missing: No such file or directory\n", host.ReadFile("/sub/errors"));
        Assert.False(host.FileExists("/sub/missing"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ExportFixtureImportsSourcedDefinitionIntoFileLaunch()
    {
        await using var host = new ExportTestHost();
        host.AddFile("/definitions", "f(){ printf '<%s>' \"$0\" \"$@\"; }\n");
        host.AddFile("/sub/unit", "f \"$@\"\n");

        var result = await host.RunAsync(
            "source /definitions; export -f f; cd sub; /usr/bin/bash ./unit 'a b'", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<./unit><a b>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }
}

using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class QueryDiagnosticTests
{
    // Bash 5.2 results with the fixture executable prefix omitted.
    [Theory]
    [InlineData("command -V qdg_missing",
        "", "line 1: command: qdg_missing: not found\n", 1)]
    [InlineData("type ''",
        "", "line 1: type: : not found\n", 1)]
    [InlineData("command -V ''",
        "", "line 1: command: : not found\n", 1)]
    [InlineData("type printf qdg_missing : qdg_absent",
        "printf is a shell builtin\n: is a shell builtin\n", "line 1: type: qdg_missing: not found\nline 1: type: qdg_absent: not found\n", 1)]
    [InlineData("command -V printf qdg_missing : qdg_absent",
        "printf is a shell builtin\n: is a shell builtin\n", "line 1: command: qdg_missing: not found\nline 1: command: qdg_absent: not found\n", 0)]
    [InlineData("type printf qdg_missing : qdg_absent 2>&1",
        "printf is a shell builtin\nline 1: type: qdg_missing: not found\n: is a shell builtin\nline 1: type: qdg_absent: not found\n", "", 1)]
    [InlineData("command -V printf qdg_missing : qdg_absent 2>&1",
        "printf is a shell builtin\nline 1: command: qdg_missing: not found\n: is a shell builtin\nline 1: command: qdg_absent: not found\n", "", 0)]
    [InlineData("type printf qdg_missing : 1>&2",
        "", "printf is a shell builtin\nline 1: type: qdg_missing: not found\n: is a shell builtin\n", 1)]
    [InlineData("command -V printf qdg_missing : 1>&2",
        "", "printf is a shell builtin\nline 1: command: qdg_missing: not found\n: is a shell builtin\n", 0)]
    [InlineData("command -V printf : 1>&-; printf '[%s]' \"$?\"",
        "[0]", "", 0)]
    [InlineData("command -v printf : 1>&-; printf '[%s]' \"$?\"",
        "[0]", "", 0)]
    [InlineData("type qdg_missing printf qdg_absent 2>&-; printf '[%s]' \"$?\"",
        "printf is a shell builtin\n[1]", "", 0)]
    [InlineData("command -V qdg_missing printf qdg_absent 2>&-; printf '[%s]' \"$?\"",
        "printf is a shell builtin\n[0]", "", 0)]
    [InlineData("x=0; command -V \"$((x++))\" printf \"$((x++))\" 2>&1; printf '[%s:%s]' \"$?\" \"$x\"",
        "line 1: command: 0: not found\nprintf is a shell builtin\nline 1: command: 1: not found\n[0:2]", "", 0)]
    [InlineData("type ${qdg_unset}",
        "", "", 0)]
    [InlineData("command -V printf qdg_missing : qdg_absent 1>&-; printf '[%s]' \"$?\"",
        "[0]", "line 1: command: qdg_missing: not found\nline 1: command: qdg_absent: not found\n", 0)]
    [InlineData("type printf qdg_missing 1>&- 2>&-; printf '[%s]' \"$?\"",
        "[1]", "", 0)]
    [InlineData("command -V printf qdg_missing 1>&- 2>&-; printf '[%s]' \"$?\"",
        "[0]", "", 0)]
    [InlineData("builtin command -V printf qdg_missing 2>&1",
        "printf is a shell builtin\nline 1: command: qdg_missing: not found\n", "", 0)]
    [InlineData("type", "", "", 0)]
    [InlineData("type --", "", "", 0)]
    [InlineData("type -t qdg_missing", "", "", 1)]
    [InlineData("command -v qdg_missing", "", "", 1)]
    [InlineData("type -t printf qdg_missing", "builtin\n", "", 1)]
    [InlineData("f(){ :; }; type -p printf f if cat", "/usr/bin/cat\n", "", 0)]
    [InlineData("f(){ :; }; type -p printf f if >&-", "", "", 0)]
    [InlineData("type -p printf qdg_missing", "", "", 1)]
    [InlineData("type -p -- -f printf", "", "", 1)]
    [InlineData("type -- -p printf", "printf is a shell builtin\n", "line 1: type: -p: not found\n", 1)]
    [InlineData("type -tp printf cat", "/usr/bin/cat\n", "", 0)]
    [InlineData("type -pt printf cat", "builtin\nfile\n", "", 0)]
    [InlineData("f(){ :; }; type -f f printf 2>&1", "line 1: type: f: not found\nprintf is a shell builtin\n", "", 1)]
    [InlineData("cat(){ :; }; type -pf cat", "/usr/bin/cat\n", "", 0)]
    [InlineData("printf(){ :; }; type -ft printf", "builtin\n", "", 0)]
    [InlineData("cat(){ :; }; type -Pf cat printf", "/usr/bin/cat\n/usr/bin/printf\n", "", 0)]
    [InlineData("type -Pt printf", "file\n", "", 0)]
    [InlineData("type -tP printf", "/usr/bin/printf\n", "", 0)]
    [InlineData("type -Pp printf : if", "/usr/bin/printf\n", "", 1)]
    [InlineData("type -P -- -t printf", "/usr/bin/printf\n", "", 1)]
    [InlineData("echo(){ :; }; type -at echo if", "function\nbuiltin\nfile\nkeyword\n", "", 0)]
    [InlineData("echo(){ :; }; type -aft echo", "builtin\nfile\n", "", 0)]
    [InlineData("f(){ :; }; type -ap f echo if", "/usr/bin/echo\n", "", 0)]
    [InlineData("function if { :; }; type -at if", "keyword\nfunction\n", "", 0)]
    [InlineData("type -t if IF '[[' '[[x' ']]' ']]x'", "keyword\nkeyword\nkeyword\n", "", 1)]
    [InlineData("type -aPt echo if", "file\n", "", 1)]
    [InlineData("type -t -- -a shift", "builtin\n", "", 1)]
    [InlineData("type -- --help", "", "line 1: type: --help: not found\n", 1)]
    [InlineData("type echo --help", "echo is a shell builtin\n", "line 1: type: --help: not found\n", 1)]
    [InlineData("type -taz --help", "", "line 1: type: -z: invalid option\ntype: usage: type [-afptP] name [name ...]\n", 2)]
    [InlineData("command -v printf qdg_missing", "printf\n", "", 0)]
    [InlineData("command -V -- -v printf", "printf is a shell builtin\n", "line 1: command: -v: not found\n", 0)]
    [InlineData("command -V printf -v", "printf is a shell builtin\n", "line 1: command: -v: not found\n", 0)]
    [InlineData("command -v printf -V", "printf\n", "", 0)]
    [InlineData("command -V -- -- printf", "printf is a shell builtin\n", "line 1: command: --: not found\n", 0)]
    public async Task QueriesPreserveChannelsOrderAndBuiltinSpecificStatus(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("type $'\\xff'",
        "", "6C696E6520313A20747970653A20FF3A206E6F7420666F756E640A", 1)]
    [InlineData("command -V $'\\xff'",
        "", "6C696E6520313A20636F6D6D616E643A20FF3A206E6F7420666F756E640A", 1)]
    [InlineData("command -V printf $'\\xff' : 2>&1",
        "7072696E74662069732061207368656C6C206275696C74696E0A6C696E6520313A20636F6D6D616E643A20FF3A206E6F7420666F756E640A3A2069732061207368656C6C206275696C74696E0A", "", 0)]
    [InlineData("type $'a\\n\\xff\\r' 2>&1",
        "6C696E6520313A20747970653A20610AFF0D3A206E6F7420666F756E640A", "", 1)]
    [InlineData("command -V $'a\\n\\xff\\r' 2>&1",
        "6C696E6520313A20636F6D6D616E643A20610AFF0D3A206E6F7420666F756E640A", "", 1)]
    [InlineData("type -t $'\\xff'", "", "", 1)]
    [InlineData("type $'-t\\xff'", "", "6C696E6520313A20747970653A202DFF3A20696E76616C6964206F7074696F6E0A747970653A2075736167653A2074797065205B2D61667074505D206E616D65205B6E616D65202E2E2E5D0A", 2)]
    [InlineData("command -v $'\\xff'", "", "", 1)]
    public async Task QueriesRetainInvalidUtf8InDiagnostics(
        string script, string outputHex, string errorHex, int expectedExitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task QueryDiagnosticsUseTheRetainedDefinitionAndReachedErrorDescriptor()
    {
        await using var host = new TestHost();
        host.AddFile("/definitions.sh", "f() {\n type $'\\xff' true 2>report >&-\n}\n");
        host.AddFile("/caller.sh", ":\nf\n");

        var result = await host.RunAsync("source ./definitions.sh\nsource ./caller.sh", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
        byte[] expected =
        [
            .. "./definitions.sh: line 2: type: "u8, 0xff, .. ": not found\n"u8,
            .. "./definitions.sh: line 2: type: write error: Bad file descriptor\n"u8
        ];
        Assert.Equal(expected, host.ReadFileBytes("/report"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("command -Vq printf", "line 1: command: -q: invalid option\n")]
    [InlineData("command -V -z printf", "line 1: command: -z: invalid option\n")]
    [InlineData("command -V --unknown printf", "line 1: command: --: invalid option\n")]
    public async Task InvalidQueryOptionsUseTheCommandRedirection(string command, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(command + " 2>/errors", EndToEndHarness.BuildTree([]));

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(expectedError + "command: usage: command [-pVv] command [arg ...]\n",
            await EndToEndHarness.ReadFileAsync(result.Host, "/errors"));
    }

    [Fact]
    public async Task InvalidTypeBundleUsesTheActiveErrorDescriptor()
    {
        var result = await EndToEndHarness.RunScriptAsync("type -tfz printf 2>/errors", EndToEndHarness.BuildTree([]));

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal("line 1: type: -z: invalid option\ntype: usage: type [-afptP] name [name ...]\n",
            await EndToEndHarness.ReadFileAsync(result.Host, "/errors"));
    }
}

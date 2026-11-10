using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class QueryDiagnosticTests
{
    // Bash 5.2 results, without its executable/line prefix on diagnostics.
    [Theory]
    [InlineData("command -V qdg_missing",
        "", "command: qdg_missing: not found\n", 1)]
    [InlineData("type ''",
        "", "type: : not found\n", 1)]
    [InlineData("command -V ''",
        "", "command: : not found\n", 1)]
    [InlineData("type printf qdg_missing : qdg_absent",
        "printf is a shell builtin\n: is a shell builtin\n", "type: qdg_missing: not found\ntype: qdg_absent: not found\n", 1)]
    [InlineData("command -V printf qdg_missing : qdg_absent",
        "printf is a shell builtin\n: is a shell builtin\n", "command: qdg_missing: not found\ncommand: qdg_absent: not found\n", 0)]
    [InlineData("type printf qdg_missing : qdg_absent 2>&1",
        "printf is a shell builtin\ntype: qdg_missing: not found\n: is a shell builtin\ntype: qdg_absent: not found\n", "", 1)]
    [InlineData("command -V printf qdg_missing : qdg_absent 2>&1",
        "printf is a shell builtin\ncommand: qdg_missing: not found\n: is a shell builtin\ncommand: qdg_absent: not found\n", "", 0)]
    [InlineData("type printf qdg_missing : 1>&2",
        "", "printf is a shell builtin\ntype: qdg_missing: not found\n: is a shell builtin\n", 1)]
    [InlineData("command -V printf qdg_missing : 1>&2",
        "", "printf is a shell builtin\ncommand: qdg_missing: not found\n: is a shell builtin\n", 0)]
    [InlineData("command -V printf : 1>&-; printf '[%s]' \"$?\"",
        "[0]", "", 0)]
    [InlineData("command -v printf : 1>&-; printf '[%s]' \"$?\"",
        "[0]", "", 0)]
    [InlineData("type qdg_missing printf qdg_absent 2>&-; printf '[%s]' \"$?\"",
        "printf is a shell builtin\n[1]", "", 0)]
    [InlineData("command -V qdg_missing printf qdg_absent 2>&-; printf '[%s]' \"$?\"",
        "printf is a shell builtin\n[0]", "", 0)]
    [InlineData("x=0; command -V \"$((x++))\" printf \"$((x++))\" 2>&1; printf '[%s:%s]' \"$?\" \"$x\"",
        "command: 0: not found\nprintf is a shell builtin\ncommand: 1: not found\n[0:2]", "", 0)]
    [InlineData("type ${qdg_unset}",
        "", "", 0)]
    [InlineData("command -V printf qdg_missing : qdg_absent 1>&-; printf '[%s]' \"$?\"",
        "[0]", "command: qdg_missing: not found\ncommand: qdg_absent: not found\n", 0)]
    [InlineData("type printf qdg_missing 1>&- 2>&-; printf '[%s]' \"$?\"",
        "[1]", "", 0)]
    [InlineData("command -V printf qdg_missing 1>&- 2>&-; printf '[%s]' \"$?\"",
        "[0]", "", 0)]
    [InlineData("builtin command -V printf qdg_missing 2>&1",
        "printf is a shell builtin\ncommand: qdg_missing: not found\n", "", 0)]
    [InlineData("type", "", "", 0)]
    [InlineData("type --", "", "", 0)]
    [InlineData("type -t qdg_missing", "", "", 1)]
    [InlineData("command -v qdg_missing", "", "", 1)]
    [InlineData("type -t printf qdg_missing", "builtin\n", "", 1)]
    [InlineData("command -v printf qdg_missing", "printf\n", "", 0)]
    [InlineData("command -V -- -v printf", "printf is a shell builtin\n", "command: -v: not found\n", 0)]
    [InlineData("command -V printf -v", "printf is a shell builtin\n", "command: -v: not found\n", 0)]
    [InlineData("command -v printf -V", "printf\n", "", 0)]
    [InlineData("command -V -- -- printf", "printf is a shell builtin\n", "command: --: not found\n", 0)]
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
        "", "747970653A20FF3A206E6F7420666F756E640A", 1)]
    [InlineData("command -V $'\\xff'",
        "", "636F6D6D616E643A20FF3A206E6F7420666F756E640A", 1)]
    [InlineData("command -V printf $'\\xff' : 2>&1",
        "7072696E74662069732061207368656C6C206275696C74696E0A636F6D6D616E643A20FF3A206E6F7420666F756E640A3A2069732061207368656C6C206275696C74696E0A", "", 0)]
    [InlineData("type $'a\\n\\xff\\r' 2>&1",
        "747970653A20610AFF0D3A206E6F7420666F756E640A", "", 1)]
    [InlineData("command -V $'a\\n\\xff\\r' 2>&1",
        "636F6D6D616E643A20610AFF0D3A206E6F7420666F756E640A", "", 1)]
    [InlineData("type -t $'\\xff'", "", "", 1)]
    [InlineData("command -v $'\\xff'", "", "", 1)]
    public async Task QueriesRetainInvalidUtf8InDiagnostics(
        string script, string outputHex, string errorHex, int expectedExitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("command -Vq printf", "command: -q: invalid option\n")]
    [InlineData("command -V -z printf", "command: -z: invalid option\n")]
    [InlineData("command -V --unknown printf", "command: --: invalid option\n")]
    public async Task InvalidQueryOptionsUseTheCommandRedirection(string command, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(command + " 2>/errors", EndToEndHarness.BuildTree([]));

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(expectedError + "command: usage: command [-pVv] command [arg ...]\n",
            await EndToEndHarness.ReadFileAsync(result.Host, "/errors"));
    }
}

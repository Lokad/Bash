using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class TestBuiltinTests
{
    [Theory]
    [InlineData("test", 1)]
    [InlineData("test ''", 1)]
    [InlineData("test -z", 0)]
    [InlineData("test !", 0)]
    [InlineData("test '('", 0)]
    [InlineData("test ! -z", 1)]
    [InlineData("test -n =", 0)]
    [InlineData("test -z = ''", 1)]
    [InlineData("test -n = -n", 0)]
    [InlineData("test ! = '!'", 0)]
    [InlineData("test '(' = '('", 0)]
    [InlineData("test '(' x ')'", 0)]
    [InlineData("test x -a -z", 0)]
    [InlineData("test '!' -a ''", 1)]
    [InlineData("test ! '(' x ')'", 1)]
    [InlineData("test x -a -n = x", 1)]
    [InlineData("test ! ! ! ! -n", 0)]
    [InlineData("test '  +001 \t' -eq 1", 0)]
    [InlineData("test 010 -eq 10", 0)]
    [InlineData("test -9223372036854775808 -lt 9223372036854775807", 0)]
    [InlineData("[ -z ]", 0)]
    [InlineData("[ ]", 1)]
    [InlineData("test -o unknown", 1)]
    [InlineData("test -o pipefail", 0)]
    [InlineData("test -o errexit", 1)]
    [InlineData("set -e; test -o errexit", 0)]
    [InlineData("set -e; [[ -o errexit ]]", 0)]
    [InlineData("test '(' -o 1 ')'", 1)]
    public async Task ArgumentCountAndDecimalRulesMatchGnu(string script, int exitCode)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("test abc -eq 1", "test: abc: integer expression expected\n")]
    [InlineData("test 1 -eq ''", "test: : integer expression expected\n")]
    [InlineData("test 0x10 -eq 16", "test: 0x10: integer expression expected\n")]
    [InlineData("test 1+1 -eq 2", "test: 1+1: integer expression expected\n")]
    [InlineData("test 9223372036854775808 -eq 0", "test: 9223372036854775808: integer expression expected\n")]
    [InlineData("test '' -a abc -eq 1", "test: abc: integer expression expected\n")]
    [InlineData("test x -o abc -eq 1", "test: abc: integer expression expected\n")]
    [InlineData("test '(' x", "test: (: unary operator expected\n")]
    [InlineData("test -n x = x", "test: too many arguments\n")]
    [InlineData("test '!' != '' -o x", "test: too many arguments\n")]
    [InlineData("[ x", "[: missing `]'\n")]
    [InlineData("[", "[: missing `]'\n")]
    public async Task ErrorsReturnTwoAndADiagnosticThenContinue(string script, string diagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script + "; printf '%s' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2", host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("test", "")]
    [InlineData("[", " ]")]
    public async Task ComparisonsAndLengthsPreserveInvalidUtf8(string name, string closing)
    {
        await using var host = new TestHost();
        var script = "x=$'\\377'; y=$'\\376'; "
            + name + " \"$x\" = \"$x\"" + closing + "; printf '%s:' \"$?\"; "
            + name + " \"$x\" != \"$y\"" + closing + "; printf '%s:' \"$?\"; "
            + name + " \"$y\" \\< \"$x\"" + closing + "; printf '%s:' \"$?\"; "
            + name + " -n \"$x\"" + closing + "; printf '%s:' \"$?\"; "
            + name + " -z \"$x\"" + closing + "; printf '%s' \"$?\"";
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0:0:0:0:1", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task InvalidByteNumericDiagnosticPreservesItsOperand()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("test $'\\377' -eq 0", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(Convert.FromHexString("746573743a20ff3a20696e74656765722065787072657373696f6e2065787065637465640a"),
            host.StandardError.Content.ToArray());
    }
}

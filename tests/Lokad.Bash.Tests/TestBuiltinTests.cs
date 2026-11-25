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
    [InlineData("test '(prefix' x ')'", 0)]
    [InlineData("test '(prefix' '' ')suffix'", 1)]
    [InlineData("[ ! '(' x ')suffix' ]", 1)]
    [InlineData("test '(prefix' '=' ')suffix'", 1)]
    [InlineData("test x -a -z", 0)]
    [InlineData("test '!' -a ''", 1)]
    [InlineData("test ! '(' x ')'", 1)]
    [InlineData("test x -a -n = x", 1)]
    [InlineData("test ! ! ! ! -n", 0)]
    [InlineData("test '  +001 \t' -eq 1", 0)]
    [InlineData("[ 7 -eq $'\\r\\n\\v\\f+007\\t ' ]", 0)]
    [InlineData("test 010 -eq 10", 0)]
    [InlineData("test -9223372036854775808 -lt 9223372036854775807", 0)]
    [InlineData("[ -z ]", 0)]
    [InlineData("[ ]", 1)]
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
    [InlineData("test -o braceexpand", 0)]
    [InlineData("[ -o braceexpand ]", 0)]
    [InlineData("option=braceexpand; [[ -o \"$option\" ]]", 0)]
    [InlineData("test -o unknown", 1)]
    [InlineData("test -o pipefail", 0)]
    [InlineData("test -o errexit", 1)]
    [InlineData("set -e; test -o errexit", 0)]
    [InlineData("set -e; [[ -o errexit ]]", 0)]
    public async Task OptionPredicates_ReportFixedAndMutableOptions(string script, int exitCode)
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
    [InlineData("test $'7\\r' -eq 7", "test: 7\r: integer expression expected\n")]
    [InlineData("[ $'02\\n' -gt $'1\\v' ]", "[: 02\n: integer expression expected\n")]
    [InlineData("[ 7 -lt $'8\\v\\t' ]", "[: 8\v\t: integer expression expected\n")]
    [InlineData("test yes -o $'7\\f' -eq 7", "test: 7\f: integer expression expected\n")]
    [InlineData("test '' -a abc -eq 1", "test: abc: integer expression expected\n")]
    [InlineData("test x -o abc -eq 1", "test: abc: integer expression expected\n")]
    [InlineData("test '(' x", "test: (: unary operator expected\n")]
    [InlineData("test -n x = x", "test: too many arguments\n")]
    [InlineData("test '!' != '' -o x", "test: too many arguments\n")]
    [InlineData("test ! ! ! ! !", "test: argument expected\n")]
    [InlineData("test x = x -a '!'", "test: argument expected\n")]
    [InlineData("[ -n x -o '!' '!' ]", "[: argument expected\n")]
    [InlineData("test x -a '(' x -o y", "test: `)' expected\n")]
    [InlineData("[ x -a '(' x -o y ]", "[: `)' expected, found ]\n")]
    [InlineData("test x -a '(' x extra ')'", "test: `)' expected, found extra\n")]
    [InlineData("[ '(prefix' -n '!' ')' ]", "[: syntax error: `-n' unexpected\n")]
    [InlineData("[ x", "[: missing `]'\n")]
    [InlineData("[", "[: missing `]'\n")]
    public async Task ErrorsReturnTwoAndADiagnosticThenContinue(string script, string diagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script + "; printf '%s' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2", host.StandardOutput.TextContent);
        Assert.Equal("line 1: " + diagnostic, host.StandardError.TextContent);
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
        Assert.Equal<byte>([.. "line 1: test: "u8, 0xff, .. ": integer expression expected\n"u8],
            host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task UnexpectedGroupOperandPreservesItsBytesInTheDiagnostic()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("test x -a '(' x $'\\377' ')'", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal<byte>([.. "line 1: test: `)' expected, found "u8, 0xff, 10], host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task RetainedPredicateReportsTheDefinitionLineThroughItsRedirection()
    {
        await using var host = new TestHost();
        host.AddFile("/sample.sh", """

            f(){
            :
            test bad -eq 1 2>\
            errors
            printf "<status:%s>" "$?"
            }
            """);
        var result = await host.RunAsync("""
            :
            source ./sample.sh

            f
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<status:2>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("./sample.sh: line 4: test: bad: integer expression expected\n", host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task EvalPredicateReportsTheParsedInvocationLineThroughItsRedirection()
    {
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync("""
            :
            eval ':
            [ 1 -eq bad ] \
             2>errors
            printf "<status:%s>" "$?"
            '
            """, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<status:2>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("line 7: [: bad: integer expression expected\n", host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
    }
}

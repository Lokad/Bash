using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArithmeticCommandBoundaryTestsEndToEnd
{
    [Theory]
    [InlineData("x=0; ((x=(1+2))); printf '%s:%s\\n' \"$?\" \"$x\"", "0:3\n")]
    [InlineData("x=0; ((x=((1+2)))); printf '%s:%s\\n' \"$?\" \"$x\"", "0:3\n")]
    [InlineData("x=0; ((x=(1)+(2))); printf '%s:%s\\n' \"$?\" \"$x\"", "0:3\n")]
    [InlineData("x=0; ((1 || (x=(1+2)))); printf '%s:%s\\n' \"$?\" \"$x\"", "0:0\n")]
    [InlineData("for ((i=(1+1);i<(2+1);i++)); do printf '%s\\n' \"$i\"; done; printf '%s\\n' \"$i\"", "2\n3\n")]
    [InlineData("x=0; (\\\n(x=(1+\\\n2))); printf '%s:%s\\n' \"$?\" \"$x\"", "0:3\n")]
    [InlineData("x=0; ((x=(1+2)\\\n)); printf '%s:%s\\n' \"$?\" \"$x\"", "0:3\n")]
    [InlineData("printf '%s\\n' \"$(( (1+2))\\\n)\"", "3\n")]
    [InlineData("printf '%s\\n' \"$(x=0; ((x=(1+2))); printf '%s' \"$x\")\"", "3\n")]
    [InlineData("((printf one); printf two); printf after", "onetwoafter")]
    [InlineData("( (printf one); printf two ); printf after", "onetwoafter")]
    [InlineData("((printf one); ((1))); printf after", "oneafter")]
    [InlineData("((printf ')'); printf two); printf after", ")twoafter")]
    [InlineData("missing=2; ((x=${missing:-)})); printf '%s:%s\\n' \"$?\" \"$x\"", "0:\n")]
    [InlineData("missing=2; ((x=${missing:-(})); printf '%s:%s\\n' \"$?\" \"$x\"", "0:\n")]
    [InlineData("false && ((x=${missing:-1)); printf after", "after")]
    [InlineData("false && ((x=${a:-${b:-1)); printf after", "after")]
    [InlineData("false && ((x=(${missing:-1))); printf after", "after")]
    [InlineData("false && ((x=\"${a:-))}\")); printf after", "after")]
    [InlineData("false && ((x=${a:-\"))\"})); printf after", "after")]
    [InlineData("false && ((x=${a:-(1})); printf after", "after")]
    [InlineData("false && ((x=${a:-(1))})); printf after", "after")]
    [InlineData("false && ((x=${a:-{1})); printf after", "after")]
    [InlineData("false && ((x=${a:-$(printf '))')})); printf after", "after")]
    [InlineData("false && ((x=${a:-$((1))})); printf after", "after")]
    public async Task CompleteArithmeticBoundariesPreserveCommandsAndSubshells(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("i=7", "\n", "", true)]
    [InlineData("i=7;1", "; ", "", false)]
    [InlineData("i=7;0;;i++", "\n", "", true)]
    [InlineData("i=$(printf 7; :);1", "\n", "", true)]
    [InlineData("i=\"7;1\";1", "\n", "", true)]
    [InlineData(@"i=7\;1;1", "\n", "", true)]
    [InlineData("i=7", "\n", "false && ", true)]
    [InlineData("a[raw;key]=7;1;", "\n", "", true)]
    public async Task ArithmeticForRequiresExactlyTwoSourceSeparators(
        string header, string separator, string prefix, bool earlierUnit)
    {
        var script = ": >before" + separator + prefix +
            $"for (({header})); do : >body; break; done\n: >after";
        Assert.False(Parser.TryParse(script, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("Arithmetic for header", error.Message);
        Assert.Equal(script.IndexOf("((", StringComparison.Ordinal), error.Offset);

        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains(error.Message, host.StandardError.TextContent);
        Assert.Equal(earlierUnit ? new[] { "before" } : [], host.CaptureEntries().Keys);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("", "body:0")]
    [InlineData(" \t ", "body:0")]
    [InlineData("\n", ":0")]
    [InlineData(" \n \t ", ":0")]
    [InlineData("\\\n", "body:0")]
    [InlineData("$(printf 1; :)", "body:0")]
    [InlineData("0", ":0")]
    public async Task ArithmeticForDistinguishesAnOmittedConditionFromAnExplicitZero(string condition, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"for ((;{condition};)); do printf body; break; done; printf ':%s' \"$?\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\\\n")]
    public async Task ArithmeticForSeparatorScanReusesNestedUpdateLookahead(string continuation)
    {
        var script = ": >unreached";
        for (var depth = 0; depth < 8; depth++)
            script = $"for ((;0;i=$({script}; printf {continuation}1))); do : >unreached; done";
        script += "\nprintf tail";

        var governor = new MemoryGovernor(96 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }
}

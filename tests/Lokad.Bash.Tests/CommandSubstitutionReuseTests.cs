using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class CommandSubstitutionReuseTests
{
    [Theory]
    [InlineData("f() { printf '<%s>' \"$(printf '%s' \"$value\")\"; }; value=one; f; value=two; f; printf '\\n'", "<one><two>\n")]
    [InlineData("value=$(cat <<'END'\n$(not-executed) )\nEND\n); printf '<%s>\\n' \"$value\"", "<$(not-executed) )>\n")]
    [InlineData("value=ok; result=$(cat <<END\n$(printf '%s' \"$value\")\nEND\n); printf '<%s>\\n' \"$result\"", "<ok>\n")]
    [InlineData("printf '<%s>\\n' \"$(case b in a|b) printf '%s' \"$(printf chosen)\";; esac)\"", "<chosen>\n")]
    [InlineData("value=ok; printf '<%s>\\n' \"${value:-`if`}\"", "<ok>\n")]
    [InlineData("printf '<%s>\\n' \"$\\\n(printf joined)\"", "<joined>\n")]
    [InlineData("printf '<%s><%s>' \"$((printf 'two words');)\" \"$((2 + 3))\"", "<two words><5>")]
    [InlineData("printf '<%s>' \"$((1 + $((printf 2);)))\"", "<3>")]
    [InlineData("v=held; printf '<%s>' \"${v:-$((printf wrong >&2);)}\"", "<held>")]
    [InlineData("cat <<END\n$((printf selected);)\nEND\n", "selected\n")]
    [InlineData("printf '<%s>' \"$((cat <<'END'\nline ) ))\nEND\n);)\"", "<line ) ))>")]
    public async Task RetainedSyntaxPreservesNativeExecution(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task CompleteArithmeticShapeKeepsItsExpressionFailure()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("printf '<%s>' \"$((printf wrong >decoy))\"; printf tail", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("arithmetic", host.StandardError.TextContent);
        Assert.Empty(host.CaptureEntries());
    }
}

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
    public async Task RetainedSyntaxPreservesNativeExecution(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
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
}

using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArithmeticValuesTestsEndToEnd
{
    [Theory]
    [InlineData("printf '%s\\n' \"$((-9223372036854775808))\"", "-9223372036854775808\n")]
    [InlineData("value=18446744073709551617; ((value += 1)); printf '%s:%s\\n' \"$?\" \"$value\"", "0:2\n")]
    [InlineData("x=9223372036854775807; ((x++)); printf '%s:%s\\n' \"$?\" \"$x\"", "0:-9223372036854775808\n")]
    [InlineData("let 'x=-9223372036854775808' 'x/=-1'; printf '%s:%s\\n' \"$?\" \"$x\"; let 'x-=x'; printf '%s:%s\\n' \"$?\" \"$x\"", "0:-9223372036854775808\n1:0\n")]
    [InlineData("for ((i=9223372036854775807; i>0; ++i)); do printf '%s\\n' \"$i\"; done; printf '%s\\n' \"$i\"", "9223372036854775807\n-9223372036854775808\n")]
    [InlineData("value=-18446744073709551617; printf '%s\\n' \"$((value))\"", "-1\n")]
    [InlineData("x=9223372036854775807; printf '%s:' \"$((x++))\" \"$x\" \"$((--x))\"; printf '%s\\n' \"$x\"", "9223372036854775807:-9223372036854775808:9223372036854775807:9223372036854775807\n")]
    public async Task ArithmeticConsumers_PreserveSigned64BitValues(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

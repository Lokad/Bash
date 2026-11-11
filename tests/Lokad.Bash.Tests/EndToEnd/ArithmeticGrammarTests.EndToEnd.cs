using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArithmeticGrammarTestsEndToEnd
{
    [Theory]
    [InlineData("x=3; printf '%s:%s\\n' \"$((0++1))\" \"$x\"", "1:3\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((0--1))\" \"$x\"", "1:3\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((x++++1))\" \"$x\"", "4:4\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((x----1))\" \"$x\"", "4:2\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((0+++x))\" \"$x\"", "4:4\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((0---x))\" \"$x\"", "-2:2\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((x += ++x))\" \"$x\"", "7:7\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((0 && (x++++1)))\" \"$x\"", "0:3\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((1 || (0+++x)))\" \"$x\"", "1:3\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((++1))\" \"$x\"", "1:3\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((--(x)))\" \"$x\"", "3:3\n")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((++x + x++))\" \"$x\"", "8:5\n")]
    public async Task ArithmeticSigns_PreserveValueAndMutationOrder(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

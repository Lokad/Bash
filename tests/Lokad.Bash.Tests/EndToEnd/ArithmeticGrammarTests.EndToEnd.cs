using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArithmeticGrammarTestsEndToEnd
{
    [Theory]
    [InlineData("""printf '<%s>' a$[2 + (1 + 2)]b""", "<a5b>")]
    [InlineData("""a=([1]=9); printf '<%s>' "$[a[$[1]]+1]" """, "<10>")]
    [InlineData("""x=0; printf '<%s>' "$(( $[x+=2]+1 ))" "$[ $((x+1))+2 ]" "$x" """, "<3><5><2>")]
    [InlineData("""printf '<%s>' "$[$(printf "2")+3]" "$[\"2\"+3]" """, "<5><5>")]
    [InlineData("""[[ $[1+2] == 3 ]] && printf '<%s>' "${missing:-$[7+1]}" """, "<8>")]
    [InlineData("read -r value <<EOF\n$[1+2]\nEOF\nprintf '<%s>' \"$value\"", "<3>")]
    [InlineData("""x='$[1+2]'; printf '<%s>' "$x" "\$[1+2]" $'$[1+2]'""", "<$[1+2]><$[1+2]><$[1+2]>")]
    [InlineData("""f(){ printf '<%s>' $[4 + 2]; }; eval "$(declare -f f)"; f""", "<6>")]
    public async Task LegacyArithmeticUsesExistingExpansionAndBindingPaths(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

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

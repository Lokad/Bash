using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationIntegerDeclarationTestsEndToEnd
{
    [Theory]
    [InlineData("""declare -i n='2+3'; n='4*7'; n+='6/2'; printf '<%s:%s>' "$n" "${n@a}"; declare +i n='5+6'; declare -p n""",
        "<31:i>declare -- n=\"5+6\"\n")]
    [InlineData("""i=1; n='i=4'; declare -i n; printf '<%s>' "$n"; n+=i; printf '<%s:%s>' "$n" "$i" """,
        "<i=4><8:4>")]
    [InlineData("""declare -i n; printf '<%s:%s>' "${n+set}" "${n@A}"; n=' '; printf '<%s>' "$n" """,
        "<:declare -i n><0>")]
    [InlineData("""declare -ai a=([2]='7*6' [20]='8+9'); a[2]+='2+3'; a[4]='020'; declare -p a""",
        "declare -ai a=([2]=\"47\" [4]=\"16\" [20]=\"17\")\n")]
    [InlineData("""declare -Ai a=([a]='2*8' [b]='20-1'); a[a]+='5+6'; a[c]='0x20'; declare -p a""",
        "declare -Ai a=([a]=\"27\" [b]=\"19\" [c]=\"32\" )\n")]
    [InlineData("""i=0; declare -ai a; declare -n out='a[i++]'; out='i++'; printf '<%s:%s>' "$i" "${a[0]}" """,
        "<2:1>")]
    [InlineData("""declare -i n=1; read -r n <<< '2+3'; printf '<%s>' "$n"; printf -v n '%s' '2*9'; printf '<%s>' "$n" """,
        "<5><18>")]
    [InlineData("""declare -i n; printf '<%s:%s>' "${n:=2+3}" "$n"; for n in '4+5' '6*7'; do printf '<%s>' "$n"; done""",
        "<5:5><9><42>")]
    [InlineData("""declare -i n; a=7; getopts a n -a; printf '<%s:%s:%s>' "$n" "$OPTIND" "${OPTIND@a}" """,
        "<7:2:i>")]
    [InlineData("""OPTIND='1+1'; getopts ab opt -a -b; printf '<%s:%s>' "$opt" "$OPTIND"; unset OPTIND; OPTIND='1+1'; declare -p OPTIND""",
        "<b:3>declare -- OPTIND=\"1+1\"\n")]
    [InlineData("""i=0; declare -ai a=([8]=9); mapfile -t -O 10 a <<< $'i++\na[10]+7'; declare -p a; printf '<%s>' "$i" """,
        "declare -ai a=([8]=\"9\" [10]=\"0\" [11]=\"7\")\n<1>")]
    [InlineData("""i=0; declare -ai a; read -ra a <<< 'i++ i++ a[1]+1'; declare -p a; printf '<%s>' "$i" """,
        "declare -ai a=([0]=\"0\" [1]=\"1\" [2]=\"2\")\n<2>")]
    [InlineData("""declare -i n=2; f(){ declare -p n; }; n='3+4' f; n+='3+4' f; declare -p n""",
        "declare -x n=\"3+4\"\ndeclare -x n=\"9\"\ndeclare -i n=\"2\"\n")]
    [InlineData("""declare -i n=4; n='2+3' printf -v n '%s' '6+7'; n='2+3' read -r n <<< '18*2'; declare -p n""",
        "declare -i n=\"13\"\n")]
    [InlineData("""declare -i n=3; f(){ local n=5; declare -g n='n+2'; declare -p n; }; f; declare -p n""",
        "declare -- n=\"5\"\ndeclare -i n=\"7\"\n")]
    [InlineData("""v=value; declare -in r=v; printf '<%s:%s>' "$?" "${r-unset}"; declare -i r=5; declare -n r=v; declare -p r""",
        "<1:unset>declare -n r=\"v\"\n")]
    [InlineData("""x=old; declare -n r=x; declare -in r=y; printf '<%s>' "$?"; declare -p r x""",
        "<1>declare -in r=\"x\"\ndeclare -- x=\"old\"\n")]
    [InlineData("""x=one; y=two; declare -n ref=x; declare -in ref=y; for ref in x y; do printf '<%s>' "$ref"; done; declare -p ref""",
        "declare -in ref=\"x\"\n")]
    [InlineData("""declare -i OPTARG; getopts a: opt -a '2+3'; printf '<%s:%s>' "$OPTARG" "${OPTARG@a}"; getopts b opt -a '2+3' -b; declare -p OPTARG""",
        "<5:i>declare -i OPTARG=\"0\"\n")]
    public async Task IntegerWritesUseTheSelectedBinding(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("""declare -ix n=2; f(){ local -I n; n='3+4'; declare -p n; }; f; declare -p n""",
        "declare -ix n=\"7\"\ndeclare -ix n=\"2\"\n", "")]
    [InlineData("""declare -a a=([4]=outer); f(){ local -I a+=(inner); declare -p a; }; f; declare -p a""",
        "declare -a a=([4]=\"outer\" [5]=\"inner\")\ndeclare -a a=([4]=\"outer\")\n", "")]
    [InlineData("""declare -A a=([old]=old); f(){ declare -I a=([new]=value); declare -p a; }; f; declare -p a""",
        "declare -A a=([new]=\"value\" )\ndeclare -A a=([old]=\"old\" )\n", "")]
    [InlineData("""x=outer; declare -n r=x; f(){ local -I r; declare -p r; r=inner; declare -p r x; }; f; declare -p r""",
        "declare -- r=\"x\"\ndeclare -- r=\"inner\"\ndeclare -- x=\"outer\"\ndeclare -n r=\"x\"\n", "")]
    [InlineData("""declare -n r; f(){ local -I r; declare -p r; }; f; declare -p r""",
        "declare -- r\ndeclare -n r\n", "")]
    [InlineData("""readonly x=outer; f(){ local -I x; printf '<%s>' "$?"; }; f; declare -p x""",
        "<1>declare -r x=\"outer\"\n", "local: x: readonly variable\n")]
    [InlineData("""declare -A a=([k]=x); f(){ local -Ia a; printf '<%s>' "$?"; }; f; declare -p a""",
        "<1>declare -A a=([k]=\"x\" )\n", "local: a: cannot convert associative to indexed array\n")]
    [InlineData("""getopts ab x -ab; f(){ local -I OPTIND; getopts cd y -cd; printf '<%s:%s>' "$y" "$OPTIND"; }; f; getopts ab x -ab; printf '<%s:%s>' "$x" "$OPTIND" """,
        "<c:1><b:2>", "")]
    public async Task InheritedLocalsSeparateCallerAndCalleeBindings(string script, string expected, string diagnostics)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(diagnostics, result.StdErr);
    }

    [Theory]
    [InlineData("read -ra a <<< 'i++ 1+'", "read: ", 0)]
    [InlineData("mapfile -t -O 10 a <<< $'i++\\n1+'", "mapfile: ", 10)]
    public async Task InvalidIntegerInputKeepsEarlierWrites(string input, string commandPrefix, int index)
    {
        var state = new Eval(Parser.Parse("i=0; declare -ai a=([8]=9); " + input + "; printf tail"));
        await using var host = new TestHost();

        var result = await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(commandPrefix + "1+: invalid arithmetic expression\n", host.StandardError.TextContent);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        var values = state.IndexedArrays["a"];
        Assert.Equal(index + 1, values.Count);
        Assert.Equal("0", values[index].ToUtf8String());
        if (index != 0)
            Assert.Equal("9", values[8].ToUtf8String());
        Assert.Equal("1", state.Variables["i"].ToUtf8String());
    }

    [Theory]
    [InlineData("read -ra a <<< 'i++ long_value'")]
    [InlineData("mapfile -t -O 10 a <<< $'i++\\nlong_value'")]
    public async Task IntegerInputRefusalRestoresTheOriginalArray(string input)
    {
        var state = new Eval(Parser.Parse("i=0; declare -ai a=([8]=9); " + input));
        state.SetExportedVariable("long_value", ShellValue.FromText(new string(' ', 65536) + "x+1"));
        var governor = new MemoryGovernor(320 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent, StringComparison.Ordinal);
        Assert.Equal("1", state.Variables["i"].ToUtf8String());
        var values = state.IndexedArrays["a"];
        Assert.Equal(9, values.Count);
        Assert.Equal("9", values[8].ToUtf8String());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}

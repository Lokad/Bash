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
    [InlineData("""declare -ni r; printf '<%s>' "$?"; declare -p r""",
        "<0>declare -in r\n", "", 0)]
    [InlineData("""r=dest; declare -ni r; printf '<%s>' "$?"; declare -p r""",
        "<0>declare -in r=\"dest\"\n", "", 0)]
    [InlineData("""dest=10; r=outer; f(){ local -nix r=dest; printf '<%s>' "$?"; declare -p r; }; f; declare -p r""",
        "<1>declare -ix r\ndeclare -- r=\"outer\"\n", "", 0)]
    [InlineData("""i=0; dest='i++'; declare -ni r=dest; printf '<%s:%s:%s>' "$?" "$i" "${r+set}" """,
        "<1:1:>", "", 0)]
    [InlineData("""i=0; j=0; a='i++'; declare -n r=a; declare -ni r+='j++'; printf '<%s:%s:%s>' "$?" "$i" "$j"; declare -p r a""",
        "<1:1:1>declare -in r=\"a\"\ndeclare -- a=\"i++\"\n", "line 1: declare: `j++': not a valid identifier\n", 0)]
    [InlineData("""readonly r=old; declare -ni r=; printf '<%s>' "$?"; declare -p r""",
        "<1>declare -r r=\"old\"\n", "line 1: declare: r: readonly variable\n", 0)]
    [InlineData("""readonly r=old; declare -ni r='not a name'; printf '<%s>' "$?"; declare -p r""",
        "<1>declare -r r=\"old\"\n", "line 1: declare: `not a name': invalid variable name for name reference\n", 0)]
    [InlineData("""declare -a r=([2]=held); declare -ni r=dest; printf '<%s>' "$?"; declare -p r""",
        "<1>declare -a r=([2]=\"held\")\n", "line 1: declare: r: reference variable cannot be an array\n", 0)]
    [InlineData("""d=10; r=caller; f(){ local -n r=d; declare -i +n r=2; printf '<%s>' "$?"; declare -p r d; }; f; declare -p r""",
        "<1>declare -in r=\"d\"\ndeclare -- d=\"10\"\ndeclare -- r=\"caller\"\n", "line 1: declare: `2': not a valid identifier\n", 0)]
    [InlineData("""declare -nx r; declare -i +n r=dest; status=$?; r='2+3'; printf '<%s>' "$status"; declare -p r""",
        "<1>declare -- r=\"2+3\"\n", "", 0)]
    [InlineData("""r=caller; f(){ local -r r=old; local -ni r=r; printf '<%s>' "$?"; declare -p r; }; f""",
        "<1>declare -r r=\"old\"\n", "line 1: local: warning: r: circular name reference\nline 1: local: r: readonly variable\n", 0)]
    [InlineData("""r=old; declare -ni r+='not a name'; printf unreachable""",
        "", "line 1: declare: not a name: syntax error in expression (error token is \"a name\")\n", 1)]
    [InlineData("""dest=7; declare -ni first bad=dest last; printf '<%s:%s>' "$?" "${bad+set}"; declare -p first last""",
        "<1:>declare -in first\ndeclare -in last\n", "", 0)]
    public async Task IntegerAndNamerefOptionsKeepTheReachedDeclaration(
        string script, string expected, string diagnostics, int status)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(diagnostics, result.StdErr);
    }

    [Fact]
    public async Task MapfileIntegerOriginWrapsWhilePreservingUnrelatedSparsePositions()
    {
        const string script = """
            declare -ai a=([0]=90 [2]=12 [4294967296]=80)
            mapfile -t -O 4294967295 a <<< $'2+3\n4+5\na[0]+4'
            printf '<%s:%s:%s:%s:%s:%s>' "$?" "${a[4294967295]}" "${a[0]}" "${a[1]}" "${a[2]}" "${a[4294967296]}"
            """;

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0:5:9:13:12:80>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task MapfileIntegerOriginWrapsAcrossInputBatches()
    {
        const string script = """
            declare -ai a=([0]=80)
            { mapfile -t -O 4294967168 -n 129 a; status=$?; IFS= read -r tail; } <records
            printf '<%s:%s:%s:%s:%s>' "$status" "${a[4294967168]}" "${a[4294967295]}" "${a[0]}" "$tail"
            """;
        var records = string.Concat(Enumerable.Repeat("2+3\n", 127)) + "4+5\na[4294967295]+1\ntail\n";
        await using var host = EndToEndHarness.BuildTree([], [("/records", records)]);

        var result = await EvaluationTestHelper.RunAsync(script, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0:5:9:10:tail>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task ReadarrayIntegerOriginWrapsWithACustomDelimiter()
    {
        const string script = """
            declare -ai a=([0]=80 [2]=9)
            { readarray -td: -O 4294967294 -n 3 a; status=$?; IFS= read -rd: tail; } <records
            printf '<%s:%s:%s:%s:%s:%s>' "$status" "${a[4294967294]}" "${a[4294967295]}" "${a[0]}" "${a[2]}" "$tail"
            """;
        await using var host = EndToEndHarness.BuildTree([], [("/records", "1+2:3+4:a[4294967295]+1:tail:")]);

        var result = await EvaluationTestHelper.RunAsync(script, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0:3:7:8:9:tail>", result.StdOut);
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
        "<1>declare -r x=\"outer\"\n", "line 1: local: x: readonly variable\n")]
    [InlineData("""declare -A a=([k]=x); f(){ local -Ia a; printf '<%s>' "$?"; }; f; declare -p a""",
        "<1>declare -A a=([k]=\"x\" )\n", "line 1: local: a: cannot convert associative to indexed array\n")]
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
        Assert.Equal("line 1: " + commandPrefix + "1+: syntax error: operand expected (error token is \"+\")\n", host.StandardError.TextContent);
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
    [InlineData("true; number='1 +'; printf skipped", "[1:2:1]EXIT[0:2]")]
    [InlineData("true | true; number='1 +'", "[1:2:1]EXIT[0:2]")]
    [InlineData("(exit 7) || number='1 +'", "[7:2:7]EXIT[0:2]")]
    [InlineData("false | true || number='1 +'", "[1:2:1 0]EXIT[0:2]")]
    public Task InvalidIntegerAssignmentPublishesStatusAtTheNextUnit(string unit, string expected)
    {
        var script = """
            declare -i number=2
            trap 'printf "EXIT[%s:%s]" "$?" "$number"' EXIT
            set -e
            """ + "\n" + unit + "\n" + """printf '[%s:%s:%s]' "$?" "$number" "${PIPESTATUS[*]}" """;
        return CheckIntegerFailureAsync(script, expected, 0, configure: null, check: null);
    }

    [Theory]
    [InlineData("eval 'number=\"1 +\"; printf inside'")]
    [InlineData("source ./fixture")]
    public Task InvalidIntegerAssignmentUnwindsNestedScriptCalls(string invocation) =>
        CheckIntegerFailureAsync("declare -i number=2\n" + invocation + "; printf skipped\n"
                + """printf '[%s:%s:%s]' "$?" "$number" "${PIPESTATUS[*]}" """,
            "[1:2:1]", 0, host => host.AddFile("/fixture", "number='1 +'\nprintf inside"), check: null);

    [Theory]
    [InlineData("""
        declare -i number=2
        value=$(printf prior; (exit 7); number='1 +'
        printf skipped)
        printf '[%s:%s:%s]' "$?" "$number" "$value"
        """, "[1:2:prior]", 0)]
    [InlineData("""
        declare -i number=2
        set -e
        trap 'number="1 +"; printf skipped' EXIT
        true
        """, "", 0)]
    [InlineData("""
        declare -i number=2
        set -e
        trap 'number="1 +"; printf skipped' EXIT
        exit 7
        """, "", 7)]
    public Task InvalidIntegerAssignmentStopsAtCaptureAndExitActionBoundaries(string script, string expected, int status) =>
        CheckIntegerFailureAsync(script, expected, status, configure: null, check: null);

    [Theory]
    [InlineData("-n", "", "<3>\n<0:3>\ndeclare -i dst=\"3\"\n", "")]
    [InlineData("-nr", "", "<3>\n<0:3>\ndeclare -i dst=\"3\"\n", "")]
    [InlineData("-n", "; readonly dst", "<2:0>\ndeclare -ir dst\n", "line 2: dst: readonly variable\n")]
    public async Task IntegerDefaultThroughNamerefsConvertsOnlyForAWritableReferent(
        string flags, string attributes, string expected, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            declare -i dst; declare -n hop=dst; declare {{flags}} ref=hop; i=0{{attributes}}
            printf '<%s>\n' "${ref:=i+=3}"
            printf '<%s:%s>\n' "$?" "$i"
            declare -p dst
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Fact]
    public Task InvalidIntegerAssignmentRestoresCompoundRedirection() =>
        CheckIntegerFailureAsync("""
            declare -i number=2
            { printf prior; number='1 +'; printf skipped; } >inner
            printf continued
            printf '[%s:%s:%s]' "$?" "$number" "${PIPESTATUS[*]}"
            """, "continued[0:2:0]", 0, configure: null,
            check: host => Assert.Equal("prior", host.ReadFile("/inner")));

    private static async Task CheckIntegerFailureAsync(string script, string expected, int status,
        Action<TestHost>? configure, Action<TestHost>? check)
    {
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            configure?.Invoke(host);
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(status, result.ExitCode);
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.NotEmpty(host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
            check?.Invoke(host);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
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

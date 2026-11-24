using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class CompoundArrayAssignmentTestsEndToEnd
{
    [Theory]
    [InlineData("a=", "old", "")]
    [InlineData("a+=", "first", "")]
    [InlineData("declare -A a=", "old", "skiplate")]
    [InlineData("declare -A a+=", "first", "skiplate")]
    public Task EmptyAssociativeKeysPreserveTheReachedInitializerPhase(string command, string first, string laterEffects)
    {
        const string items = """([one]=first ['']=$(printf skip >&2; printf rejected) [later]=$(printf late >&2; printf last))""";
        var script = """
            declare -A a; a=([one]=old)
            trap 'printf "<%s|%s|%s>" "$?" "${a[one]-}" "${a[later]-}"' EXIT
            set -e
            """ + "\n" + command + items + "\nprintf skipped\n";
        var item = laterEffects.Length == 0 ? "['']=$(printf skip 1>&2; printf rejected)" : "['']='rejected'";
        return AssertBothEntryPointsAsync(script, 1, $"<1|{first}|>", laterEffects + $"line 4: {item}: bad array subscript\n");
    }

    [Fact]
    public Task AssociativeEmptyPairSkipsItsValueAndExpandsTheNextPair()
    {
        const string script = """
            declare -A a; empty=
            a=("$empty" "$(printf forbidden >&2)" later "$(printf next >&2; printf value)")
            printf "<%s|%s>" "$?" "${a[later]-}"
            """;
        return AssertBothEntryPointsAsync(script, 0, "<0|value>", "line 2: \"$empty\": bad array subscript\nnext");
    }

    [Theory]
    [InlineData("a=", "oldtwo", "old")]
    [InlineData("a+=", "oldonetwo", "oldonetwo")]
    [InlineData("declare -A a+=", "oldonetwo", "old")]
    public Task AssociativeInitializerExpansionsAndElementAppendsSelectTheirReachedBinding(
        string command, string first, string later)
    {
        var script = "declare -A a; a=([one]=old)\n" + command + """
            ([one]+=one [one]+=two [later]="${a[one]-}")
            printf "<%s|%s>" "${a[one]-}" "${a[later]-}"
            """;
        return AssertBothEntryPointsAsync(script, 0, $"<{first}|{later}>", "");
    }

    [Theory]
    [InlineData("a", "5")]
    [InlineData("declare -Ai a", "4")]
    public Task AssociativeIntegerAssignmentsRunAtTheirInitializerPhase(string command, string later)
    {
        var script = "declare -Ai a; i=4\n" + command + """
            =([one]=i++ [later]="$i")
            printf "<%s|%s|%s>" "${a[one]-}" "${a[later]-}" "$i"
            """;
        return AssertBothEntryPointsAsync(script, 0, $"<4|{later}|5>", "");
    }

    [Theory]
    [InlineData("sel=-3; value='one two'\na=([1]=keep [$sel]+=\"$value\" next)",
        "next", "line 2: [-3]+=one two: bad array subscript\n")]
    [InlineData("a=(\n[-3]=$(printf 'rhs\\n' >&2; printf once)\n ok\n)",
        "ok", "rhs\nline 4: [-3]=once: bad array subscript\n")]
    [InlineData("declare -a a=(\n[-3]=plain\n ok\n)",
        "ok", "line 1: [-3]=plain: bad array subscript\n")]
    [InlineData("a=(\n[-3]=plain ok\n) \\\n2>log",
        "ok", "line 3: [-3]=plain: bad array subscript\n")]
    [InlineData("a=([4]=old)\na+=([-1]=new ok)", "ok", "")]
    public Task NegativeInitializerIndicesUseTheExpandedItemAndReachedLine(
        string body, string expectedLastValue, string expectedError)
    {
        var script = body + "\nprintf '<%s><%s>' \"$?\" \"${a[-1]}\"\n";
        return AssertBothEntryPointsAsync(script, 0, "<0><" + expectedLastValue + ">", expectedError);
    }

    [Theory]
    [InlineData("items=([0]=first [-3]=rejected later) && printf unused", 1)]
    [InlineData("f() { local -a items=([0]=first [-3]=rejected later); }; f && printf unused", 1)]
    [InlineData("(exit 7) || items=([0]=first [-3]=rejected later)", 7)]
    public Task NegativeInitializerIndicesUnderErrexitPreserveOnlyEarlierWrites(string body, int status)
    {
        var script = "trap 'printf \"<%s>[%s]\" \"$?\" \"${items[@]}\"' EXIT\nset -e\n"
            + body + "\nprintf skipped\n";
        return AssertBothEntryPointsAsync(script, status, $"<{status}>[first]", "line 3: [-3]=rejected: bad array subscript\n");
    }

    [Theory]
    [InlineData("(exit 7) || a=([@]=bad last) seen=\"$?:${PIPESTATUS[*]}\"", "<0|1:1|last>", "[@]=bad: cannot assign to non-numeric index")]
    [InlineData("a=([*]=bad \"$(printf last; exit 7)\") seen=\"$?:${PIPESTATUS[*]}\"", "<1|1:1|last>", "[*]=bad: cannot assign to non-numeric index")]
    [InlineData("a=([@]=bad last) b=$(printf ok; exit 7) seen=$?", "<7|7|last>", "[@]=bad: cannot assign to non-numeric index")]
    [InlineData("a=([]=$(printf bad; exit 7) last) seen=$?", "<7|7|last>", "[]=bad: bad array subscript")]
    public Task RejectedInitializerSelectorsUpdateStatusAtTheReachedItem(string body, string output, string diagnostic)
    {
        var script = body + "\nprintf '<%s|%s|%s>' \"$?\" \"$seen\" \"${a[-1]}\"\n";
        return AssertBothEntryPointsAsync(script, 0, output, "line 1: " + diagnostic + "\n");
    }

    [Theory]
    [InlineData("(exit 7) || a=([0]=first [@]=bad later)", 1, "[@]=bad: cannot assign to non-numeric index")]
    [InlineData("(exit 7) || a=([0]=first []=bad later)", 7, "[]=bad: bad array subscript")]
    [InlineData("declare -a a=([0]=first [*]=bad later) && printf unused", 1, "[*]=bad: cannot assign to non-numeric index")]
    public Task RejectedInitializerSelectorsStopLaterWritesUnderErrexit(string body, int status, string diagnostic)
    {
        var script = "trap 'printf \"<%s>[%s]\" \"$?\" \"${a[@]}\"' EXIT\nset -e\n"
            + body + "\nprintf skipped\n";
        return AssertBothEntryPointsAsync(script, status, $"<{status}>[first]", "line 3: " + diagnostic + "\n");
    }

    private const string RejectedQuotedKey = "[\"lead$({ cat <<DATA\nbody\nDATA\n}; printf extra)tail\"]";

    [Theory]
    [InlineData("printf 'unit\\n' >&2; values=({earlier} {item} tail) printf unreached", "after:1\n", "unit\nearly\n", "=text tail)")]
    [InlineData("false && values=({earlier} {item}) printf unreached", "after:1\n", "", "")]
    [InlineData("f() { values=({earlier} {item}) printf unreached; }\nprintf 'defined\\n'", "defined\nafter:0\n", "", "")]
    public async Task QuotedBracketDiagnosticPrecedesEveryCommandInTheUnit(
        string form, string output, string effects, string expandedSuffix)
    {
        const string earlier = "\"$(printf 'early\\n' >&2; printf item)\"";
        var body = form.Replace("{earlier}", earlier).Replace("{item}", RejectedQuotedKey + "=text");
        var error = CaptureRecord(9, "=text") + effects;
        if (expandedSuffix.Length != 0)
            error += CaptureRecord(9, expandedSuffix);

        await AssertReadDiagnosticAsync(body, 0, output, error);
    }

    [Theory]
    [InlineData("set -e; ", 0, "after:1\n")]
    [InlineData("set -e\n", 2, "")]
    public async Task QuotedBracketDiagnosticUsesOptionsAtTheStartOfTheUnit(string prefix, int status, string output)
    {
        var body = prefix + "false && values=(" + RejectedQuotedKey + "=text) printf unreached";
        var error = status == 2 ? "command substitution: line 10: syntax error near unexpected token `printf'\n"
            : CaptureRecord(9, "=text");

        await AssertReadDiagnosticAsync(body, status, output, error);
    }

    [Fact]
    public async Task DeclarationReaderReportsTheAssignmentHeadBeforeTheCompleteItem()
    {
        await AssertReadDiagnosticAsync("declare -a values=(" + RejectedQuotedKey + "=\"one\ntwo\")", 2, "",
            CaptureRecord(9, "=") + CaptureRecord(10, "=\"one")
            + "array assign: line 4: syntax error near unexpected token `printf'\n"
            + "array assign: line 4: ` } printf extra)tail\"]=\"one'\n");
    }

    [Fact]
    public async Task NestedBracketDiagnosticUsesTheEnclosingReaderLine()
    {
        await AssertReadDiagnosticAsync("value=\"$(values=(" + RejectedQuotedKey + "=text) printf unreached)\"", 2, "",
            CaptureRecord(9, "=text") + CaptureRecord(9, "=text) printf unreached)\""));
    }

    private static string CaptureRecord(int line, string suffix) =>
        $"command substitution: line {line}: syntax error near unexpected token `printf'\n"
        + $"command substitution: line {line}: ` }} printf extra)tail\"]{suffix}'\n";

    private static Task AssertReadDiagnosticAsync(string body, int expectedStatus, string output, string error) =>
        AssertBothEntryPointsAsync("printf 'before\\n'\n" + body + "\nprintf 'after:%s\\n' \"$?\"\n",
            expectedStatus, "before\n" + output, error);

    private static async Task AssertBothEntryPointsAsync(string script, int expectedStatus, string output, string error)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(expectedStatus, status);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("values=(\n{0}\n{1} \"one\ntwo\"\n)", 2, "before\n", "", "array assign: line 4: ", " } printf extra)tail\" \"one")]
    [InlineData("declare -A values=([first]={0} [next]={1})", 2, "before\n", "", "array assign: line 4: ", " } printf extra)tail\"")]
    [InlineData("values=({0} {1}) printf unreached", 0, "before\nafter:1\n", "earlier", "command substitution: line 9: ", " } printf extra)tail\")")]
    [InlineData("declare -a 'values=({1})'", 0, "before\nafter:1\n", "", "command substitution: line 6: ", " } printf extra)tail\"")]
    [InlineData("false && values=({0} {1})", 0, "before\nafter:1\n", "", "", "")]
    [InlineData("set -e\nvalues=({1})", 2, "before\n", "", "array assign: line 4: ", "")]
    public async Task RejectedCaptureUsesTheReachedInitializerReader(
        string form, int exitCode, string output, string earlierError, string diagnostic, string displayedLine)
    {
        const string earlier = "\"$(printf earlier >&2; printf item)\"";
        const string rejected = "\"$({ cat <<DATA\nbody\nDATA\n}; printf extra)tail\"";
        var script = "printf 'before\\n'\n" + form.Replace("{0}", earlier).Replace("{1}", rejected)
            + "\nprintf 'after:%s\\n' \"$?\"\n";
        var error = diagnostic.Length == 0 ? ""
            : earlierError + diagnostic + "syntax error near unexpected token `printf'\n";
        if (displayedLine.Length != 0)
            error += diagnostic + "`" + displayedLine + "'\n";

        await AssertBothEntryPointsAsync(script, exitCode, output, error);
    }

    [Fact]
    public async Task RejectedPrefixSelectorReportsItsParenthesizedOperand()
    {
        const string item = "\"${map[\"lead$({ cat <<DATA\nbody\nDATA\n}; printf extra)tail\"]:-fallback}\"";
        var result = await EvaluationTestHelper.RunAsync("printf 'before\\n'\nvalues=(" + item
            + ") printf unreached\nprintf 'after:%s\\n' \"$?\"\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before\nafter:1\n", result.StdOut);
        Assert.Equal("command substitution: line 9: syntax error near unexpected token `printf'\n"
            + "command substitution: line 9: ` } printf extra)tail\"]:-fallback}\")'\n"
            + "line 5: bad substitution: no closing `}' in (\"${map[\"lead$({ cat <<DATA\nbody\nDATA\n"
            + " } printf extra)tail\"]:-fallback}\")\n", result.StdErr);
    }

    // Bash 5.2.21: += preserves element-zero prefixing when an assignment becomes literal data.
    [Theory]
    [InlineData("a=(old); i='['; a+=([$i]=x tail); printf '<%s>' \"${a[@]}\"", "<old><old[[]=x><tail>")]
    [InlineData("a=(old); a+=([0]=new ['[']=x); printf '<%s>' \"${a[@]}\"", "<new><new[[]=x>")]
    [InlineData("a=(old); a+=(['[']=x $((a=2))); printf '<%s>' \"${a[@]}\"", "<2><2[[]=x><2>")]
    [InlineData("a=(old); a+=(['[']=x [0]=new ['[']=y); printf '<%s>' \"${a[@]}\"", "<new><new[[]=y>")]
    [InlineData("a=([5]=old); a+=(['[']=x); printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "<5><6><old><[[]=x>")]
    [InlineData("a=(old); a+=(['[']+=x); printf '<%s>' \"${a[@]}\"", "<old><old[[]+=x>")]
    [InlineData("a=(old); a+=( ['0]bad']=x ); printf '<%s>' \"${a[@]}\"", "<old><old[0]bad]=x>")]
    [InlineData("a=(old); i='['; a=([$i]=x); printf '<%s>' \"${a[@]}\"", "<[[]=x>")]
    [InlineData("a=(old); i='['; a+=(\"[$i]=x\"); printf '<%s>' \"${a[@]}\"", "<old><[[]=x>")]
    public async Task Evaluation_ReclassifiedAppendItemsReadElementZeroAtAssignmentTime(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Bash 5.2.21: staged selectors and nested aggregates retain their expansion context.
    [Theory]
    [InlineData("k=@; a=([$k]=bad tail); printf '<%s>' \"${a[@]}\"", "<tail>", "line 1: [@]=bad: cannot assign to non-numeric index\n", 0)]
    [InlineData("k='*'; a=(old); a+=([$k]=bad tail); printf '<%s>' \"${a[@]}\"", "<old><tail>", "line 1: [*]=bad: cannot assign to non-numeric index\n", 0)]
    [InlineData("a=([\"*\"]=bad tail); printf '<%s>' \"${a[@]}\"", "<tail>", "line 1: [*]=bad: cannot assign to non-numeric index\n", 0)]
    [InlineData("a=([$(printf @; printf selector >&2)]=$(printf bad; printf rhs >&2) tail); printf '<%s>' \"${a[@]}\"", "<tail>", "selectorrhsline 1: [@]=bad: cannot assign to non-numeric index\n", 0)]
    [InlineData("declare -A a; k=@; a=([$k]=at [\"*\"]=star); printf '<%s>' \"${a['@']}\" \"${a['*']}\"", "<at><star>", "", 0)]
    [InlineData("k=@; a=(['$k']=bad tail); printf after", "", "line 1: @: syntax error: operand expected (error token is \"@\")\n", 1)]
    [InlineData("k=@; a[$k]=bad; printf after", "", "line 1: @: syntax error: operand expected (error token is \"@\")\n", 1)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${missing:-${src[@]}}); printf '<%s>' \"${a[0]}\"", "<x y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${yes:+$@}); printf '<%s>' \"${a[0]}\"", "<x y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=\"${missing:-${src[@]}}\"); printf '<%s>' \"${a[0]}\"", "<x:y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${missing:-\"${src[@]}\"}); printf '<%s>' \"${a[0]}\"", "<x:y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${missing:-${other:-\"${src[@]}\"}}); printf '<%s>' \"${a[0]}\"", "<x:y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=\"${missing:-${other:-${src[@]}}}\"); printf '<%s>' \"${a[0]}\"", "<x:y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${missing:-\"${src[@]}\"$((IFS=2))${src[@]}}); printf '<%s>' \"${a[0]}\"", "<x:y2x y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${missing:-${src[@]#x}}); printf '<%s>' \"${a[0]}\"", "< y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${missing:-${src[@]:0:2}}); printf '<%s>' \"${a[0]}\"", "<x y>", "", 0)]
    [InlineData("IFS=:; yes=1; set -- x y; src=(x y); a=([0]=${missing:-${!src[@]}}); printf '<%s>' \"${a[0]}\"", "<0 1>", "", 0)]
    public async Task Evaluation_CompoundSelectorsAndNestedOperandsUseExpandedContext(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("a=([\"$empty\"]=x tail); printf '<%s>' \"${a[@]}\"", "<tail>", "line 1: []=x: bad array subscript\n")]
    [InlineData("a=(['']=x tail); printf '<%s>' \"${a[@]}\"", "<tail>", "line 1: []=x: bad array subscript\n")]
    [InlineData("a=([$(true)]=x tail); printf '<%s>' \"${a[@]}\"", "<tail>", "line 1: []=x: bad array subscript\n")]
    [InlineData("a=(old); a+=([\"$empty\"]=x tail); printf '<%s>' \"${a[@]}\"", "<old><tail>", "line 1: []=x: bad array subscript\n")]
    [InlineData("declare -A a; a=([bad] [ok]=yes); printf '<%s>' \"${a[ok]}\" \"${a['[bad]']}\"", "<yes><>", "a: must use subscript when assigning associative array\n")]
    [InlineData("declare -A a; a=([bad] $(printf forbidden >&2) [ok]=yes); printf '<%s>' \"${a[ok]}\"", "<yes>", "a: must use subscript when assigning associative array\na: must use subscript when assigning associative array\n")]
    [InlineData("declare -A a; a=('[bad]' value [ok]=yes tail); printf '<%s>' \"${a['[bad]']}\" \"${a['[ok]=yes']}\"", "<value><tail>", "")]
    [InlineData("a=(['[']=x tail); printf '<%s>' \"${a[@]}\"", "<[[]=x><tail>", "")]
    [InlineData("i='['; v='u v'; a=([$i]=$v tail); printf '<%s>' \"${a[@]}\"", "<[[]=u v><tail>", "")]
    [InlineData("i='0]=z'; a=([$i]=x tail); printf '<%s>' \"${a[@]}\"", "<z]=x><tail>", "")]
    [InlineData("i='0]+=z'; a=(old); a+=([$i]=x tail); printf '<%s>' \"${a[@]}\"", "<oldz]=x><tail>", "")]
    [InlineData("i=\"'\"; a=([$i]=x tail); printf '<%s>' \"${a[@]}\"", "<[']=x><tail>", "")]
    [InlineData("HOME=house; declare -A a; a=([x]=~ [y]=~/file); printf '<%s>' \"${a[x]}\" \"${a[y]}\"", "<~><~/file>", "")]
    [InlineData("HOME=house; declare -A a; a=(~ ~/file); printf '<%s>' \"${a['~']}\" \"${a[house]}\"", "<~/file><>", "")]
    [InlineData("HOME=house; declare -A a; a=([x]=old); a+=([x]+=~); printf '<%s>' \"${a[x]}\"", "<old~>", "")]
    [InlineData("IFS=:; set -- x y; a=([0]=\"$@\"); printf '<%s>' \"${a[@]}\"", "<x:y>", "")]
    [InlineData("IFS=:; set -- x y; a=(old); a+=([0]+=\"$@\"); printf '<%s>' \"${a[@]}\"", "<oldx:y>", "")]
    [InlineData("IFS=; set -- x y; a=([0]=\"$@\" [1]=\"$*\"); printf '<%s>' \"${a[@]}\"", "<x y><xy>", "")]
    [InlineData("IFS=é; source=(x y); a=([0]=\"${source[@]}\"); printf '<%s>' \"${a[0]}\"", "<xéy>", "")]
    [InlineData("IFS=:; set -- x y; a=([0]=\"$@\"$((IFS=2))\"$@\"); printf '<%s>' \"${a[@]}\"", "<x:y2x2y>", "")]
    [InlineData("IFS=:; set -- x y; scalar=\"$@\"; a=(\"$@\" [4]=\"$@\"); printf '<%s>' \"$scalar\" \"${a[@]}\"", "<x y><x><y><x:y>", "")]
    [InlineData("IFS=:; set -- x y; declare -A a; a=([x]=\"$@\"); printf '<%s>' \"${a[x]}\"", "<x y>", "")]
    public async Task Evaluation_CompoundItemsRetainTheirExpansionContext(
        string script, string expectedOutput, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("value='a b'; values=([[ value ]]); printf '<%s>\\n' \"${values[@]}\"", "<[[ value ]]>\n")]
    [InlineData("value='a b'; values=([one two]); printf '<%s>\\n' \"${values[@]}\"", "<[one two]>\n")]
    [InlineData("value='a b'; values=([one two]tail); printf '<%s>\\n' \"${values[@]}\"", "<[one two]tail>\n")]
    [InlineData("value='a b'; values=(before [one two] after); printf '<%s>\\n' \"${values[@]}\"", "<before>\n<[one two]>\n<after>\n")]
    [InlineData("value='a b'; values=(foo[one two]); printf '<%s>\\n' \"${values[@]}\"", "<foo[one>\n<two]>\n")]
    [InlineData("value='a b'; values=([ one  two ]); printf '<%s>\\n' \"${values[@]}\"", "<[ one  two ]>\n")]
    [InlineData("value='a b'; values=('[one' two]); printf '<%s>\\n' \"${values[@]}\"", "<[one>\n<two]>\n")]
    [InlineData("value='a b'; values=(\\[one two]); printf '<%s>\\n' \"${values[@]}\"", "<[one>\n<two]>\n")]
    [InlineData("value='a b'; values=([one 'two three']); printf '<%s>\\n' \"${values[@]}\"", "<[one two three]>\n")]
    [InlineData("value='a b'; values=([one \"two three\"]); printf '<%s>\\n' \"${values[@]}\"", "<[one two three]>\n")]
    [InlineData("value='a b'; values=([one [two three] four]); printf '<%s>\\n' \"${values[@]}\"", "<[one [two three] four]>\n")]
    [InlineData("value='a b'; values=([one ]]); printf '<%s>\\n' \"${values[@]}\"", "<[one ]]>\n")]
    [InlineData("value='a b'; values=([$value]); printf '<%s>\\n' \"${values[@]}\"", "<[a>\n<b]>\n")]
    [InlineData("value='a b'; values=([pre${value}post]); printf '<%s>\\n' \"${values[@]}\"", "<[prea>\n<bpost]>\n")]
    [InlineData("value='a b'; values=([\"$value\"]); printf '<%s>\\n' \"${values[@]}\"", "<[a b]>\n")]
    [InlineData("value='a b'; values=([$(printf 'a b')]); printf '<%s>\\n' \"${values[@]}\"", "<[a>\n<b]>\n")]
    [InlineData("value='a b'; values=([${missing:-a b}]); printf '<%s>\\n' \"${values[@]}\"", "<[a>\n<b]>\n")]
    [InlineData("value='a b'; values=([[ a = b ]]); printf '<%s>\\n' \"${values[@]}\"", "<[[ a = b ]]>\n")]
    [InlineData("value='a b'; values=([[ a && b ]]); printf '<%s>\\n' \"${values[@]}\"", "<[[ a && b ]]>\n")]
    [InlineData("value='a b'; values=([a;b]); printf '<%s>\\n' \"${values[@]}\"", "<[a;b]>\n")]
    [InlineData("value='a b'; values=([a|b]); printf '<%s>\\n' \"${values[@]}\"", "<[a|b]>\n")]
    [InlineData("value='a b'; values=([a\nb]); printf '<%s>\\n' \"${values[@]}\"", "<[a\nb]>\n")]
    [InlineData("value='a b'; values=([a\\\nb]); printf '<%s>\\n' \"${values[@]}\"", "<[ab]>\n")]
    [InlineData("value='a b'; values=([a\\ b]); printf '<%s>\\n' \"${values[@]}\"", "<[a b]>\n")]
    [InlineData("value='a b'; values=([\"a]b\"]); printf '<%s>\\n' \"${values[@]}\"", "<[a]b]>\n")]
    [InlineData("value='a b'; values=([a\\]b]); printf '<%s>\\n' \"${values[@]}\"", "<[a]b]>\n")]
    [InlineData("value='a b'; values=([]); printf '<%s>\\n' \"${values[@]}\"", "<[]>\n")]
    [InlineData("value='a b'; values=([ ]); printf '<%s>\\n' \"${values[@]}\"", "<[ ]>\n")]
    [InlineData("value='a b'; values=([a]suffix); printf '<%s>\\n' \"${values[@]}\"", "<[a]suffix>\n")]
    [InlineData("value='a b'; values=([a]suffix more); printf '<%s>\\n' \"${values[@]}\"", "<[a]suffix>\n<more>\n")]
    [InlineData("value='a b'; values=([a]suffix[one two]); printf '<%s>\\n' \"${values[@]}\"", "<[a]suffix[one>\n<two]>\n")]
    [InlineData("value='a b'; values=([a]suffix'one two'); printf '<%s>\\n' \"${values[@]}\"", "<[a]suffixone two>\n")]
    [InlineData("value='a b'; values=([a][one two]); printf '<%s>\\n' \"${values[@]}\"", "<[a][one>\n<two]>\n")]
    [InlineData("value='a b'; values=([a]\"$value\"); printf '<%s>\\n' \"${values[@]}\"", "<[a]a b>\n")]
    [InlineData("value='a b'; values=([2]=two); printf '<%s>\\n' \"${values[@]}\"", "<two>\n")]
    [InlineData("value='a b'; values=([2]=two tail); printf '<%s>\\n' \"${values[@]}\"", "<two>\n<tail>\n")]
    [InlineData("value='a b'; values=([1 + 2]=three tail); printf '<%s>\\n' \"${values[@]}\"", "<three>\n<tail>\n")]
    [InlineData("value='a b'; values=([a b]+); printf '<%s>\\n' \"${values[@]}\"", "<[a b]+>\n")]
    [InlineData("value='a b'; values=([a > b]); printf '<%s>\\n' \"${values[@]}\"", "<[a > b]>\n")]
    [InlineData("value='a b'; values=([a (b) c]); printf '<%s>\\n' \"${values[@]}\"", "<[a (b) c]>\n")]
    [InlineData("value='a b'; values=([a # b]); printf '<%s>\\n' \"${values[@]}\"", "<[a # b]>\n")]
    [InlineData("value='a b'; values=([a\\\n b]); printf '<%s>\\n' \"${values[@]}\"", "<[a b]>\n")]
    [InlineData("value='a b'; values=([a \"$(printf ']')\" b]); printf '<%s>\\n' \"${values[@]}\"", "<[a ] b]>\n")]
    [InlineData("value='a b'; values=([a ${missing:-]} b]); printf '<%s>\\n' \"${values[@]}\"", "<[a ] b]>\n")]
    [InlineData("value='a b'; values=([a $(printf ']') b]); printf '<%s>\\n' \"${values[@]}\"", "<[a ] b]>\n")]
    [InlineData("value='a b'; values=(\"[\"one two]); printf '<%s>\\n' \"${values[@]}\"", "<[one>\n<two]>\n")]
    [InlineData("value='a b'; values=($'['one two]); printf '<%s>\\n' \"${values[@]}\"", "<[one>\n<two]>\n")]
    [InlineData("value='a b'; values=([a][b c]tail); printf '<%s>\\n' \"${values[@]}\"", "<[a][b>\n<c]tail>\n")]
    [InlineData("values=([one two]); values+=([three four]); printf '<%s>\\n' \"${values[@]}\"", "<[one two]>\n<[three four]>\n")]
    public async Task Evaluation_CompoundBracketWordsPreserveGroupingAndExpansion(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CompoundBracketWordPreservesCarriageReturnData()
    {
        var result = await EvaluationTestHelper.RunAsync("values=([a\rb]); printf '<%s>' \"${values[0]}\"");

        Assert.Equal("<[a\rb]>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UntypedCompoundAssignment_UsesIndexedArithmeticSubscripts()
    {
        var script = @"index=2; arr=([index]=value); printf '<%s>:<%s>\n' ""${arr[2]-missing}"" ""${arr[index]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<value>:<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("arr=([1/0]=bad)")]
    public async Task Evaluation_IndexedAssignment_RejectsInvalidArithmeticSubscript(string assignment)
    {
        var script = $@"arr=(keep); {assignment}; status=$?; printf '%s:<%s>:%s\n' ""$status"" ""${{arr[0]}}"" ""${{#arr[@]}}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: 1/0: division by 0 (error token is \"0\")\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CompoundArrayAssignment_ClearsSparseElements()
    {
        var script = @"arr[2]=two; arr=(); printf '<%s>\n' ""${arr[2]-unset}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<unset>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CompoundArrayAssignment_ExpandsAgainstPreviousValue()
    {
        var script = @"arr=(old); arr=(""${arr[0]}"" new); printf '<%s><%s>\n' ""${arr[0]}"" ""${arr[1]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<old><new>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignments_PreserveArrayAggregateFieldsByContext()
    {
        var script = @"source=(a b); copy=(""${source[@]}""); scalar=""${source[@]}""; empty=(); cleared=(""${empty[@]}""); printf '%s:<%s>:<%s>:<%s>:%s\n' ""${#copy[@]}"" ""${copy[0]}"" ""${copy[1]}"" ""$scalar"" ""${#cleared[@]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("2:<a>:<b>:<a b>:0\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CompoundArrayAssignment_ExpansionFailurePreservesPreviousValue()
    {
        var script = @"arr=(old); arr=(new ""${missing:?boom}""); status=$?; printf '%s:<%s>\n' ""$status"" ""${arr[0]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("missing: boom\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CompoundArrayAssignment_PreservesMixedItemsInSourceOrder()
    {
        var script = @"arr=(new [2]=two tail); printf '<%s><%s><%s><%s>\n' ""${arr[0]-unset}"" ""${arr[1]-unset}"" ""${arr[2]-unset}"" ""${arr[3]-unset}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<new><unset><two><tail>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CompoundArrayAssignment_SupportsIndicesPastInt32Maximum()
    {
        var script = @"arr=(old); arr=([2147483647]=far tail); printf '%s:<%s>:<%s>:<%s>\n' ""$?"" ""${arr[0]-unset}"" ""${arr[2147483647]}"" ""${arr[2147483648]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0:<unset>:<far>:<tail>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssociativeCompoundExpandsExplicitKeysAgainstPreviousState()
    {
        var script = @"declare -A map; map=([a]=old); map=([a]=${map[a]} [b]=""new value""); printf '<%s><%s>\n' ""${map[a]}"" ""${map[b]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<old><new value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.37 output/status; diagnostics use the engine's subject/message format.
    [Theory]
    [InlineData("arr=([1 + 2]=\"a b\" tail); printf '<%s>\\n' \"${arr[@]}\" \"${!arr[@]}\"", "<a b>\n<tail>\n<3>\n<4>\n", "", 0)]
    [InlineData("declare -A map; map[a]=old; map[b]=prev; map=([a]=new [b]=\"${map[a]}\" [c]=\"${map[b]}\"); printf '<%s>\\n' \"${map[a]}\" \"${map[b]}\" \"${map[c]}\"", "<new>\n<old>\n<prev>\n", "", 0)]
    [InlineData("i=0; arr=([i++]=$((i+=10)) [$((i++))]=$((i+=100))); printf '<%s>\\n' \"$i\" \"${!arr[@]}\" \"${arr[@]}\"", "<112>\n<10>\n<111>\n<111>\n<10>\n", "", 0)]
    [InlineData("i=0; declare -A map; map=([$((i++))]=$((i+=10)) [$((i++))]=$((i+=100))); printf '<%s>:<%s>:<%s>:<%s>:<%s>\\n' \"$i\" \"${map[0]-x}\" \"${map[1]-x}\" \"${map[11]-x}\" \"${map[12]-x}\"", "<112>:<11>:<x>:<112>:<x>\n", "", 0)]
    [InlineData("arr=([$(printf 2; printf s1 >&2)]=$(printf v1; printf r1 >&2) [$(printf 4; printf s2 >&2)]=$(printf v2; printf r2 >&2)); printf '<%s>\\n' \"${arr[@]}\"", "<v1>\n<v2>\n", "s1r1s2r2", 0)]
    [InlineData("declare -A map; map=([$(printf a; printf s1 >&2)]=$(printf v1; printf r1 >&2) [$(printf b; printf s2 >&2)]=$(printf v2; printf r2 >&2)); printf '<%s>:<%s>\\n' \"${map[a]}\" \"${map[b]}\"", "<v1>:<v2>\n", "s1r1s2r2", 0)]
    [InlineData("arr=(old); readonly arr; arr=([$(printf 2; printf selector >&2)]=$(printf new; printf rhs >&2)); printf after", "", "line 1: arr: readonly variable\n", 1)]
    [InlineData("declare -A map; readonly map; map=([$(printf a; printf selector >&2)]=$(printf new; printf rhs >&2)); printf after", "", "line 1: map: readonly variable\n", 1)]
    [InlineData("arr=(old); arr=(new [-2]=bad tail); printf '<%s>\\n' \"$?\" \"${arr[@]}\" \"${!arr[@]}\"", "<0>\n<new>\n<tail>\n<0>\n<1>\n", "line 1: [-2]=bad: bad array subscript\n", 0)]
    [InlineData("arr[5]=old; arr=([-1]=new tail); printf '<%s>\\n' \"$?\" \"${arr[@]}\" \"${!arr[@]}\"", "<0>\n<tail>\n<0>\n", "line 1: [-1]=new: bad array subscript\n", 0)]
    [InlineData("arr=(old); arr=(new []=bad tail); printf '<%s>\\n' \"$?\" \"${arr[@]}\" \"${!arr[@]}\"", "<0>\n<new>\n<tail>\n<0>\n<1>\n", "line 1: []=bad: bad array subscript\n", 0)]
    [InlineData("declare -A map; map[old]=old; map=([a]=new [\"\"]=bad [b]=tail); printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${map[a]}\" \"${map[b]}\"", "<0>:<new>:<tail>\n", "line 1: [\"\"]=bad: bad array subscript\n", 0)]
    [InlineData("declare -A map; map=([a]=one plain [b]=two); printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${map[a]}\" \"${map[b]}\"", "<0>:<one>:<two>\n", "map: must use subscript when assigning associative array\n", 0)]
    [InlineData("declare -A map; map=(foo bar [x]=y); printf '<%s>:<%s>:<%s>\\n' \"${map[foo]}\" \"${map['[x]=y']-missing}\" \"${#map[@]}\"", "<bar>:<>:<2>\n", "", 0)]
    [InlineData("i=0; declare -A map; map=($((i++)) $((i+=10)) $((i++)) $((i+=100))); printf '<%s>:<%s>:<%s>\\n' \"$i\" \"${map[0]}\" \"${map[11]}\"", "<112>:<11>:<112>\n", "", 0)]
    [InlineData("arr[5]=old; arr+=(one [2]=two tail); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<2>\n<3>\n<5>\n<6>\n<two>\n<tail>\n<old>\n<one>\n", "", 0)]
    [InlineData("arr=old; arr+=(new); printf '<%s>\\n' \"${arr[@]}\"", "<old>\n<new>\n", "", 0)]
    [InlineData("declare -A map; map[a]=old; map[b]=keep; map+=([a]=new [c]=tail); printf '<%s>:<%s>:<%s>\\n' \"${map[a]}\" \"${map[b]}\" \"${map[c]}\"", "<new>:<keep>:<tail>\n", "", 0)]
    [InlineData("arr=(old); declare -n ref=arr; ref+=(new); printf '<%s>\\n' \"${arr[@]}\"", "<old>\n<new>\n", "", 0)]
    [InlineData("arr=(old); arr+=([0]+=new tail); printf '<%s>\\n' \"${arr[@]}\"", "<oldnew>\n<tail>\n", "", 0)]
    [InlineData("arr=(old); arr=([0]+=new tail); printf '<%s>\\n' \"${arr[@]}\"", "<new>\n<tail>\n", "", 0)]
    [InlineData("declare -A map; map[a]=old; map+=([a]+=new); printf '<%s>\\n' \"${map[a]}\"", "<oldnew>\n", "", 0)]
    [InlineData("arr=([5]=five [-1]=last tail); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<5>\n<6>\n<last>\n<tail>\n", "", 0)]
    [InlineData("set -- a b; arr=([2]=\"$@\" tail); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<2>\n<3>\n<a b>\n<tail>\n", "", 0)]
    [InlineData("v='a b'; arr=($v [4]=$v $v); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<0>\n<1>\n<4>\n<5>\n<6>\n<a>\n<b>\n<a b>\n<a>\n<b>\n", "", 0)]
    [InlineData("arr=({a,b} [4]={c,d}); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<0>\n<1>\n<2>\n<3>\n<a>\n<b>\n<[4]=c>\n<[4]=d>\n", "", 0)]
    [InlineData("arr[0]=(one two); printf after", "", "line 1: arr[0]: cannot assign list to array member\n", 1)]
    [InlineData("arr=(keep)\narr[2]=(); printf skipped\nprintf '<%s><%s>' \"$?\" \"${arr[0]}\"", "<1><keep>", "line 2: arr[2]: cannot assign list to array member\n", 0)]
    [InlineData("arr=(keep)\narr[2]+=( ); printf skipped\nprintf '<%s><%s>' \"$?\" \"${arr[0]}\"", "<1><keep>", "line 2: arr[2]: cannot assign list to array member\n", 0)]
    [InlineData("arr[$(printf 0; printf selector >&2)]=($(printf a; printf rhs >&2)); printf after", "", "line 1: arr[$(printf 0; printf selector >&2)]: cannot assign list to array member\n", 1)]
    [InlineData("arr[2]=()\"tail words\"; printf '<%s>' \"${arr[2]}\"", "<()tail words>", "", 0)]
    [InlineData("arr[0]=(a \"b c\")d; printf '<%s>\\n' \"${arr[0]}\"", "<(a b c)d>\n", "", 0)]
    [InlineData("arr=('[2]=value' \"[3]=quoted\"); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<0>\n<1>\n<[2]=value>\n<[3]=quoted>\n", "", 0)]
    [InlineData("x=wrong; declare -A map; map=(['$x']='a b' [\"a=b]\"]=c); printf '<%s>:<%s>\\n' \"${map['$x']}\" \"${map['a=b]']}\"", "<a b>:<c>\n", "", 0)]
    [InlineData("HOME=/home/test; declare -A map; map=([~]=unquoted [\"~\"]=quoted [{a,b}]=brace); printf '<%s>:<%s>:<%s>\\n' \"${map[/home/test]}\" \"${map['~']}\" \"${map['{a,b}']}\"", "<>:<quoted>:<brace>\n", "", 0)]
    [InlineData("empty=(); arr=(a \"${empty[@]}\" b \"\" c); printf '<%s>\\n' \"${arr[@]}\"", "<a>\n<b>\n<>\n<c>\n", "", 0)]
    [InlineData("arr=(old); arr=(\"${arr[0]:=fallback}\" \"${arr[2]:=effect}\"); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<0>\n<1>\n<old>\n<effect>\n", "", 0)]
    [InlineData("arr=([1/0]=$(printf a; printf first >&2) [$(printf 2; printf selector >&2)]=$(printf b; printf second >&2)); printf after", "", "firstselectorsecondline 1: 1/0: division by 0 (error token is \"0\")\n", 1)]
    [InlineData("arr=([$(printf 2; printf selector >&2)]=$(printf a; printf first >&2) \"${missing:?boom}\" $(printf b; printf second >&2)); printf after", "", "selectorfirstmissing: boom\n", 1)]
    [InlineData("arr=(new [-2]=$(printf bad; printf rhs >&2) $(printf tail; printf later >&2)); printf '<%s>\\n' \"${arr[@]}\"", "<new>\n<tail>\n", "rhslaterline 1: [-2]=bad: bad array subscript\n", 0)]
    [InlineData("declare -A map; map=([a]=one $(printf plain; printf rhs >&2) [b]=two); printf '<%s>:<%s>\\n' \"${map[a]}\" \"${map[b]}\"", "<one>:<two>\n", "map: must use subscript when assigning associative array\n", 0)]
    [InlineData("v='a b'; declare -A map; map=($v value next); printf '<%s>:<%s>:<%s>\\n' \"${map['a b']-x}\" \"${map[a]-x}\" \"${map[next]-x}\"", "<value>:<x>:<>\n", "", 0)]
    [InlineData("declare -A map; map=(a one '' bad b two); printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${map[a]}\" \"${map[b]}\"", "<0>:<one>:<two>\n", "line 1: '': bad array subscript\n", 0)]
    [InlineData("x='a b'; declare -A map; map=(foo bar [\"$x\"]=y value); printf '<%s>:<%s>\\n' \"${map['[a b]=y']-x}\" \"${map[foo]}\"", "<value>:<bar>\n", "", 0)]
    [InlineData("declare -A map; map=([a]={x,y}); printf '<%s>\\n' \"${map[a]}\"", "<{x,y}>\n", "", 0)]
    [InlineData("v=old; v+=new; arr=(zero one); arr[1]+=tail; declare -A map; map[a]=old; map[a]+=new; printf '<%s>:<%s>:<%s>\\n' \"$v\" \"${arr[1]}\" \"${map[a]}\"", "<oldnew>:<onetail>:<oldnew>\n", "", 0)]
    [InlineData("i=0; arr=([i++]=first [i++]=second); printf '<%s>\\n' \"$i\" \"${!arr[@]}\" \"${arr[@]}\"", "<2>\n<0>\n<1>\n<first>\n<second>\n", "", 0)]
    [InlineData("arr=([1 \\\n+ 2]\\\n=three tail); printf '<%s>\\n' \"${arr[@]}\" \"${!arr[@]}\"", "<three>\n<tail>\n<3>\n<4>\n", "", 0)]
    [InlineData("x={a,b}; printf '<%s>\\n' \"$x\"", "<{a,b}>\n", "", 0)]
    [InlineData("arr=(old); arr+=(); printf '<%s>\\n' \"${arr[@]}\"", "<old>\n", "", 0)]
    [InlineData("arr=([3]={2..2}); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<0>\n<[3]=2>\n", "", 0)]
    [InlineData("declare -A map; empty=(); map=(\"${empty[@]}\" value [a]=tail); printf '<%s>:<%s>\\n' \"$?\" \"${map[value]-missing}\"", "<0>:<missing>\n", "line 1: \"${empty[@]}\": bad array subscript\n", 0)]
    [InlineData("v=old; f() { printf '<%s>\\n' \"$v\"; }; v+=new f; printf '<%s>\\n' \"$v\"", "<oldnew>\n<old>\n", "", 0)]
    [InlineData("arr=(old tail); f() { printf '<%s>\\n' \"${arr[@]}\"; }; arr+=(new) f; printf '<%s>\\n' \"${arr[@]}\"", "<old(new)>\n<old>\n<tail>\n", "", 0)]
    [InlineData("i=1; f() { printf '<%s>\\n' \"$i\"; }; i+=$((i+=2)) f; printf '<%s>\\n' \"$i\"", "<33>\n<3>\n", "", 0)]
    [InlineData("v=; f() { printf '<%s>\\n' \"$v\"; }; v+=${v:=new} f; printf '<%s>\\n' \"$v\"", "<newnew>\n<new>\n", "", 0)]
    [InlineData("v=old; f() { printf '<%s>\\n' \"$v\"; }; v+=$(printf new; printf rhs >&2) f; printf '<%s>\\n' \"$v\"", "<oldnew>\n<old>\n", "rhs", 0)]
    [InlineData("v=old; f() { printf '<%s>\\n' \"$v\"; }; v+=a v+=b f; printf '<%s>\\n' \"$v\"", "<oldab>\n<old>\n", "", 0)]
    [InlineData("arr=(zero one); f() { printf '<%s>\\n' \"${arr[@]}\"; }; arr+=new f; printf '<%s>\\n' \"${arr[@]}\"", "<zeronew>\n<zero>\n<one>\n", "", 0)]
    [InlineData("arr=($(false)); printf '<%s>\\n' \"$?\" \"${#arr[@]}\"", "<1>\n<0>\n", "", 0)]
    [InlineData("a=(['1']=one [\"2\"]=two); printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "<1><2><one><two>", "", 0)]
    [InlineData("x=1; a=(['$x']=ok); printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "<1><ok>", "", 0)]
    [InlineData("HOME=house; declare -A a; a=([~]=value ['$x']=literal); printf '<%s>' \"${a['~']}\" \"${a['$x']}\"", "<value><literal>", "", 0)]
    [InlineData("i=0; a=(zero one); a[i++]+=new; printf '<%s>' \"$i\" \"${a[@]}\"", "<1><zeronew><one>", "", 0)]
    [InlineData("i=0; a=(old); f(){ printf '<%s>' \"${a[@]}\"; }; a[i++]+=$((i+=2)) f; printf '<%s>' \"$i\" \"${a[@]}\"", "<old><0><old>", "a[i++]: not a valid identifier\n", 0)]
    [InlineData("a=(old tail); f(){ printf '<%s>' \"${a[@]}\"; }; a[1]=$(printf forbidden >&2;printf new) f; printf '<%s>' \"$?\" \"${a[@]}\"", "<old><tail><0><old><tail>", "a[1]: not a valid identifier\n", 0)]
    [InlineData("a=(old tail); a[1]+=new false; printf '<%s>' \"$?\" \"${a[@]}\"", "<1><old><tail>", "a[1]: not a valid identifier\n", 0)]
    [InlineData("x='$(printf side >&2)1'; a=([$x]=ok); printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "<1><ok>", "side", 0)]
    [InlineData("i=0; a=(['$((i++))']=ok [$((i++))]=other); printf '<%s>' \"$i\" \"${a[@]}\" \"${!a[@]}\"", "<2><other><ok><0><1>", "", 0)]
    public async Task Evaluation_CompoundArrayAssignment_PreservesStagingAndItemForms(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("arr=(old); arr=(first [1/0]=bad)", new[] { "first" }, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("arr=(old); arr=(first \"${arr[2]:=effect}\" \"${missing:?boom}\")", new[] { "old", "", "effect" }, "missing: boom\n")]
    public async Task Evaluation_CompoundArrayFailure_RetainsEffectsOfItsCompletedStage(
        string script, string[] expectedArray, string expectedError)
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Lokad.Bash.Syntax.Parser.Parse(script));
        var host = EndToEndHarness.WithMemoryBudget(EvaluationTestHelper.BuildDefaultTree(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(1, evaluation.LastExitCode);
        Assert.Equal(expectedArray.Select(ShellValue.FromText), evaluation.IndexedArrays["arr"]);
        Assert.Equal(expectedError, System.Text.Encoding.UTF8.GetString(host.StandardError.Content.Span));
    }

    [Fact]
    public void Parse_CompoundArrayAssignment_RejectsSemicolonBetweenItems()
    {
        Assert.Throws<Lokad.Bash.Syntax.SyntaxException>(() => Lokad.Bash.Syntax.Parser.Parse("arr=(one; two)"));
    }

    // The sparse storage contract excludes Bash's signed-overflow positions.
    [Fact]
    public async Task Evaluation_CompoundArrayAssignment_RejectsSequentialIndexOverflow()
    {
        var result = await EvaluationTestHelper.RunAsync("arr=([9223372036854775807]=last next); printf after");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("arr: array index overflow\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

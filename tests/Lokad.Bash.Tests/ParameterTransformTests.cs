using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ParameterTransformTests
{
    [Theory]
    [InlineData("""x=; declare y; export z; show "${x@A}" "${y@A}" "${z@A}" "${missing@A}" """,
        "<4>[x=''][][declare -x z][]\n")]
    [InlineData("""a=([4]='two words'); readonly a; show "${a@a}" "${a[9]@a}" "${a[@]@a}"; show "${a[4]@A}" "${a[9]@A}" """,
        "<3>[ar][ar][ar]\n<2>[declare -ar a='two words'][declare -ar a]\n")]
    [InlineData("""a=('two words'); show "pre${a[@]@A}post"; IFS=:; show "${a[@]@A}"; show "${a[*]@A}" """,
        "<3>[predeclare][-a][a=([0]=\"two words\")post]\n<1>[declare -a a=([0]=\"two words\")]\n<1>[declare -a a=([0]=\"two words\")]\n")]
    [InlineData("""declare -a a; show "${a[@]@a}"; show "${a[@]@A}"; a=(); show "${a[@]@a}"; show "${a[@]@A}"; show "${a[@]@K}" """,
        "<1>[a]\n<3>[declare][-a][a]\n<0>\n<3>[declare][-a][a=()]\n<0>\n")]
    [InlineData("""a=([4]='two words' [12]=''); d="${a[*]@A}"; unset a; eval "$d"; show "${a[4]}" "${a[12]+present}" "${!a[@]}" """,
        "<4>[two words][present][4][12]\n")]
    [InlineData("""a=([4]='two words' [9]=''); show "${a[@]@K}"; show "pre${a[@]@k}post"; show pre${a[@]@k}post""",
        "<1>[4 \"two words\" 9 \"\"]\n<4>[pre4][two words][9][post]\n<5>[pre4][two][words][9][post]\n")]
    [InlineData("""declare -A m=(['b key']='two words' ['a key']=''); show "${m[@]@K}"; show "${m[@]@k}"; show "${m[*]@A}" """,
        "<1>[\"a key\" \"\" \"b key\" \"two words\" ]\n<4>[a key][][b key][two words]\n<1>[declare -A m=([\"a key\"]=\"\" [\"b key\"]=\"two words\" )]\n")]
    [InlineData("""a=([4]='two words' [9]=''); IFS=:; x="${a[@]@k}"; show "$x"; [[ ${a[@]@k} == '4 two words 9 ' ]] && show match""",
        "<1>[4:two words:9:]\n<1>[match]\n")]
    [InlineData("""a=(one two ''); IFS=:; x="${a[@]@a}"; show "$x"; show "${a[@]@a}"; show "${a[*]@a}" """,
        "<1>[a:a:a]\n<3>[a][a][a]\n<1>[a:a:a]\n")]
    [InlineData("""set -- 'first value' ''; show "${@@A}"; IFS=:; show "${*@A}"; show "${@@a}" """,
        "<4>[set][--]['first value']['']\n<1>[set -- 'first value':'']\n<2>[][]\n")]
    [InlineData("""a=(zero one two); declare -n r='a[i++]'; i=0; show "${r@A}" "$i"; i=0; show "${!r@A}" "$i"; i=0; p='a[i++]'; show "${!p@A}" "$i" """,
        "<2>[][1]\n<2>[declare -a a='zero'][1]\n<2>[declare -a a='one'][2]\n")]
    [InlineData("""export x=value; declare -n r=x; show "${r@A}" "${!r@A}" "${!r@K}" """,
        "<3>[declare -x x='value'][declare -x x='x']['x']\n")]
    public async Task InspectionTransformsRetainSupportedFields(string script, string expected)
    {
        const string Show = """show(){ printf '<%s>' "$#"; for arg; do printf '[%s]' "$arg"; done; printf '\n'; }; """;
        var result = await EndToEndHarness.RunScriptAsync(Show + script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task InspectionTransformsUseSpaceJoinedFieldsInHereDocuments()
    {
        const string Script = "a=(one 'two words'); set -- 'first value' last; IFS=:; cat <<EOF\n"
            + "${a[@]@k}\n${a[*]@k}\n${@@A}\n${*@A}\nEOF\n";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0 one 1 two words\n0 one 1 two words\nset -- 'first value' 'last'\nset -- 'first value' 'last'\n",
            result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task KeyValueTransformsKeepMalformedUtf8AtTheByteBoundary()
    {
        const string Script = """a=([4]=$'x\377y' [9]=''); printf '[%s]' "${a[@]@K}" "${a[@]@k}" """;
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("5B34202427785C333737792720392022225D5B345D5B78FF795D5B395D5B5D"),
            result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclarationRenderingAdmitsExpandedBytesBeforeOpeningOutput(bool denseEscapes)
    {
        var governor = new MemoryGovernor(512 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var state = new Eval(Parser.Parse("""printf before; : "${value@A}" >effect"""));
        var bytes = new byte[32 * 1024];
        bytes.AsSpan().Fill(denseEscapes ? (byte)0xff : (byte)'x');
        state.SetExportedVariable("value", ShellValue.FromBytes(bytes));

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(denseEscapes ? 1 : 0, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal(denseEscapes, host.StandardError.TextContent.Contains("shared memory", StringComparison.Ordinal));
        Assert.Equal(!denseEscapes, host.CaptureEntries().ContainsKey("effect"));
        Assert.Equal(bytes, state.Variables["value"].Bytes.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("a=abc", "1|<>|2;0|<>|2")]
    [InlineData("a=(abc)", "1|<>|2;0|<>|2")]
    [InlineData("a=(abc '')", "1|<''>|2;1|<''>|2")]
    [InlineData("a=([1]=abc)", "1|<>|1;0|<>|1")]
    [InlineData("declare -A a=([0]=abc)", "1|<>|2;0|<>|2")]
    public async Task IndirectQuoteDistinguishesUnsetAndEmptySelections(string setup, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + "; p='a[$((i++))]'; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; "
            + "i=0; f \"${!p@Q}\"; printf '|%s;' \"$i\"; i=0; f ${!p@Q}; printf '|%s' \"$i\"");

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Reconciled from the saved Bash 5.2.37 parameter-operator cases.
    [Theory]
    [InlineData("x=abcabc; printf '<%s>\\n' \"${x/a/Z}\" \"${x//a/Z}\" \"${x/#a/Z}\" \"${x/%c/Z}\"\n", "<Zbcabc>\n<ZbcZbc>\n<Zbcabc>\n<abcabZ>\n", "", 0)]
    [InlineData("x=abcabc; printf '<%s>\\n' \"${x/a*c/Z}\" \"${x//?/Z}\" \"${x//a/\\&}\" \"${x//a/&X}\"\n", "<Z>\n<ZZZZZZ>\n<&bc&bc>\n<aXbcaXbc>\n", "", 0)]
    [InlineData("x='a/b'; printf '<%s>\\n' \"${x/\\//X}\" \"${x/'a'/Z}\"\n", "<aXb>\n<Z/b>\n", "", 0)]
    [InlineData("x=abC; printf '<%s>\\n' \"${x^}\" \"${x^^}\" \"${x,}\" \"${x,,}\" \"${x^^[ab]}\"\n", "<AbC>\n<ABC>\n<abC>\n<abc>\n<ABC>\n", "", 0)]
    [InlineData("x=\"a'b\"; printf '<%s>\\n' \"${x@Q}\"; unset x; printf 'argc='; f(){ printf '%s\\n' \"$#\"; }; f \"${x@Q}\"\n", "<'a'\\''b'>\nargc=1\n", "", 0)]
    [InlineData("x=''; printf '<%s>\\n' \"${x/*/X}\" \"${x//*/X}\"; x=abc; printf '<%s>\\n' \"${x//x*/X}\" \"${x//a*/X}\"\n", "<X>\n<X>\n<abc>\n<X>\n", "", 0)]
    [InlineData("unset x; printf '<%s>\\n' \"${x/$(printf bad >&2)/$(printf bad >&2)}\"\n", "<>\n", "", 0)]
    [InlineData("unset x; printf '<%s>\\n' \"${x^^$(printf bad >&2)}\"\n", "<>\n", "", 0)]
    [InlineData("x=abC; printf '<%s>\\n' \"${x@U}\" \"${x@u}\" \"${x@L}\"\n", "<ABC>\n<AbC>\n<abc>\n", "", 0)]
    [InlineData("x=abcabc; p=a; r='&X'; printf '<%s>\\n' \"${x//$p/$r}\" \"${x//$p/\"$r\"}\" \"${x//a/'&X'}\"\n", "<aXbcaXbc>\n<&Xbc&Xbc>\n<&Xbc&Xbc>\n", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p/a/Z}\" \"$i\"\n", "<uvwxyz>\n<4>\n", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p@Q}\" \"$i\"\n", "<'uvwxyz'>\n<4>\n", "", 0)]
    [InlineData("a=([2]=abc); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p/$(printf pattern >&2; printf a)/$(printf replacement >&2; printf X)}\" \"$i\"\n", "<>\n<4>\n", "patternreplacement", 0)]
    public async Task SavedValueOperators(string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Linux Bash 5.2.21 / C.UTF-8. Keep effects, argument counts and literal replacement bytes observable.
    [Theory]
    [InlineData("x=abc; printf '<%s>' \"${x/#/X}\" \"${x/%/X}\" \"${x//?/X}\" \"${x//*/X}\" \"${x//*/&X}\" \"${x//z*/X}\" \"${x/a*}\"", "<Xabc><abcX><XXX><X><abcX><abc><>")]
    [InlineData("x=ababa; printf '<%s>' \"${x//a?/X}\" \"${x/*b/X}\" \"${x/%a*/X}\" \"${x/#b/X}\" \"${x//[ab]/X}\"", "<XXa><Xa><X><ababa><XXXXX>")]
    [InlineData("x=abc; r='\\&'; printf '<%s>' \"${x/b/$r}\" \"${x/b/\"$r\"}\"; r='\\\\&'; printf '<%s>' \"${x/b/$r}\"", "<a&c><a\\&c><a\\bc>")]
    [InlineData("x='a/b'; p='/'; r='/&/'; printf '<%s>' \"${x/$p/$r}\" \"${x/$(printf /)/X}\" \"${x/a/${missing:-X/Y}}\"", "<a///b><aXb><X/Y/b>")]
    [InlineData("x='*ab'; p='*'; printf '<%s>' \"${x/\"$p\"/X}\" \"${x/'*'/X}\" \"${x/\\*/X}\" \"${x/$p/X}\"", "<Xab><Xab><Xab><X>")]
    [InlineData("x='abc'; printf '<%s>' \"${x//a/}\" \"${x//a}\" \"${x//}\" \"${x/}\"", "<bc><bc><abc><abc>")]
    [InlineData("x=''; printf '<%s>' \"${x/*/X}\" \"${x//*/X}\" \"${x/#/X}\" \"${x/%/X}\" \"${x//?/X}\"", "<X><X><X><X><>")]
    [InlineData("x=abc; printf '<%s>' \"${x^^a}\" \"${x^b}\" \"${x^^''}\" \"${x^^[ab]}\" \"${x@u}\" \"${x@U}\"; x=ABC; printf '<%s>' \"${x,,[AB]}\" \"${x,B}\" \"${x@L}\"", "<Abc><abc><abc><ABc><Abc><ABC><abC><ABC><abc>")]
    [InlineData("x='éλ🙂z'; printf '<%s>' \"${x@U}\" \"${x//?/X}\" \"${x/λ/Y}\" \"${x^^[éλ]}\"", "<ÉΛ🙂Z><XXXX><éY🙂z><ÉΛ🙂z>")]
    [InlineData("x='🙂a'; printf '<%s>' \"${x//[🙂]/X}\" \"${x/%?/X}\" \"${x/#?/X}\"", "<Xa><🙂X><Xa>")]
    [InlineData("a=([2]='a b' [7]='' [9]=cat); IFS=:; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f \"${a[@]//a/X}\"; f \"${a[*]@U}\"; f \"${a[@]@Q}\"; f \"${a[*]@Q}\"", "3|<X b><><cXt>1|<A B::CAT>3|<'a b'><''><'cat'>1|<'a b':'':'cat'>")]
    [InlineData("set -- 'a b' '' c; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f \"${@@Q}\"; f \"${*//?/X}\"; set --; f \"${@@Q}\"; f \"${*@Q}\"", "3|<'a b'><''><'c'>1|<XXX  X>0|<>1|<>")]
    [InlineData("unset x; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f \"${x@Q}\"; x=''; f \"${x@Q}\"; unset a; f \"${a[@]@Q}\"; a=(''); f \"${a[@]@Q}\"", "1|<>1|<''>0|<>1|<''>")]
    [InlineData("x='a b'; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f ${x@Q}; f \"${x@Q}\"; f pre\"${x@Q}\"post", "2|<'a><b'>1|<'a b'>1|<pre'a b'post>")]
    [InlineData("a=(aa bb); printf '<%s>' \"${a[@]//${a[0]:=x}/X}\" \"${a[@]}\"", "<X><bb><aa><bb>")]
    [InlineData("x=abc; printf '<%s>' \"${x//a/$((i+=1))}\" \"$i\"; unset x; printf '<%s>' \"${x//${y:=a}/${z:=b}}\" \"$y\" \"$z\"", "<1bc><1><><><>")]
    [InlineData("a=(one two three); i=0; p='a[i++]'; printf '%s|%s' \"${!p@U}\" \"$i\"", "TWO|2")]
    [InlineData("readonly x='a b'; q=${x@Q}; eval \"y=$q\"; printf '%s|%s' \"$x\" \"$y\"", "a b|a b")]
    [InlineData("x='/a/b/'; printf '<%s>' \"${x///}\" \"${x////Z}\" \"${x///b/_}\" \"${x///_}\"", "<ab><ZaZbZ></a_/></a/b/>")]
    [InlineData("x='ab/xy'; printf '<%s>' \"${x///?/Z}\" \"${x/#/Z}\" \"${x/%/Z}\" \"${x/#//Z}\" \"${x/%//Z}\"", "<abZy><Zab/xy><ab/xyZ></Zab/xy><ab/xy/Z>")]
    [InlineData("a=('' xyz); printf '<%s>' \"${a[@]//x/${a[0]:=new}}\" \"${a[@]}\"", "<new><newyz><new><xyz>")]
    [InlineData("a=(abc def); printf '<%s>' \"${a[@]//a/$((a[9]=9))}\" \"${a[@]}\"", "<9bc><def><9><abc><def><9>")]
    [InlineData("a=('' xyz); declare -n p=a; printf '<%s>' \"${p[*]//x/${a[0]:=new}}\"", "<new newyz>")]
    [InlineData("a=('' xyz); p='a[@]'; printf '<%s>' \"${!p//x/${a[0]:=new}}\"", "<new><newyz>")]
    [InlineData("declare -A a=([key]=''); printf '<%s>' \"${a[@]//x/${a[key]:=new}}\"", "<new>")]
    [InlineData("a=(abc def); printf '<%s>' \"${a[@]^^$((a[0]=7))}\" \"${a[@]}\"", "<7><def><7><def>")]
    [InlineData("a=(ABC DEF); printf '<%s>' \"${a[*],,${a[9]:=D}}\" \"${a[@]}\"", "<ABC dEF d><ABC><DEF><D>")]
    [InlineData("x=one; printf '<%s>' \"${x//o/$((x=9))}\" \"$x\"", "<9ne><9>")]
    [InlineData("x=abc; r='\\'; printf '<%s>' \"${x/b/$r'&'}\" \"${x/b/$r\\&}\" \"${x/b/$r\"&\"}\" \"${x/b/$r\"$r\"&}\"", "<a\\bc><a\\bc><a\\bc><a\\&c>")]
    [InlineData("x=abc; r='\\'; printf '<%s>' \"${x/b/\"$r\"&}\" \"${x/b/$r\"$r\"}\" \"${x/b/\"$r\"$r'&'}\"", "<a\\bc><a\\\\c><a\\\\bc>")]
    [InlineData("x=abcabc; p='#a'; printf '<%s>' \"${x/$p/Z}\" \"${x/\"$p\"/Z}\" \"${x//$p/Z}\"; p='%c'; printf '<%s>' \"${x/$p/Z}\" \"${x/\"$p\"/Z}\" \"${x//$p/Z}\"", "<Zbcabc><abcabc><abcabc><abcabZ><abcabc><abcabc>")]
    [InlineData("x='#abc#abc'; p='#a'; printf '<%s>' \"${x/$p/Z}\" \"${x/\"$p\"/Z}\" \"${x//$p/Z}\" \"${x/#$p/Z}\"", "<#abc#abc><Zbc#abc><ZbcZbc><Zbc#abc>")]
    [InlineData("x=abc; p=''; printf '<%s>' \"${x/${p}#a/Z}\" \"${x/${p}%c/Z}\" \"${x/$(printf '#a')/Z}\" \"${x/$'#a'/Z}\" \"${x/\\#a/Z}\"", "<Zbc><abZ><Zbc><abc><abc>")]
    [InlineData("x=abc; p='#'; q='%'; printf '<%s>' \"${x/$p/Z}\" \"${x/$q/Z}\"; p='\\#'; x='#abc'; printf '<%s>' \"${x/$p/Z}\"", "<Zabc><abcZ><Zabc>")]
    [InlineData("x='a*b'; p='\\*'; printf '<%s>' \"${x//$p/X}\" \"${x//\"$p\"/X}\"; x='a\\b'; p='\\\\'; printf '<%s>' \"${x/$p/X}\"", "<aXb><a*b><aXb>")]
    [InlineData("x=aba; p='\\a'; printf '<%s>' \"${x^^$p}\" \"${x^^\"$p\"}\"; x=ABA; p='\\A'; printf '<%s>' \"${x,,$p}\"", "<AbA><aba><aBa>")]
    [InlineData("x='a]b'; p='[\\]]'; printf '<%s>' \"${x//$p/X}\"; x='a-b'; p='[a\\-c]'; printf '<%s>' \"${x//$p/X}\"", "<aXb><XXb>")]
    [InlineData("x='a*b'; p='\\'; printf '<%s>' \"${x/$p*/X}\" \"${x/$p'*'/X}\"; x='a\\*b'; printf '<%s>' \"${x/$p'*'/X}\"", "<aXb><a*b><a\\*b>")]
    [InlineData("x=$'a\\001*b'; p='\\'; printf '<%s>' \"${x/$p'*'/X}\"; x=$'a\\001\\001b'; p=$'\\\\\\001'; printf '<%s>' \"${x/$p/X}\"", "<aX><aXb>")]
    [InlineData("x=abc; p='\\'; printf '<%s>' \"${x/$p/${y:=X}}\" \"$y\" \"${x^^$p}\"; x='a\\b'; printf '<%s>' \"${x/$p/X}\" \"${x/\"$p\"/X}\"", "<abc><X><abc><a\\b><aXb>")]
    [InlineData("a=(abc def); p='\\'; printf '<%s>' \"${a[@]/$p/${a[9]:=X}}\" \"${a[@]}\"", "<abc><def><X><abc><def><X>")]
    [InlineData("x=$'a\\377b'; p=$'\\\\\\377'; printf '<%s>' \"${x/$p/X}\"; x='aλb'; p='\\λ'; printf '<%s>' \"${x/$p/X}\"", "<aXb><aXb>")]
    [InlineData("x='a/b'; p='/'; printf '<%s>' \"${x/$p/Z}\" \"${x/${p}a/Z}\"; x='a#b'; p=''; printf '<%s>' \"${x/$p\\#/Z}\"", "<aZb><a/b><aZb>")]
    public async Task ValueOperatorsRetainFieldsAndQuoting(string script, string expectedOutput)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("x=$'\\377aλ'; printf '%s' \"${x@U}\"", "FF41CE9B")]
    [InlineData("x=$'\\377aλ'; printf '%s' \"${x//?/X}\"", "585858")]
    [InlineData("x=$'a\\377b'; printf '%s' \"${x/$'\\377'/$'\\376'}\"", "61FE62")]
    [InlineData("x=$'a\\377b'; printf '%s' \"${x//a/$'\\376&'}\"", "FE26FF62")]
    [InlineData("x=$'\\377'; printf '%s' \"${x@Q}\"", "24275C33373727")]
    [InlineData("x='\\377'; printf '%s' \"${x@E}\"", "FF")]
    [InlineData("x='\\uD800'; printf '%s' \"${x@E}\"", "EDA080")]
    [InlineData("x='\\U00200000'; printf '%s' \"${x@E}\"", "F888808080")]
    [InlineData("x='\\cλ'; printf '%s' \"${x@E}\"", "0EBB")]
    [InlineData("x='\\c🙂'; printf '%s' \"${x@E}\"", "109F9982")]
    [InlineData("x='\\303\\251'; printf '%s' \"${x@E}\"", "C3A9")]
    [InlineData("x='\\0tail'; printf '%s' \"${x@E}\"", "")]
    [InlineData("x='\\U80000000'; printf '%s' \"${x@E}\"", "")]
    [InlineData("x=$'\\377λ\\n'; q=${x@Q}; eval \"y=$q\"; printf '%s' \"$y\"", "FFCEBB0A")]
    public async Task ValueOperatorsPreserveRawBytes(string script, string expectedHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(expectedHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("x=abc; printf '%s' \"${x/${bad:?pattern}/$(printf late >&2)}\"; printf after", "bad: pattern\n")]
    [InlineData("x=abc; printf '%s' \"${x/$(printf first >&2; printf a)/${bad:?replacement}}\"; printf after", "firstbad: replacement\n")]
    public async Task FailedOperandsStopLaterEffects(string script, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Fact]
    public async Task UnsuccessfulReplacementCompletesWithoutExecutionGas()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse("printf '%s' \"${x//*z/Y}\"; printf after"));
        evaluation.SetVariable("x", ShellValue.FromText(new string('a', 2000)));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));

        await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(new string('a', 2000) + "after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }
}

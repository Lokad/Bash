using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ParameterTransformTests
{
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

    [Theory]
    [InlineData("${x@Q}")]
    [InlineData("${x@E}")]
    [InlineData("${x@U}")]
    [InlineData("${x//a/xxxxxxxx}")]
    public async Task TransformsChargeTheSharedExpansionBudget(string expression)
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse("printf '%s' \"" + expression + "\"; printf after"));
        evaluation.SetVariable("x", ShellValue.FromText(new string('a', 1024)));
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 800);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));

        await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("shell: word expansion exceeds its word or character limit\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task ReplacementBoundsUnsuccessfulPatternSearch()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse("printf '%s' \"${x//*z/Y}\"; printf after"));
        evaluation.SetVariable("x", ShellValue.FromText(new string('a', 2000)));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));

        await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("shell: parameter pattern matching exceeds its work limit\n", host.StandardError.TextContent);
    }
}

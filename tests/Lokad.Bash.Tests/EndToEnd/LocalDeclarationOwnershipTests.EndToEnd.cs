using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class LocalDeclarationOwnershipTestsEndToEnd
{
    [Theory]
    [InlineData("eval", "local x=inner", "", "outer")]
    [InlineData("eval", "local -a x=(inner); export x", "", "outer")]
    [InlineData("eval", "local -ar x=(inner)", "", "outer")]
    [InlineData("eval", "local -ar x=(inner)", "x=call ", "inner")]
    [InlineData("source", "local x=inner; export x", "", "outer")]
    [InlineData("source", "local -Ar x=([key]=inner)", "", "outer")]
    [InlineData("source", "local -Ar x=([key]=inner)", "x=call ", "inner")]
    public async Task LocalsDeclaredAfterUnsettingAScriptPrefixOutliveTheScript(
        string invocation, string declaration, string functionPrefix, string outside)
    {
        var observe = "printf \"<%s>/\" \"${x[*]}\"";
        var body = "unset x; " + declaration + "; " + observe;
        var command = invocation == "eval" ? "eval '" + body + "'" : ". /declaration";
        var result = await EndToEndHarness.RunScriptAsync(
            "x=outer; f(){ x=prefix " + command + "; " + observe + "; }; " + functionPrefix + "f; " + observe,
            EndToEndHarness.BuildTree([("/declaration", body)]));

        Assert.Equal("<inner>/<inner>/<" + outside + ">/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("eval", "export x", "local x=inner", "inner")]
    [InlineData("eval", "export -n x", "local x=inner", "inner")]
    [InlineData("eval", "export x", "local x", "unset")]
    [InlineData("eval", "export x", "local -a x=(inner)", "inner")]
    [InlineData("eval", "export x", "local -n x=y", "referent")]
    [InlineData("eval", "export x", "declare x=inner", "inner")]
    [InlineData("source", "export -n x", "local x=inner", "inner")]
    [InlineData("source", "export x", "local x", "unset")]
    [InlineData("source", "export x", "local -a x=(inner)", "inner")]
    public async Task LocalsDeclaredAfterExposingAScriptPrefixRestoreTheCaller(
        string invocation, string attribute, string declaration, string localValue)
    {
        var observe = "printf \"<%s>/\" \"${x-unset}\"";
        var body = "unset x; " + attribute + "; " + declaration + "; " + observe;
        var command = invocation == "eval" ? "eval '" + body + "'" : ". /declaration";
        var result = await EndToEndHarness.RunScriptAsync(
            "x=outer; y=referent; f(){ x=prefix " + command + "; " + observe + "; }; f; " + observe,
            EndToEndHarness.BuildTree([("/declaration", body)]));

        Assert.Equal("<" + localValue + ">/<" + localValue + ">/<outer>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("eval", "local r=inner", "inner")]
    [InlineData("eval", "local r", "prefix")]
    [InlineData("eval", "local r+=tail", "prefixtail")]
    [InlineData("eval", "local -a r=(inner tail)", "inner tail")]
    [InlineData("eval", "local -A r=([key]=inner)", "inner")]
    [InlineData("eval", "declare r=inner", "inner")]
    [InlineData("source", "local r=inner", "inner")]
    [InlineData("source", "local r+=tail", "prefixtail")]
    public async Task LocalNamerefDeclarationsKeepScriptPrefixOwnership(
        string invocation, string declaration, string temporaryValue)
    {
        var observe = "printf \"<%s>/\" \"${x[*]}\"";
        var body = declaration + "; " + observe;
        var command = invocation == "eval" ? "eval '" + body + "'" : ". /declaration";
        var result = await EndToEndHarness.RunScriptAsync(
            "x=outer; f(){ local -n r=x; x=prefix " + command + "; " + observe + "; }; f; " + observe,
            EndToEndHarness.BuildTree([("/declaration", body)]));

        Assert.Equal("<" + temporaryValue + ">/<outer>/<outer>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("x=prefix eval 'local -n r=x; local r=inner'")]
    [InlineData("local -n r=x; x=prefix eval 'other=another eval \"local r=inner\"'")]
    [InlineData("local -n r=x; x=prefix eval 'local r=inner; return'")]
    [InlineData("local x=local; local -n r=x; x=prefix eval 'local r=inner'")]
    [InlineData("local -n r=x; other=prefix eval 'local r=inner'")]
    public async Task NamerefDeclarationOwnersSurviveNestedScriptsAndFunctionReturn(string body)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "x=outer; f(){ " + body + "; }; f; printf '<%s>' \"$x\"");

        Assert.Equal("<outer>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ConvertedLocalNamerefArrayRetainsRawBytes()
    {
        var run = await EndToEndHarness.RunScriptAsync(
            "f(){ local -n r; local -a r=(saved); r=$'\\xff'; r+=$'\\xfe'; declare +n r; printf %s \"${r[*]}\"; }; f",
            EndToEndHarness.BuildTree([]), ReadOnlyMemory<byte>.Empty);

        Assert.Equal(new byte[] { 0xff, 0xfe }, run.Host.StandardOutput.Content.ToArray());
        Assert.Empty(run.Host.StandardError.Content.ToArray());
        Assert.Equal(0, run.ExitCode);
    }

    [Theory]
    [InlineData("local -r dest=old", "local ref[]=new", "declare -ar dest=()", 1, "local: ref: readonly variable")]
    [InlineData("local -r dest", "local -A ref[key]=new", "declare -Ar dest", 1, "local: ref: readonly variable")]
    [InlineData("local -r dest=old", "declare -I ref[i++]=new", "declare -ar dest=()", 1, "declare: ref: readonly variable")]
    [InlineData("local -r dest=old", "local ref[i++]", "declare -ar dest=()", 0, "")]
    [InlineData("local -ar dest=([2]=old)", "local -A ref[key]", "declare -ar dest=([2]=\"old\")", 1,
        "local: ref: cannot convert indexed to associative array")]
    public async Task LocalNamerefElementDeclarationPreparesTheReadonlyReferent(
        string binding, string declaration, string expectedLocal, int status, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"ref=caller; dest=outer; i=0\nf(){{ {binding}; local -n ref=dest; {declaration}; "
            + "printf '<%s:%s>\\n' \"$?\" \"$i\"; declare -p ref dest; }\nf\ndeclare -p ref dest");

        Assert.Equal($"<{status}:0>\ndeclare -n ref=\"dest\"\n{expectedLocal}\ndeclare -- ref=\"caller\"\ndeclare -- dest=\"outer\"\n", result.StdOut);
        Assert.Equal(diagnostic.Length == 0 ? "" : "line 2: " + diagnostic + "\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(":", "local -x ref[]=j++", "declare -ax dest", "ref[]", 0)]
    [InlineData("local -x dest=seed", "declare -i ref[i++-9]=j++", "declare -aix dest=()", "ref[i++-9]", 1)]
    [InlineData("local -Ax dest=([key]=seed)", "local +x ref[]=j++", "declare -A dest=([key]=\"seed\" )", "ref[]", 0)]
    [InlineData("local -x dest=seed", "dest=prefix typeset -ix ref[]=j++", "declare -aix dest=()", "ref[]", 0)]
    public async Task RejectedLocalElementInitializerKeepsTheReferentAttributes(
        string setup, string declaration, string expected, string operand, int indexEffects)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"ref=caller; dest=outer; i=0; j=0\nf(){{ {setup}; local -n ref=dest; {declaration}; "
            + "printf '<%s:%s:%s>\n' \"$?\" \"$i\" \"$j\"; declare -p ref dest; }\nf\ndeclare -p ref dest");

        Assert.Equal($"<1:{indexEffects}:0>\ndeclare -n ref=\"dest\"\n{expected}\ndeclare -- ref=\"caller\"\ndeclare -- dest=\"outer\"\n",
            result.StdOut);
        Assert.Equal($"line 2: {operand}: bad array subscript\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -i 'ref[${dest@a}]=j++'", "<0:1><missing><0>")]
    [InlineData("local -i \"ref[${dest@a}]=j++\"", "<0:1><0><missing>")]
    public async Task LocalElementInitializerObservesAttributesAtTheSelectorExpansionPhase(
        string declaration, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"j=0; f(){{ local -A dest=([0]=old); local -n ref=dest; {declaration}; "
            + "printf '<%s:%s><%s><%s>' \"$?\" \"$j\" \"${dest[A]-missing}\" \"${dest[Ai]-missing}\"; }; f");

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local ref[i++]=new", "declare -a dest=([0]=\"new\")", 0, 1, "")]
    [InlineData("local -r ref[i++]=new", "declare -ar dest", 0, 1, "line 2: ref: readonly variable\n")]
    public async Task ReadonlyLocalNamerefCanDeclareItsWritableReferent(
        string declaration, string expectedLocal, int status, int selector, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"dest=outer; i=0\nf(){{ local -nr ref=dest; {declaration}; "
            + "printf '<%s:%s>\\n' \"$?\" \"$i\"; declare -p ref dest; }\nf\ndeclare -p dest");

        Assert.Equal($"<{status}:{selector}>\ndeclare -nr ref=\"dest\"\n{expectedLocal}\ndeclare -- dest=\"outer\"\n", result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local ref[i++]=new", "declare -a dest=([0]=\"new\")", 0, 1, "")]
    [InlineData("local ref=new", "declare -- dest=\"new\"", 0, 0, "")]
    [InlineData("declare -I ref[i++]=new", "declare -ar dest=()", 1, 0, "line 2: declare: ref: readonly variable\n")]
    public async Task LocalNamerefDeclarationCanShadowTheEnclosingReadonlyReferent(
        string declaration, string expectedLocal, int status, int selector, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"dest=global; i=0\ninner(){{ local -n ref=dest; {declaration}; "
            + "printf '<%s:%s>\\n' \"$?\" \"$i\"; declare -p ref dest; }\n"
            + "outer(){ local -r dest=parent; inner; declare -p dest; }; outer\ndeclare -p dest");

        Assert.Equal($"<{status}:{selector}>\ndeclare -n ref=\"dest\"\n{expectedLocal}\ndeclare -r dest=\"parent\"\ndeclare -- dest=\"global\"\n", result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Intentional divergence from Bash 5.2: conversion clears the invalid nameref/array hybrid.
    [Theory]
    [InlineData("local -a r", "r[2]=value", "value")]
    [InlineData("local -A r", "r[key]=value", "value")]
    [InlineData("local -a r=(one two)", "r+=tail", "onetail two")]
    [InlineData("local -A r=([key]=one)", "r[key]+=tail", "onetail")]
    [InlineData("local r=(one two)", "r[0]=new", "new two")]
    [InlineData("local r+=(one two)", "r[0]=new", "new two")]
    [InlineData("local -a r=()", ": \"${r:=fallback}\"", "fallback")]
    public async Task UnsetLocalNamerefBecomesAnArrayWithoutChangingItsOuterBinding(
        string declaration, string assignment, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "r=outer; f(){ local -n r; " + declaration + "; " + assignment
            + "; printf '<%s>/' \"${r[*]}\"; }; f; printf '<%s>' \"$r\"");

        Assert.Equal("<" + expected + ">/<outer>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -ar r=(one two)", "r=new")]
    [InlineData("local -Ar r=([key]=one)", "r[key]=new")]
    public async Task ConvertedLocalNamerefArrayEnforcesReadonly(string declaration, string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "f(){ local -n r; " + declaration + "; " + assignment + "; printf should-not-run; }; f");

        Assert.Empty(result.StdOut);
        Assert.Equal("line 1: r: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("f(){ local -n r=a; declare +n r; printf '<%s>' \"${r-unset}\"; }; f", "<unset>")]
    [InlineData("f(){ local -n r=x; declare +n r; x=after; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<after>:<after>")]
    [InlineData("f(){ local -n r=x; declare +n r=value; r=after; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<after>:<after>")]
    [InlineData("declare -n s=t; f(){ local -n r=s; declare +n r; t=after; printf '<%s>:<%s>' \"$r\" \"$s\"; }; f", "<s>:<after>")]
    [InlineData("declare -n s=t; f(){ local -n r=s; local +n r=value; printf '<%s>:<%s>' \"$r\" \"${t-unset}\"; }; f", "<value>:<unset>")]
    [InlineData("declare -n s=t; f(){ local -n r=s; declare +n r+=tail; printf '<%s>:<%s>' \"$r\" \"${t-unset}\"; }; f", "<stail>:<unset>")]
    [InlineData("declare -n s=t; f(){ local -n r=s; (declare +n r=value; printf '<%s>/' \"$r\"); t=after; printf '<%s>' \"$r\"; }; f", "<value>/<after>")]
    [InlineData("x=old; f(){ local -n r=x; local +n r; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<x>:<old>")]
    [InlineData("x=old; f(){ local -n r=x; local +n r=value; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<value>:<old>")]
    [InlineData("x=old; f(){ local -n r=x; declare +n r+=tail; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<xtail>:<old>")]
    [InlineData("f(){ declare x; local -n r=x; declare +n r=value; printf '<%s>:<%s>' \"$r\" \"${x-unset}\"; }; f", "<value>:<unset>")]
    [InlineData("readonly x=old; f(){ local -n r=x; declare +n r=value; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<value>:<old>")]
    [InlineData("x=old; f(){ local -n r=x; r=temp declare +n r; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<x>:<old>")]
    [InlineData("x=old; f(){ local -n r=x; r=temp eval 'declare +n r=value'; printf '<%s>:<%s>' \"$r\" \"$x\"; }; f", "<value>:<old>")]
    [InlineData("x=old; f(){ x=prefix local -a x=(one two); printf 'n=%s v=<%s>/' \"${#x[@]}\" \"${x[*]}\"; }; f; printf '<%s>' \"$x\"", "n=0 v=<>/<old>")]
    [InlineData("x=old; f(){ x=prefix local -A x=([key]=one); printf 'n=%s v=<%s>/' \"${#x[@]}\" \"${x[*]}\"; }; f; printf '<%s>' \"$x\"", "n=0 v=<>/<old>")]
    public async Task LocalNamerefDeclarationsPreserveReferentsAndPrefixBindings(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local +n r=new", "new")]
    [InlineData("declare +n r+=tail", "tail")]
    [InlineData("typeset +n r='a[1]'", "a[1]")]
    public async Task ClearingAnUnsetLocalNamerefKeepsTheValidatedInitializer(string declaration, string value)
    {
        var result = await EvaluationTestHelper.RunAsync("a=([1]=outer)\nf() {\nlocal -n r\n" + declaration
            + "\nprintf '<%s:%s:%s>' \"$?\" \"$r\" \"${a[1]}\"\ndeclare -p r\n}\nf\n");

        Assert.Equal($"<0:{value}:outer>declare -- r=\"{value}\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n r", "r=$((++i))", "unset", "-n r", "line 4: local: `1': not a valid identifier\n", 1)]
    [InlineData("local -n r=target", "r+=' two words'", "stored", "-n r=\"target\"", "line 4: local: ` two words': not a valid identifier\n", 0)]
    [InlineData("local -n r='a[1]'; local r=before", "r+=tail", "before", "-n r=\"a[1]\"", "", 0)]
    public async Task ClearingALocalNamerefValidatesBeforeDetaching(string setup, string operand, string value,
        string listing, string diagnostic, int counter)
    {
        var result = await EvaluationTestHelper.RunAsync("a=([1]=outer); target=stored; i=0\nf() {\n" + setup
            + "\nlocal +n " + operand + "\nprintf '<%s:%s:%s:%s>' \"$?\" \"${r-unset}\" \"${a[1]}\" \"$i\"\ndeclare -p r\n}\nf\n");

        Assert.Equal($"<1:{value}:outer:{counter}>declare {listing}\n", result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -nr r; local -x +n r", "-rx")]
    [InlineData("declare -gnr r; declare -g +n r", "-r")]
    public async Task NameOnlyClearPreservesTheReadonlyAttributeOfAnUnsetNameref(string declaration, string attributes)
    {
        var result = await EvaluationTestHelper.RunAsync("f() {\n" + declaration
            + "\nprintf '<%s:%s>' \"$?\" \"${r-unset}\"\ndeclare -p r\n}\nf\n");

        Assert.Equal($"<0:unset>declare {attributes} r\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local r=inside")]
    [InlineData("local +n r=inside")]
    public async Task ReadonlyElementNamerefCanCreateASeparateLocalForTheTargetSpelling(string declaration)
    {
        var result = await EvaluationTestHelper.RunAsync("a=([1]=outer)\nf() {\nlocal -nr r='a[1]'\n" + declaration
            + "\nfirst=$?\nlocal +n r=late\nprintf '<%s:%s:%s:%s>' \"$first\" \"$?\" \"$r\" \"${a[1]}\"\ndeclare -p r\n}\nf\n");

        Assert.Equal("<0:1:inside:outer>declare -nr r=\"a[1]\"\n", result.StdOut);
        Assert.Equal("line 6: local: r: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareScalar_ShadowsInheritedNameref()
    {
        var script = @"readonly target=outer; declare -n reference=target; f() { declare reference=inner; printf 'inside=<%s>:<%s>\n' ""$reference"" ""$target""; }; f; printf 'outside=<%s>:<%s>\n' ""$reference"" ""$target""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inside=<inner>:<outer>\noutside=<outer>:<outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Local_RestoresOuterArraySlot()
    {
        var script = @"arr[2]=outer; f() { local arr; arr[0]=inner; printf '<%s>\n' ""${arr[0]}""; }; f; printf '<%s><%s>\n' ""${arr[0]-missing}"" ""${arr[2]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<inner>\n<missing><outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=(old); f(){ local -n r=a; local r=(new); printf '<%s>/' \"${a[*]}\"; }; f; printf '<%s>' \"${a[*]}\"", "<new>/<old>", "", 0)]
    [InlineData("a=(old); f(){ local -n r=a; local r+=(new); printf '<%s>/' \"${a[*]}\"; }; f; printf '<%s>' \"${a[*]}\"", "<new>/<old>", "", 0)]
    [InlineData("a=(old); f(){ local -n r=a; local -A r=([key]=new); printf '<%s>/' \"${a[key]}\"; }; f; printf '<%s>' \"${a[*]}\"", "<new>/<old>", "", 0)]
    [InlineData("declare -A a=([key]=old); f(){ local -n r=a; local r+=(new); printf '<%s>/' \"${a[*]}\"; }; f; printf '<%s>' \"${a[*]}\"", "<new>/<old>", "", 0)]
    [InlineData("a=(old); f(){ local -n r=a; local a=(prior); local r+=(new); printf '<%s>/' \"${a[*]}\"; }; f; printf '<%s>' \"${a[*]}\"", "<prior new>/<old>", "", 0)]
    [InlineData("value=outer; f() { value= local value; printf '<%s>\\n' \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<>\n<outer>\n", "", 0)]
    [InlineData("target=referent; value=outer; f() { value=temporary declare value; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<0>:<temporary>\n<outer>\n", "", 0)]
    [InlineData("target=referent; value=outer; f() { value=temporary declare value+=suffix; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<0>:<temporarysuffix>\n<outer>\n", "", 0)]
    [InlineData("target=referent; value=outer; f() { value=temporary declare -a value; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<0>:<unset>\n<outer>\n", "", 0)]
    [InlineData("target=referent; value=outer; f() { value=temporary declare -A value; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<0>:<unset>\n<outer>\n", "", 0)]
    [InlineData("target=referent; value=outer; f() { value=temporary local value; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<0>:<temporary>\n<outer>\n", "", 0)]
    [InlineData("target=referent; value=outer; f() { value=temporary declare value=explicit; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<0>:<explicit>\n<outer>\n", "", 0)]
    [InlineData("target=referent; value=outer; f() { local value=prior; value=temporary declare value; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"; }; f; printf '<%s>\\n' \"$value\"", "<0>:<temporary>\n<outer>\n", "", 0)]
    [InlineData("x=global; f() { x=changed; local x=local; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<changed>\n", "", 0)]
    [InlineData("x=global; f() { declare -g x=changed; local x=local; }; f; printf '<%s>\\n' \"$x\"", "<changed>\n", "", 0)]
    [InlineData("f() { declare -g x=changed; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"$x\"", "inside=<changed>\noutside=<changed>\n", "", 0)]
    [InlineData("x=global; inner() { x=changed; local x=inner; }; outer() { local x=outer; inner; printf 'outer=<%s>\\n' \"$x\"; }; outer; printf 'global=<%s>\\n' \"$x\"", "outer=<changed>\nglobal=<global>\n", "", 0)]
    [InlineData("x=global; f() { declare +g x=local; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<global>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; printf 'inside=<%s>\\n' \"$x\"; }; x=prefix f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<global>\n", "", 0)]
    [InlineData("x=global; f() { x=changed; local x=local; printf 'inside=<%s>\\n' \"$x\"; }; OTHER=prefix f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<changed>\n", "", 0)]
    [InlineData("arr=(old); f() { arr=(changed); local -a arr=(local); }; f; printf '<%s>\\n' \"${arr[@]}\"", "<changed>\n", "", 0)]
    [InlineData("first=one; second=two; declare -n ref=first; f() { declare -gn ref=second; local ref=local; }; f; printf '<%s>\\n' \"$ref\"", "<two>\n", "", 0)]
    [InlineData("x=global; f() { export x=changed; local x=local; }; f; printf '<%s>\\n' \"$x\"", "<changed>\n", "", 0)]
    [InlineData("x=global; f() { x=changed; local -n x='bad name'; }; f; printf '<%s>\\n' \"$x\"", "<changed>\n", "line 1: local: `bad name': invalid variable name for name reference\n", 0)]
    [InlineData("x=global; f() { unset x; local x=local; }; f; printf '<%s>\\n' \"${x-unset}\"", "<unset>\n", "", 0)]
    [InlineData("x=global; f() { x=changed; local x=local; }; x=prefix f; printf '<%s>\\n' \"$x\"", "<global>\n", "", 0)]
    [InlineData("x=global; f() { export x=changed; local x=local; }; x=prefix f; printf '<%s>\\n' \"$x\"", "<local>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; export x=changed; }; x=prefix f; printf '<%s>\\n' \"$x\"", "<changed>\n", "", 0)]
    [InlineData("x=global; f() { export x=changed; local x; unset x; }; x=prefix f; printf '<%s>\\n' \"${x-unset}\"", "<global>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; export x=changed; unset x; }; x=prefix f; printf '<%s>\\n' \"${x-unset}\"", "<global>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; readonly x; }; x=prefix f; printf '<%s>\\n' \"$x\"", "<local>\n", "", 0)]
    [InlineData("x=global; f() { export x=changed; local x=local; }; OTHER=prefix f; printf '<%s>\\n' \"$x\"", "<changed>\n", "", 0)]
    public async Task Evaluation_LocalDeclarations_RestoreHiddenBindingsAndRespectPrefixPromotion(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }
}

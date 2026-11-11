using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class GlobalDeclarationOwnershipTestsEndToEnd
{
    [Theory]
    [InlineData("a=prefix ", "-g", "=", false, "old")]
    [InlineData("link=prefix ", "-g", "=", false, "old")]
    [InlineData("b=prefix ", "-g", "=", false, "old")]
    [InlineData("a=prefix ", "-g", "+=", false, "old")]
    [InlineData("b=prefix ", "-ga", "+=", false, "old")]
    [InlineData("a=prefix ", "-g +n", "=", false, "old")]
    [InlineData("b=prefix ", "-g +n", "+=", false, "old")]
    [InlineData("a=prefix ", "-g", "=", true, "old")]
    [InlineData("b=prefix ", "-g", "+=", true, "old")]
    [InlineData("", "-g", "=", false, "new")]
    [InlineData("other=prefix ", "-g", "+=", false, "oldnew")]
    public async Task GlobalElementWritesThroughActiveNamerefsRespectTemporaryPrefixes(
        string prefix, string flags, string assignment, bool associative, string value)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(global); " + (associative ? "declare -A b=([0]=old)" : "b=(old)")
            + "; declare -n r='a[0]'; f(){ local -n a=link link=b; " + prefix + "declare " + flags
            + " r" + assignment + "new; printf '<%s>/' \"${b[0]}\"; }; f; printf '<%s>/<%s>' \"${b[0]}\" \"${a[0]}\"");

        Assert.Equal("<" + value + ">/<" + value + ">/<global>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -n r=s s", "local s=local", "-g", "<changed>/<changed>/<unset>/<unset>")]
    [InlineData("declare -n r=s s", "local s=local", "-g +n", "<changed>/<changed>/<unset>/<unset>")]
    [InlineData("declare -n r=s s", "local r=masked s=local", "-g", "<masked>/<changed>/<unset>/<unset>")]
    [InlineData("declare -n r=s s=t t", "local s=local t=last", "-g", "<changed>/<changed>/<unset>/<unset>")]
    [InlineData("declare -n r=s s=t t", "local s=local t=last", "-g +n", "<changed>/<changed>/<unset>/<unset>")]
    [InlineData("declare -n r=s s", "local -n s=t; local t=local", "-g", "<changed>/<changed>/<unset>/<unset>")]
    [InlineData("declare -n r=s s", "local -n s=t; local t=local", "-g +n", "<changed>/<changed>/<unset>/<unset>")]
    [InlineData("declare -n r=s", "local s=local", "-g", "<local>/<local>/<changed>/<changed>")]
    [InlineData("declare s; declare -n r=s", "local s=local", "-g", "<local>/<local>/<changed>/<changed>")]
    [InlineData("s=global; declare -n r=s", "local s=local", "-g", "<local>/<local>/<changed>/<changed>")]
    public async Task UnsetGlobalNamerefChainsUseActiveReferentsWithoutDetachingTheChain(
        string setup, string localDeclaration, string flags, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(setup + "; f(){ " + localDeclaration
            + "; declare " + flags + " r=changed; printf '<%s>/<%s>/' \"$r\" \"$s\"; }; f; "
            + "printf '<%s>/<%s>' \"${r-unset}\" \"${s-unset}\"");

        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("-gr", "")]
    [InlineData("-gr +n", "")]
    [InlineData("-gr", "other=temp ")]
    [InlineData("-gr +n", "other=temp ")]
    [InlineData("-gr", "s=temp ")]
    public async Task UnsetGlobalNamerefChainsApplyReadonlyToTheActiveReferent(string flags, string prefix)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r=s s; f(){ local s=local; " + prefix + "declare " + flags + " r; s=changed; printf should-not-run; }; f");

        Assert.Empty(result.StdOut);
        Assert.Equal("s: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("-gx", "")]
    [InlineData("-gx +n", "")]
    [InlineData("-gx", "other=temp ")]
    [InlineData("-gx +n", "other=temp ")]
    [InlineData("-gx", "s=temp ")]
    [InlineData("-gx +n", "s=temp ")]
    public async Task UnsetGlobalNamerefChainsExportTheActiveReferent(string flags, string prefix)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "declare -n r=s s; f(){ local s=local; " + prefix + "declare " + flags
            + " r=changed; printf '<%s>/' \"$s\"; sh -c 'printf \"<%s>\" \"${s-unset}\"'; }; f", EndToEndHarness.BuildTree([]));

        Assert.Equal("<changed>/<changed>", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("-g", "s=temp", "local")]
    [InlineData("-g +n", "s=temp", "local")]
    [InlineData("-ga", "s=temp", "local")]
    [InlineData("-gA", "s=temp", "local")]
    [InlineData("-g", "other=temp", "changed")]
    [InlineData("-g +n", "other=temp", "changed")]
    [InlineData("-ga", "other=temp", "changed")]
    [InlineData("-gA", "other=temp", "changed")]
    public async Task UnsetGlobalNamerefFallbackKeepsOnlyMatchingPrefixWritesTemporary(string flags, string prefix, string value)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r=s s; f(){ local s=local; " + prefix + " declare " + flags
            + " r=changed; printf '<%s>' \"${s[*]}\"; }; f");

        Assert.Equal("<" + value + ">", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("-g")]
    [InlineData("-g +n")]
    public async Task UnsetGlobalNamerefFallbackAppendsToTheCallerBinding(string flags)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r=s s; f(){ local s=old; other=temp declare " + flags
            + " r+=new; printf '<%s>' \"$s\"; }; f");

        Assert.Equal("<oldnew>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=temporary ", "-g +n", "$r")]
    [InlineData("other=temporary ", "-g +n", "$r")]
    [InlineData("a=temporary ", "-g", "${!r}")]
    [InlineData("other=temporary ", "-g", "${!r}")]
    [InlineData("a=temporary ", "-gx", "${!r}")]
    [InlineData("other=temporary ", "-gx", "${!r}")]
    public async Task GlobalNamerefElementWritesTheActiveLocalArray(string prefix, string flags, string referenceExpansion)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(global); declare -n r='a[0]'; f(){ local a=(local); " + prefix
            + "declare " + flags + " r+=new; printf '%s/<%s>/' \"$?\" \"${a[*]}\"; }; f; printf '<%s>/<%s>' \"${a[*]}\" \"" + referenceExpansion + "\"");

        Assert.Equal("0/<localnew>/<global>/<a[0]>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local a=(local)", "-gr +n", "new", "$r", "")]
    [InlineData("local -r a=(local)", "-g +n", "local", "$r", "a: readonly variable\n")]
    [InlineData("local a=(local)", "-gr", "new", "${!r}", "")]
    [InlineData("local -r a=(local)", "-g", "local", "${!r}", "a: readonly variable\n")]
    public async Task GlobalNamerefElementSeparatesReadonlyAttributeAndWriteOwners(
        string localDeclaration, string flags, string localValue, string referenceExpansion, string error)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(global); declare -n r='a[0]'; f(){ " + localDeclaration + "; declare " + flags
            + " r=new; printf '%s/<%s>/' \"$?\" \"${a[*]}\"; }; f; printf '<%s>/<%s>' \"${a[*]}\" \"" + referenceExpansion + "\"");

        Assert.Equal("0/<" + localValue + ">/<global>/<a[0]>", result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=temporary ", "-g")]
    [InlineData("other=temporary ", "-g")]
    [InlineData("a=temporary ", "-gA")]
    [InlineData("other=temporary ", "-gA")]
    public async Task GlobalElementNamerefChainsWriteTheCallerAssociativeArray(string prefix, string flags)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -A a=([key]=global); declare -n r=s s='a[key]'; f(){ local -A a=([key]=local); "
            + prefix + "declare " + flags + " r+=new; printf '%s/<%s>/' \"$?\" \"${a[key]}\"; }; f; printf '<%s>' \"${a[key]}\"");

        Assert.Equal("0/<localnew>/<global>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a[-1]", "a=temporary ", "localnew mid last", "1")]
    [InlineData("a[-1]", "other=temporary ", "local mid lastnew", "1")]
    [InlineData("a[i++]", "i=2 ", "local mid lastnew", "3")]
    public async Task GlobalElementWritesUsePrefixSelectionWithoutReplayingEffects(
        string reference, string prefix, string values, string index)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(global); declare -n r='" + reference + "'; f(){ local a=(local mid last) i=1; "
            + prefix + "declare -g r+=new; printf '<%s>/<%s>/' \"${a[*]}\" \"$i\"; }; f; printf '<%s>' \"${a[*]}\"");

        Assert.Equal("<" + values + ">/<" + index + ">/<global>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "", "local")]
    [InlineData("", "=new", "new")]
    [InlineData("other=temporary ", "=new", "new")]
    [InlineData("a=temporary ", "", "local")]
    [InlineData("a=temporary ", "=new", "new")]
    public async Task GlobalElementReadonlyAttributesProtectOnlyTheHiddenArray(string prefix, string initializer, string value)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(global); declare -n r='a[0]'; f(){ local a=(local); " + prefix + "declare -gr r" + initializer
            + "; printf '<%s>/' \"${a[0]}\"; a[0]=still-local; }; f; a[0]=fail; printf should-not-run");

        Assert.Equal("<" + value + ">/", result.StdOut);
        Assert.Equal("a: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("", "a: readonly variable\n")]
    [InlineData("=new", "a: readonly variable\na: readonly variable\n")]
    public async Task GlobalElementReadonlyAttributesOutliveTopLevelPrefixes(string initializer, string error)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old); declare -n r='a[0]'; a=temporary declare -gr r" + initializer
            + "; printf '<%s>/' \"${a[0]}\"; a[0]=fail; printf should-not-run");

        Assert.Equal("<old>/", result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("local x=local", "", "=", "new")]
    [InlineData("local x=local", "", "+=", "localnew")]
    [InlineData("local -r x=local", "", "=", "new")]
    [InlineData("local -a x=(local tail)", "", "=", "new tail")]
    [InlineData("local x=local", "x=temporary ", "=", "new")]
    [InlineData("local x=local", "other=temporary ", "+=", "localnew")]
    public async Task GlobalIndexedScalarInitializersWriteTheActiveBinding(
        string localDeclaration, string prefix, string assignment, string localValue)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "x=global; f(){ " + localDeclaration + "; " + prefix + "declare -ga x" + assignment
            + "new; printf '<%s>/' \"${x[*]}\"; }; f; printf '<%s>' \"${x[*]}\"");

        Assert.Equal("<" + localValue + ">/<global>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("r=global; ", "global")]
    [InlineData("r=(global); ", "global")]
    [InlineData("declare -n r=s s; ", "")]
    public async Task GlobalIndexedInitializerKeepsNamerefPrefixWritesTemporary(string setup, string globalValue)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + "f(){ local -n r=t; local t=old; t=tmp declare -ga r=new; "
            + "r=tmp declare -ga r+=tail; printf '<%s>/' \"$t\"; "
            + "other=tmp declare -ga r+=kept; printf '<%s>/' \"$t\"; }; f; printf '<%s>' \"${r[*]}\"");

        Assert.Equal("<old>/<oldkept>/<" + globalValue + ">", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobalNamerefRemovalKeepsTheReadonlyAttributeOnTheGlobalReferent()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "t=old; declare -n r=t; f(){ local t=local; declare -gr +n r=value; "
            + "printf '<%s>/<%s>;' \"$r\" \"$t\"; }; f; r=detached; t=fail; printf should-not-run");

        Assert.Equal("<t>/<local>;", result.StdOut);
        Assert.Equal("t: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("", "local", "value")]
    [InlineData("A", "local", "value")]
    [InlineData("a", "value", "old")]
    public async Task GlobalNamerefRemovalSelectsTheLastGlobalReferenceBeforeWriting(string arrayFlag, string localValue, string globalValue)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "t=old; declare -n r=s s=t; f(){ local s=local; declare -g" + arrayFlag + " +n r=value; "
            + "printf '%s/<%s>/<%s>/<%s>;' \"$?\" \"$r\" \"$s\" \"$t\"; }; f; "
            + "printf '<%s>/<%s>/<%s>' \"$r\" \"$s\" \"$t\"");

        Assert.Equal("0/<" + localValue + ">/<" + localValue + ">/<" + globalValue + ">;<t>/<t>/<" + globalValue + ">", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=temp ")]
    [InlineData("other=temp ")]
    public async Task RecreatedGlobalIndexedInitializerBypassesTemporaryPrefixes(string prefix)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r=s s; f(){ local r=old; " + prefix
            + "declare -ga r+=value; printf '%s:<%s>' \"$?\" \"${r[*]}\"; }; f");

        Assert.Equal("0:<oldvalue>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -r r=old", "r", "=", "value")]
    [InlineData("local -r r=old", "r", "+=", "oldvalue")]
    [InlineData("local -ar r=(old extra)", "r", "=", "value extra")]
    [InlineData("local -ar r=(old extra)", "r", "+=", "oldvalue extra")]
    [InlineData("local -n r=t; readonly t=old", "t", "=", "value")]
    [InlineData("local -n r=t; readonly t=old", "t", "+=", "oldvalue")]
    public async Task RecreatedGlobalIndexedInitializerKeepsReadonlyOnItsActiveTarget(
        string declaration, string target, string assignment, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r=s s; f(){ " + declaration + "; declare -ga r" + assignment
            + "value; printf '%s:<%s>' \"$?\" \"${r[*]}\"; " + target + "=fail; printf should-not-run; }; f");

        Assert.Equal("0:<" + output + ">", result.StdOut);
        Assert.Equal(target + ": readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("eval 'declare -g x=changed; printf \"inside=%s/\" \"$x\"'", false)]
    [InlineData("eval 'declare -g x=changed; printf \"inside=%s/\" \"$x\"'", true)]
    [InlineData("source /script", false)]
    [InlineData("source /script", true)]
    public async Task GlobalDeclarationsBypassTemporaryScriptPrefixes(string invocation, bool inFunction)
    {
        var body = "x=temp " + invocation + "; printf 'after=%s' \"$x\"";
        var script = "x=global; " + (inFunction ? "f(){ " + body + "; }; f" : body);
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([
            ("/script", "declare -g x=changed; printf 'inside=%s/' \"$x\"")
        ]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("inside=temp/after=changed", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
    }

    // Bash 5.2.21 (Linux) and 5.2.37 (Git Bash); element writes remain active even with -g.
    [Theory]
    [InlineData("x=global; f() { local x=local; declare -g x=changed; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<changed>\n", "", 0)]
    [InlineData("x=global; inner() { local x=inner; declare -g x=changed; printf 'inner=<%s>\\n' \"$x\"; }; outer() { local x=outer; inner; printf 'outer=<%s>\\n' \"$x\"; }; outer; printf 'global=<%s>\\n' \"$x\"", "inner=<inner>\nouter=<outer>\nglobal=<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; declare -g x+=tail; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<globaltail>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; declare -g x=\"$x\"; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<local>\n", "", 0)]
    [InlineData("x=global; f() { declare -g x=changed; printf 'inside=<%s>\\n' \"$x\"; }; x=prefix f; printf 'outside=<%s>\\n' \"$x\"", "inside=<prefix>\noutside=<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; declare -g x=changed; printf 'inside=<%s>\\n' \"$x\"; }; x=prefix f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; x=prefix declare -g x=changed; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"$x\"", "inside=<local>\noutside=<changed>\n", "", 0)]
    [InlineData("target=global; declare -n ref=target; f() { local target=local; declare -g ref=changed; printf 'inside=<%s>:<%s>\\n' \"$ref\" \"$target\"; }; f; printf 'outside=<%s>:<%s>\\n' \"$ref\" \"$target\"", "inside=<local>:<local>\noutside=<changed>:<changed>\n", "", 0)]
    [InlineData("target=global; declare -n ref=target; f() { local ref=mask target=local; declare -g ref=changed; printf 'inside=<%s>:<%s>\\n' \"$ref\" \"$target\"; }; f; printf 'outside=<%s>:<%s>\\n' \"$ref\" \"$target\"", "inside=<mask>:<local>\noutside=<changed>:<changed>\n", "", 0)]
    [InlineData("arr=(global); f() { local -a arr=(local); declare -ga arr=(\"${arr[0]}\" changed); printf 'inside=<%s>\\n' \"${arr[@]}\"; }; f; printf 'outside=<%s>\\n' \"${arr[@]}\"", "inside=<local>\noutside=<local>\noutside=<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; (declare -g x=child; printf 'child=<%s>\\n' \"$x\"); printf 'parent=<%s>\\n' \"$x\"; }; f; printf 'global=<%s>\\n' \"$x\"", "child=<local>\nparent=<local>\nglobal=<global>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; value=$(declare -g x=child; printf '<%s>' \"$x\"); printf 'child=%s parent=<%s>\\n' \"$value\" \"$x\"; }; f; printf 'global=<%s>\\n' \"$x\"", "child=<local> parent=<local>\nglobal=<global>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; { declare -g x=child; printf 'child=<%s>\\n' \"$x\"; } | cat; printf 'parent=<%s>\\n' \"$x\"; }; f; printf 'global=<%s>\\n' \"$x\"", "child=<local>\nparent=<local>\nglobal=<global>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; declare -g x=changed; export x=tail; printf 'inside=<%s>\\n' \"$x\"; }; x=prefix f; printf 'outside=<%s>\\n' \"$x\"", "inside=<tail>\noutside=<tail>\n", "", 0)]
    [InlineData("arr=(global); f() { local -a arr=(local); local i=2; declare -g arr[i++]=changed; printf 'inside=<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\"; }; f; printf 'outside=<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\"", "inside=<local>:<changed>:<3>\noutside=<global>:<unset>\n", "", 0)]
    [InlineData("arr=(global); f() { local -a arr=(local); local i=2; declare -ga arr[i++]=changed; printf 'inside=<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\"; }; f; printf 'outside=<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\"", "inside=<local>:<changed>:<3>\noutside=<global>:<unset>\n", "", 0)]
    [InlineData("x=global; OTHER=temp declare -g x=changed; printf '<%s>\\n' \"$x\"", "<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; OTHER=temp declare -g x=changed; printf '<%s>\\n' \"$x\"; }; f; printf '<%s>\\n' \"$x\"", "<local>\n<changed>\n", "", 0)]
    [InlineData("x=global; x=temp declare -g x=changed; printf '<%s>\\n' \"$x\"", "<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; export x=tail; declare -g x=changed; printf '<%s>\\n' \"$x\"; }; x=prefix f; printf '<%s>\\n' \"$x\"", "<tail>\n<tail>\n", "", 0)]
    [InlineData("x=global; inner() { declare -g x=changed; printf 'inner=<%s>\\n' \"$x\"; }; outer() { local x=outer; x=inner inner; printf 'outer=<%s>\\n' \"$x\"; }; x=prefix outer; printf 'global=<%s>\\n' \"$x\"", "inner=<inner>\nouter=<outer>\nglobal=<changed>\n", "", 0)]
    [InlineData("x=global; inner() { declare -g x=changed; }; outer() { local x=outer; OTHER=inner inner; printf 'outer=<%s>\\n' \"$x\"; }; OTHER=outer outer; printf 'global=<%s>\\n' \"$x\"", "outer=<outer>\nglobal=<changed>\n", "", 0)]
    [InlineData("first=one; second=two; ref=first; f() { local ref=local; declare -gn ref=second; printf 'inside=<%s>\\n' \"$ref\"; }; f; printf 'outside=<%s>\\n' \"$ref\"", "inside=<local>\noutside=<two>\n", "", 0)]
    [InlineData("first=one; declare -n ref=first; f() { local ref=local first=mask; declare -g +n ref=changed; printf 'inside=<%s>:<%s>\\n' \"$ref\" \"$first\"; }; f; printf 'outside=<%s>:<%s>\\n' \"$ref\" \"$first\"", "inside=<local>:<mask>\noutside=<first>:<changed>\n", "", 0)]
    [InlineData("target=global; declare -n first=second second=target; f() { local first=one second=two target=local; declare -g first=changed; printf '<%s>:<%s>:<%s>\\n' \"$first\" \"$second\" \"$target\"; }; f; printf '<%s>:<%s>:<%s>\\n' \"$first\" \"$second\" \"$target\"", "<one>:<two>:<local>\n<changed>:<changed>:<changed>\n", "", 0)]
    [InlineData("arr=(global); declare -n ref='arr[i++]'; i=0; f() { local -a arr=(local); local i=2; declare -g ref=changed; printf 'inside=<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\"; }; f; printf 'outside=<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\"", "inside=<local>:<changed>:<3>\noutside=<global>:<unset>:<0>\n", "", 0)]
    [InlineData("arr=(global); i=0; f() { local -a arr=(local); local i=2; declare -ga arr=([i++]=changed); printf 'inside=<%s>:<%s>\\n' \"${arr[0]}\" \"$i\"; }; f; printf 'outside=<%s>:<%s>:<%s>\\n' \"${arr[0]-unset}\" \"${arr[2]-unset}\" \"$i\"", "inside=<local>:<3>\noutside=<unset>:<changed>:<0>\n", "", 0)]
    [InlineData("arr=(global); f() { local -a arr=(local); declare -ga arr+=(\"${arr[0]}\"); printf 'inside=<%s>\\n' \"${arr[@]}\"; }; f; printf 'outside=<%s>\\n' \"${arr[@]}\"", "inside=<local>\noutside=<global>\noutside=<local>\n", "", 0)]
    [InlineData("declare -A arr=([key]=global); f() { local -A arr=([key]=local); declare -gA arr=([key]=\"${arr[key]}\" [next]=changed); printf 'inside=<%s>:<%s>\\n' \"${arr[key]}\" \"${arr[next]-unset}\"; }; f; printf 'outside=<%s>:<%s>\\n' \"${arr[key]}\" \"${arr[next]}\"", "inside=<local>:<unset>\noutside=<local>:<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; declare -gr x=changed; x=stilllocal; printf '<%s>\\n' \"$x\"; }; f; x=fail; printf '<%s>\\n' \"$x\"", "<stilllocal>\n", "x: readonly variable\n", 1)]
    [InlineData("x=global; f() { local -r x=local; declare -g x=changed; printf '<%s>\\n' \"$x\"; }; f; printf '<%s>\\n' \"$x\"", "<local>\n<changed>\n", "", 0)]
    [InlineData("f() { local x=local; declare -g x; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'outside=<%s>\\n' \"${x-unset}\"; declare -g x=changed; printf '<%s>\\n' \"$x\"", "inside=<local>\noutside=<unset>\n<changed>\n", "", 0)]
    [InlineData("x=global; f() { local x=local; (declare -g x=child; printf 'child=<%s>\\n' \"$x\"); printf 'parent=<%s>\\n' \"$x\"; }; x=prefix f; printf 'global=<%s>\\n' \"$x\"", "child=<local>\nparent=<local>\nglobal=<global>\n", "", 0)]
    [InlineData("x=global; inner() { local x=inner; (declare -g x=child; printf 'child=<%s>\\n' \"$x\"); }; outer() { local x=outer; inner; printf 'outer=<%s>\\n' \"$x\"; }; outer; printf 'global=<%s>\\n' \"$x\"", "child=<inner>\nouter=<outer>\nglobal=<global>\n", "", 0)]
    [InlineData("x=global; f() { local y=local; (declare -g x=child; printf 'child=<%s>\\n' \"$x\"); printf 'parent=<%s>\\n' \"$x\"; }; f; printf 'global=<%s>\\n' \"$x\"", "child=<child>\nparent=<global>\nglobal=<global>\n", "", 0)]
    [InlineData("arr=(global); i=0; f() { local -a arr=(local); local i=2; OTHER=prefix declare -ga arr=([i++]=changed); printf 'inside=<%s>:<%s>\\n' \"${arr[0]}\" \"$i\"; }; f; printf 'outside=<%s>:<%s>\\n' \"${arr[2]}\" \"$i\"", "inside=<local>:<3>\noutside=<changed>:<0>\n", "", 0)]
    [InlineData("arr=(global); declare -n ref='arr[i++]'; i=0; f() { local -a arr=(local); local i=2; declare -g +n ref=changed; printf 'inside=<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\"; }; f; printf 'outside=<%s>:<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\" \"$ref\"", "inside=<local>:<changed>:<3>\noutside=<global>:<unset>:<0>:<arr[i++]>\n", "", 0)]
    [InlineData("arr=(global); declare -n ref='arr[i++]'; i=0; f() { local -a arr=(local); local i=2; declare -ga ref=changed; printf 'inside=<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\"; }; f; printf 'outside=<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[2]-unset}\" \"$i\"", "inside=<local>:<changed>:<3>\noutside=<global>:<unset>:<0>\n", "", 0)]
    [InlineData("first=one; second=two; declare -n ref=first; f() { local -n ref=second; declare -g ref=changed; printf 'inside=<%s>:<%s>:<%s>\\n' \"$ref\" \"$first\" \"$second\"; }; f; printf 'outside=<%s>:<%s>:<%s>\\n' \"$ref\" \"$first\" \"$second\"", "inside=<two>:<changed>:<two>\noutside=<changed>:<changed>:<two>\n", "", 0)]
    [InlineData("x=global; f() { declare +g -g x=changed; printf '<%s>\\n' \"$x\"; }; f; printf '<%s>\\n' \"$x\"", "<changed>\n<changed>\n", "", 0)]
    [InlineData("x=global; f() { declare -g +g x=changed; printf '<%s>\\n' \"$x\"; }; f; printf '<%s>\\n' \"$x\"", "<changed>\n<changed>\n", "", 0)]
    [InlineData("x=global; f(){ local x=local; declare -g x=changed; printf 'inside=%s/' \"$x\"; }; x=temp eval 'f; printf \"temporary=%s/\" \"$x\"'; printf 'after=%s' \"$x\"", "inside=local/temporary=temp/after=changed", "", 0)]
    [InlineData("x=global; declare -n r=x; f(){ local x=local; declare -ga r=(one two); printf '<%s>/' \"${x[*]}\"; }; f; printf '<%s>' \"${x[*]}\"", "<one two>/<global>", "", 0)]
    [InlineData("x=global; declare -n r=x; f(){ local x=local; declare -g r=(one two); printf '<%s>/' \"${x[*]}\"; }; f; printf '<%s>' \"${x[*]}\"", "<one two>/<global>", "", 0)]
    [InlineData("declare -A x=([key]=global); declare -n r=x; f(){ local x=local; declare -ga r=(one two); printf '%s:<%s>' \"$?\" \"$x\"; }; f", "1:<local>", "r: cannot convert associative to indexed array\ndeclare: r: cannot convert associative to indexed array\n", 0)]
    [InlineData("x=(global); declare -n r=x; f(){ local x=local; declare -gA r=(one two); printf '%s:<%s>' \"$?\" \"$x\"; }; f", "1:<local>", "r: cannot convert indexed to associative array\ndeclare: r: cannot convert indexed to associative array\n", 0)]
    [InlineData("x=global; declare -n r=x; f(){ local -a x=(local); declare -gA r=(one two); printf '<%s>:<%s>/' \"${!x[@]}\" \"${x[*]}\"; }; f; printf '<%s>' \"$x\"", "<one>:<two>/<global>", "", 0)]
    [InlineData("x=global; declare -n r=x; f(){ local -A x=([key]=local); declare -ga r=(one two); printf '<%s>' \"${x[@]}\"; }; f; printf '/<%s>' \"$x\"", "<'one'><'two'>/<global>", "", 0)]
    [InlineData("x=global; declare -n r=x; f(){ local -A x=([key]=local); declare -g r=('two words' '' \"q'uote\"); printf '<%s>' \"${x[@]}\"; }; f", "<'two words'><''><'q'\\''uote'>", "", 0)]
    [InlineData("x=global; declare -n r=x; f(){ local -A x=([key]=local); declare -ga r=([1]=one); printf after; }; f", "", "'1': invalid arithmetic expression\n", 1)]
    [InlineData("declare -A x=([old]=prior); declare -n r=x; declare -g r=(one two); printf '<%s>:<%s>' \"${!x[*]}\" \"${x[*]}\"", "<one>:<two>", "", 0)]
    [InlineData("declare -A x=([old]=prior); declare -n r=x; declare -ga r=(one two); printf after", "", "r: cannot convert associative to indexed array\n", 1)]
    [InlineData("declare -n r=s s; f(){ local r=local; declare -ga r=(one two); printf '<%s>/' \"$r\"; }; f; printf '<%s>/<%s>' \"${r[*]}\" \"${s[*]}\"", "<local>/<one two>/<>", "", 0)]
    [InlineData("declare -nr s; declare -n r=s; f(){ local r=local; declare -gA r=([key]=value); }; f; printf '<%s>/<%s>' \"${r[*]}\" \"${s[*]}\"", "<value>/<>", "", 0)]
    [InlineData("declare -n r=s s; f(){ local r=local; declare -ga r+=value; printf '<%s>/' \"$r\"; }; f; printf '<%s>/<%s>' \"${r[*]}\" \"${s[*]}\"", "<localvalue>/<>/<>", "", 0)]
    [InlineData("declare -n r=s s; f(){ local r=local; declare -gA r=value; printf '<%s>/' \"$r\"; }; f; printf '<%s>/<%s>' \"${r[*]}\" \"${s[*]}\"", "<local>/<value>/<>", "", 0)]
    [InlineData("declare -n r=s s; f(){ local s=local; declare -gA r=value; printf '<%s>/' \"$s\"; }; f; printf '<%s>/<%s>' \"${r[*]}\" \"${s[*]}\"", "<value>/<>/<>", "", 0)]
    [InlineData("declare -n r=s s; f(){ local -n r; declare -ga r=value; printf 'status=%s/' \"$?\"; }; f; printf '<%s>/<%s>' \"${r[*]}\" \"${s[*]}\"", "status=0/<>/<>", "declare: `': not a valid identifier\n", 0)]
    [InlineData("declare -A a=([key]=old); f(){ local a=inner; declare -ga a=(new); printf 'status=%s:<%s>/' \"$?\" \"$a\"; }; f; printf '<%s>' \"${a[key]}\"", "status=1:<inner>/<old>", "a: cannot convert associative to indexed array\ndeclare: a: cannot convert associative to indexed array\n", 0)]
    [InlineData("declare -A a=([key]=old); f(){ declare -ga a=(new); printf 'status=%s/' \"$?\"; }; f; printf '<%s>' \"${a[key]}\"", "status=1/<old>", "a: cannot convert associative to indexed array\ndeclare: a: cannot convert associative to indexed array\n", 0)]
    public async Task Evaluation_GlobalDeclarations_SelectOwnersWithoutLeakingAcrossScopes(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }
}

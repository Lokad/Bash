using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ExportDeclarationEffectsTestsEndToEnd
{
    [Theory]
    [InlineData("eval", "unset x; local -a x; x=(inner)", "unset")]
    [InlineData("eval", "unset x; local -A x=([key]=inner)", "unset")]
    [InlineData("eval", "unset x; local -a x=()", "unset")]
    [InlineData("eval", "unset x; local -a x", "outer")]
    [InlineData("eval", "unset x; local +x -a x=(inner)", "outer")]
    [InlineData("eval", "unset x; local -a x=(inner); export -n x", "outer")]
    [InlineData("eval", "export -n x; unset x; local -a x=(inner)", "unset")]
    [InlineData("eval", "local -n r=x; unset r; local -a r=(inner)", "unset")]
    [InlineData("eval", "unset x; local x=inner", "inner")]
    [InlineData("eval", "unset x; local -xr x=inner", "inner")]
    [InlineData("eval", "unset x; local -ar x=(inner)", "unset")]
    [InlineData("eval", "unset x; local -a x=(inner); export x", "unset")]
    [InlineData("eval", "unset x; declare -a x=(inner)", "unset")]
    [InlineData("source", "unset x; local -a x=(inner)", "unset")]
    [InlineData("source", "unset x; local -A x=()", "unset")]
    [InlineData("source", "unset x; local -Ar x=([key]=inner)", "unset")]
    [InlineData("source", "unset x; local x=inner; export x", "inner")]
    [InlineData("function", "unset x; local x=inner", "inner")]
    [InlineData("function", "unset x; local -xr x=inner", "inner")]
    [InlineData("function", "unset x; local -ar x=(inner)", "unset")]
    [InlineData("function", "unset x; local -A x; x[key]=inner", "unset")]
    [InlineData("function", "unset x; local -a x=()", "unset")]
    [InlineData("function", "unset x; local -a x", "outer")]
    [InlineData("function", "unset x; local +x x=inner", "outer")]
    [InlineData("function", "local x=first; unset x; local x; x=inner", "inner")]
    [InlineData("function", "local +x x=local; unset x; local x=inner", "outer")]
    [InlineData("function", "local x=local; export -n x; unset x; local -a x=(inner)", "outer")]
    [InlineData("eval", "local x=local; export -n x; unset x; local x=inner", "outer")]
    [InlineData("eval", "local +x x=local; unset x; local -A x=([key]=inner)", "outer")]
    [InlineData("source", "local +x x=local; unset x; local -a x=(inner)", "outer")]
    [InlineData("source", "local x=local; export -n x; unset x; local x=inner", "outer")]
    [InlineData("function", "local x=local; unset x; export -n x; local x=inner", "outer")]
    [InlineData("eval", "local x=local; unset x; local +x x; unset x; local x=inner", "outer")]
    [InlineData("function", "x=script eval \"unset x; local +x x=first; unset x; local x=inner\"", "outer")]
    [InlineData("function", "local +x x=local; unset x; local -x x=inner", "inner")]
    [InlineData("eval", "local +x x=local; export x; unset x; local x=inner", "inner")]
    [InlineData("function", "local x=local; unset x; x=inner", "inner")]
    [InlineData("eval", "local x=local; unset x; x=inner", "inner")]
    [InlineData("plain", "local -x x=local; unset x; x=inner", "outer")]
    [InlineData("plain", "local x=first; unset x; local -a x=(inner)", "outer")]
    public async Task RedeclaredPrefixesInheritExportsWithoutChangingTheirLifetime(
        string invocation, string body, string childValue)
    {
        var command = invocation switch
        {
            "eval" => "f(){ x=prefix eval '" + body + "; child'; }; f",
            "source" => "f(){ x=prefix . /declaration; }; f",
            "function" => "f(){ " + body + "; child; }; x=prefix f",
            "plain" => "f(){ " + body + "; child; }; f",
            _ => throw new ArgumentOutOfRangeException(nameof(invocation))
        };
        var result = await EndToEndHarness.RunScriptAsync(
            "export x=outer; child(){ sh -c 'printf \"<%s>/\" \"${x-unset}\"'; }; " + command + "; child; x=after",
            EndToEndHarness.BuildTree([("/declaration", body + "; child")]));

        Assert.Equal("<" + childValue + ">/<outer>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("f(){ local +x x=local; unset x; local x=inner; child; }; x=prefix f")]
    [InlineData("f(){ x=prefix eval 'local x=local; export -n x; unset x; local x=inner; child'; }; f")]
    [InlineData("f(){ local +x x=local; unset x; (local x=inner; child); }; x=prefix f")]
    [InlineData("f(){ x=prefix eval 'local +x x=local; unset x; (local x=inner; child)'; }; f")]
    public async Task ClearedLocalExportsStayAbsentFromChildEnvironments(string invocation)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "x=outer; child(){ sh -c 'printf \"<%s>/\" \"${x-unset}\"'; }; " + invocation + "; child",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("<unset>/<unset>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("f(){ unset x; local x=inner; export x; }; x=prefix f")]
    [InlineData("f(){ unset x; local -ar x=(inner); }; x=prefix f")]
    [InlineData("f(){ unset x; local -Ax x=([key]=inner); }; x=prefix f")]
    [InlineData("f(){ x=prefix eval 'unset x; local -xr x=inner'; }; f")]
    [InlineData("f(){ x=prefix eval 'unset x; local -ar x=(inner)'; }; f")]
    [InlineData("f(){ x=prefix eval 'unset x; declare -ax x=(inner)'; }; f")]
    [InlineData("f(){ x=prefix eval 'unset x; local -a x; x=(inner); export x'; }; f")]
    public async Task LocalsDeclaredAfterPrefixUnsetDoNotPromoteValuesOrAttributes(string invocation)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "x=outer; " + invocation + "; printf '<%s>/' \"${x[*]}\"; "
            + "sh -c 'printf \"<%s>/\" \"${x-unset}\"'; x=after",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("<outer>/<unset>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("f(){ x=prefix eval '(unset x; local -a x=(inner); child); child'; }; f")]
    [InlineData("f(){ x=prefix eval 'captured=$(unset x; local -a x=(); child); printf %s \"$captured\"; child'; }; f")]
    [InlineData("f(){ (unset x; local -A x=([key]=inner); child); child; }; x=prefix f")]
    [InlineData("f(){ { unset x; local -a x=(inner); child; } | cat; child; }; x=prefix f")]
    public async Task RedeclaredPrefixExportsRemainIsolatedInChildEvaluations(string invocation)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "export x=outer; child(){ sh -c 'printf \"<%s>/\" \"${x-unset}\"'; }; " + invocation + "; child",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("<unset>/<prefix>/<outer>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("x=outer", "unset x; export x", "outer", "outer", "outer")]
    [InlineData("unset x", "unset x; export x", "unset", "unset", "unset")]
    [InlineData("x=outer", "local x; export x", "prefix", "prefix", "prefix")]
    [InlineData("x=outer", "local x; other=temp eval 'export x'", "prefix", "prefix", "prefix")]
    [InlineData("x=outer", "local x; g(){ export x; }; g", "prefix", "prefix", "prefix")]
    [InlineData("x=outer", "local x; g(){ h(){ export x; }; h; }; g", "prefix", "prefix", "prefix")]
    [InlineData("x=outer", "local x; g(){ other=temp eval 'export x'; }; g", "prefix", "prefix", "prefix")]
    [InlineData("x=outer", "local x=assigned; g(){ export x; }; g", "assigned", "assigned", "assigned")]
    [InlineData("x=outer", "local x; unset x; g(){ export x; }; g", "unset", "outer", "unset")]
    [InlineData("x=outer", "local x=local; g(){ unset x; export x=after; }; g", "after", "after", "after")]
    [InlineData("x=outer", "local x=local; g(){ unset x; }; g; export x=after", "after", "after", "after")]
    [InlineData("x=outer", "local x=local; g(){ unset x; export x; }; g; x=after", "after", "after", "after")]
    [InlineData("x=outer", "local x; unset x; export x", "unset", "outer", "unset")]
    [InlineData("x=outer", "x=inner eval 'unset x; export x'", "prefix", "prefix", "prefix")]
    [InlineData("x=outer", "local x; x=inner eval 'unset x; export x'", "prefix", "prefix", "prefix")]
    [InlineData("x=outer", "x=inner eval 'local x; unset x; export x'", "prefix", "outer", "unset")]
    [InlineData("target=outer; declare -n x=target", "unset x; export x", "outer", "outer", "unset")]
    public async Task ValuelessExportsAfterUnsetKeepTheSurvivingPrefixBinding(
        string setup, string body, string inside, string outside, string exported)
    {
        var result = await EndToEndHarness.RunScriptAsync(setup + "; f(){ " + body
            + "; printf '<%s>/' \"${x-unset}\"; }; x=prefix f; printf '<%s>/' \"${x-unset}\"; "
            + "sh -c 'printf \"<%s>\" \"${x-unset}\"'", EndToEndHarness.BuildTree([]));

        Assert.Equal("<" + inside + ">/<" + outside + ">/<" + exported + ">", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("x=outer", "unset x; readonly x", "outer")]
    [InlineData("unset x", "unset x; readonly x", "unset")]
    [InlineData("x=outer", "local x; readonly x", "prefix")]
    [InlineData("x=outer", "local x; g(){ readonly x; }; g", "prefix")]
    [InlineData("x=outer", "local x; g(){ other=temp eval 'readonly x'; }; g", "prefix")]
    [InlineData("x=outer", "x=inner eval 'unset x; readonly x'", "prefix")]
    [InlineData("x=outer", "local x; x=inner eval 'unset x; readonly x'", "prefix")]
    public async Task ValuelessReadonlyDeclarationsOutliveAnUnsetPrefix(string setup, string body, string value)
    {
        var result = await EvaluationTestHelper.RunAsync(setup + "; f(){ " + body
            + "; printf '<%s>/' \"${x-unset}\"; }; x=prefix f; printf '<%s>/' \"${x-unset}\"; x=after; printf should-not-run");

        Assert.Equal("<" + value + ">/<" + value + ">/", result.StdOut);
        Assert.Equal("x: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("local x; unset x; readonly x")]
    [InlineData("x=inner eval 'local x; unset x; readonly x'")]
    public async Task UnsetLocalsDoNotPromoteTheirNewReadonlyAttribute(string body)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "x=outer; f(){ " + body + "; }; x=prefix f; x=after; printf '%s' \"$x\"");

        Assert.Equal("after", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AnExportedUnsetBindingSurvivesFunctionPrefixRestoration()
    {
        var evaluation = new ShellEvaluation(Parser.Parse("f(){ unset x; export x; }; x=prefix f"));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.DoesNotContain("x", evaluation.Variables.Keys);
        Assert.Contains("x", evaluation.ExportedVariables);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("unset x; export -n x", "function", "outer", "unset", "outer", "unset")]
    [InlineData("unset x; export -n x", "eval", "outer", "unset", "outer", "unset")]
    [InlineData("unset x; declare +x x", "eval", "outer", "unset", "outer", "unset")]
    [InlineData("unset x; export x; export -n x", "function", "outer", "unset", "outer", "unset")]
    [InlineData("unset x; export x; export -n x", "eval", "outer", "unset", "outer", "unset")]
    [InlineData("unset x; readonly x; export -n x", "function", "outer", "unset", "outer", "unset")]
    [InlineData("unset x; readonly x; export -n x", "eval", "outer", "unset", "outer", "unset")]
    [InlineData("export -n x", "function", "prefix", "outer", "outer", "outer")]
    [InlineData("export -n x", "eval", "prefix", "outer", "outer", "outer")]
    [InlineData("export x; export -n x", "function", "prefix", "outer", "prefix", "prefix")]
    [InlineData("readonly x; export -n x", "function", "prefix", "outer", "prefix", "prefix")]
    [InlineData("export x; export -n x", "eval", "prefix", "outer", "outer", "outer")]
    [InlineData("local x=local; unset x; export -n x", "function", "unset", "outer", "outer", "outer")]
    public async Task ExportRemovalDistinguishesExposedCallersFromLivePrefixes(
        string body, string invocation, string inside, string insideExport, string outside, string outsideExport)
    {
        var observe = "; printf \"<%s>/\" \"${x-unset}\"; child";
        var script = "export x=outer; child(){ sh -c 'printf \"<%s>/\" \"${x-unset}\"'; }; "
            + (invocation == "function" ? "f(){ " + body + observe + "; }; x=prefix f"
                : "x=prefix eval '" + body + observe + "'") + observe;
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal("<" + inside + ">/<" + insideExport + ">/<" + outside + ">/<" + outsideExport + ">/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("f(){ (unset x; export -n x; child); child; }; x=prefix f")]
    [InlineData("x=prefix eval '(unset x; export -n x; child); child'")]
    [InlineData("f(){ y=temp eval '(unset x; export -n x; child); child'; }; x=prefix f")]
    [InlineData("f(){ x=prefix eval '(unset x; export -n x; child); child'; }; f")]
    [InlineData("f(){ x=prefix eval '(unset x; export x; export -n x; child); child'; }; f")]
    [InlineData("f(){ x=prefix eval 'captured=$(unset x; export -n x; child); printf %s \"$captured\"; child'; }; f")]
    [InlineData("f(){ x=prefix eval '{ unset x; export -n x; child; } | cat; child'; }; f")]
    public async Task ExposingACallerInAChildDoesNotCancelTheParentPrefix(string invocation)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "export x=outer; child(){ sh -c 'printf \"<%s>/\" \"${x-unset}\"'; }; " + invocation + "; child",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("<unset>/<prefix>/<outer>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("export x")]
    [InlineData("readonly x")]
    public async Task ValuelessAttributesInAScriptChildRestoreTheExposedCaller(string attribute)
    {
        var observe = "printf \"<%s>/\" \"${x-unset}\"";
        var result = await EvaluationTestHelper.RunAsync(
            "x=outer; f(){ x=prefix eval '(unset x; " + attribute + "; " + observe + "); "
            + observe + "'; }; f; " + observe);

        Assert.Equal("<outer>/<prefix>/<outer>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task SourceChildExportRemovalKeepsTheParentPrefixIsolated()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "export x=outer; child(){ sh -c 'printf \"<%s>/\" \"${x-unset}\"'; }; "
            + "f(){ x=prefix . /declaration; }; f; child",
            EndToEndHarness.BuildTree([("/declaration", "(unset x; export -n x; child); child")]));

        Assert.Equal("<unset>/<prefix>/<outer>/", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task SourceExportRemovalUpdatesTheBindingExposedByUnset()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "export x=outer; x=prefix . /declaration; printf '<%s>/' \"$x\"; sh -c 'printf \"<%s>\" \"${x-unset}\"'",
            EndToEndHarness.BuildTree([("/declaration", "unset x; readonly x; export -n x")]));

        Assert.Equal("<outer>/<unset>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local +x x=local", "child", "global")]
    [InlineData("local x=local; export -n x", "child", "global")]
    [InlineData("local x=local; unset x", "child", "global")]
    [InlineData("local x; unset x; export x", "child", "global")]
    [InlineData("local x=local; unset x; declare -x x", "child", "global")]
    [InlineData("local x; unset x; local -x x", "child", "global")]
    [InlineData("local x; unset x; export x", "(child)", "global")]
    [InlineData("local x; unset x; export x", "y=temp eval '(child)'", "global")]
    [InlineData("local x", "y=temp eval '(unset x; export x; child)'", "global")]
    [InlineData("local x", "y=temp eval '(g(){ unset x; export x; }; g; child)'", "global")]
    [InlineData("local x", "g(){ unset x; export x; child; }; g", "global")]
    [InlineData("local x", "g(){ unset x; export x; child; }; x=innerprefix g", "prefix")]
    [InlineData("local x", "g(){ local +x x=inner; unset x; export x; child; }; g", "prefix")]
    [InlineData("local x; unset x; export x=restored", "child", "restored")]
    [InlineData("local +x x=local; declare -gx x=changed", "child", "changed")]
    [InlineData("local +x x=local", "g(){ local x; child; }; g", "global")]
    [InlineData("local x", "g(){ local +x x=inner; child; }; g", "prefix")]
    [InlineData("local +x x=local", "child | cat", "global")]
    [InlineData("local +x x=local", "y=temp eval 'child'", "global")]
    [InlineData("local +x x=local", "y=temp eval '(child)'", "global")]
    [InlineData("local x", "child", "prefix")]
    [InlineData("local x=local", "child", "local")]
    public async Task UnexportedLocalsHideTheirSavedFunctionCallPrefix(string declaration, string invocation, string value)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "child(){ sh -c 'printf \"<%s>\" \"${x-unset}\"'; }; export x=global; f(){ "
            + declaration + "; " + invocation + "; }; x=prefix f", EndToEndHarness.BuildTree([]));

        Assert.Equal("<" + value + ">", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("x=global")]
    [InlineData("unset x")]
    public async Task ReexportingAnUnsetLocalDoesNotExposeItsCanceledPrefix(string setup)
    {
        var result = await EndToEndHarness.RunScriptAsync(setup
            + "; f(){ local x; unset x; export x; sh -c 'printf \"<%s>\" \"${x-unset}\"'; }; x=prefix f",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("<unset>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2: unassigned exports fall through to outer scopes; assigned arrays hide them without exporting.
    [Theory]
    [InlineData("local x=local", "", "global")]
    [InlineData("local x=local", "=changed", "changed")]
    [InlineData("local -x x=local", "=changed", "local")]
    [InlineData("local -x x", "", "global")]
    [InlineData("local -nx x", "", "global")]
    [InlineData("local -ax x", "", "global")]
    [InlineData("local -ax x=()", "", "unset")]
    [InlineData("local -ax x=(local)", "", "unset")]
    [InlineData("local -Ax x", "", "global")]
    [InlineData("local -Ax x=([key]=local)", "", "unset")]
    [InlineData("local -ax x; x+=()", "", "unset")]
    [InlineData("local -Ax x; x+=()", "", "unset")]
    [InlineData("local -ax x; declare -a x+=()", "", "unset")]
    [InlineData("local -Ax x; declare -A x+=()", "", "unset")]
    [InlineData("local -x x=local; export -n x", "", "global")]
    [InlineData("local -x x=local; unset x", "", "global")]
    public async Task HiddenGlobalExportsRespectLocalValueAndAttributePrecedence(
        string localDeclaration, string initializer, string childValue)
    {
        var result = await EndToEndHarness.RunScriptAsync("x=global; f(){ " + localDeclaration
            + "; declare -gx x" + initializer + "; sh -c 'printf \"<%s>\" \"${x-unset}\"'; }; f",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("<" + childValue + ">", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("child", "global")]
    [InlineData("other=prefix child", "global")]
    [InlineData("x=prefix child", "prefix")]
    [InlineData("g(){ local x=inner; child; }; g", "global")]
    [InlineData("child | cat", "global")]
    [InlineData("(child)", "global")]
    [InlineData("eval 'child'", "global")]
    [InlineData("x=prefix eval 'export -n x; child'", "global")]
    public async Task HiddenGlobalExportsReachNestedCommandEnvironments(string invocation, string childValue)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "child(){ sh -c 'printf \"<%s>\" \"${x-unset}\"'; }; x=global; declare -n r=x; "
            + "f(){ local x=local; declare -gx r; " + invocation + "; }; f", EndToEndHarness.BuildTree([]));

        Assert.Equal("<" + childValue + ">", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExportedOuterLocalsRetainTheirBytesWhenShadowedByAnUnexportedLocal()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "f(){ local -x x=$'\\377'; g(){ local +x x=inner; sh -c 'printf %s \"$x\"'; }; g; }; f",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(new byte[] { 0xff }, result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("t=old; declare -n r=t; declare -x +n r", "old")]
    [InlineData("t=old; declare -n r=s s=t; declare -x +n r=value", "value")]
    [InlineData("export t=old; declare -n r=s s=t; declare +xn r", "unset")]
    [InlineData("t=old; declare -n r=t; f(){ local t=local; declare -gx +n r=value; }; f", "value")]
    [InlineData("t=old; declare -n r=s s=t; f(){ local s=local; declare -gax +n r; }; f", "unset")]
    public async Task NamerefRemovalKeepsExportChangesOnTheFormerReferent(string declaration, string value)
    {
        var result = await EndToEndHarness.RunScriptAsync(declaration
            + "; sh -c 'printf \"<%s>/<%s>/<%s>\" \"${r-unset}\" \"${s-unset}\" \"${t-unset}\"'",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("<unset>/<unset>/<" + value + ">", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -nx r=t", false, "t", "unset")]
    [InlineData("declare -nrx r=t", false, "t", "unset")]
    [InlineData("export r=t; declare -n r", false, "t", "unset")]
    [InlineData("local -nx r=t", true, "t", "unset")]
    [InlineData("declare -nx r", false, "unset", "unset")]
    [InlineData("declare -n r=t; export r", false, "unset", "hello")]
    [InlineData("declare -n r=t; declare -x r", false, "unset", "hello")]
    [InlineData("t=$'\\377'; declare -nx r=t", false, "t", "unset")]
    public async Task ExportedNamerefsPassTheirTargetTextToChildShells(string declaration, bool local, string reference, string target)
    {
        var script = "t=hello; " + declaration + "; sh -c 'printf \"<%s>/<%s>\" \"${r-unset}\" \"${t-unset}\"'";
        if (local) script = "f(){ " + script + "; }; f";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal("<" + reference + ">/<" + target + ">", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.ExitCode);
    }

    // Git Bash 5.2.37; companion declare -p probes supply the expected final attributes.
    [Theory]
    [InlineData("export x=outer; x=temp export -n x=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<0> value=<new> after=<unset>\n", "", true, false)]
    [InlineData("x=outer; x=temp export -n x=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<0> value=<new> after=<unset>\n", "", false, false)]
    [InlineData("export x=outer; x=temp declare +x x=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<0> value=<outer> after=<unset>\n", "", true, false)]
    [InlineData("export x=outer; x=temp export -n x; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<0> value=<outer> after=<unset>\n", "", true, false)]
    [InlineData("export x=old; readonly x; export -n x=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<1> value=<old> after=<unset>\n", "x: readonly variable\n", false, true)]
    [InlineData("export x=old; readonly x; declare +x x=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<1> value=<old> after=<unset>\n", "declare: x: readonly variable\n", true, true)]
    [InlineData("x=old; readonly x; export x=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<1> value=<old> after=<unset>\n", "x: readonly variable\n", true, true)]
    [InlineData("x=old; readonly x; readonly x=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<1> value=<old> after=<unset>\n", "x: readonly variable\n", false, true)]
    [InlineData("export x=old; readonly x; export -n x=new after=kept; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<1> value=<old> after=<kept>\n", "x: readonly variable\n", false, true)]
    [InlineData("export x=outer; f() { export -n x=new; printf 'inside=<%s>\\n' \"$x\"; }; x=temp f; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "inside=<new>\nstatus=<0> value=<outer> after=<unset>\n", "", true, false)]
    [InlineData("export x=outer; f() { x=temp export -n x=new; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "inside=<new>\nstatus=<0> value=<new> after=<unset>\n", "", true, false)]
    [InlineData("export x=outer; f() { local x=local; x=temp export -n x=new; printf 'inside=<%s>\\n' \"$x\"; }; f; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "inside=<new>\nstatus=<0> value=<outer> after=<unset>\n", "", true, false)]
    [InlineData("export x=outer; declare -n ref=x; x=temp export -n ref=new; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<0> value=<outer> after=<unset>\n", "", true, false)]
    [InlineData("export x=outer; x=temp export -n x+=tail; printf 'status=<%s> value=<%s> after=<%s>\\n' \"$?\" \"$x\" \"${after-unset}\"", "status=<0> value=<outertail> after=<unset>\n", "", true, false)]
    [InlineData("x=old; readonly x; declare -n ref=x; export -n ref=new; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "status=<1> value=<old> second=<unset>\n", "x: readonly variable\n", false, true)]
    [InlineData("x=old; readonly x; declare -n ref=x; export ref=new; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "status=<1> value=<old> second=<unset>\n", "x: readonly variable\n", true, true)]
    [InlineData("x=old; readonly x; declare -n ref=x; readonly ref=new; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "status=<1> value=<old> second=<unset>\n", "x: readonly variable\n", false, true)]
    [InlineData("export x=outer; x=temp export -n x=first x+=tail; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "status=<0> value=<firsttail> second=<unset>\n", "", true, false)]
    [InlineData("export x=outer; x=temp export -n x+=\"$x\"; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "status=<0> value=<outerouter> second=<unset>\n", "", true, false)]
    [InlineData("x=(outer extra); x=temp export -n x+=tail; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "status=<0> value=<outertail> second=<extra>\n", "", false, false)]
    [InlineData("x=outer; declare -n ref=x; ref=temp export -n ref=new; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "status=<0> value=<outer> second=<unset>\n", "", false, false)]
    [InlineData("export x=old; readonly x; (export -n x=new); printf 'child=<%s>\\n' \"$?\"; printf 'status=<%s> value=<%s> second=<%s>\\n' \"$?\" \"$x\" \"${x[1]-unset}\"", "child=<1>\nstatus=<0> value=<old> second=<unset>\n", "x: readonly variable\n", true, true)]
    public async Task Evaluation_ExportWrites_KeepValueAndAttributeEffectsSeparate(
        string script, string expectedOutput, string expectedError, bool expectedExport, bool expectedReadonly)
    {
        var evaluation = new ShellEvaluation(Parser.Parse(script));
        var host = EndToEndHarness.WithMemoryBudget(EvaluationTestHelper.BuildDefaultTree(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(expectedOutput, System.Text.Encoding.UTF8.GetString(host.StandardOutput.Content.Span));
        Assert.Equal(expectedError, System.Text.Encoding.UTF8.GetString(host.StandardError.Content.Span));
        Assert.Equal(expectedExport, evaluation.ExportedVariables.Contains("x"));
        Assert.Equal(expectedReadonly, evaluation.ReadonlyVariables.Contains("x"));
    }

    [Theory]
    [InlineData("ref=new")]
    [InlineData("ref+=tail")]
    public async Task Evaluation_ExportClearThroughPrefix_DoesNotRebindPersistentNameref(string operand)
    {
        var evaluation = new ShellEvaluation(Parser.Parse(
            "x=outer; declare -n ref=x; ref=temp export -n " + operand));
        var host = EndToEndHarness.WithMemoryBudget(EvaluationTestHelper.BuildDefaultTree(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(ShellValue.FromText("x"), evaluation.Namerefs["ref"]);
        Assert.Equal(ShellValue.FromText("outer"), evaluation.Variables["x"]);
        Assert.False(evaluation.Variables.ContainsKey("new"));
        Assert.False(evaluation.Variables.ContainsKey("temp"));
        Assert.DoesNotContain("x", evaluation.ExportedVariables);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
    }
}

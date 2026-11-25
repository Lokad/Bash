using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class NamerefAssignmentFailureTestsEndToEnd
{
    private const string CircularDeclarationWarnings = "line 1: warning: s: circular name reference\nline 1: warning: s: circular name reference\n";
    private const string CircularFallbackWarnings = CircularDeclarationWarnings
        + "line 1: warning: s: circular name reference\nline 1: warning: r: circular name reference\n";
    private const string LoadedCircularFallbackWarnings = CircularDeclarationWarnings
        + "/script: line 1: warning: s: circular name reference\n/script: line 1: warning: r: circular name reference\n";

    [Fact]
    public async Task PrintfPublishesFormatCountBeforeRejectingAnUnsetElementDestination()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n ref; i=0; j=0; mark=unset\n"
            + "printf -v 'ref[i++]' '%s%n' \"data$((j++))\" mark\n"
            + "printf '<%s|%s|%s|%s>' \"$?\" \"$i\" \"$j\" \"$mark\"; declare -p ref");

        Assert.Equal("<1|1|1|5>declare -n ref\n", result.StdOut);
        Assert.Equal("line 2: printf: `': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("read -r 'ref[i++]' <<< 'data'", "<1|1>", "read: `': not a valid identifier")]
    [InlineData("read -r 'ref[-1]' <<< 'data'", "<1|0>", "ref[-1]: bad array subscript")]
    [InlineData("printf -v 'ref[@]' '%s' data", "<1|0>", "ref[@]: bad array subscript")]
    public async Task BuiltinElementWritesCheckTheSelectorBeforeRejectingAnUnsetDestination(
        string command, string output, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n ref; i=0\n" + command + "\nprintf '<%s|%s>' \"$?\" \"$i\"; declare -p ref");

        Assert.Equal(output + "declare -n ref\n", result.StdOut);
        Assert.Equal("line 2: " + diagnostic + "\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("printf -v 'ref[${ref:=a},2]' '%s' data")]
    [InlineData("read -r 'ref[${ref:=a},2]' <<< 'data'")]
    public async Task BuiltinElementWritesCanUseTheTargetEstablishedByTheSelector(string command)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(0 1 2); declare -n ref\n" + command
            + "\nprintf '<%s|%s>' \"$?\" \"${a[2]}\"; declare -p ref");

        Assert.Equal("<0|data>declare -n ref=\"a\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task UnsetElementAssignmentDiscardsTheTestedListEvenWithErrexit()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n ref; i=0; j=0; set -e\n"
            + "ref[i++]=data$((j++)) || printf recovered\n"
            + "printf '<%s|%s|%s|%s>\\n' \"$?\" \"${PIPESTATUS[*]}\" \"$i\" \"$j\"; declare -p ref");

        Assert.Equal("<1|1|1|1>\ndeclare -n ref\n", result.StdOut);
        Assert.Equal("line 2: `': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("ref=tail tail")]
    public async Task UnsetElementAssignmentRestoresCallerBindings(string locals)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "i=0; j=0; ref=outer\n"
            + "f(){ local -n " + locals + "; local PIPESTATUS=(0);\n"
            + "ref[i++]=data$((j++))\nprintf inside; }\nf\n"
            + "printf '<%s|%s|%s|%s|%s>\\n' \"$?\" \"${PIPESTATUS[*]}\" \"$i\" \"$j\" \"$ref\"");

        Assert.Equal("<1|0|1|1|outer>\n", result.StdOut);
        Assert.Equal("line 3: `': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("eval", false)]
    [InlineData("eval", true)]
    [InlineData("source", false)]
    [InlineData("source", true)]
    public async Task LoadedUnsetElementAssignmentReturnsFailureToItsCaller(string reader, bool errexit)
    {
        const string body = "ref[i++]=data$((j++)); printf inside";
        var invocation = reader == "eval" ? "eval '" + body + "'" : "source /script";
        var result = await EndToEndHarness.RunScriptAsync(
            "declare -n ref; i=0; j=0" + (errexit ? "; set -e" : "") + "\n"
            + invocation + "\nprintf '<%s|%s|%s>\\n' \"$?\" \"$i\" \"$j\"",
            EndToEndHarness.BuildTree([("/script", body)]));

        Assert.Equal(errexit ? "" : "<1|1|1>\n", result.StdOut);
        Assert.Equal((reader == "eval" ? "line 2" : "/script: line 1") + ": `': not a valid identifier\n",
            result.StdErr);
        Assert.Equal(errexit ? 1 : 0, result.ExitCode);
    }

    [Theory]
    [InlineData("counter=0", "counter", "${ref:=counter},2", "=", "counter", "([0]=\"0\" [2]=\"data\")", "")]
    [InlineData("values=(0 0 0)", "values", "${ref:=values},2", "=", "values", "([0]=\"0\" [1]=\"0\" [2]=\"data\")", "")]
    [InlineData("values=(0 0 0)", "values", "${ref:=values},2", "+=", "values", "([0]=\"0\" [1]=\"0\" [2]=\"0data\")", "")]
    [InlineData("values=(0 0 0)", "values", "${ref:=values},-1", "=", "values", "([0]=\"0\" [1]=\"0\" [2]=\"0\")",
        "line 3: ref[${ref:=values},-1]: bad array subscript\n")]
    [InlineData("values=(0 0 0)", "values[1]", "${ref:=\"values[1]\"},2", "=", "values", "([0]=\"0\" [1]=\"0\" [2]=\"0\")",
        "line 3: `values[1]': not a valid identifier\n")]
    public async Task DirectElementAssignmentUsesTheTargetInitializedByItsSelector(
        string setup, string target, string selector, string operation, string array, string elements, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(
            setup + "\ndeclare -n ref\nref[" + selector + "]" + operation + "data\n"
            + "printf '<%s>\\n' \"$?\"; declare -p ref " + array);

        var status = diagnostic.Length == 0 ? 0 : 1;
        Assert.Equal($"<{status}>\ndeclare -n ref=\"{target}\"\ndeclare -a {array}={elements}\n", result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target")]
    [InlineData("r+=target")]
    [InlineData(": ${r:=target}")]
    [InlineData("h(){ r=target; }; h")]
    public async Task InnerUnsetLocalsPreserveFallbackWritesInTheSourceFrame(string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n s; f(){ local -n r=s; g(){ local -n s; " + assignment
            + "; printf '<%s>/' \"${s-unset}\"; }; g; printf '<%s>/' \"${s-unset}\"; }; f; printf '<%s>' \"${s-unset}\"");

        Assert.Equal("<unset>/<target>/<unset>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target")]
    [InlineData("r+=target")]
    [InlineData(": ${r:=target}")]
    public async Task OuterUnsetLocalsRetainCallerFallbackPriority(string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "s=; g(){ local -n s; f(){ local -n r=s; " + assignment
            + "; printf '<%s>/' \"${s-unset}\"; }; f; printf '<%s>/' \"${s-unset}\"; }; g; printf '<%s>' \"$s\"");

        Assert.Equal("<unset>/<unset>/<target>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target", 1)]
    [InlineData(": ${r:=target}", 2)]
    public async Task InvalidHiddenArraySubscriptsAbortTheScript(string assignment, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old other); declare -n s='a[-3]'; f(){ local -n r=s s; "
            + assignment + "; printf unreachable; }; f; printf unreachable");

        Assert.Empty(result.StdOut);
        Assert.Equal("line 1: a[-3]: bad array subscript\n", result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("eval", "r=target")]
    [InlineData("eval", ": ${r:=target}")]
    [InlineData("source", "r=target")]
    [InlineData("source", ": ${r:=target}")]
    public async Task ScriptBoundariesRecoverInvalidHiddenArraySubscripts(string command, string assignment)
    {
        var body = assignment + "; printf unreachable";
        var invocation = command == "eval" ? "eval '" + body + "'" : "source /script";
        var result = await EndToEndHarness.RunScriptAsync(
            "a=(old other); declare -n s='a[-3]'; f(){ local -n r=s s; "
            + invocation + "; printf '<%s>' \"$?\"; }; f",
            EndToEndHarness.BuildTree([("/script", body)]));

        Assert.Equal("<1>", result.StdOut);
        var source = command == "source" ? "/script: " : "";
        Assert.Equal(source + "line 1: a[-3]: bad array subscript\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "r=target", "", "target")]
    [InlineData("r=old;", "r+=target", "", "oldtarget")]
    [InlineData("", "printf '<%s>/' \"${r:=target}\"", "<target>/", "target")]
    [InlineData("q=old; declare -n r=q;", "r=target", "", "target")]
    public async Task HiddenCircularReferencesWarnAndWriteTheGlobalSourceBinding(
        string setup, string assignment, string output, string value)
    {
        var result = await EvaluationTestHelper.RunAsync(
            setup + " declare -n s=t t=s; f(){ local -n r=s s; " + assignment
            + "; printf '<%s>/' \"${s-unset}\"; }; f; printf '<%s>' \"$r\"");

        Assert.Equal(output + "<unset>/<" + value + ">", result.StdOut);
        Assert.Equal(CircularFallbackWarnings, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target", "<target>/<old>")]
    [InlineData("r+=target", "<localtarget>/<old>")]
    [InlineData("printf '<%s>/' \"${r:=target}\"", "<target>/<target>/<old>")]
    [InlineData("g(){ local q=inner; r=target; printf '<%s>/' \"$q\"; }; g", "<target>/<local>/<old>")]
    public async Task CircularRetriesFollowGlobalNamerefTargetsInTheActiveScope(string assignment, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "q=old; declare -n r=q s=t t=s; f(){ local q=local; local -n r=s s; " + assignment
            + "; printf '<%s>/' \"$q\"; }; f; printf '<%s>' \"$q\"");

        Assert.Equal(output, result.StdOut);
        Assert.Equal(CircularFallbackWarnings, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target", 1, "line 1: q: readonly variable\n")]
    [InlineData(": ${r:=target}", 2, "line 1: q: readonly variable\n")]
    public async Task CircularRetriesRespectReadonlyLocalReferents(string assignment, int exitCode, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "q=old; declare -n r=q s=t t=s; f(){ local -r q=local; local -n r=s s; "
            + assignment + "; printf unreachable; }; f; printf unreachable");

        Assert.Empty(result.StdOut);
        Assert.Equal(CircularFallbackWarnings + diagnostic, result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("", "r=target", 1)]
    [InlineData("", "r+=target", 1)]
    [InlineData("", ": ${r:=target}", 2)]
    [InlineData("local -n q;", "r=target", 1)]
    [InlineData("local -n q;", ": ${r:=target}", 2)]
    public async Task CircularRetriesRejectIndirectUnsetReferents(string declaration, string assignment, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "target=kept; declare -n r=q q s=t t=s; f(){ " + declaration + " local -n r=s s; "
            + assignment + "; printf unreachable; }; f; printf unreachable");

        Assert.Empty(result.StdOut);
        Assert.Equal(CircularFallbackWarnings, result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("r='bad name'", "bad name", 1)]
    [InlineData("r=", "", 1)]
    [InlineData(": ${r:=bad name}", "bad name", 2)]
    public async Task CircularRetriesDiagnoseInvalidDirectUnsetInitializers(string assignment, string value, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r s=t t=s; f(){ local -n r=s s; " + assignment
            + "; printf unreachable; }; f; printf unreachable");

        Assert.Empty(result.StdOut);
        Assert.Equal(CircularFallbackWarnings + "line 1: `" + value + "': not a valid identifier\n", result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("eval", "r=target")]
    [InlineData("eval", ": ${r:=target}")]
    [InlineData("source", "r=target")]
    [InlineData("source", ": ${r:=target}")]
    public async Task ScriptBoundariesRecoverCircularUnsetRetriesWithoutRebinding(string command, string assignment)
    {
        var body = assignment + "; printf unreachable";
        var invocation = command == "eval" ? "eval '" + body + "'" : "source /script";
        var result = await EndToEndHarness.RunScriptAsync(
            "target=kept; declare -n r=q q s=t t=s; f(){ local -n r=s s; " + invocation
            + "; printf '<%s>/' \"$?\"; }; f; printf '<%s>' \"${q-unset}\"",
            EndToEndHarness.BuildTree([("/script", body)]));

        Assert.Equal("<1>/<unset>", result.StdOut);
        Assert.Equal(command == "source" ? LoadedCircularFallbackWarnings : CircularFallbackWarnings, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target")]
    [InlineData("r+=target")]
    [InlineData(": ${r:=target}")]
    public async Task CircularRetriesCanInitializeTheDirectUnsetSource(string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "target=kept; declare -n r s=t t=s; f(){ local -n r=s s; " + assignment
            + "; }; f; printf '<%s>/<%s>' \"$r\" \"${!r}\"");

        Assert.Equal("<kept>/<target>", result.StdOut);
        Assert.Equal(CircularFallbackWarnings, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("q=(old other)", "q[1]", "", "r=target", 1)]
    [InlineData("q=(old other)", "q[1]", "local -a q=(local extra);", ": ${r:=target}", 2)]
    [InlineData("declare -A q=([key]=old)", "q[key]", "", "r=target", 1)]
    [InlineData("declare -A q=([key]=old)", "q[key]", "", ": ${r:=target}", 2)]
    public async Task CircularRetriesRejectArrayElementReferents(
        string setup, string reference, string declaration, string assignment, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(
            setup + "; declare -n r='" + reference + "' s=t t=s; f(){ " + declaration
            + " local -n r=s s; " + assignment + "; printf unreachable; }; f; printf unreachable");

        Assert.Empty(result.StdOut);
        Assert.Equal(CircularFallbackWarnings, result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("eval", "r=target")]
    [InlineData("eval", ": ${r:=target}")]
    [InlineData("source", "r=target")]
    [InlineData("source", ": ${r:=target}")]
    public async Task ScriptBoundariesRecoverCircularElementRetriesWithoutSelectorEffectsOrWrites(
        string command, string assignment)
    {
        var body = assignment + "; printf unreachable";
        var invocation = command == "eval" ? "eval '" + body + "'" : "source /script";
        var result = await EndToEndHarness.RunScriptAsync(
            "q=(old other); i=0; declare -n r='q[i++]' s=t t=s; f(){ local -n r=s s; " + invocation
            + "; printf '<%s>/<%s>/' \"$?\" \"$i\"; }; f; printf '<%s>' \"${q[@]}\"",
            EndToEndHarness.BuildTree([("/script", body)]));

        Assert.Equal("<1>/<0>/<old><other>", result.StdOut);
        Assert.Equal(command == "source" ? LoadedCircularFallbackWarnings : CircularFallbackWarnings, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target", "<target><extra>")]
    [InlineData("r+=target", "<oldtarget><extra>")]
    public async Task CircularRetriesCanWriteDirectGlobalArrays(string assignment, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "r=(old extra); declare -n s=t t=s; f(){ local -n r=s s; "
            + assignment + "; }; f; printf '<%s>' \"${r[@]}\"");

        Assert.Equal(output, result.StdOut);
        Assert.Equal(CircularFallbackWarnings, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=(zero one); declare -n s='a[1]'", "1")]
    [InlineData("declare -A a=([0]=zero [key]=one); declare -n s='a[key]'", "key")]
    public async Task HiddenArrayElementDefaultsEmitElementZero(string setup, string subscript)
    {
        var result = await EvaluationTestHelper.RunAsync(
            setup + "; f(){ local -n r=s s; printf '<%s>/' \"${r:=new}\"; }; f; printf '<%s>' \"${a[" + subscript + "]}\"");

        Assert.Equal("<zero>/<new>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HiddenArrayElementDefaultRetainsRawElementZeroBytes()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "a=($'\\xff' old); declare -n s='a[1]'; f(){ local -n r=s s; printf '%s' \"${r:=new}\"; }; f",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(new byte[] { 0xff }, result.Host.StandardOutput.Content.ToArray());
        Assert.True(result.Host.StandardError.Content.IsEmpty);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HiddenArrayElementDefaultWithoutElementZeroSafelyEmitsEmpty()
    {
        // Bash 5.2 crashes when this fallback array lacks element zero; preserve the write and emit empty.
        var result = await EvaluationTestHelper.RunAsync(
            "a=([1]=old); declare -n s='a[1]'; f(){ local -n r=s s; printf '<%s>/' \"${r:=new}\"; }; f; printf '<%s>' \"${a[1]}\"");

        Assert.Equal("<>/<new>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n r=s s", "r=target", "")]
    [InlineData("local -n r=s s", "r+=target", "")]
    [InlineData("local -n r=s s", "printf '<%s>/' \"${r:=target}\"", "<target>/")]
    [InlineData("local -nr r=s; local -n s", "r=target", "")]
    [InlineData("local -nr r=s; local -n s", "printf '<%s>/' \"${r:=target}\"", "<target>/")]
    [InlineData("local -n r=s s", "g(){ r=target; }; g", "")]
    [InlineData("local -n r=s s", "(r=target)", "")]
    [InlineData("local -n r=s s", "eval 'r=target'", "")]
    [InlineData("local -n r=s s; i=0", "r=$((i++)); printf '<%s>/' \"$i\"", "<1>/")]
    public async Task LocalNamerefAssignmentsLeaveEntirelyUnsetChainsUnchanged(
        string declaration, string assignment, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n s; f(){ " + declaration + "; " + assignment
            + "; printf '<%s>/<%s>/<%s>/' \"$?\" \"${r-unset}\" \"${s-unset}\"; }; f; printf end");

        Assert.Equal(output + "<0>/<unset>/<unset>/end", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("s=global; f(){ local -n r=s s; r=target; printf '<%s>/' \"${s-unset}\"; }; f; printf '<%s>' \"$s\"", "<unset>/<target>")]
    [InlineData("s=global; f(){ local -n r=s s; r+=target; }; f; printf '<%s>' \"$s\"", "<globaltarget>")]
    [InlineData("s=global; f(){ local -n r=s s; printf '<%s>/' \"${r:=target}\"; }; f; printf '<%s>' \"$s\"", "<target>/<target>")]
    [InlineData("declare -n s; f(){ local -n r=s; r=target; printf '<%s>/' \"$s\"; }; f; printf '<%s>' \"${s-unset}\"", "<target>/<unset>")]
    [InlineData("declare -nr s; f(){ local -n r=s; r=target; printf '<%s>/' \"$s\"; }; f; printf '<%s>' \"${s-unset}\"", "<target>/<unset>")]
    [InlineData("declare -nx s; f(){ local -n r=s; r=target; sh -c 'printf \"<%s>/\" \"${s-unset}\"'; }; f; printf '<%s>' \"${s-unset}\"", "<unset>/<unset>")]
    [InlineData("declare -n s; f(){ local -n r=s; g(){ r=target; }; g; printf '<%s>/' \"$s\"; }; f; printf '<%s>' \"${s-unset}\"", "<target>/<unset>")]
    [InlineData("s=global; g(){ local s=caller; f(){ local -n r=s s; r=target; }; f; printf '<%s>/' \"$s\"; }; g; printf '<%s>' \"$s\"", "<target>/<global>")]
    [InlineData("s=(old extra); f(){ local -n r=s s; r+=tail; }; f; printf '<%s>' \"${s[@]}\"", "<oldtail><extra>")]
    [InlineData("t=old; declare -n s=t; f(){ local t=local; local -n r=s s; r=target; printf '<%s>/' \"$t\"; }; f; printf '<%s>' \"$t\"", "<local>/<target>")]
    public async Task LocalNamerefFallbackWritesRespectCallerAndLocalLifetimes(string script, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "r=target", 1)]
    [InlineData("local -n s;", "r=target", 1)]
    [InlineData("local -n s;", ": ${r:=target}", 2)]
    public async Task GlobalNamerefSourcesStillRejectUnsetChainsInsideFunctions(
        string declaration, string assignment, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r=s s; f(){ " + declaration + assignment + "; printf unreachable; }; f; printf unreachable");

        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("r=target", 1, "line 1: s: readonly variable\n")]
    [InlineData(": ${r:=target}", 2, "line 1: s: readonly variable\n")]
    public async Task LocalNamerefFallbackWritesEnforceHiddenReadonlyAttributes(
        string assignment, int exitCode, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "s=old; f(){ local -n r=s s; declare -gr s; " + assignment + "; printf unreachable; }; f; printf unreachable");

        Assert.Empty(result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("eval", "declare -n r", "line 1: `bad name': not a valid identifier\n")]
    [InlineData("source", "declare -n r", "/script: line 1: `bad name': not a valid identifier\n")]
    [InlineData("eval", "declare -n r=s s", "")]
    [InlineData("source", "declare -n r=s s", "")]
    public async Task ScriptBoundariesRecoverNamerefDefaultFailures(
        string command, string declaration, string error)
    {
        // Bash 5.2 eval/source return 1; the remaining script is skipped and the caller can recover.
        var body = declaration + "; : ${r:=bad name}; printf unreachable";
        var invocation = command == "eval" ? "eval '" + body + "'" : "source /script";
        var result = await EndToEndHarness.RunScriptAsync(
            invocation + " || printf '<%s>/' \"$?\"; printf end",
            EndToEndHarness.BuildTree([("/script", body)]));

        Assert.Equal("<1>/end", result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("+n", "=")]
    [InlineData("+n", "+=")]
    [InlineData("-x +n", "=")]
    public async Task ReadonlyScalarNamerefRemovalReportsFailureAndVisitsLaterOperands(string flags, string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "readonly x=old; declare -n r=x; declare " + flags + " r" + assignment
            + "new later=kept; printf '%s/<%s>/<%s>/<%s>' \"$?\" \"$x\" \"$r\" \"$later\"");

        Assert.Equal("1/<old>/<old>/<kept>", result.StdOut);
        Assert.Equal("line 1: declare: r: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.21 (Linux) and 5.2.37 (Git Bash); failure handling depends on the assignment context.
    [Theory]
    [InlineData("declare -n ref; ref='bad name'; printf 'after=<%s>\\n' \"$?\"", "", "line 1: `bad name': not a valid identifier\n", 1)]
    [InlineData("declare -n ref; ref=''; printf 'after=<%s>\\n' \"$?\"", "", "line 1: `': not a valid identifier\n", 1)]
    [InlineData("target=value; declare -n ref; ref+=target; printf '<%s>\\n' \"$ref\"", "<value>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; first=target; printf 'after=<%s>\\n' \"$?\"", "", "", 1)]
    [InlineData("target=value; declare -n first=second second; first+=target; printf 'after=<%s>\\n' \"$?\"", "", "", 1)]
    [InlineData("declare -n ref; declare ref='bad name'; printf 'after=<%s>\\n' \"$?\"", "after=<1>\n", "line 1: declare: `bad name': not a valid identifier\n", 0)]
    [InlineData("declare -n ref; export ref='bad name'; printf 'after=<%s>\\n' \"$?\"", "after=<1>\n", "line 1: export: `bad name': not a valid identifier\nline 1: export: `': not a valid identifier\n", 0)]
    [InlineData("target=value; declare -n first=second second; declare -g first=target; printf 'after=<%s>\\n' \"$?\"", "after=<0>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; declare first=target; printf 'after=<%s>\\n' \"$?\"", "after=<0>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; export first=target; printf 'after=<%s>\\n' \"$?\"", "after=<1>\n", "line 1: export: `': not a valid identifier\n", 0)]
    [InlineData("target=value; declare -n first=second second; first=target true; printf 'after=<%s>\\n' \"$?\"", "after=<0>\n", "", 0)]
    [InlineData("declare -n ref; printf '<%s>\\n' \"${ref:=bad name}\"; printf 'after=<%s>\\n' \"$?\"", "", "line 1: `bad name': not a valid identifier\n", 2)]
    [InlineData("target=value; declare -n first=second second; printf '<%s>\\n' \"${first:=target}\"; printf 'after=<%s>\\n' \"$?\"", "", "", 2)]
    [InlineData("declare -n ref; ref='bad name' || printf recovered; printf after", "", "line 1: `bad name': not a valid identifier\n", 1)]
    [InlineData("target=value; declare -n first=second second; (first=target; printf child); printf 'parent=<%s>\\n' \"$?\"", "parent=<1>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; out=$(first=target; printf child); printf 'status=<%s>:<%s>\\n' \"$?\" \"$out\"", "status=<1>:<>\n", "", 0)]
    [InlineData("declare -nr ref; ref='bad name'; printf after", "", "line 1: `bad name': not a valid identifier\n", 1)]
    [InlineData("target=value; declare -n first=second; first=target; printf '<%s>:<%s>\\n' \"$first\" \"$second\"", "<target>:<target>\n", "", 0)]
    [InlineData("declare -n first=second second; i=0; first=$((i++)); printf 'after=<%s>\\n' \"$i\"", "", "", 1)]
    [InlineData("target=value; declare -n ref; printf '<%s>\\n' \"${ref:=target}\"; printf '<%s>\\n' \"$ref\"", "<target>\n<value>\n", "", 0)]
    [InlineData("target=value; declare -nr ref; ref=target; printf after", "", "line 1: ref: readonly variable\n", 1)]
    [InlineData("declare -nr ref; declare ref='bad name'; printf 'status=<%s>\\n' \"$?\"", "status=<1>\n", "line 1: declare: ref: readonly variable\n", 0)]
    [InlineData("declare -nr ref; printf '<%s>\\n' \"${ref:=bad name}\"; printf after", "", "line 1: `bad name': not a valid identifier\n", 2)]
    [InlineData("declare -nr first=second; declare -n second; first=target; printf after", "", "", 1)]
    [InlineData("declare -n ref; (printf '<%s>\\n' \"${ref:=bad name}\"; printf child); printf 'parent=<%s>\\n' \"$?\"", "parent=<2>\n", "line 1: `bad name': not a valid identifier\n", 0)]
    [InlineData("declare -n ref; declare ref='bad name' after=kept; printf '<%s>:<%s>\\n' \"$?\" \"$after\"", "<1>:<kept>\n", "line 1: declare: `bad name': not a valid identifier\n", 0)]
    [InlineData("declare -n ref; export -n ref='bad name'; printf 'status=<%s>\\n' \"$?\"", "status=<1>\n", "line 1: export: `bad name': not a valid identifier\n", 0)]
    [InlineData("target=value; declare -n first=second second; export -n first=target; printf 'status=<%s>:<%s>:<%s>\\n' \"$?\" \"$first\" \"$second\"", "status=<1>:<>:<>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; declare -x first=target; printf 'status=<%s>:<%s>:<%s>\\n' \"$?\" \"$first\" \"$second\"", "status=<0>:<>:<>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; declare +n first=target; printf 'status=<%s>:<%s>:<%s>\\n' \"$?\" \"$first\" \"$second\"", "status=<0>:<>:<>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; readonly first=target; printf 'status=<%s>:<%s>:<%s>\\n' \"$?\" \"$first\" \"$second\"", "status=<1>:<>:<>\n", "line 1: readonly: `': not a valid identifier\n", 0)]
    [InlineData("declare -n ref; readonly ref='bad name'; printf 'status=<%s>\\n' \"$?\"", "status=<1>\n", "line 1: readonly: `bad name': not a valid identifier\nline 1: readonly: `': not a valid identifier\n", 0)]
    [InlineData("target=value; declare -n first=second second; declare -g first=target; printf 'status=<%s>:<%s>:<%s>\\n' \"$?\" \"$first\" \"$second\"", "status=<0>:<>:<>\n", "", 0)]
    [InlineData("target=value; declare -n first=second second; declare first=target; printf 'status=<%s>:<%s>:<%s>\\n' \"$?\" \"$first\" \"$second\"", "status=<0>:<>:<>\n", "", 0)]
    public async Task Evaluation_NamerefAssignments_PreserveContextSpecificFailures(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UnsetNamerefChain_FailsAfterRhsWithoutRebinding()
    {
        var evaluation = new ShellEvaluation(Parser.Parse(
            "declare -n first=second second; i=0; first=$((i++)); after=unreachable"));
        var host = EndToEndHarness.WithMemoryBudget(EvaluationTestHelper.BuildDefaultTree(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(1, evaluation.LastExitCode);
        Assert.Equal(ShellValue.FromText("1"), evaluation.Variables["i"]);
        Assert.False(evaluation.Variables.ContainsKey("after"));
        Assert.Equal(ShellValue.FromText("second"), evaluation.Namerefs["first"]);
        Assert.Equal(ShellValue.Empty, evaluation.Namerefs["second"]);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
    }
}

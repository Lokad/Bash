using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ScopedEvaluationTestsEndToEnd
{
    // Bash 5.2.37: redirection writes reach in-process commands after preparing prefix bindings.
    [Theory]
    [InlineData("x=1; f() { printf '%s' \"$x\"; }; A=1 f <<<\"$((x=2))\"; printf '/%s' \"$x\"", "2/2")]
    [InlineData("x=1; f() { printf '%s' \"$x\"; }; x=3 f <<<\"$((x=1))\"; printf '/%s' \"$x\"", "1/1")]
    [InlineData("unset x; f() { printf '<%s>/<%s>' \"$A\" \"$x\"; }; A=$x f <<<\"${x:=new}\"; printf '/%s' \"$x\"", "<>/<new>/new")]
    [InlineData("x=1; A=1 eval 'printf \"%s\" \"$x\"' <<<\"$((x=2))\"; printf '/%s' \"$x\"", "2/2")]
    [InlineData("f() { printf '<%s>' \"${fd:+set}\"; }; A=1 f {fd}>/dev/null; printf '/%s' \"${fd:+set}\"", "<set>/set")]
    [InlineData("f() { test \"$fd\" != prefix; printf '%s' \"$?\"; }; fd=prefix f {fd}>/dev/null", "0")]
    [InlineData("unset x; declare -n ref=x; f() { printf '%s' \"$x\"; }; A=1 f <<<\"${ref:=new}\"; printf '/%s' \"$x\"", "new/new")]
    [InlineData("x=1; f() { printf '%s' \"$x\"; }; A=1 f <<<\"$(x=2; printf input)\"; printf '/%s' \"$x\"", "1/1")]
    [InlineData("x=0; f() { printf '%s' \"$x\"; }; A=1 f <<<\"$((x+=1))\" <<<\"$((x+=1))\"; printf '/%s' \"$x\"", "2/2")]
    [InlineData("x=1; declare -n r=x; f() { printf '%s' \"$x\"; }; x=3 f <<<\"$((r=2))\"; printf '/%s' \"$x\"", "3/2")]
    [InlineData("x=1; declare -n r=x; f() { printf '%s' \"$x\"; }; x=1 f <<<\"$((r=2))\"; printf '/%s' \"$x\"", "1/2")]
    [InlineData("x=1; declare -n r=x; f() { printf '%s' \"$x\"; }; r=3 f <<<\"$((r=2))\"; printf '/%s' \"$x\"", "3/2")]
    [InlineData("x=1; declare -n r=x; f() { printf '%s' \"$x\"; }; A=3 f <<<\"$((r=2))\"; printf '/%s' \"$x\"", "2/2")]
    [InlineData("x=1; declare -n r=x; f() { printf '%s' \"$x\"; }; x=3 f <<<\"$((x=2))\" <<<\"$((r=4))\"; printf '/%s' \"$x\"", "2/4")]
    [InlineData("x=1; declare -n r=x; declare -n s=r; f() { printf '%s' \"$x\"; }; x=3 f <<<\"$((s=2))\"; printf '/%s' \"$x\"", "3/2")]
    [InlineData("unset x; declare -n r=x; f() { printf '%s' \"$x\"; }; x=3 f <<<\"${r:=2}\"; printf '/%s' \"$x\"", "3/2")]
    [InlineData("x=1; declare -n r=x; f() { printf '%s' \"$x\"; }; x=3 f {r}>/dev/null; printf '/%s' \"${x:+set}\"", "3/set")]
    [InlineData("x=1; declare -n r=x; x=3 eval 'printf \"%s\" \"$x\"' <<<\"$((r=2))\"; printf '/%s' \"$x\"", "3/2")]
    public async Task RedirectionAssignments_ReachTemporaryCommandBindings(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task PrefixedFunction_CanWriteThroughAnAllocatedDescriptor()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "f() { printf hello >&\"$fd\"; }; A=1 f {fd}>/result",
            EndToEndHarness.BuildTree([]));

        // Persistent VFS writes are published when evaluation closes the descriptor.
        Assert.Equal("hello", await EndToEndHarness.ReadFileAsync(result.Host, "/result"));
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HostedEnvironment_RetainsPreparedBindingsDuringRedirection()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "export x=1; A=1 sh -c 'printf \"%s\" \"$x\"' <<<\"$((x=2))\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("1", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.37: finish caller-side operand effects before creating temporary bindings.
    [Theory]
    [InlineData("x=1; f() { printf '<%s>' \"$x\"; }; A=1 f \"$((x=2))\"; printf '/%s' \"$x\"", "<2>/2", "")]
    [InlineData("unset x; f() { printf '<%s>' \"$x\"; }; A=1 f \"${x:=new}\"; printf '/%s' \"$x\"", "<new>/new", "")]
    [InlineData("x=1; f() { printf '%s/%s' \"$x\" \"$1\"; }; x=3 f \"$((x=2))\"; printf '/%s' \"$x\"", "3/2/2", "")]
    [InlineData("unset x; A=$x sh -c 'printf \"<%s>/<%s>\" \"$A\" \"$1\"' sh \"${x:=new}\"", "<new>/<new>", "")]
    [InlineData("x=1; A=1 eval 'printf \"%s\" \"$x\"; :' \"$((x=2))\"; printf '/%s' \"$x\"", "2/2", "")]
    [InlineData("export x=1; A=1 sh -c 'printf \"%s/%s\" \"$x\" \"$1\"' sh \"$((x=2))\"", "2/2", "")]
    [InlineData("x=1; f() { printf '%s/%s/%s' \"$A\" \"$x\" \"$1\"; }; A=$(printf prefix >&2; printf '%s' \"$x\") f \"$((x+=1))$(printf arg >&2)\"; printf '/%s' \"$x\"", "2/2/2/2", "argprefix")]
    [InlineData("x=0; A=1 declare \"name$((x+=1))=ok\"; printf '%s' \"$x\"", "1", "")]
    [InlineData("x=1; A=$x true \"$((x=2))\"; printf '%s' \"$x\"", "2", "")]
    public async Task CommandExecution_SeesCompletedCallerExpansion(string script, string expected, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Source_SeesAssignmentsFromItsArguments()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "x=1; A=1 source /scope.sh \"$((x=2))\"; printf '/%s' \"$x\"",
            EndToEndHarness.BuildTree([("/scope.sh", "printf '%s/%s' \"$x\" \"$1\"")]));

        Assert.Equal("2/2/2", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.37: caller expansion is separate from the command's temporary bindings.
    [Theory]
    [InlineData("v=outer; v=inner printf '%s/%s' \"$v\" \"$(printf '%s' \"$v\")\"; printf '/%s' \"$v\"", "outer/outer/outer")]
    [InlineData("v=outer; v=inner captured=$(printf '%s' \"$v\") eval 'printf \"%s\" \"$captured\"'; printf '/%s' \"$v\"", "inner/outer")]
    [InlineData("v=outer; v=inner cat <<<\"$(printf '%s' \"$v\")\"", "outer\n")]
    [InlineData("c=printf; c=false \"$(printf '%s' \"$c\")\" hello; printf '/%s' \"$?\"", "hello/0")]
    [InlineData("v=outer; v=inner command printf '%s' \"$(printf '%s' \"$v\")\"", "outer")]
    [InlineData("v=outer; v=inner builtin printf '%s' \"$(printf '%s' \"$v\")\"", "outer")]
    [InlineData("v=outer; v=inner eval \"printf '%s' '$(printf '%s' \"$v\")'\"", "outer")]
    [InlineData("f() { printf '%s/%s' \"$1\" \"$v\"; }; v=outer; v=inner f \"$(printf '%s' \"$v\")\"; printf '/%s' \"$v\"", "outer/inner/outer")]
    [InlineData("v=outer; v=inner sh -c 'printf \"%s/%s\" \"$1\" \"$v\"' sh \"$(printf '%s' \"$v\")\"; printf '/%s' \"$v\"", "outer/inner/outer")]
    [InlineData("IFS=: read one two <<<\"left:right\"; printf '<%s>/<%s>' \"$one\" \"$two\"", "<left>/<right>")]
    public async Task CommandExpansion_UsesCallerBindings(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.37: child frames isolate local names and cannot inherit loops across a function call.
    [Theory]
    [InlineData("f() { printf '<%s>' \"$(break 2>/dev/null; printf after)\"; }; for n in one two; do f; done", "<after><after>")]
    [InlineData("f() { printf '<%s>' \"$(continue 2>/dev/null; printf after)\"; }; for n in one two; do f; done", "<after><after>")]
    [InlineData("f() { for n in one two; do printf '<%s>' \"$(break; printf unreachable)\"; done; }; for outer in one two; do f; done", "<><><><>")]
    [InlineData("f() { for n in one two; do printf '<%s>' \"$(continue; printf unreachable)\"; done; }; for outer in one two; do f; done", "<><><><>")]
    [InlineData("v=global; f() { v=parent; printf '<%s>' \"$(local v=child; printf '%s' \"$v\")\"; }; f; printf '<%s>' \"$v\"", "<child><parent>")]
    [InlineData("v=global; f() { v=parent; (local v=child; printf '<%s>' \"$v\"); }; f; printf '<%s>' \"$v\"", "<child><parent>")]
    [InlineData("v=global; f() { v=parent; { local v=child; printf '<%s>' \"$v\"; } | cat; }; f; printf '<%s>' \"$v\"", "<child><parent>")]
    public async Task ChildContext_IsolatesFunctionFrames(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("A=outer; A=inner B=$(printf '%s' \"$A\") sh -c 'printf \"%s/%s\\n\" \"$A\" \"$B\"'; printf '%s\\n' \"$A\"", "inner/inner\nouter\n")]
    [InlineData("arr[2147483648]=far; printf '%s\\n' \"$(printf '%s' \"${arr[2147483648]}\")\"", "far\n")]
    [InlineData("value=outer; declare -n ref=value; printf '%s\\n' \"$(ref=inner; printf '%s' \"$value\")\"; printf '%s\\n' \"$value\"", "inner\nouter\n")]
    public async Task Substitution_ClonesTheActiveScope(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("cd /tmp; printf '%s\\n' \"$(pwd)\"; printf '%s\\n' \"$(cd /; pwd)\"; pwd", "/tmp\n/\n/tmp\n")]
    [InlineData("f() { printf 'outer'; }; printf '%s\\n' \"$(f; f() { printf 'inner'; }; f)\"; f", "outerinner\nouter")]
    [InlineData("false; printf '%s\\n' \"$(printf '%s' \"$?\")\"", "1\n")]
    [InlineData("f() { printf '%s\\n' \"$(return 7; printf 'unexpected')\"; printf 'after\\n'; }; f", "\nafter\n")]
    [InlineData("v=global; f() { local v=outer; printf '%s\\n' \"$(local v=inner; printf '%s' \"$v\"; return 7; printf 'unexpected')\"; printf '%s\\n' \"$v\"; }; f; printf '%s\\n' \"$v\"", "inner\nouter\nglobal\n")]
    [InlineData("outer() { local v=outer; inner() { printf '%s\\n' \"$(local v=inner; printf '%s' \"$(return 7; printf 'unexpected')\"; printf '%s' \"$v\")\"; }; inner; printf '%s\\n' \"$v\"; }; outer", "inner\nouter\n")]
    [InlineData("for n in one two; do printf '%s\\n' \"$(break; printf '%s' \"$n\")\"; done", "\n\n")]
    [InlineData("for n in one two; do printf '%s\\n' \"$(continue; printf '%s' \"$n\")\"; done", "\n\n")]
    [InlineData("printf '%s\\n' \"$(for n in one two; do printf '%s' \"$n\"; break; done)\"", "one\n")]
    [InlineData("f() { cat <<<\"$(return 7; printf 'unexpected')\"; printf 'after\\n'; }; f", "\nafter\n")]
    [InlineData("pushd /tmp >/dev/null; printf '%s\\n' \"$(dirs; popd >/dev/null; pwd)\"; dirs", "/tmp /\n/\n/tmp /\n")]
    public async Task Substitution_ForksCurrentShellStateWithoutChangingTheCaller(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree(["/tmp"], []));

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

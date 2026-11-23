using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class UnsetFunctionTestsEndToEnd
{
    [Theory]
    [InlineData("unset --help value -q")]
    [InlineData("command unset -fv --help value")]
    public async Task ReachedHelpPreservesTheOperandAndReturnsUsageStatus(string invocation)
    {
        var result = await EvaluationTestHelper.RunAsync("value=keep; " + invocation
            + "; printf '<%s><%s>' \"$?\" \"$value\"");

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("unset: unset [-f] [-v] [-n] [name ...]\n", result.StdOut);
        Assert.EndsWith("<2><keep>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("unset -v --help value >&-", "<2><keep>", "")]
    [InlineData("unset -vq --help value", "<2><keep>",
        "unset: -q: invalid option\nunset: usage: unset [-f] [-v] [-n] [name ...]\n")]
    [InlineData("unset -- --help value", "<0><>", "")]
    public async Task HelpRespectsLeadingOptionsAndTheOutputDescriptor(
        string invocation, string expectedOutput, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync("value=keep; " + invocation
            + "; printf '<%s><%s>' \"$?\" \"$value\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("keep=value; unset '' bad-name keep; printf '<%s><%s>' \"$?\" \"${keep-unset}\"",
        "<0><unset>", "")]
    [InlineData("function bad-name { printf body; }; unset -v bad-name; printf '<%s>' \"$?\"; bad-name",
        "<1>body", "unset: `bad-name': not a valid identifier\n")]
    [InlineData("a=(one); unset -v '' 'a[]'; printf '<%s><%s>' \"$?\" \"${a[0]}\"",
        "<1><one>", "unset: `': not a valid identifier\nunset: `a[]': not a valid identifier\n")]
    [InlineData("a=(one two); i=1; unset -v 'a[i++]'; printf '<%s><%s><%s>' \"$?\" \"$i\" \"${a[1]-unset}\"",
        "<0><2><unset>", "")]
    [InlineData("keep=value; unset -v bad-name keep 2>&-; printf '<%s><%s>' \"$?\" \"${keep-unset}\"",
        "<1><unset>", "")]
    public async Task InvalidNamesFollowTheSelectedNamespace(string script, string expectedOutput, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Fact]
    public async Task UnsetVariableWritesRawIdentifierBytesToTheReachedDescriptor()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "first=one; last=two; unset -v -- first $'\\377' last 2>diag; printf '<%s><%s><%s>' \"$?\" \"${first-unset}\" \"${last-unset}\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1><unset><unset>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal<byte>([.. "unset: `"u8, 0xff, .. "': not a valid identifier\n"u8], host.ReadFileBytes("/diag"));
    }

    [Theory]
    [InlineData("-n")]
    [InlineData("-vn")]
    public async Task UnsetNamerefChecksTheLastReferenceAndContinues(string options)
    {
        var script = $$"""
            target=kept
            declare -rn tail=target
            declare -n head=tail other=target
            unset {{options}} head other
            printf '<%s:%s>' "$?" "${PIPESTATUS[*]}"
            declare -p head tail target
            [[ -R other ]]; printf '<other:%s>' "$?"
            """;

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:1>declare -n head=\"tail\"\ndeclare -nr tail=\"target\"\ndeclare -- target=\"kept\"\n<other:1>", result.StdOut);
        Assert.Equal("line 4: unset: tail: cannot unset: readonly variable\n", result.StdErr);
    }

    [Fact]
    public async Task ContinuedReadonlyUnsetReportsItsOperandAndVisitsLaterOperands()
    {
        var result = await EvaluationTestHelper.RunAsync(
            ":\nreadonly target=old; second=ready\nunset \\\n target second\n"
            + "printf '<%s:%s:%s>' \"$?\" \"$target\" \"${second-missing}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:old:missing>", result.StdOut);
        Assert.Equal("line 4: unset: target: cannot unset: readonly variable\n", result.StdErr);
    }

    [Theory]
    [InlineData("unset")]
    [InlineData("unset -v")]
    public async Task UnsetThroughAnEmptyReferencePreservesTheReadonlyTerminal(string command)
    {
        var script = $$"""
            declare -rn tail
            declare -n head=tail
            {{command}} head
            printf '<%s:%s>' "$?" "${PIPESTATUS[*]}"
            declare -p tail
            [[ -R head ]]; printf '<head:%s>' "$?"
            """;

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0:0>declare -nr tail\n<head:1>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("declare -n tail=target; declare -rn head=tail", "-n", "declare -nr head=\"tail\"\n")]
    [InlineData("declare -rn head", "-v", "declare -nr head\n")]
    [InlineData("declare -rn head", "-n", "declare -nr head\n")]
    public async Task UnsetPreservesAnExplicitlyNamedReadonlyReference(string setup, string options, string declaration)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "target=kept; " + setup + "; unset " + options + " head; "
            + """printf '<%s:%s>' "$?" "${PIPESTATUS[*]}"; declare -p head""");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:1>" + declaration, result.StdOut);
        Assert.Equal("line 1: unset: head: cannot unset: readonly variable\n", result.StdErr);
    }

    // Git Bash 5.2.37, isolated environment; omit only script-path/line diagnostic prefixes.
    [Theory]
    [InlineData("x=global; f(){ local x=local; unset 'x[0]'; export x; printf '<%s>\\n' \"${x-unset}\"; }; x=prefix f; declare -p x", "<unset>\ndeclare -- x=\"global\"\n", "", 0)]
    [InlineData("x=global; f(){ local x=local; declare -n r=x; unset 'r[0]'; export x; printf '<%s>\\n' \"${x-unset}\"; }; x=prefix f; declare -p x", "<unset>\ndeclare -- x=\"global\"\n", "", 0)]
    [InlineData("x=global; f(){ local x=local; declare -n r='x[0]'; unset r; export x; printf '<%s>\\n' \"${x-unset}\"; }; x=prefix f; declare -p x", "<unset>\ndeclare -- x=\"global\"\n", "", 0)]
    [InlineData("x=global; f(){ local x=local; i=0; unset 'x[i++]'; export x; printf '<%s>/<%s>\\n' \"${x-unset}\" \"$i\"; }; x=prefix f; declare -p x", "<unset>/<1>\ndeclare -- x=\"global\"\n", "", 0)]
    [InlineData("function bad-name { :; }; unset -n bad-name; declare -F bad-name", "", "", 1)]
    [InlineData("function bad-name { :; }; readonly -f bad-name; unset -n bad-name; printf '<%s>' \"$?\"; declare -Fp bad-name", "<1>declare -fr bad-name\n", "line 1: unset: bad-name: cannot unset: readonly function\n", 0)]
    [InlineData("function a[1] { :; }; a=(zero one); unset -n 'a[1]'; declare -p a; declare -F 'a[1]'", "declare -a a=([0]=\"zero\" [1]=\"one\")\n", "", 1)]
    [InlineData("function a[1] { :; }; readonly -f 'a[1]'; unset -n 'a[1]'; printf '<%s>' \"$?\"; declare -Fp 'a[1]'", "<1>declare -fr a[1]\n", "line 1: unset: a[1]: cannot unset: readonly function\n", 0)]
    [InlineData("function bad-name { :; }; unset -vn bad-name; printf '<%s>' \"$?\"; declare -F bad-name", "<1>bad-name\n", "unset: `bad-name': not a valid identifier\n", 0)]
    [InlineData("function a[1] { :; }; unset -vn 'a[1]'; printf '<%s>' \"$?\"; declare -F 'a[1]'", "<1>a[1]\n", "unset: `a[1]': not a valid identifier\n", 0)]
    [InlineData("unset -n bad-name; printf '<%s>' \"$?\"", "<0>", "", 0)]
    [InlineData("function a[1] { :; }; a=(zero one); unset 'a[1]'; declare -p a; declare -F 'a[1]'", "declare -a a=([0]=\"zero\")\na[1]\n", "", 0)]
    [InlineData("function a[1] { :; }; unset 'a[1]'; declare -F 'a[1]'", "a[1]\n", "", 0)]
    [InlineData("function a[1] { :; }; a=(zero); unset 'a[1]'; declare -p a; declare -F 'a[1]'", "declare -a a=([0]=\"zero\")\na[1]\n", "", 0)]
    [InlineData("function a[1] { :; }; a=(zero one); readonly a; unset 'a[1]'; printf 'status=%s\\n' \"$?\"; declare -F 'a[1]'", "status=1\na[1]\n", "line 1: unset: a: cannot unset: readonly variable\n", 0)]
    [InlineData("function a[1] { :; }; a=(zero one); unset -v 'a[1]'; declare -p a; declare -F 'a[1]'", "declare -a a=([0]=\"zero\")\na[1]\n", "", 0)]
    [InlineData("function a[1] { :; }; a=(zero one); unset -f 'a[1]'; declare -p a; declare -F 'a[1]'", "declare -a a=([0]=\"zero\" [1]=\"one\")\n", "", 1)]
    [InlineData("function r[1] { :; }; a=(zero one); declare -n r=a; unset 'r[1]'; declare -p a r; declare -F 'r[1]'", "declare -a a=([0]=\"zero\")\ndeclare -n r=\"a\"\nr[1]\n", "", 0)]
    [InlineData("function a[1] { :; }; a=scalar; unset 'a[1]'; printf 'status=%s\\n' \"$?\"; declare -p a; declare -F 'a[1]'", "status=1\ndeclare -- a=\"scalar\"\na[1]\n", "unset: a: not an array variable\n", 0)]
    [InlineData("function a[0] { :; }; a=scalar; unset 'a[0]'; printf '<%s>\\n' \"${a-unset}\"; declare -F 'a[0]'", "<unset>\na[0]\n", "", 0)]
    [InlineData("function a[@] { :; }; a=(zero one); unset 'a[@]'; declare -p a; declare -F 'a[@]'", "declare -a a=()\na[@]\n", "", 0)]
    [InlineData("function a[*] { :; }; declare -a a; unset 'a[*]'; declare -p a; declare -F 'a[*]'", "declare -a a\na[*]\n", "", 0)]
    [InlineData("function a[@] { :; }; declare -A a=([@]=at [key]=kept); unset 'a[@]'; printf '<%s>/<%s>\\n' \"${a[@]-unset}\" \"${a[key]}\"; declare -F 'a[@]'", "<kept>/<kept>\na[@]\n", "", 0)]
    [InlineData("function a[i++] { :; }; a=(zero one two); i=1; unset 'a[i++]'; printf '<%s>\\n' \"$i\"; declare -p a; declare -F 'a[i++]'", "<2>\ndeclare -a a=([0]=\"zero\" [2]=\"two\")\na[i++]\n", "", 0)]
    [InlineData("function a[] { :; }; a=(zero one); unset 'a[]'; declare -p a; declare -F 'a[]'", "declare -a a=([0]=\"zero\" [1]=\"one\")\n", "", 1)]
    [InlineData("f(){ :; }; declare -n f=missing; unset f; declare -F f", "", "", 1)]
    [InlineData("f(){ :; }; declare -n f=missing; readonly f; declare -p missing; unset f; printf 'status=%s\\n' \"$?\"; declare -F f", "declare -r missing\nstatus=1\nf\n", "line 1: unset: missing: cannot unset: readonly variable\n", 0)]
    [InlineData("f(){ :; }; declare -n f=missing; declare -r f; unset f; printf 'status=%s\\n' \"$?\"; declare -F f", "status=1\nf\n", "line 1: unset: missing: cannot unset: readonly variable\n", 0)]
    [InlineData("f(){ :; }; declare -n f=missing; export f; declare -p missing; unset f; declare -F f; unset f; declare -F f", "declare -x missing\nf\n", "", 1)]
    [InlineData("f(){ :; }; declare -n f=missing; declare -x f; declare -p missing; unset -v f; declare -F f", "declare -x missing\nf\n", "", 0)]
    [InlineData("f(){ :; }; declare -n f=missing; declare f; declare -p missing; unset f; declare -F f", "declare -- missing\nf\n", "", 0)]
    [InlineData("f(){ :; }; declare -n f=missing; export -n f; unset f; declare -F f", "", "", 1)]
    [InlineData("f(){ :; }; declare -n f=missing; export f; export -n f; declare -p missing; unset f; declare -F f", "declare -- missing\nf\n", "", 0)]
    [InlineData("f(){ :; }; declare -n f; unset f; declare -F f; declare -p f", "declare -n f\n", "", 0)]
    [InlineData("f(){ :; }; readonly -f f; declare -n f=missing; unset f", "", "line 1: unset: f: cannot unset: readonly function\n", 1)]
    [InlineData("f(){ :; }; target=value; declare -n f=target; unset f; declare -F f; unset f; declare -F f", "f\n", "", 1)]
    [InlineData("f(){ :; }; declare -n f=missing; unset -v f; declare -F f", "f\n", "", 0)]
    [InlineData("f(){ :; }; g(){ local -n f=missing; unset f; declare -F f; declare -p f; }; g", "declare -n f=\"missing\"\n", "", 0)]
    [InlineData("f(){ :; }; declare -n f=missing; g(){ local missing; unset missing; unset f; declare -F f; }; g", "f\n", "", 0)]
    [InlineData("f(){ :; }; a=(x); declare -n f='a[0]'; unset f; declare -F f; declare -p a", "declare -a a=([0]=\"x\")\n", "", 0)]
    [InlineData("a=(x); declare -n f='a[0]'; unset f; declare -p a f", "declare -a a=()\ndeclare -n f=\"a[0]\"\n", "", 0)]
    [InlineData("g(){ :; }; f(){ local g; unset g; unset g; }; f; declare -F g", "g\n", "", 0)]
    [InlineData("g(){ :; }; h(){ unset g; }; f(){ local g; unset g; h; }; f; declare -F g", "g\n", "", 0)]
    [InlineData("g(){ :; }; f(){ local g; unset g; eval 'unset g'; }; f; declare -F g", "g\n", "", 0)]
    [InlineData("g(){ :; }; f(){ local g; unset g; unset -f g; }; f; declare -F g", "", "", 1)]
    [InlineData("f(){ printf old; }; unset -f f; printf 'status=%s\\n' \"$?\"; declare -F f; printf 'found=%s\\n' \"$?\"", "status=0\nfound=1\n", "", 0)]
    [InlineData("unset -f missing; printf 'status=%s\\n' \"$?\"", "status=0\n", "", 0)]
    [InlineData("unset -f '' 'bad-name' 'a[1]'; printf 'status=%s\\n' \"$?\"", "status=0\n", "", 0)]
    [InlineData("f(){ printf old; }; readonly -f f; unset -f f; printf 'status=%s\\n' \"$?\"; f", "status=1\nold", "line 1: unset: f: cannot unset: readonly function\n", 0)]
    [InlineData("f(){ :; }; g(){ :; }; readonly -f f; unset -f f g missing; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\ndeclare -fr f\n", "line 1: unset: f: cannot unset: readonly function\n", 0)]
    [InlineData("f(){ :; }; readonly f=value; unset -f f; printf 'status=%s\\n' \"$?\"; declare -F; declare -p f", "status=0\ndeclare -r f=\"value\"\n", "", 0)]
    [InlineData("f(){ :; }; f=value; unset -f f; declare -p f; declare -F", "declare -- f=\"value\"\n", "", 0)]
    [InlineData("f(){ :; }; unset f; declare -F; printf 'status=%s\\n' \"$?\"", "status=0\n", "", 0)]
    [InlineData("f(){ :; }; f=value; unset f; declare -F; unset f; declare -F", "declare -f f\n", "", 0)]
    [InlineData("f(){ :; }; declare f; unset f; declare -F; unset f; declare -F", "declare -f f\n", "", 0)]
    [InlineData("f(){ :; }; readonly f=value; unset f; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\ndeclare -f f\n", "line 1: unset: f: cannot unset: readonly variable\n", 0)]
    [InlineData("f(){ :; }; unset -v f; declare -F; f=value; unset -v f; declare -F", "declare -f f\ndeclare -f f\n", "", 0)]
    [InlineData("f(){ :; }; target=value; declare -n f=target; unset -fn f; declare -F; declare -p f target", "declare -n f=\"target\"\ndeclare -- target=\"value\"\n", "", 0)]
    [InlineData("f(){ :; }; f=value; unset -fv f; printf 'status=%s\\n' \"$?\"; declare -F; declare -p f", "status=1\ndeclare -f f\ndeclare -- f=\"value\"\n", "unset: cannot simultaneously unset a function and a variable\n", 0)]
    [InlineData("f(){ :; }; unset -v -f f; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\ndeclare -f f\n", "unset: cannot simultaneously unset a function and a variable\n", 0)]
    [InlineData("f(){ :; }; unset -fz f; printf 'status=%s\\n' \"$?\"; declare -F", "status=2\ndeclare -f f\n", "unset: -z: invalid option\nunset: usage: unset [-f] [-v] [-n] [name ...]\n", 0)]
    [InlineData("f(){ :; }; unset -f -- f; declare -F", "", "", 0)]
    [InlineData("f(){ :; }; g(){ :; }; unset -f f -v g; printf 'status=%s\\n' \"$?\"; declare -F", "status=0\n", "", 0)]
    [InlineData("f(){ :; }; g(){ :; }; names='f g'; unset -f $names; declare -F", "", "", 0)]
    [InlineData("f(){ :; }; g(){ :; }; names='f g'; unset -f \"$names\"; declare -F", "declare -f f\ndeclare -f g\n", "", 0)]
    [InlineData("f(){ :; }; flag=-f; name=f; unset \"$flag\" \"$name\"; declare -F", "", "", 0)]
    [InlineData("f(){ unset -f f; printf finish; }; f; declare -F", "finish", "", 0)]
    [InlineData("f(){ :; }; g(){ unset -f f; }; x=prefix g; declare -F", "declare -f g\n", "", 0)]
    [InlineData("f(){ :; }; (unset -f f; declare -F); declare -F", "declare -f f\n", "", 0)]
    [InlineData("f(){ :; }; value=$(unset -f f; declare -F); printf '<%s>\\n' \"$value\"; declare -F", "<>\ndeclare -f f\n", "", 0)]
    [InlineData("f(){ :; }; unset -f f | cat; declare -F", "declare -f f\n", "", 0)]
    [InlineData("f(){ :; }; ! unset -f f; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\n", "", 0)]
    [InlineData("f(){ :; }; x=old; x=temp unset -f f; printf 'x=%s\\n' \"$x\"; declare -F", "x=old\n", "", 0)]
    [InlineData("f(){ :; }; export -f f; unset -f f; f(){ printf new; }; declare -Fp f; f", "declare -f f\nnew", "", 0)]
    [InlineData("f(){ :; }; n=0; unset -f \"$((n+=1))\" f \"$((n+=1))\"; printf 'n=%s status=%s\\n' \"$n\" \"$?\"; declare -F", "n=2 status=0\n", "", 0)]
    [InlineData("f(){ :; }; unset -n f; declare -F; printf 'status=%s\\n' \"$?\"", "declare -f f\nstatus=0\n", "", 0)]
    [InlineData("f(){ :; }; target=value; declare -n f=target; unset -n f; declare -F; declare -p target; declare -p f", "declare -f f\ndeclare -- target=\"value\"\n", "declare: f: not found\n", 1)]
    [InlineData("f(){ readonly -f f; unset -f f; printf finish; }; f; declare -Fp f", "finishdeclare -fr f\n", "line 1: unset: f: cannot unset: readonly function\n", 0)]
    [InlineData("f(){ :; }; eval 'unset -f f'; declare -F", "", "", 0)]
    [InlineData("function if { :; }; unset -f if; declare -F", "", "", 0)]
    [InlineData("x=value; unset -n x; printf 'status=%s x=%s\\n' \"$?\" \"${x-unset}\"", "status=0 x=value\n", "", 0)]
    [InlineData("target=value; declare -rn ref=target; unset -n ref; printf 'status=%s\\n' \"$?\"; declare -p ref target", "status=1\ndeclare -nr ref=\"target\"\ndeclare -- target=\"value\"\n", "line 1: unset: ref: cannot unset: readonly variable\n", 0)]
    [InlineData("f(){ :; }; readonly -f f; unset f; printf 'status=%s\\n' \"$?\"; declare -Fp f", "status=1\ndeclare -fr f\n", "line 1: unset: f: cannot unset: readonly function\n", 0)]
    [InlineData("unset; unset -f; unset -v; unset -n; printf 'status=%s\\n' \"$?\"", "status=0\n", "", 0)]
    public async Task Evaluation_Unset_SelectsNamespaceAndPreservesFunctionLifecycle(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }
}

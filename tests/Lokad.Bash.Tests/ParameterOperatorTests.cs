using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ParameterOperatorTests
{
    [Theory]
    [InlineData(":1:2", "bc")]
    [InlineData("#a", "bcdef")]
    [InlineData("##a*", "")]
    [InlineData("%f", "abcde")]
    [InlineData("%%*f", "")]
    [InlineData("/a/Z", "Zbcdef")]
    [InlineData("^", "Abcdef")]
    [InlineData("^^", "ABCDEF")]
    [InlineData(",", "abcdef")]
    [InlineData(",,", "abcdef")]
    [InlineData("@Q", "'abcdef'")]
    [InlineData("@E", "abcdef")]
    public async Task IndirectValueOperatorsSelectNamedNamerefsOnce(string operation, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(abcdef uvwxyz); i=0; declare -n r='a[i++]'; p=r; "
            + "printf '<%s>|%s' \"${!p" + operation + "}\" \"$i\"");

        Assert.Equal("<" + expected + ">|1", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.21: unbound namerefs fail before operands; invalid assignment targets fail after the RHS.
    [Theory]
    [InlineData("declare -n p; printf '%s' \"${!p}\"; printf after", "p: invalid indirect expansion\n")]
    [InlineData("declare -n p; printf '%s' \"${!p:-$(printf effect >&2; printf fallback)}\"; printf after", "p: invalid indirect expansion\n")]
    [InlineData("declare -n p; printf '%s' \"${!p:=$(printf effect >&2; printf value)}\"; printf after", "p: invalid indirect expansion\n")]
    [InlineData("p=(); printf '%s' \"${!p[@]:=$(printf effect >&2; printf value)}\"; printf after", "effect!p[@]: invalid indirect expansion\n")]
    [InlineData("declare -A p; printf '%s' \"${!p[*]:=$(printf effect >&2; printf value)}\"; printf after", "effect!p[*]: invalid indirect expansion\n")]
    [InlineData("p=(); printf '%s' \"${!p[@]:=${missing:?inner}}\"; printf after", "missing: inner\n")]
    [InlineData("a=(); p='a[0]'; printf '%s' \"${!p:=new}\"; printf after", "a[0]: invalid variable name\n")]
    [InlineData("a=(); p='a[0]'; printf '%s' \"${!p=new}\"; printf after", "a[0]: invalid variable name\n")]
    [InlineData("declare -A a; p='a[key]'; printf '%s' \"${!p:=new}\"; printf after", "a[key]: invalid variable name\n")]
    [InlineData("declare -A a; p='a[key]'; printf '%s' \"${!p=new}\"; printf after", "a[key]: invalid variable name\n")]
    [InlineData("a=(); p='a[$(printf selector >&2; printf 0)]'; printf '%s' \"${!p:=$(printf rhs >&2; printf new)}\"; printf after", "selectorrhsa[$(printf selector >&2; printf 0)]: invalid variable name\n")]
    [InlineData("declare -A a; p='a[$(printf selector >&2; printf key)]'; printf '%s' \"${!p:=$(printf rhs >&2; printf new)}\"; printf after", "selectorrhsa[$(printf selector >&2; printf key)]: invalid variable name\n")]
    [InlineData("a=(); p='a[0]'; printf '%s' \"${!p:=${bad:?inner}}\"; printf after", "bad: inner\n")]
    public async Task Evaluation_InvalidIndirectionRetainsOnlyReachedOperandEffects(string script, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("declare -n p=absent; printf '%s' \"${!p:-fallback}\"", "absent")]
    [InlineData("declare -n p; value=keep; printf '%s' \"${value:-${!p}}\"", "keep")]
    [InlineData("declare -n p; false && printf '%s' \"${!p}\"; printf after", "after")]
    [InlineData("p=(); printf '%s' \"${!p[@]:-fallback}\"", "fallback")]
    [InlineData("a=(old); p='a[0]'; printf '%s' \"${!p:=$(printf bad >&2; printf new)}\"", "old")]
    [InlineData("a=(''); p='a[0]'; printf '<%s>' \"${!p=new}\"", "<>")]
    [InlineData("p=a; printf '%s|%s' \"${!p:=new}\" \"$a\"", "new|new")]
    [InlineData("declare -n p=absent; printf '%s' \"${!p:?stop}\"", "absent")]
    public async Task Evaluation_IndirectFailuresDoNotRejectNamedTargetsOrUnreachedOperands(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Bash exits 127 for these errors; the engine retains its fatal-expansion status of 1.
    [Theory]
    [InlineData("p=absent; printf '%s' \"${!p:?stop}\"; printf after", "!p: stop\n")]
    [InlineData("p=absent; printf '%s' \"${!p?}\"; printf after", "!p: parameter not set\n")]
    [InlineData("p=absent; absent=; printf '%s' \"${!p:?}\"; printf after", "!p: parameter null or not set\n")]
    [InlineData("p=absent; printf '%s' \"${!p:?${bad:?inner}}\"; printf after", "bad: inner\n")]
    public async Task Evaluation_IndirectErrorRetainsSubjectAndFirstFailure(string script, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Bash 5.2.21: indirect aggregate reads select the name before applying its value operator.
    [Theory]
    [InlineData("p=(target); target=VALUE; printf '<%s>' \"${!p[@]:1}\"", "<ALUE>")]
    [InlineData("p=(target); target=VALUE; printf '<%s>' \"${!p[*]:1:2}\"", "<AL>")]
    [InlineData("declare -A p; p[key]=target; target=VALUE; printf '<%s>' \"${!p[@]:1}\"", "<ALUE>")]
    [InlineData("p=([999999999]=target); target=VALUE; printf '<%s>' \"${!p[@]:1}\"", "<ALUE>")]
    [InlineData("p=(target); target=VALUE; i=0; printf '<%s>|%s' \"${!p[@]:i++:i++}\" \"$i\"", "<V>|2")]
    [InlineData("p=(target); target=VALUE; printf '<%s>' \"${!p[@]}\"", "<0>")]
    [InlineData("set -- target; target=VALUE; printf '<%s>' \"${!@:-fallback}\" \"${!*#V}\" \"${!@:1}\"", "<VALUE><ALUE><ALUE>")]
    [InlineData("set -- target; target=VALUE; printf '<%s>' \"${!@:+yes}\" \"${!@%UE}\"", "<yes><VAL>")]
    [InlineData("set -- target; target=VALUE; printf '<%s>' \"${!@=$(printf bad >&2; printf one)}\"", "<VALUE>")]
    [InlineData("set -- missing; printf '<%s>' \"${!@:-fallback}\"", "<fallback>")]
    [InlineData("set --; printf '<%s>' \"${!@}\" \"${!*:-fallback}\"", "<><fallback>")]
    [InlineData("set -- target; printf '<%s>' \"${!#:-fallback}\"", "<target>")]
    [InlineData("set -- zero one; false; printf '<%s>' \"${!?:1}\"", "<ero>")]
    [InlineData("p=(a b); IFS=; ab=VALUE; printf '<%s>' \"${!p[*]:1}\"", "<ALUE>")]
    public async Task Evaluation_IndirectAggregatesSupportValueOperators(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("set -- target; unset target", "!@")]
    [InlineData("set -- target; unset target", "!*")]
    [InlineData("set --", "!@")]
    public async Task Evaluation_IndirectPositionalAssignmentRejectsBeforeOperandEffects(string setup, string parameter)
    {
        var result = await EndToEndHarness.RunScriptAsync(setup
            + "; printf '%s' \"${" + parameter + ":=$(printf bad >&2; printf one)}\"; printf after",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("$" + parameter + ": cannot assign in this way\n", result.StdErr);
    }

    [Theory]
    [InlineData("set -- a b; IFS=:; ab=VALUE", "${!@:-fallback}")]
    [InlineData("p=(a b); IFS=; ab=VALUE", "${!p[@]:1}")]
    public async Task Evaluation_IndirectAtFieldsRetainNameSeparators(string setup, string expansion)
    {
        var result = await EndToEndHarness.RunScriptAsync(setup
            + "; printf '%s' \"" + expansion + "\"; printf after", EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("a b: invalid variable name\n", result.StdErr);
    }

    // These operators are recognized but intentionally unsupported; do not leak CLR exceptions or selector effects.
    [Theory]
    [InlineData("x@A")]
    [InlineData("x@P")]
    [InlineData("a[$(printf effect >&2; printf 0)]@A")]
    public async Task Evaluation_UnsupportedParameterOperatorsFailBeforeSelection(string expression)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "x=hello; printf '%s' \"${" + expression + "}\"; printf after", EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("${" + expression + "}: unsupported parameter operator\n", result.StdErr);
    }

    [Theory]
    [InlineData("$")]
    public async Task Evaluation_UnsupportedSpecialParametersFailWithoutThrowing(string parameter)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "printf '%s' \"${" + parameter + "}\"; printf after", EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("${" + parameter + "}: unsupported special parameter\n", result.StdErr);
    }

    [Fact]
    public async Task Evaluation_UnreachedUnsupportedParameterOperatorsRemainLazy()
    {
        const string Script = "false && printf '%s' \"${x@A}\"; x=keep; printf '%s' \"${x:-${a[$(printf effect >&2; printf 0)]@A}}\"";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("keep", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Isolated Git Bash 5.2.37 files; remove only recorded diagnostic location prefixes.
    [Theory]
    [InlineData("printf '<%s>\\n' \"${bad!}\"; printf 'after\\n'\n", "", "${bad!}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${bad.dot}\"; printf 'after\\n'\n", "", "${bad.dot}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${bad name}\"; printf 'after\\n'\n", "", "${bad name}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${2bad}\"; printf 'after\\n'\n", "", "${2bad}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${}\"; printf 'after\\n'\n", "", "${}: bad substitution\n", 1)]
    [InlineData("false && printf '<%s>\\n' \"${bad!}\"; printf 'after\\n'\n", "after\n", "", 0)]
    [InlineData("name=ok; printf '<%s>\\n' \"${name:-${bad!}}\"; printf 'after\\n'\n", "<ok>\nafter\n", "", 0)]
    [InlineData("unset name; printf '<%s>\\n' \"${name:-${bad!}}\"; printf 'after\\n'\n", "", "${bad!}: bad substitution\n", 1)]
    [InlineData("probe_aa=1; probe_bb=2; printf '<%s>\\n' \"${!probe_*}\"; printf 'after\\n'\n", "<probe_aa probe_bb>\nafter\n", "", 0)]
    [InlineData("x=abc; printf '<%s>\\n' \"${x:}\" \"${x:1:}\"\n", "", "${x:}: bad substitution\n", 1)]
    [InlineData("set -- a b c d e; x=abc; printf '<%s>\\n' \"${##abc}\" \"${#x:-d}\"\n", "", "${#x:-d}: bad substitution\n", 1)]
    [InlineData("p=target; target=VALUE; printf '<%s>\\n' \"${!p}\"; p=absent; printf '<%s>\\n' \"${!p:-fallback}\"\n", "<VALUE>\n<fallback>\n", "", 0)]
    [InlineData("p='bad!'; printf '<%s>\\n' \"${!p}\"; printf after\n", "", "bad!: invalid variable name\n", 1)]
    [InlineData("unset p; printf '<%s>\\n' \"${!p}\"; printf after\n", "", "p: invalid indirect expansion\n", 1)]
    [InlineData("x=VALUE; declare -n p=x; printf '<%s>\\n' \"${!p}\"\n", "<x>\n", "", 0)]
    [InlineData("set -- target; unset target; printf '<%s>\\n' \"${!1:=value}\" \"$target\"\n", "<value>\n<value>\n", "", 0)]
    [InlineData("p=9; set --; printf '<%s>\\n' \"${!p:=$(printf effect >&2; printf value)}\"; printf after\n", "", "effect9: invalid variable name\n", 1)]
    [InlineData("a=(zero one two); p='a[i++]'; i=1; printf '<%s>\\n' \"${!p}\" \"$i\"\n", "<one>\n<2>\n", "", 0)]
    [InlineData("false && printf '<%s>\\n' \"${x@Z}\"; printf after\n", "after", "", 0)]
    [InlineData("probe_a=1; probe_b=2; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${!probe_@}\"\n", "n=2\n<probe_a>\n<probe_b>\n", "", 0)]
    [InlineData("printf '<%s>\\n' \"${!:-missing}\" \"$!\" \"${#!}\"\n", "<missing>\n<>\n<0>\n", "", 0)]
    [InlineData("printf '<%s>\\n' \"${!:=$(printf bad >&2; printf value)}\"; printf after\n", "", "$!: cannot assign in this way\n", 1)]
    [InlineData("printf '<%s>\\n' \"${!!}\"; printf after\n", "", "${!!}: bad substitution\n", 1)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p:-fallback}\" \"$i\"\n", "<abcdef>\n<3>\n", "", 0)]
    [InlineData("a=([2]=abc [3]=xyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p#x}\" \"$i\"\n", "<yz>\n<4>\n", "", 0)]
    public async Task Evaluation_ParameterGrammarAndOperators(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Diagnostic source rendering remains separate from the reached-error/effect contract.
    [Theory]
    [InlineData("printf '<%s>\\n' \"$(printf 'effect\\n' >&2)${bad!}\"; printf 'after\\n'\n", 1)]
    [InlineData("printf '<%s>\\n' \"${bad!}$(printf 'effect\\n' >&2)\"; printf 'after\\n'\n", 0)]
    public async Task Evaluation_MalformedParametersRetainOnlyEarlierEffects(string script, int expectedEffects)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("bad substitution", result.StdErr);
        Assert.Equal(expectedEffects, result.StdErr.Split("effect\n").Length - 1);
    }

    [Fact]
    public async Task Evaluation_InvalidParameterTailDoesNotEvaluateItsSelector()
    {
        const string Script = "printf '<%s>\\n' \"${a[$(printf bad >&2; printf 0)]!}\"; printf after\n";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.StartsWith("${a[", result.StdErr);
        Assert.Contains("bad substitution", result.StdErr);
    }
}

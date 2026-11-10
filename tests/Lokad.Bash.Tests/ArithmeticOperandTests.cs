using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticOperandTests
{
    // Bash 5.2.21: unquoted source arithmetic retains ANSI-C quotes, including nested indices/bounds.
    [Theory]
    [InlineData("let x=$'2'; printf '%s/%s' \"$?\" \"$x\"", "0/2", "", 0)]
    [InlineData("a=(zero one); printf '%s' \"${a[$'1']}\"", "one", "", 0)]
    [InlineData("v=abc; printf '%s' \"${v:$'1'}\"", "bc", "", 0)]
    [InlineData("v=abc; printf '%s' \"${v:0:$'1'}\"", "a", "", 0)]
    [InlineData("a=(1 2); ((x=\"${a[$'1']}\")); printf '%s/%s' \"$?\" \"$x\"", "0/2", "", 0)]
    [InlineData("v=12; ((x=\"${v:$'1'}\")); printf '%s/%s' \"$?\" \"$x\"", "0/2", "", 0)]
    [InlineData("v=12; ((x=\"${v:0:$'1'}\")); printf '%s/%s' \"$?\" \"$x\"", "0/1", "", 0)]
    [InlineData("declare -A a; a[1]=2; ((x=${a[$'1']})); printf '%s/%s' \"$?\" \"$x\"", "0/2", "", 0)]
    [InlineData("((x=\"${missing:=$'2'}\")); printf '%s/%s/%s' \"$?\" \"$x\" \"$missing\"", "0/2/2", "", 0)]
    [InlineData("false && ((x=$'$(printf bad >&2; printf 2)')); printf after", "after", "", 0)]
    public async Task Evaluation_AnsiQuotesRetainOperandContextAndLaziness(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Git Bash 5.2.37, isolated script files; remove only recorded path/line diagnostic prefixes.
    [Theory]
    [InlineData("((i=\"2\")); printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "s=0 i=2\n", "", 0)]
    [InlineData("x=3; ((i=$x+1)); printf 's=%s i=%s x=%s\\n' \"$?\" \"$i\" \"$x\"\n\n", "s=0 i=4 x=3\n", "", 0)]
    [InlineData("unset x; ((i=${x:=2}+1)); printf 's=%s i=%s x=%s\\n' \"$?\" \"$i\" \"$x\"\n\n", "s=0 i=3 x=2\n", "", 0)]
    [InlineData("i=0; ((j=$((i+=2))+i)); printf 's=%s i=%s j=%s\\n' \"$?\" \"$i\" \"$j\"\n\n", "s=0 i=2 j=4\n", "", 0)]
    [InlineData("i=0; ((0 && $(printf 'effect\\n' >&2; printf 2))); printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "s=1 i=0\n", "effect\n", 0)]
    [InlineData("false && ((i=$(printf 'bad\\n' >&2; printf 2))); printf 's=%s i=%s\\n' \"$?\" \"${i-unset}\"\n\n", "s=1 i=unset\n", "", 0)]
    [InlineData("((0)); printf 's=%s\\n' \"$?\"\n\n", "s=1\n", "", 0)]
    [InlineData("unset missing; ((i=${missing:?stop})); printf continued\n\n", "", "missing: stop\n", 1)]
    [InlineData("x=9; f(){ local x=2; ((x=$x+1)); printf 'inner=%s\\n' \"$x\"; }; f; printf 'outer=%s\\n' \"$x\"\n\n", "inner=3\nouter=9\n", "", 0)]
    [InlineData("x=1; ( ((x=$x+1)); printf 'inner=%s\\n' \"$x\" ); printf 'outer=%s\\n' \"$x\"\n\n", "inner=2\nouter=1\n", "", 0)]
    [InlineData("unset x; (($x)); printf 's=%s\\n' \"$?\"\n\n", "s=1\n", "", 0)]
    [InlineData("for ((i=\"0\";i<\"2\";i+=\"1\")); do printf '<%s>\\n' \"$i\"; done; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "<0>\n<1>\ns=0 i=2\n", "", 0)]
    [InlineData("for ((i=$(printf 'init\\n' >&2; printf 0);i<$(printf 'condition\\n' >&2; printf 2);i+=$(printf 'update\\n' >&2; printf 1))); do printf '<%s>\\n' \"$i\"; done; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "<0>\n<1>\ns=0 i=2\n", "init\ncondition\nupdate\ncondition\nupdate\ncondition\n", 0)]
    [InlineData("for ((i=0;i<2;i+=$(printf 'update\\n' >&2; printf 1))); do continue; done; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "s=0 i=2\n", "update\nupdate\n", 0)]
    [InlineData("for ((i=$(printf 'init\\n' >&2; printf 0);1;i+=$(printf 'bad\\n' >&2; printf 1))); do break; done; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "s=0 i=0\n", "init\n", 0)]
    [InlineData("i=0; for ((;;i++)); do ((i==2)) && break; done; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "s=0 i=2\n", "", 0)]
    [InlineData("unset missing; for ((i=0;i<${missing:?stop};i++)); do printf bad; done; printf continued\n\n", "", "missing: stop\n", 1)]
    [InlineData("unset missing; for ((i=0;i<1;i+=${missing:?stop})); do printf body; done; printf continued\n\n", "body", "missing: stop\n", 1)]
    [InlineData("false && for ((i=${missing:?stop};i<1;i++)); do printf bad; done; printf 's=%s\\n' \"$?\"\n\n", "s=1\n", "", 0)]
    [InlineData("x=2; let \"i=$x\"; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "s=0 i=2\n", "", 0)]
    [InlineData("let \"i=$(printf 'effect\\n' >&2; printf 2)\"; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "s=0 i=2\n", "effect\n", 0)]
    [InlineData("x=9; f(){ local x=2; let \"x=$x+1\"; printf 'inner=%s\\n' \"$x\"; }; f; printf 'outer=%s\\n' \"$x\"\n\n", "inner=3\nouter=9\n", "", 0)]
    [InlineData("i=0; readonly j=1; ((i=$((i+=1))+1,j=3)); printf 's=%s i=%s j=%s\\n' \"$?\" \"$i\" \"$j\"\n\n", "s=1 i=2 j=1\n", "j: readonly variable\n", 0)]
    [InlineData("((x=${missing:-1)); printf after\n\n", "", "bad substitution: no closing `}' in x=${missing:-1\n", 1)]
    [InlineData("false && ((x=${missing:-1)); printf after\n\n", "after", "", 0)]
    public async Task Evaluation_ArithmeticOperandsPreserveScopeAndTiming(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Preserve effects and continuation without freezing incomplete arithmetic diagnostic wording.
    [Theory]
    [InlineData("i=0; ((i=2, j=1/0)); printf 's=%s i=%s j=%s\\n' \"$?\" \"$i\" \"${j-unset}\"\n\n", "s=1 i=2 j=unset\n", 0, "i=2, j=1/0", "")]
    [InlineData("i=0; for ((;i<1;i=2,j=1/0)); do printf body; done; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n\n", "bodys=1 i=2\n", 0, "i=2,j=1/0", "")]
    [InlineData("x='$y'; y=2; let \"i=$x\"; printf 's=%s i=%s\\n' \"$?\" \"${i-unset}\"\n\n", "s=1 i=unset\n", 0, "i=$y", "")]
    [InlineData("i=0; let 'j=1/0' \"k=$((i+=1))\"; printf 's=%s i=%s k=%s\\n' \"$?\" \"$i\" \"${k-unset}\"\n\n", "s=1 i=1 k=unset\n", 0, "j=1/0", "")]
    [InlineData("((x=$'2')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x='2'", "")]
    [InlineData("((x=$'\\062')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x='2'", "")]
    [InlineData("((x=$'')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x=''", "")]
    [InlineData("((x=${missing:=$'2'})); printf '%s/%s/%s' \"$?\" \"${x-unset}\" \"$missing\"", "1/unset/'2'", 0, "x='2'", "")]
    [InlineData("((x=$'$(printf effect >&2; printf 2)')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x='2'", "effect")]
    [InlineData("((x=$'\"2\"')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x='2'", "")]
    [InlineData("((x=$'\"')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x=''", "")]
    [InlineData("((x=$'\\x22')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x=''", "")]
    [InlineData("((x=$'\"$(printf effect >&2; printf 2)')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x='2'", "effect")]
    [InlineData("for ((i=$'\"';i<1;i++)); do printf bad; done; printf '/%s' \"$?\"", "/1", 0, "i=''", "")]
    [InlineData("printf '%s' \"$(( $'\"' ))\"; printf after", "", 1, " '' ", "")]
    [InlineData("a=(1 2); ((x=${a[$'1']})); printf after", "", 1, "'1'", "")]
    [InlineData("a=(1 2); printf '%s' \"$(( ${a[$'1']} ))\"; printf after", "", 1, "'1'", "")]
    [InlineData("v=12; ((x=${v:$'1'})); printf after", "", 1, "'1'", "")]
    [InlineData("v=12; printf '%s' \"$(( ${v:$'1'} ))\"; printf after", "", 1, "'1'", "")]
    [InlineData("v=12; ((x=${v:0:$'1'})); printf after", "", 1, "'1'", "")]
    [InlineData("a=(1 2); ((x=${a[$'$(printf effect >&2; printf 1)']})); printf after", "", 1, "'1'", "effect")]
    [InlineData("((x=$'\\0472\\047')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x=''\\''2'\\'''", "")]
    [InlineData("((x=$'\\x24\\x272\\x27')); printf '%s/%s' \"$?\" \"${x-unset}\"", "1/unset", 0, "x='$'\\''2'\\'''", "")]
    [InlineData("printf '%s' \"$(( $'2' ))\"; printf after", "", 1, " '2' ", "")]
    [InlineData("for ((i=$'0';i<1;i++)); do printf bad; done; printf '/%s' \"$?\"", "/1", 0, "i='0'", "")]
    [InlineData("for ((i=0;$'1';i++)); do printf bad; break; done; printf '/%s' \"$?\"", "/1", 0, "'1'", "")]
    [InlineData("for ((i=0;i<1;i=$'1')); do printf body; done; printf '/%s' \"$?\"", "body/1", 0, "i='1'", "")]
    public async Task Evaluation_ArithmeticFailuresRetainCompletedEffects(
        string script, string expectedOutput, int expectedStatus, string expandedExpression, string effectPrefix)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.StartsWith(effectPrefix, result.StdErr);
        Assert.Contains(expandedExpression + ":", result.StdErr);
    }

    [Theory]
    [InlineData("((x=$(printf 'effect\\n' >&2; printf 2)+${missing:-1)); printf after\n\n", "effect\n")]
    [InlineData("((x=${missing:-1+$(printf 'effect\\n' >&2; printf 2))); printf after\n\n", "")]
    [InlineData("((x=\\2+${missing:-1)); printf after\n\n", "")]
    [InlineData("((x=$(printf 'effect\\n' >&2; printf 2)+${bad!})); printf after\n\n", "effect\n")]
    public async Task Evaluation_MalformedOperandsStopAfterEarlierEffects(string script, string effectPrefix)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.StartsWith(effectPrefix, result.StdErr);
        Assert.Contains("bad substitution", result.StdErr);
    }

    [Fact]
    public void ArithmeticForSplitsHeaderWithoutSplittingNestedCommands()
    {
        const string Init = "i=$(printf 'init'; printf 0)";
        const string Condition = "i<$(printf 'condition'; printf 2)";
        const string Update = "i+=$(printf 'update'; printf 1)";
        var script = Parser.Parse($"for (({Init};{Condition};{Update})); do :; done");
        var loop = Assert.IsType<ArithmeticForCommand>(Assert.Single(script.Commands));

        loop.Split(out var init, out var condition, out var update);

        Assert.Equal(Init, init.ToUtf8String());
        Assert.Equal(Condition, condition.ToUtf8String());
        Assert.Equal(Update, update.ToUtf8String());
    }
}

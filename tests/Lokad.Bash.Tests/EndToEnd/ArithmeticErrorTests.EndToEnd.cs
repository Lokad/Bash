using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArithmeticErrorTestsEndToEnd
{
    [Theory]
    [InlineData("source /script", "1:10:30:before")]
    [InlineData(". /script", "1:10:30:before")]
    [InlineData("source /script inside", "1:10:30:before")]
    [InlineData("! source /script", "0:10:30:before")]
    [InlineData("x=15 source /script", "1:10:30:before")]
    [InlineData("source /script || printf caught", "caught0:10:30:before")]
    public async Task Source_ArithmeticFailureKeepsCompletedWritesAndRestoresCallerBindings(
        string command, string expectedOutput)
    {
        var tree = EndToEndHarness.BuildTree([("/script", "x=$((y=30,1/0)); printf bad")]);
        var result = await EndToEndHarness.RunScriptAsync(
            "x=10; y=20; set -- before; " + command + "; printf '%s:%s:%s:%s' \"$?\" \"$x\" \"$y\" \"$1\"", tree);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("y=30,1/0: division by 0\n", result.StdErr);
    }

    [Theory]
    [InlineData("source /outer; printf after")]
    [InlineData("eval 'source /inner; printf middle'; printf after")]
    [InlineData("source /eval; printf after")]
    [InlineData("f() { source /inner; printf middle; }; f; printf after")]
    public async Task SourceAndEval_ArithmeticFailureStopsOnlyTheInnermostText(string script)
    {
        var tree = EndToEndHarness.BuildTree([
            ("/inner", "x=$((1/0)); printf bad"),
            ("/outer", "source /inner; printf middle"),
            ("/eval", "eval 'x=$((1/0)); printf bad'; printf middle")]);
        var result = await EndToEndHarness.RunScriptAsync(script, tree);

        Assert.Equal("middleafter", result.StdOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1/0: division by 0\n", result.StdErr);
    }

    [Theory]
    [InlineData("exit 7", "source /script; printf bad", "", 7, "")]
    [InlineData("exec cat <<<\"$((x=30,1/0))\"; printf bad", "x=10; source /script; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "x=30,1/0: division by 0\n")]
    [InlineData("exec cat <<<\"${MISSING:?required}\"; printf bad", "source /script; printf bad", "", 1, "MISSING: required\n")]
    [InlineData("exec cat <<<'ok'; printf bad", "source /script; printf bad", "ok\n", 0, "")]
    [InlineData("x=${MISSING:?required}", "source /script; printf bad", "", 1, "MISSING: required\n")]
    [InlineData("x=$((1+${MISSING:?required}))", "source /script; printf bad", "", 1, "MISSING: required\n")]
    [InlineData("printf bad", "source /script \"$((1/0))\"; printf bad", "", 1, "1/0: division by 0\n")]
    [InlineData("printf bad", "source /script <<<\"$((1/0))\"; printf bad", "", 1, "1/0: division by 0\n")]
    [InlineData("x=$((1/0)); printf bad", "source /script; printf '%s' \"$((2/0))\"; printf bad", "", 1, "1/0: division by 0\n2/0: division by 0\n")]
    [InlineData("x=$((1/0)); printf bad", "source /script 2>/error; printf '%s\\n' \"$?\"; cat /error", "1\n1/0: division by 0\n", 0, "")]
    public async Task Source_ArithmeticRecoveryPreservesOtherFailureAndRedirectionSemantics(
        string content, string script, string expectedOutput, int expectedStatus, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([("/script", content)]));

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("x=10; y=20; eval 'x=$((y=30,1/0)); printf bad'; printf '%s:%s:%s' \"$?\" \"$x\" \"$y\"", "1:10:30", 0, "y=30,1/0: division by 0\n")]
    [InlineData("eval 'x=$((1/0)); printf bad' || printf caught; printf after", "caughtafter", 0, "1/0: division by 0\n")]
    [InlineData("! eval 'x=$((1/0)); printf bad'; printf '%s' \"$?\"", "0", 0, "1/0: division by 0\n")]
    [InlineData("inner='x=$((1/0)); printf bad'; eval 'eval \"$inner\"; printf middle'; printf outer", "middleouter", 0, "1/0: division by 0\n")]
    [InlineData("f() { eval 'x=$((1/0)); printf bad'; printf inner; }; f; printf outer", "innerouter", 0, "1/0: division by 0\n")]
    [InlineData("eval 'f() { x=$((1/0)); printf bad; }; f; printf bad'; printf after", "after", 0, "1/0: division by 0\n")]
    [InlineData("x=10; y=20; x=15 eval 'x=$((y=30,1/0)); printf bad'; printf '%s:%s:%s' \"$?\" \"$x\" \"$y\"", "1:10:30", 0, "y=30,1/0: division by 0\n")]
    [InlineData("readonly r=1; eval 'x=$((r=2)); printf bad'; printf '%s:%s' \"$?\" \"$r\"", "1:1", 0, "r: readonly variable\n")]
    [InlineData("eval 'x=$((1/0))' 2>/error; printf '%s\\n' \"$?\"; cat /error", "1\n1/0: division by 0\n", 0, "")]
    [InlineData("eval 'x=$((1/0))'; printf '%s' \"$((2/0))\"; printf bad", "", 1, "1/0: division by 0\n2/0: division by 0\n")]
    [InlineData("eval \"$((1/0))\"; printf bad", "", 1, "1/0: division by 0\n")]
    [InlineData("eval 'printf bad' <<<\"$((1/0))\"; printf bad", "", 1, "1/0: division by 0\n")]
    [InlineData("eval 'x=${MISSING:?required}; printf bad'; printf bad", "", 1, "MISSING: required\n")]
    [InlineData("eval 'x=$((1+${MISSING:?required})); printf bad'; printf bad", "", 1, "MISSING: required\n")]
    [InlineData("eval 'exit 7'; printf bad", "", 7, "")]
    [InlineData("x=10; eval 'exec cat <<<\"$((x=30,1/0))\"; printf bad'; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "x=30,1/0: division by 0\n")]
    [InlineData("x=10; eval 'exec cat <\"$((x=30,1/0))\"; printf bad'; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "x=30,1/0: division by 0\n")]
    [InlineData("x=10; eval 'exec cat >\"$((x=30,1/0))\"; printf bad'; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "x=30,1/0: division by 0\n")]
    [InlineData("x=10; eval 'exec cat <<EOF\n$((x=30,1/0))\nEOF\nprintf bad'; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "x=30,1/0: division by 0\n")]
    [InlineData("eval 'command exec cat <<<\"$((1/0))\"; printf bad' || printf caught; printf after", "caughtafter", 0, "1/0: division by 0\n")]
    [InlineData("eval 'exec cat 2>/error <<<\"$((1/0))\"'; printf '%s\\n' \"$?\"; cat /error", "1\n1/0: division by 0\n", 0, "")]
    [InlineData("eval 'exec cat <<<\"${MISSING:?required}\"; printf bad'; printf bad", "", 1, "MISSING: required\n")]
    [InlineData("eval 'exec cat <<<ok; printf bad'; printf bad", "ok\n", 0, "")]
    public async Task Eval_HandlesOnlyArithmeticFailuresInsideItsText(
        string script, string expectedOutput, int expectedStatus, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("x=10; ((x++,)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:11\n", 0, "invalid arithmetic expression")]
    [InlineData("x=10; ((x=30, 1/0)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; locked=20; y=50; readonly locked; ((x=30, locked=40, y=60)); printf '%s:%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\" \"$y\"", "1:30:20:50\n", 0, "locked: readonly variable")]
    [InlineData("x=3; ((++x++)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:4\n", 0, "invalid arithmetic expression")]
    [InlineData("x=3; ((1 || ++x++)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:3\n", 0, "invalid arithmetic expression")]
    [InlineData("x=10; let 'x=30, 1/0' 'x=40'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("let ''; printf '%s\\n' \"$?\"", "1\n", 0, "")]
    [InlineData("(( )); printf '%s\\n' \"$?\"", "1\n", 0, "")]
    [InlineData("printf '%s\\n' \"$(( ))\"; printf done", "0\ndone", 0, "")]
    [InlineData("x=10; for ((x=30, 1/0;0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; for ((;x=30, 1/0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; for ((;x<20;x=30, 1/0)); do printf body; done; printf '%s:%s\\n' \"$?\" \"$x\"", "body1:30\n", 0, "division by 0")]
    [InlineData("x=10; ! for ((x=30,1/0;0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "0:30\n", 0, "division by 0")]
    [InlineData("x=10; printf '%s\\n' \"$((x++,))\"; printf after", "", 1, "invalid arithmetic expression")]
    [InlineData("x=10; printf '%s\\n' \"$((x=30, 1/0))\"; printf after", "", 1, "division by 0")]
    [InlineData("x=10; locked=20; readonly locked; printf '%s\\n' \"$((x=30, locked=40))\"; printf after", "", 1, "locked: readonly variable")]
    [InlineData("f() { printf '%s\\n' \"$((1/0))\"; printf inside; }; f || printf fallback; printf after", "", 1, "division by 0")]
    [InlineData("x=10; ! ((x=30,1/0)); printf '%s:%s\\n' \"$?\" \"$x\"", "0:30\n", 0, "division by 0")]
    [InlineData("i=0; for (( ; ; )); do ((i++)); break; done; printf '%s:%s\\n' \"$?\" \"$i\"", "0:1\n", 0, "")]
    [InlineData("for ((\r;0;)); do printf bad; done; printf '%s\\n' \"$?\"", "1\n", 0, "invalid arithmetic expression")]
    [InlineData("for ((;\r;)); do break; done; printf '%s\\n' \"$?\"", "1\n", 0, "invalid arithmetic expression")]
    [InlineData("for ((;1;\r)); do printf body; done; printf '%s\\n' \"$?\"", "body1\n", 0, "invalid arithmetic expression")]
    [InlineData("let 0 'x=2'; printf '%s:%s\\n' \"$?\" \"$x\"", "0:2\n", 0, "")]
    [InlineData("x=10; readonly locked=20; let 'x=30, locked=40' 'x=50'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "1:30:20\n", 0, "locked: readonly variable")]
    [InlineData("x=10; readonly locked=20; let 'locked=(x=30)'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "1:30:20\n", 0, "locked: readonly variable")]
    [InlineData("x=10; readonly locked=20; let '1 || (locked=(x=30))'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "0:10:20\n", 0, "")]
    [InlineData("x=10; ((x=30,2**-1)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "exponent less than 0")]
    [InlineData("x=10; ((x=30,1%0)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((1+=2))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$(((x)++))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((x++++))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2\r+3))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2\u000b+3))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2\f+3))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2 +3))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((++x++))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((--x--))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((x++--))\" \"$x\"", "", 1, "invalid arithmetic expression")]
    [InlineData("x=10; x=20 let 'x=30,1/0' 'x=40'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; x=20 let '1/0' 'x=40'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "division by 0")]
    [InlineData("x=10; y=50; readonly locked=20; x=15 y=60 let 'x=30, locked=40, y=70'; printf '%s:%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\" \"$y\"", "1:30:20:50\n", 0, "locked: readonly variable")]
    [InlineData("x=10; x=20 let 'x=0'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:0\n", 0, "")]
    [InlineData("x=10; readonly locked=20; x=15 let 'locked=(x=30)'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "1:30:20\n", 0, "locked: readonly variable")]
    [InlineData("x=10; f() { x=20 let 'x=30,1/0'; printf '%s:%s\\n' \"$?\" \"$x\"; }; x=15 f; printf '%s\\n' \"$x\"", "1:30\n10\n", 0, "division by 0")]
    [InlineData("x=10; ((x=30@)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "invalid arithmetic expression")]
    [InlineData("x=10; let 'x=30@'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "invalid arithmetic expression")]
    [InlineData("x=10; let 'x=12foo'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "invalid arithmetic expression")]
    [InlineData("x=10; y=20; ((x=(y=30)**=2)); printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$y\"", "1:10:30\n", 0, "invalid arithmetic expression")]
    [InlineData("x=10; for ((x=30@;0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "invalid arithmetic expression")]
    [InlineData("a=(10 kept); let 'a=1,a+=2'; printf '%s:<%s>:<%s>\\n' \"$?\" \"${a[0]}\" \"${a[1]}\"", "0:<3>:<kept>\n", 0, "")]
    [InlineData("a=(10 kept); let 'a=30,a'; printf '%s:<%s>:<%s>\\n' \"$?\" \"${a[0]}\" \"${a[1]}\"", "0:<30>:<kept>\n", 0, "")]
    [InlineData("declare -A a; a[0]=10; a[k]=kept; let 'a=1,a+=2'; printf '%s:<%s>:<%s>\\n' \"$?\" \"${a[0]}\" \"${a[k]}\"", "0:<3>:<kept>\n", 0, "")]
    [InlineData("a=(10 kept); declare -n r=a; let 'r=1,r+=2'; printf '%s:<%s>:<%s>\\n' \"$?\" \"${a[0]}\" \"${a[1]}\"", "0:<3>:<kept>\n", 0, "")]
    [InlineData("cat <<< \"$((1/0))\" || printf fallback; printf after", "fallbackafter", 0, "division by 0")]
    [InlineData("cat > \"$((1/0))\"; printf 'after:%s\\n' \"$?\"", "after:1\n", 0, "division by 0")]
    [InlineData("cat < \"$((1/0))\"; printf 'after:%s\\n' \"$?\"", "after:1\n", 0, "division by 0")]
    [InlineData("cat <<EOF\n$((1/0))\nEOF\nprintf 'after:%s\\n' \"$?\"", "after:1\n", 0, "division by 0")]
    [InlineData("! cat <<< \"$((1/0))\"; printf 'after:%s\\n' \"$?\"", "after:0\n", 0, "division by 0")]
    [InlineData("x=10; cat <<< \"$((x=30,1/0))\"; printf 'after:%s:%s\\n' \"$?\" \"$x\"", "after:1:10\n", 0, "division by 0")]
    [InlineData("x=10; cat <<< '$((x=30))'; printf '%s\\n' \"$x\"", "$((x=30))\n10\n", 0, "")]
    [InlineData("x=10; cat 2>/dev/null <<< \"$((x=30))\"; printf '%s\\n' \"$x\"", "30\n10\n", 0, "")]
    [InlineData("cat <<< \"${x:=30}\"; printf '<%s>\\n' \"$x\"", "30\n<>\n", 0, "")]
    [InlineData("cat <<<{#..%}{x:=30} >/dev/null; printf '<%s>\\n' \"$x\"", "<>\n", 0, "")]
    [InlineData("x=10; cat <<EOF\n$((x=30))\nEOF\nprintf '%s\\n' \"$x\"", "30\n10\n", 0, "")]
    [InlineData("true <<< \"$((1/0))\" || printf fallback; printf after", "", 1, "division by 0")]
    [InlineData("f() { :; }; f <<< \"$((1/0))\"; printf after", "", 1, "division by 0")]
    [InlineData("cat \"$((1/0))\"; printf after", "", 1, "division by 0")]
    [InlineData("exec cat <<< \"$((1/0))\"; printf after", "", 1, "division by 0")]
    [InlineData("cat <<< \"$((1/0))\"; printf '%s' \"$((2/0))\"; printf after", "", 1, "division by 0")]
    [InlineData("cat 2>/error <<< \"$((1/0))\"; printf 'after:%s\\n' \"$?\"; cat /error", "after:1\n1/0: division by 0\n", 0, "")]
    public async Task ArithmeticErrors_PreserveEarlierEffectsAndApplyContextStatus(
        string script, string expectedOutput, int expectedStatus, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedStatus, result.ExitCode);
        if (diagnostic.Length == 0)
            Assert.Empty(result.StdErr);
        else
            Assert.Contains(diagnostic, result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Let_FailurePreservesArrayBytesOutsideTheWrittenElement()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "a=(10 $'\\377'); a=20 let 'a=30,1/0' 'a=40'; printf '%s:<%s>:<%s>' \"$?\" \"${a[0]}\" \"${a[1]}\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(Convert.FromHexString("313A3C33303E3A3CFF3E"), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal("a=30,1/0: division by 0\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticCommand_ReadonlyWriteKeepsEarlierWrites()
    {
        var script = @"first=10; readonly locked=20; ((first=30, locked=40)); status=$?; printf '%s:<%s>:<%s>\n' ""$status"" ""$first"" ""$locked""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1:<30>:<20>\n", result.StdOut);
        Assert.Equal("locked: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticExpansion_FailureStopsScript()
    {
        var script = @"readonly value=10; printf '<%s>:<%s>\n' ""before$((value=20))after"" ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Equal("value: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

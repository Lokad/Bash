using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArithmeticErrorTestsEndToEnd
{
    [Theory]
    [InlineData("x++ y", "1:4:9", "syntax error in expression", "y")]
    [InlineData("(x) a[z++]", "1:3:1", "syntax error in expression", "a[z++]")]
    [InlineData("x++ y 12red", "1:4:0", "value too great for base", "12red")]
    [InlineData("++x y@", "1:3:0", "syntax error: invalid arithmetic operator", "@")]
    [InlineData("x++ a[", "1:4:0", "bad array subscript", "a[")]
    [InlineData("1 || x++ y", "1:3:0", "syntax error in expression", "y")]
    [InlineData("1 || x++ y@", "1:3:0", "syntax error: invalid arithmetic operator", "@")]
    public async Task TrailingArithmeticReference_ValidatesAndReadsInEvaluationOrder(
        string expression, string state, string diagnostic, string token)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"x=3; y='z=9'; z=0; a=(8 9)\nlet '{expression}'\nprintf '%s:%s:%s' \"$?\" \"$x\" \"$z\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(state, result.StdOut);
        Assert.Equal($"line 2: let: {expression}: {diagnostic} (error token is \"{token}\")\n", result.StdErr);
    }

    [Theory]
    [InlineData("let 'x++++y'", "1:4:8:5", "let: x++++y: syntax error in expression", "++y")]
    [InlineData("((x-- ++ y))", "1:2:8:5", "((: x-- ++ y: syntax error in expression", "++ y")]
    [InlineData("result=$((x++--y))", "1:4:8:5", "x++--y: syntax error in expression", "--y")]
    [InlineData("let '1--y'", "1:3:8:5", "let: 1--y: syntax error in expression", "--y")]
    [InlineData("let '(x++++y)'", "1:4:8:5", "let: (x++++y): missing `)'", "++y)")]
    [InlineData("let '1 || x++++y'", "1:3:8:5", "let: 1 || x++++y: syntax error in expression", "++y")]
    [InlineData("let 'q=1 + x++++y'", "1:4:8:4", "let: q=1 + x++++y: syntax error in expression", "++y")]
    public async Task DoubledInfixSignsBeforeVariable_KeepReachedWritesAndReportTheToken(
        string command, string state, string diagnostic, string token)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"x=3; y=8; q=5\n{command}\nprintf '%s:%s:%s:%s' \"$?\" \"$x\" \"$y\" \"$q\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(state, result.StdOut);
        Assert.Equal($"line 2: {diagnostic} (error token is \"{token}\")\n", result.StdErr);
    }

    [Theory]
    [InlineData(": \\\n\"$((8/0))\"", 3, "8/0: division by 0 (error token is \"0\")", 0, "<1><3>")]
    [InlineData(": first \\\n\"$((8/0))\"", 2, "8/0: division by 0 (error token is \"0\")", 0, "<1><3>")]
    [InlineData("((8/\n0))", 3, "((: 8/\n0: division by 0 (error token is \"\n0\")", 0, "<1><3>")]
    [InlineData("declare -i value=target=4", 2, "target: readonly variable", 1, "")]
    public async Task ArithmeticDiagnosticFollowsTheReachedSourceLocation(
        string command, int line, string message, int exitCode, string output)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", ":\n" + command + "\n");
        var result = await host.RunAsync("readonly target=3\nsource /unit 2>report; printf '<%s><%s>' \"$?\" \"$target\"",
            CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal($"/unit: line {line}: {message}\n", host.ReadFile("/report"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("i=8/0 \t ;i<2;i++", "i=8/0 \t ", "0 \t ")]
    [InlineData("i=0;\n 8/0 \n;i++", "\n 8/0 \n", "0 \n")]
    [InlineData("i=0;i<2;\n i=8/0 \n", "\n i=8/0 \n", "0 \n")]
    public async Task ArithmeticForDiagnosticsRetainClauseWhitespace(string clauses, string operand, string token)
    {
        var result = await EvaluationTestHelper.RunAsync($":\nfor (({clauses})); do :; done");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal($"line 2: ((: {operand}: division by 0 (error token is \"{token}\")\n", result.StdErr);
    }

    [Theory]
    [InlineData("x=$((1+))", "[1:1]")]
    [InlineData("printf '%s' \"${text:1+}\"", "[1:1]")]
    [InlineData("eval 'printf -v \"a[1+]\" bad\nprintf inside'", "[1:1]")]
    [InlineData("eval 'a[$((1+))]=bad\nprintf inside'", "insideoutside[0:0]")]
    public async Task ArithmeticFailurePublishesStatusAndRecoversAtItsEvaluationBoundary(
        string command, string expectedOutput)
    {
        var script = "text=abcd\n(exit 7) | true\n" + command
            + "; printf outside\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Contains("syntax error: operand expected", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(( 1 + ))", "line 1: ((: 1 + : syntax error: operand expected (error token is \"+ \")\n")]
    [InlineData("let '08 + 2'", "line 1: let: 08: value too great for base (error token is \"08\")\n")]
    [InlineData("let '1 / 0 @'", "line 1: let: 1 / 0 @: syntax error: invalid arithmetic operator (error token is \"@\")\n")]
    [InlineData("let '10 / (2 - 2) + 8'", "line 1: let: 10 / (2 - 2) + 8: division by 0 (error token is \"(2 - 2) + 8\")\n")]
    [InlineData("let '(1 = 2)'", "line 1: let: (1 = 2): attempted assignment to non-variable (error token is \"= 2)\")\n")]
    [InlineData("let '1 ? 2'", "line 1: let: 1 ? 2: `:' expected for conditional expression (error token is \"2\")\n")]
    [InlineData("let 'i=2,a['", "line 1: let: i=2,a[: bad array subscript (error token is \"a[\")\n")]
    [InlineData("x='2 / 0'; let 'x + 3'", "line 1: let: 2 / 0: division by 0 (error token is \"0\")\n")]
    [InlineData("[[ '2 / 0' -eq 0 ]]", "line 1: [[: 2 / 0: division by 0 (error token is \"0\")\n")]
    [InlineData("x=abcd; r=x; printf '%s' \"${!r:1/0}\"", "line 1: !r: 1/0: division by 0 (error token is \"0\")\n")]
    public async Task ArithmeticDiagnosticsPreserveTheReachedRoleAndToken(string script, string expectedError)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(1, status);
            Assert.Empty(host.StandardOutput.TextContent);
            Assert.Equal(expectedError, host.StandardError.TextContent);
        }
    }

    [Theory]
    [InlineData("v=0\nprintf \"%s\" \"$((++v + # note))\"\nprintf \"<%s:%s>\" \"$?\" \"$v\"",
        0, "<1:0>", "line 2: bad substitution: no closing `)' in \"$((++v + # note))\"\n")]
    [InlineData("present=kept; v=0\nprintf \"<%s>\" $((v=7))\"$((1 # note))\"\nprintf \"[after:%s:%s]\" \"$?\" \"$v\"",
        0, "[after:1:7]", "line 2: bad substitution: no closing `)' in $((v=7))\"$((1 # note))\"\n")]
    [InlineData("present=kept; v=0\nprintf \"<%s>\" \"${present:-$((1 # note))}\"\nprintf \"[after:%s:%s]\" \"$?\" \"$v\"",
        0, "[after:1:0]", "line 2: bad substitution: no closing `)' in \"${present:-$((1 # note))}\"\n")]
    [InlineData("present=kept\nprintf \"<%s>\" \"${present:-$(printf %s \"$((2 + $((1 # note))))\")}\"\nprintf \"[after:%s]\" \"$?\"",
        0, "<kept>[after:0]", "")]
    [InlineData("present=kept; v=0\nprintf \"<%s>\" \"$((1 # note) )\"\nprintf \"[after:%s:%s]\" \"$?\" \"$v\"",
        0, "[after:1:0]", "line 2: bad substitution: no closing `)' in \"$((1 # note) )\"\n")]
    [InlineData("present=kept\nprintf \"<%s>\" \"$((1 # hidden ))\n2 ))\"\nprintf \"[after:%s]\" \"$?\"",
        0, "<>[after:0]", "line 2: 1: command not found\nline 3: 2: command not found\n")]
    [InlineData("{ IFS= read -r value; printf \"<%s>\" \"$value\"; } <<DOC\n$((1 # note))\nDOC\nprintf \"<%s>\" \"$?\"",
        0, "<1>", "line 3: bad substitution: no closing `)' in $((1 # note))\n\n")]
    [InlineData("set -e\ntrap 'printf \"exit:%s\" \"$?\"' EXIT\nprintf \"%s\" \"$((1 # note))\"\nprintf later",
        1, "exit:1", "line 3: bad substitution: no closing `)' in \"$((1 # note))\"\n")]
    [InlineData("v=0\nprintf '<%s>' \"$(( $((v=5)) + " + QuotedChild + " ))\"\nprintf '[after:%s:%s]' \"$?\" \"$v\"",
        0, "[after:1:0]", "line 2: bad substitution: no closing `)' in  $((v=5)) + " + QuotedChild + " \n")]
    [InlineData("v=0\nprintf '<%s>' \"$(( $((v=5)) + $(printf 3; printf %s $((1 # note))) ))\"\nprintf '[after:%s:%s]' \"$?\" \"$v\"",
        0, "<8>[after:0:5]", "line 2: bad substitution: no closing `)' in $((1 # note))\n")]
    [InlineData("v=0\nprintf '<%s>' \"pre$((v=5))$[1+" + QuotedChild + "]tail\"\nprintf '[after:%s:%s]' \"$?\" \"$v\"",
        0, "[after:1:5]", "line 2: bad substitution: no closing `)' in pre$((v=5))$[1+" + QuotedChild + "]tail\n")]
    [InlineData("v=0; text=abcdef\nprintf '<%s>' \"${text:" + QuotedChild + ":2}\"\nprintf '[after:%s:%s]' \"$?\" \"$v\"",
        0, "[after:1:0]", "line 2: bad substitution: no closing `)' in " + QuotedChild + ":2\n")]
    [InlineData("v=0; text=abcdef\nprintf '<%s>' \"${text:$((v=1)):" + QuotedChild + "}\"\nprintf '[after:%s:%s]' \"$?\" \"$v\"",
        0, "<bcd>[after:0:1]", "line 2: bad substitution: no closing `)' in \"$((1 # note))\"\n")]
    [InlineData("present=kept\nprintf '<%s>' \"${present:-$((1+" + QuotedChild + "))}\"\nprintf '[after:%s]' \"$?\"",
        0, "<kept>[after:0]", "")]
    [InlineData("v=0\nfor ((v=0; v<1; v=1+" + QuotedChild + ")); do printf body; done\nprintf '[after:%s:%s]' \"$?\" \"$v\"",
        0, "body[after:0:4]", "line 2: bad substitution: no closing `)' in \"$((1 # note))\"\n")]
    public async Task ArithmeticCommentDelimitersPreserveExpansionBoundaries(
        string script, int expectedStatus, string expectedOutput, string expectedError)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(expectedStatus, status);
            Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
            Assert.Equal(expectedError, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    private const string QuotedChild = "$(printf 3; printf \"%s\" \"$((1 # note))\")";

    [Fact]
    public async Task IncompleteCaptureConsumesTheRemainingArithmeticForSeparators()
    {
        const string header = "((v=1+" + QuotedChild + "; 0; v++))";
        const string script = "v=0\nif false; then for " + header + "; do :; done; fi\nprintf after";
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("line 2: syntax error: arithmetic expression required\n"
            + "line 2: syntax error: `" + header + "'\n", host.StandardError.TextContent);
        Assert.Throws<SyntaxException>(() => Parser.Parse(script));
    }

    [Fact]
    public async Task UnquotedAdjacentCommentPreservesTheScriptUnitBoundary()
    {
        const string script = "printf '<%s>' $((1 + # ) )\n2))\nprintf '[done:%s]' \"$?\"\n";
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("line 1: bad substitution: no closing `)' in $((1 + # ) )\n"
            + "syntax error near unexpected token `)'\n`2))'\n", host.StandardError.TextContent);
        Assert.Throws<SyntaxException>(() => Parser.Parse(script));
    }

    [Fact]
    public async Task LegacyArithmeticQuoteFailureKeepsEarlierEffectsAndSkipsOperandEffects()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "printf early >before\nprintf '<%s>' \"$[$(printf data >inner; printf 2)+\"3\"]\" >after",
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("bad substitution", host.StandardError.TextContent, StringComparison.Ordinal);
        Assert.Equal("early", host.ReadFile("/before"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/inner"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/after"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("""false && printf '%s' "$[1+]"; printf tail""")]
    [InlineData("""false && printf '%s' "$["2"+3]"; printf tail""")]
    public async Task LegacyArithmeticFailuresAreDeferredUntilExpansion(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", result.StdOut);
        Assert.Empty(result.StdErr);
    }

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
        Assert.Equal("/script: line 1: y=30,1/0: division by 0 (error token is \"0\")\n", result.StdErr);
    }

    [Theory]
    [InlineData("source /outer; printf after", "/inner")]
    [InlineData("eval 'source /inner; printf middle'; printf after", "/inner")]
    [InlineData("source /eval; printf after", "/eval")]
    [InlineData("f() { source /inner; printf middle; }; f; printf after", "/inner")]
    public async Task SourceAndEval_ArithmeticFailureStopsOnlyTheInnermostText(string script, string source)
    {
        var tree = EndToEndHarness.BuildTree([
            ("/inner", "x=$((1/0)); printf bad"),
            ("/outer", "source /inner; printf middle"),
            ("/eval", "eval 'x=$((1/0)); printf bad'; printf middle")]);
        var result = await EndToEndHarness.RunScriptAsync(script, tree);

        Assert.Equal("middleafter", result.StdOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{source}: line 1: 1/0: division by 0 (error token is \"0\")\n", result.StdErr);
    }

    [Theory]
    [InlineData("exit 7", "source /script; printf bad", "", 7, "")]
    [InlineData("exec cat <<<\"$((x=30,1/0))\"; printf bad", "x=10; source /script; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "/script: line 1: x=30,1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("exec cat <<<\"${MISSING:?required}\"; printf bad", "source /script; printf bad", "", 1, "/script: line 1: MISSING: required\n")]
    [InlineData("exec cat <<<'ok'; printf bad", "source /script; printf bad", "ok\n", 0, "")]
    [InlineData("x=${MISSING:?required}", "source /script; printf bad", "", 1, "/script: line 1: MISSING: required\n")]
    [InlineData("x=$((1+${MISSING:?required}))", "source /script; printf bad", "", 1, "/script: line 1: MISSING: required\n")]
    [InlineData("printf bad", "source /script \"$((1/0))\"; printf bad", "", 1, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("printf bad", "source /script <<<\"$((1/0))\"; printf bad", "", 1, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("x=$((1/0)); printf bad", "source /script; printf '%s' \"$((2/0))\"; printf bad", "", 1, "/script: line 1: 1/0: division by 0 (error token is \"0\")\nline 1: 2/0: division by 0 (error token is \"0\")\n")]
    [InlineData("x=$((1/0)); printf bad", "source /script 2>/error; printf '%s\\n' \"$?\"; cat /error", "1\n/script: line 1: 1/0: division by 0 (error token is \"0\")\n", 0, "")]
    public async Task Source_ArithmeticRecoveryPreservesOtherFailureAndRedirectionSemantics(
        string content, string script, string expectedOutput, int expectedStatus, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([("/script", content)]));

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("x=10; y=20; eval 'x=$((y=30,1/0)); printf bad'; printf '%s:%s:%s' \"$?\" \"$x\" \"$y\"", "1:10:30", 0, "line 1: y=30,1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("eval 'x=$((1/0)); printf bad' || printf caught; printf after", "caughtafter", 0, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("! eval 'x=$((1/0)); printf bad'; printf '%s' \"$?\"", "0", 0, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("inner='x=$((1/0)); printf bad'; eval 'eval \"$inner\"; printf middle'; printf outer", "middleouter", 0, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("f() { eval 'x=$((1/0)); printf bad'; printf inner; }; f; printf outer", "innerouter", 0, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("eval 'f() { x=$((1/0)); printf bad; }; f; printf bad'; printf after", "after", 0, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("x=10; y=20; x=15 eval 'x=$((y=30,1/0)); printf bad'; printf '%s:%s:%s' \"$?\" \"$x\" \"$y\"", "1:10:30", 0, "line 1: y=30,1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("readonly r=1; eval 'x=$((r=2)); printf bad'; printf '%s:%s' \"$?\" \"$r\"", "1:1", 0, "line 1: r: readonly variable\n")]
    [InlineData("eval 'x=$((1/0))' 2>/error; printf '%s\\n' \"$?\"; cat /error", "1\nline 1: 1/0: division by 0 (error token is \"0\")\n", 0, "")]
    [InlineData("eval 'x=$((1/0))'; printf '%s' \"$((2/0))\"; printf bad", "", 1, "line 1: 1/0: division by 0 (error token is \"0\")\nline 1: 2/0: division by 0 (error token is \"0\")\n")]
    [InlineData("eval \"$((1/0))\"; printf bad", "", 1, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("eval 'printf bad' <<<\"$((1/0))\"; printf bad", "", 1, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("eval 'x=${MISSING:?required}; printf bad'; printf bad", "", 1, "line 1: MISSING: required\n")]
    [InlineData("eval 'x=$((1+${MISSING:?required})); printf bad'; printf bad", "", 1, "line 1: MISSING: required\n")]
    [InlineData("eval 'exit 7'; printf bad", "", 7, "")]
    [InlineData("x=10; eval 'exec cat <<<\"$((x=30,1/0))\"; printf bad'; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "line 1: x=30,1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("x=10; eval 'exec cat <\"$((x=30,1/0))\"; printf bad'; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "line 1: x=30,1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("x=10; eval 'exec cat >\"$((x=30,1/0))\"; printf bad'; printf '%s:%s' \"$?\" \"$x\"", "1:30", 0, "line 1: x=30,1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("x=10; eval 'exec cat <<EOF\n$((x=30,1/0))\nEOF\nprintf bad'; printf '%s:%s' \"$?\" \"$x\"", "bad0:30", 0, "line 4: x=30,1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("eval 'command exec cat <<<\"$((1/0))\"; printf bad' || printf caught; printf after", "caughtafter", 0, "line 1: 1/0: division by 0 (error token is \"0\")\n")]
    [InlineData("eval 'exec cat 2>/error <<<\"$((1/0))\"'; printf '%s\\n' \"$?\"; cat /error", "1\nline 1: 1/0: division by 0 (error token is \"0\")\n", 0, "")]
    [InlineData("eval 'exec cat <<<\"${MISSING:?required}\"; printf bad'; printf bad", "", 1, "line 1: MISSING: required\n")]
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
    [InlineData("x=10; ((x++,)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:11\n", 0, "syntax error: operand expected")]
    [InlineData("x=10; ((x=30, 1/0)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; locked=20; y=50; readonly locked; ((x=30, locked=40, y=60)); printf '%s:%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\" \"$y\"", "1:30:20:50\n", 0, "locked: readonly variable")]
    [InlineData("x=3; ((++x++)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:4\n", 0, "assignment requires lvalue")]
    [InlineData("x=3; ((1 || ++x++)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:3\n", 0, "assignment requires lvalue")]
    [InlineData("x=10; let 'x=30, 1/0' 'x=40'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("let ''; printf '%s\\n' \"$?\"", "1\n", 0, "")]
    [InlineData("(( )); printf '%s\\n' \"$?\"", "1\n", 0, "")]
    [InlineData("printf '%s\\n' \"$(( ))\"; printf done", "0\ndone", 0, "")]
    [InlineData("x=10; for ((x=30, 1/0;0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; for ((;x=30, 1/0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; for ((;x<20;x=30, 1/0)); do printf body; done; printf '%s:%s\\n' \"$?\" \"$x\"", "body1:30\n", 0, "division by 0")]
    [InlineData("x=10; ! for ((x=30,1/0;0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "0:30\n", 0, "division by 0")]
    [InlineData("x=10; printf '%s\\n' \"$((x++,))\"; printf after", "", 1, "syntax error: operand expected")]
    [InlineData("x=10; printf '%s\\n' \"$((x=30, 1/0))\"; printf after", "", 1, "division by 0")]
    [InlineData("x=10; locked=20; readonly locked; printf '%s\\n' \"$((x=30, locked=40))\"; printf after", "", 1, "locked: readonly variable")]
    [InlineData("f() { printf '%s\\n' \"$((1/0))\"; printf inside; }; f || printf fallback; printf after", "", 1, "division by 0")]
    [InlineData("x=10; ! ((x=30,1/0)); printf '%s:%s\\n' \"$?\" \"$x\"", "0:30\n", 0, "division by 0")]
    [InlineData("i=0; for (( ; ; )); do ((i++)); break; done; printf '%s:%s\\n' \"$?\" \"$i\"", "0:1\n", 0, "")]
    [InlineData("for ((\r;0;)); do printf bad; done; printf '%s\\n' \"$?\"", "1\n", 0, "syntax error: operand expected")]
    [InlineData("for ((;\r;)); do break; done; printf '%s\\n' \"$?\"", "1\n", 0, "syntax error: operand expected")]
    [InlineData("for ((;1;\r)); do printf body; done; printf '%s\\n' \"$?\"", "body1\n", 0, "syntax error: operand expected")]
    [InlineData("let 0 'x=2'; printf '%s:%s\\n' \"$?\" \"$x\"", "0:2\n", 0, "")]
    [InlineData("x=10; readonly locked=20; let 'x=30, locked=40' 'x=50'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "1:30:20\n", 0, "locked: readonly variable")]
    [InlineData("x=10; readonly locked=20; let 'locked=(x=30)'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "1:30:20\n", 0, "locked: readonly variable")]
    [InlineData("x=10; readonly locked=20; let '1 || (locked=(x=30))'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "0:10:20\n", 0, "")]
    [InlineData("x=10; ((x=30,2**-1)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "exponent less than 0")]
    [InlineData("x=10; ((x=30,1%0)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((1+=2))\" \"$x\"", "", 1, "attempted assignment to non-variable")]
    [InlineData("x=3; printf '%s:%s\\n' \"$(((x)++))\" \"$x\"", "", 1, "syntax error: operand expected")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((x++++))\" \"$x\"", "", 1, "syntax error: operand expected")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2\r+3))\" \"$x\"", "", 1, "invalid arithmetic operator")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2\u000b+3))\" \"$x\"", "", 1, "invalid arithmetic operator")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2\f+3))\" \"$x\"", "", 1, "invalid arithmetic operator")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((2 +3))\" \"$x\"", "", 1, "invalid arithmetic operator")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((++x++))\" \"$x\"", "", 1, "assignment requires lvalue")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((--x--))\" \"$x\"", "", 1, "assignment requires lvalue")]
    [InlineData("x=3; printf '%s:%s\\n' \"$((x++--))\" \"$x\"", "", 1, "syntax error: operand expected")]
    [InlineData("x=10; x=20 let 'x=30,1/0' 'x=40'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:30\n", 0, "division by 0")]
    [InlineData("x=10; x=20 let '1/0' 'x=40'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "division by 0")]
    [InlineData("x=10; y=50; readonly locked=20; x=15 y=60 let 'x=30, locked=40, y=70'; printf '%s:%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\" \"$y\"", "1:30:20:50\n", 0, "locked: readonly variable")]
    [InlineData("x=10; x=20 let 'x=0'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:0\n", 0, "")]
    [InlineData("x=10; readonly locked=20; x=15 let 'locked=(x=30)'; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$locked\"", "1:30:20\n", 0, "locked: readonly variable")]
    [InlineData("x=10; f() { x=20 let 'x=30,1/0'; printf '%s:%s\\n' \"$?\" \"$x\"; }; x=15 f; printf '%s\\n' \"$x\"", "1:30\n10\n", 0, "division by 0")]
    [InlineData("x=10; ((x=30@)); printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "value too great for base")]
    [InlineData("x=10; let 'x=30@'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "value too great for base")]
    [InlineData("x=10; let 'x=12foo'; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "value too great for base")]
    [InlineData("x=10; y=20; ((x=(y=30)**=2)); printf '%s:%s:%s\\n' \"$?\" \"$x\" \"$y\"", "1:10:30\n", 0, "syntax error: operand expected")]
    [InlineData("x=10; for ((x=30@;0;)); do printf bad; done; printf '%s:%s\\n' \"$?\" \"$x\"", "1:10\n", 0, "value too great for base")]
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
    [InlineData("cat 2>/error <<< \"$((1/0))\"; printf 'after:%s\\n' \"$?\"; cat /error", "after:1\nline 1: 1/0: division by 0 (error token is \"0\")\n", 0, "")]
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
        Assert.Equal("line 1: let: a=30,1/0: division by 0 (error token is \"0\")\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticCommand_ReadonlyWriteKeepsEarlierWrites()
    {
        var script = @"first=10; readonly locked=20; ((first=30, locked=40)); status=$?; printf '%s:<%s>:<%s>\n' ""$status"" ""$first"" ""$locked""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1:<30>:<20>\n", result.StdOut);
        Assert.Equal("line 1: locked: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticExpansion_FailureStopsScript()
    {
        var script = @"readonly value=10; printf '<%s>:<%s>\n' ""before$((value=20))after"" ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Equal("line 1: value: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

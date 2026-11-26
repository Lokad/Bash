using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationSubstitutionAndSplittingTestsEndToEnd
{
    private const string ReportForegroundStatus = "printf '<%s|%s>' \"$?\" \"${PIPESTATUS[*]-unset}\"";

    [Theory]
    [InlineData("", "<1|unset>")]
    [InlineData("! (exit 7) | :\n", "<1|1>")]
    [InlineData("(exit 7) | (exit 3)\n", "<3|7 3>")]
    public async Task RejectedBarePrefixRecoversTheReachedRootStatus(string before, string expected)
    {
        var script = before + "value=$(!;); printf skipped\n" + ReportForegroundStatus;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Contains("syntax error near unexpected token `)'", host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("eval", false)]
    [InlineData("source", false)]
    [InlineData("eval", true)]
    [InlineData("source", true)]
    public async Task RejectedBarePrefixPublishesInTheLoadedReader(string builtin, bool testedWithErrexit)
    {
        var reader = "(exit 7) | (exit 3)\nvalue=$(!;)\n" + ReportForegroundStatus + "\n";
        var invocation = builtin == "eval" ? "eval " + ShellArgumentEscaping.EscapeArgument(reader) : "source ./reader";
        var script = (testedWithErrexit ? "set -e; if " + invocation + "; then printf then; else printf else; fi" : invocation)
            + "\n" + ReportForegroundStatus;

        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            if (builtin == "source")
                host.AddFile("/reader", reader);
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal(testedWithErrexit ? "<1|1>then<0|0>" : "<1|1><0|0>", host.StandardOutput.TextContent);
            Assert.Contains("syntax error near unexpected token `)'", host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task RejectedBarePrefixPublishesBeforeAForkedReaderExits()
    {
        var action = ShellArgumentEscaping.EscapeArgument(ReportForegroundStatus + " >trap-out");
        var script = "( trap " + action + " EXIT; source ./reader; printf skipped )\n" + ReportForegroundStatus;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            host.AddFile("/reader", "(exit 7) | (exit 3)\nvalue=$(!;)\nprintf skipped >later\n");
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<1|1>", host.StandardOutput.TextContent);
            Assert.Equal("<1|1>", host.ReadFile("/trap-out"));
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/later"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("$(!;)", "XY")]
    [InlineData("$(printf yielded; time;)", "XyieldedY")]
    public async Task HereDocumentExpandsTheAuthoredTerminatedBarePrefix(string substitution, string expected)
    {
        var script = "TIMEFORMAT=\nread -r value <<EOF\nX" + substitution + "Y\nEOF\nprintf '%s' \"$value\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("earlier body line\nX$(!)Y", 3, ")", "!)Y")]
    [InlineData("X$(printf skipped >effect;\ntime)Y", 4, ")", "time)Y")]
    [InlineData("X$(ti\\\nme)Y", 3, ")", "time)Y")]
    [InlineData("X$(;)Y", 3, ";", ";)Y")]
    [InlineData("X${unset_value:-$(printf >)}Y", 3, ")", "printf >)}Y")]
    [InlineData("X$(read -r inner <<INNER >\ninput\nINNER\n)Y", 5, "newline", "read -r inner <<INNER >")]
    public async Task HereDocumentReportsTheReachedCaptureToken(string body, int line, string token, string spelling)
    {
        var script = "value=seed\nread -r value <<EOF\n" + body + "\nEOF\nprintf '<%s|%s>' \"$?\" \"$value\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<1|seed>", host.StandardOutput.TextContent);
            Assert.Equal($"command substitution: line {line}: syntax error near unexpected token `{token}'\n"
                + $"command substitution: line {line}: `{spelling}'\n", host.StandardError.TextContent);
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/effect"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("2>diagnostic", true)]
    [InlineData("2>&-", false)]
    public async Task HereDocumentCaptureDiagnosticUsesTheReachedStderr(string redirect, bool writesFile)
    {
        var script = "read -r value " + redirect + " <<EOF\nλ$(time)€\r\nEOF\n" + ReportForegroundStatus;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<1|unset>", host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
            if (writesFile)
                Assert.Equal("command substitution: line 2: syntax error near unexpected token `)'\n"
                    + "command substitution: line 2: `time)€\r'\n", host.ReadFile("/diagnostic"));
            else
                Assert.False(await EndToEndHarness.FileExistsAsync(host, "/diagnostic"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("", "X$(!)Y", "<1|unset>")]
    [InlineData("true\n", "X$(!)Y", "<1|1>")]
    [InlineData("(exit 7) | (exit 3)\n", "X$(!)Y", "<3|7 3>")]
    [InlineData("", "X$(printf incomplete", "<1|unset>")]
    [InlineData("", "X$(printf 'unclosed quote", "<1|1>")]
    [InlineData("", "$(read -r inner <<INNER)\nbody", "<1|1>")]
    public async Task RejectedHereDocumentDiscardsTheRootUnit(string previous, string body, string expected)
    {
        var script = previous + "read -r value <<EOF; printf same\n" + body + "\nEOF\n" + ReportForegroundStatus;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("( : )", "<3|3>")]
    [InlineData(": | read -r value", "<3|0 3>")]
    [InlineData("./absent", "<3|3>")]
    public async Task RejectedHereDocumentPublishesTheFailedChildStatus(string command, string expected)
    {
        var script = "(exit 7) | (exit 3)\n" + command + " <<EOF\nX$(!)Y\nEOF\n" + ReportForegroundStatus;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("set -e\n", "read -r value 2>errors <<EOF || printf skipped", "X$(!)Y", "<2|0>", 4)]
    [InlineData("", "read -r value 2>errors <<EOF; printf skipped", "X$(printf '%s' \"$(!)\")Y", "<2|2>", 3)]
    public async Task RejectedHereDocumentRunsExitWithItsReachedStatusAndStderr(
        string options, string command, string body, string exitState, int line)
    {
        var action = ShellArgumentEscaping.EscapeArgument(ReportForegroundStatus + " >&2");
        var script = options + "trap " + action + " EXIT\n" + command + "\n" + body + "\nEOF\nprintf skipped\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(2, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Empty(host.StandardOutput.Content.ToArray());
            Assert.Empty(host.StandardError.Content.ToArray());
            var diagnostics = host.ReadFile("/errors");
            Assert.StartsWith($"command substitution: line {line}: syntax error near unexpected token `)'\n", diagnostics);
            Assert.EndsWith(exitState, diagnostics);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("eval", false)]
    [InlineData("source", true)]
    public async Task RejectedHereDocumentPublishesStatusInTheLoadedReader(string builtin, bool forked)
    {
        var content = "(exit 7) | (exit 3)\nread -r value <<EOF; printf skipped\nX$(!)Y\nEOF\n" + ReportForegroundStatus + "\n";
        var invocation = builtin == "source" ? "source ./reader" : "eval " + ShellArgumentEscaping.EscapeArgument(content);
        var action = ShellArgumentEscaping.EscapeArgument(ReportForegroundStatus + " >trap-out");
        var script = (forked ? "( trap " + action + " EXIT; " + invocation + "; printf skipped )" : invocation)
            + "\n" + ReportForegroundStatus;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            if (builtin == "source")
                host.AddFile("/reader", content);
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal(forked ? "<1|1>" : "<1|1><0|0>", host.StandardOutput.TextContent);
            if (forked)
                Assert.Equal("<1|1>", host.ReadFile("/trap-out"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task RejectedHereDocumentStopsTheEnclosingCapture()
    {
        const string script = "captured=$(\nprintf earlier\n(exit 7) | (exit 3)\nread -r value <<EOF\nX$(!)Y\nEOF\n"
            + "printf skipped\n)\nprintf '<%s|%s>' \"$?\" \"$captured\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<1|earlier>", host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("read -r value")]
    [InlineData("{ :; }")]
    [InlineData("value=ready")]
    public async Task RejectedHereDocumentKeepsOnlyTheReachedRedirection(string command)
    {
        var script = ": seed\n" + command + " 2>errors <<EOF 3>late\nX$(!)Y\nEOF\nprintf after >&2\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.EndsWith("after", host.ReadFile("/errors"));
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/late"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("eval")]
    public async Task RejectedHereDocumentPreservesWithinTheLoadedInvocation(string builtin)
    {
        const string reader = "read -r value 2>errors <<EOF\nX$(!)Y\nEOF\nprintf inside >&2\n";
        var invocation = builtin == "source" ? "source ./reader" : "eval " + ShellArgumentEscaping.EscapeArgument(reader);
        var script = invocation + " 2>invocation\nprintf parent >&2\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            if (builtin == "source")
                host.AddFile("/reader", reader);
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("parent", host.StandardError.TextContent);
            Assert.EndsWith("inside", host.ReadFile("/errors"));
            Assert.Empty(host.ReadFile("/invocation"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task RejectedHereDocumentKeepsTheCapturedChildsExitMapping()
    {
        const string script = "captured=$(trap 'printf child >&2' EXIT\nread -r value 2>errors <<EOF\nX$(!)Y\nEOF\n"
            + "printf skipped\n)\nprintf '<%s|%s>' \"$?\" \"$captured\"; printf parent >&2\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<1|>", host.StandardOutput.TextContent);
            Assert.Equal("parent", host.StandardError.TextContent);
            Assert.EndsWith("child", host.ReadFile("/errors"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    private const string ReportPipelineBinding = "printf '<%s|%s|%s>' \"$?\" \"${PIPESTATUS[*]-unset}\" \"${PIPESTATUS@a}\"\n";
    private const string RejectedDocument = "read -r value <<EOF\nX$(!)Y\nEOF\n";

    [Theory]
    [InlineData(RejectedDocument)]
    [InlineData("read -r value <<EOF\nX$(printf incomplete\nEOF\n")]
    [InlineData("f() { :; }\n" + RejectedDocument)]
    [InlineData("for item in; do :; done\n" + RejectedDocument)]
    [InlineData("value=$(!;)\n")]
    public async Task FreshCaptureRecoveryKeepsTheEmptyPipelineBinding(string rejection)
    {
        var script = rejection + ReportPipelineBinding + "(exit 7) | (exit 3)\n" + ReportPipelineBinding + "declare -p PIPESTATUS\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<1|unset|a><3|unset|a>declare -a PIPESTATUS=()\n", host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task RecoveredPipelineBindingKeepsItsEmptyArrayDuringElementAndWholeAssignments()
    {
        const string script = RejectedDocument + "PIPESTATUS=(8 9)\n"
            + "assigned=$((PIPESTATUS[0]=8)) observed=${PIPESTATUS[0]-missing}\n"
            + "printf '<%s|%s>' \"$assigned\" \"$observed\"\nreadonly -a PIPESTATUS=(1 2)\ndeclare -p PIPESTATUS\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<8|missing>declare -ar PIPESTATUS=()\n", host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("( unset PIPESTATUS; ", ")\n")]
    [InlineData("f() { local -a PIPESTATUS=(8); ", "}\nf\n")]
    public async Task RecoveredPipelineBindingSurvivesChildResetAndLocalShadowing(string enter, string leave)
    {
        var script = RejectedDocument + enter + ReportPipelineBinding + leave + ReportPipelineBinding;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<0|0|a><0|unset|a>", host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task UnsettingTheRecoveredPipelineBindingResumesPublication()
    {
        const string script = RejectedDocument + "unset PIPESTATUS\n(exit 7) | (exit 3)\n" + ReportPipelineBinding;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<3|7 3|a>", host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstLoadedReaderKeepsItsRecoveredPipelineBindingInTheCaller(bool source)
    {
        const string reader = RejectedDocument + ReportPipelineBinding;
        var invocation = source ? "source ./reader" : "eval " + ShellArgumentEscaping.EscapeArgument(reader);
        var script = invocation + "\n" + ReportPipelineBinding;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            if (source)
                host.AddFile("/reader", reader);
            Assert.Equal(0, await RunSubstitutionFixtureAsync(script, host, prepared));
            Assert.Equal("<1|unset|a><0|unset|a>", host.StandardOutput.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    private static async Task<int> RunSubstitutionFixtureAsync(string script, TestHost host, bool prepared) => prepared
        ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
        : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BareTimeSubstitutionPreservesAssignmentAndAbandonsTheCurrentCommandUnit(bool prepared)
    {
        const string source = "TIMEFORMAT=\nvalue=seed\n"
            + "value=$(printf child >effect; time;); printf same;\n"
            + "printf '<%s:%s>' \"$?\" \"$value\"\n";
        await using var host = new TestHost();
        var status = prepared
            ? (await EndToEndHarness.RunScriptAsync(source, host)).ExitCode
            : (await host.RunAsync(source, CancellationToken.None)).ExitCode;

        Assert.Equal(0, status);
        Assert.Equal("<1:seed>", host.StandardOutput.TextContent);
        Assert.Equal("command substitution: line 4: syntax error near unexpected token `)'\n"
            + "command substitution: line 4: `printf child > effect; time )'\n", host.StandardError.TextContent);
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/effect"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NestedBareTimeFailsOnlyWhenTheEnclosingSubstitutionIsReached(bool prepared, bool reached)
    {
        var source = "TIMEFORMAT=\nprintf earlier\n" + (reached ? "" : "true || ")
            + "value=$(printf child >effect; true || inner=$(time;); :)\nprintf later\n";
        await using var host = new TestHost();
        var status = prepared
            ? (await EndToEndHarness.RunScriptAsync(source, host)).ExitCode
            : (await host.RunAsync(source, CancellationToken.None)).ExitCode;

        Assert.Equal(reached ? 2 : 0, status);
        Assert.Equal(reached ? "earlier" : "earlierlater", host.StandardOutput.TextContent);
        if (reached)
            Assert.Equal("command substitution: line 4: syntax error near unexpected token `)'\n"
                + "command substitution: line 4: `printf child > effect; true || inner=$(time ); :)'\n",
                host.StandardError.TextContent);
        else
            Assert.Empty(host.StandardError.Content.ToArray());
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/effect"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("$((time;);)", "<0:>", "")]
    [InlineData("$( (time;);)", "<1:seed>", "command substitution: line 4: syntax error near unexpected token `)'\n"
        + "command substitution: line 4: ` ( time  ))'\n")]
    public async Task AdjacentSubshellOpeningPreservesItsFinalTimingSeparator(string substitution, string expected, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync("TIMEFORMAT=\nvalue=seed\nvalue=" + substitution
            + "\nprintf '<%s:%s>' \"$?\" \"$value\"\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BareTimeSubstitutionTerminatesAnErrexitEnabledScriptEvenInATestedGroup(bool prepared)
    {
        const string source = "TIMEFORMAT=\nset -e\n{ value=$(time;); printf inside; } || printf fallback\nprintf later\n";
        await using var host = new TestHost();
        var status = prepared
            ? (await EndToEndHarness.RunScriptAsync(source, host)).ExitCode
            : (await host.RunAsync(source, CancellationToken.None)).ExitCode;

        Assert.Equal(2, status);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal("command substitution: line 4: syntax error near unexpected token `)'\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("$(!;)", "! )")]
    [InlineData("$(! time -p;)", "time -p ! )")]
    [InlineData("\"${missing:-$(time;)}\"", "time )}\"")]
    public async Task FinalBarePrefixReportsTheRenderedCaptureAndPreservesTheAssignment(string operand, string displayed)
    {
        var source = "TIMEFORMAT=\nvalue=seed\nvalue=" + operand + "\nprintf '<%s:%s>' \"$?\" \"$value\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(source, host)).ExitCode
                : (await host.RunAsync(source, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal("<1:seed>", host.StandardOutput.TextContent);
            Assert.Equal("command substitution: line 4: syntax error near unexpected token `)'\n"
                + $"command substitution: line 4: `{displayed}'\n", host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("printf '%s' \"${missing:-$(cat <<'EOF'\na\\\nb\nEOF\n)}\"", "a\\\nb")]
    [InlineData("printf '%s' \"$\\\n((1+\\\n1))\"", "2")]
    [InlineData("value=2; printf '%s' \"$(( $\\\n{value} + 1 ))\"", "3")]
    [InlineData("printf '<%s>' a\r\nprintf end", "<a\r>end")]
    public async Task Evaluation_LogicalContinuationsPreserveNestedAndCarriageReturnData(
        string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }


    [Fact]
    public async Task Evaluation_CommandSubstitution_AcceptsContinuedOpener()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '%s' $\\\n(printf ok)");

        Assert.Equal("ok", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterExpansion_AcceptsContinuedOpener()
    {
        var result = await EvaluationTestHelper.RunAsync("value=ok; printf '%s' $\\\n{value}");

        Assert.Equal("ok", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("printf '%s' $\\\n((1+1))")]
    [InlineData("printf '%s' $(\\\n(1+1))")]
    [InlineData("printf '%s' $((1+\\\n1))")]
    [InlineData("printf '%s' $((1+1)\\\n)")]
    public async Task Evaluation_ArithmeticExpansion_AcceptsLogicalContinuations(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("2", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UnquotedCarriageReturn_RemainsWordContent()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' a\rb");

        Assert.Equal("<a\rb>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_UsesCapturedOutput()
    {
        // This test verifies that command substitution captures STDOUT and inserts it into the command line.
        // The script "echo $(echo hi)" must output "hi\n".
        // If substitution is skipped, STDOUT would be empty.
        var script = @"echo $(echo hi)";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_StripsTrailingNewlines()
    {
        // This test verifies that command substitution strips trailing newlines.
        // The script echoes the result of printf with two trailing newlines; STDOUT must be "hi\n".
        // If newlines are not stripped, the output would contain extra blank lines.
        var script = @"echo $(printf 'hi\n\n')";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_SplitsUnquotedOutput()
    {
        // This test verifies that unquoted command substitution is subject to word splitting.
        // The script prints "a b" via substitution and passes it to printf, which must output "a|b".
        // If splitting does not occur, the output would be "a b|".
        var script = @"printf '%s|%s' $(printf 'a b')";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_SkipsSplitInDoubleQuotes()
    {
        // This test verifies that quoted command substitution suppresses word splitting.
        // The script passes "$(printf 'a b')" as a single argument to printf and "tail" as the second.
        // The output must be "a b|tail".
        // If splitting occurs, the first two words would be "a" and "b".
        var script = @"printf '%s|%s' ""$(printf 'a b')"" tail";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b|tail", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("empty=; for x in $empty y; do printf '[%s]\\n' \"$x\"; done", "[y]\n")]
    [InlineData("for x in $(true) y; do printf '[%s]\\n' \"$x\"; done", "[y]\n")]
    [InlineData("empty=; for x in \"$empty\" y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    [InlineData("for x in \"\" y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    [InlineData("for x in '' y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    [InlineData("empty=; for x in $empty\"\" y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    public async Task Evaluation_WordSplitting_DistinguishesImplicitAndExplicitEmptyFields(
        string script,
        string expectedStdOut)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedStdOut, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("unset missing; sh -c 'echo \"$#\"' runner $missing", "0\n")]
    [InlineData("unset missing; sh -c 'echo \"$#\"' runner \"$missing\"", "1\n")]
    [InlineData("sh -c 'echo \"$#\"' runner \"\"", "1\n")]
    public async Task Evaluation_CommandArguments_DistinguishImplicitAndExplicitEmptyFields(
        string script,
        string expectedStdOut)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedStdOut, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsFunctionDefinitionParens()
    {
        var script = @"echo ""$(f() { echo hi; }; f)""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsNestedSubshellParens()
    {
        var script = @"echo ""$( (echo hi) )""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsNestedSubstitutionWithSubshellParens()
    {
        var script = @"echo ""$(echo $( (echo hi) ))""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsLeadingTerminatorBeforeNestedSubshellParens()
    {
        var script = "echo \"$(\n(echo hi)\n)\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsCasePatternParens()
    {
        var script = @"echo ""$(case x in x) echo hi ;; esac)""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_DoesNotLeakAssignments()
    {
        var script = @"x=outer; echo ""$(x=inner)""; echo ""$x""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\nouter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_UsesCapturedOutput()
    {
        var script = @"echo `echo hi`";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_SplitsUnquotedOutput()
    {
        var script = @"printf '%s|%s' `printf 'a b'`";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_SkipsSplitInDoubleQuotes()
    {
        var script = @"printf '%s|%s' ""`printf 'a b'`"" tail";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b|tail", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EscapedLegacyBackticks_AreLiteral()
    {
        var script = @"echo \`echo hi\`";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("`echo hi`\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_ExpandsInsideBracedParameterDefault()
    {
        var script = @"echo ${missing:-`printf hi`}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionInsideBracedParameterDefault_AllowsClosingBraceInPayload()
    {
        var script = @"echo ${missing:-$(printf '}')}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitutionInsideBracedParameterDefault_AllowsClosingBraceInPayload()
    {
        var script = @"echo ${missing:-`printf '}'`}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionInsideSingleQuotedBracedParameterDefault_IsLiteral()
    {
        var script = @"echo ${missing:-'$(printf hi)'}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("$(printf hi)\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitutionInsideSingleQuotedBracedParameterDefault_IsLiteral()
    {
        var script = @"echo ${missing:-'`printf hi`'}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("`printf hi`\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionInsideDoubleQuotedBracedParameterDefault_ExpandsWithoutQuoteLiterals()
    {
        var script = @"echo ${missing:-""$(printf hi)""}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SingleQuotedBracedParameterDefault_AllowsClosingBraceLiteral()
    {
        var script = @"echo ${missing:-'}'}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DoubleQuotedBracedParameterDefault_AllowsClosingBraceLiteral()
    {
        var script = @"echo ${missing:-""}""}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_UsesDefaultIfs()
    {
        // This test verifies that default IFS splits on whitespace.
        // The script expands "a b" into two arguments and prints them as "a|b".
        // If default splitting is disabled, the output would be "a b|".
        var script = @"var=""a b""; printf '%s|%s' $var";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_UsesCustomIfs()
    {
        // This test verifies that IFS controls splitting characters.
        // The script sets IFS=, and splits "a,b,c" into three arguments, printing "a|b|c".
        // If IFS is ignored, the output would be "a,b,c||".
        var script = @"IFS=,; var=""a,b,c""; printf '%s|%s|%s' $var";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b|c", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_PreservesEmptyFieldsForNonWhitespaceIfs()
    {
        // Count arguments: printf's missing-argument padding hides a spurious trailing field.
        var script = @"IFS=,; var=""a,,b,""; set -- $var; printf 'count=%s' ""$#""; for item in ""$@""; do printf ':<%s>' ""$item""; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("count=3:<a>:<>:<b>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_RespectsEmptyIfs()
    {
        // This test verifies that empty IFS disables splitting.
        // The script sets IFS= and expands "a b" into a single argument, printing "a b".
        // If splitting still happens, the output would be "a|b".
        var script = @"IFS=; var=""a b""; printf '%s' $var";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

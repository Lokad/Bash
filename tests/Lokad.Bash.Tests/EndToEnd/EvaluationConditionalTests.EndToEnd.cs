using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationConditionalTestsEndToEnd
{
    [Theory]
    [InlineData("printf '%s\\n' [[ value ]]", "[[\nvalue\n]]\n")]
    [InlineData("printf '%s\\n' [[", "[[\n")]
    [InlineData("printf '%s\\n' [\\\n[ value ]]", "[[\nvalue\n]]\n")]
    [InlineData("printf '%s\\n' [[ value ]\\\n]", "[[\nvalue\n]]\n")]
    [InlineData("for value in [[ value ]]; do printf '%s\\n' \"$value\"; done", "[[\nvalue\n]]\n")]
    [InlineData("true && [[ value = value ]] && printf ok", "ok")]
    [InlineData("printf '%s\\n' [[ value ]] | cat", "[[\nvalue\n]]\n")]
    [InlineData("if [[ value = value ]]; then printf ok; fi", "ok")]
    [InlineData("value=yes printf '%s\\n' [[ value ]]", "[[\nvalue\n]]\n")]
    public async Task Evaluation_ConditionalKeywordsRespectCommandPosition(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("(\\\n(1))")]
    [InlineData("((1\\\n))")]
    public async Task Evaluation_ArithmeticCommand_AcceptsLogicalContinuations(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticCommandRejectsContinuedClosingParentheses()
    {
        await Assert.ThrowsAsync<SyntaxException>(() => EvaluationTestHelper.RunAsync("((1)\\\n)"));
    }

    [Fact]
    public async Task Evaluation_ArithmeticCommand_YieldsOperation()
    {
        // This test verifies that arithmetic command evaluation succeeds for non-zero results.
        // The script "((1))" must emit no output and exit with code 0.
        // If arithmetic evaluation is broken, the exit code would be 1.
        var script = @"((1))";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticCommand_ZeroResultReturnsFailure()
    {
        // This test verifies that arithmetic command evaluation fails for zero results.
        // The script "((0))" must emit no output and exit with code 1.
        // If zero is treated as success, the exit code would be 0.
        var script = @"((0))";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_EvaluatesExpression()
    {
        // This test verifies that [[ -n ]] succeeds for a non-empty string.
        // The script "[[ -n 1 ]]" must emit no output and exit with code 0.
        // If the predicate is inverted, the exit code would be 1.
        var script = @"[[ -n 1 ]]";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_FalseReturnsFailure()
    {
        // This test verifies that [[ -n "" ]] fails for an empty string.
        // The script must emit no output and exit with code 1.
        // If empty strings are treated as true, the exit code would be 0.
        var script = @"[[ -n """" ]]";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of [[ ... ]].
        // The script "! [[ -n 1 ]]" must emit no output and exit with code 1.
        // If negation is ignored, the exit code would be 0.
        var script = @"! [[ -n 1 ]]";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_ChecksDirectoryKind()
    {
        // This test verifies that file tests are evaluated for [[ -d path ]].
        // The script "[[ -d /target ]]" must succeed with exit code 0.
        // If file tests are not wired, the exit code would be 1.
        var script = @"[[ -d /target ]]";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CaseCommand_SelectsMatchingItem()
    {
        // This test verifies that case selects the first matching pattern.
        // The script matches "foo" and executes the corresponding branch, outputting "second\n".
        // If matching fails, STDOUT would be empty.
        var script = @"case foo in bar) echo first ;; foo) echo second ;; esac";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("second\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CaseCommand_NoMatchReturnsZero()
    {
        // This test verifies that case returns success when no patterns match.
        // The script has no matching branch and must emit no output with exit code 0.
        // If no-match is treated as failure, the exit code would be 1.
        var script = @"case foo in bar) echo first ;; esac";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CaseCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a case command.
        // The script "! case foo in bar) echo first ;; esac" must emit no output and exit with code 1.
        // If negation is ignored, the exit code would be 0.
        var script = @"! case foo in bar) echo first ;; esac";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("false; case ok in ok) ;; *) printf wrong;; esac; printf 'status:%s' \"$?\"", "status:0", 0)]
    [InlineData("false; case ok in other) ;; esac; printf 'status:%s' \"$?\"", "status:0", 0)]
    [InlineData("case yes in no) ;; yes) printf selected; false;; esac", "selected", 1)]
    [InlineData("case yes in yes) ;; ${MISSING:?bad}) printf wrong;; esac; printf after", "after", 0)]
    [InlineData("! case ok in ok) ;; esac", "", 1)]
    [InlineData("case ok in\n  ok)\n    # Nothing to do.\n    ;;\nesac\nprintf after", "after", 0)]
    [InlineData("for value in no yes; do case \"$value\" in yes) ;; *) continue;; esac; printf '%s' \"$value\"; done", "yes", 0)]
    [InlineData("printf '[%s]' \"$(case ok in ok) ;; esac; printf after)\"", "[after]", 0)]
    [InlineData("false; case ok in ok) esac; printf 'status:%s' \"$?\"", "status:0", 0)]
    [InlineData("case ok in\n  ok)\n    # Nothing to do.\nesac\nprintf after", "after", 0)]
    public async Task Evaluation_CaseCommand_EmptyItemsPreserveControlFlow(string script, string expected, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CaseCommand_FinalItemCanEndAtEsac()
    {
        var result = await EvaluationTestHelper.RunAsync("case ok in ok) printf selected; false; esac");

        Assert.Equal("selected", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticForCommand_ExecutesSingleIteration()
    {
        // This test verifies that arithmetic for loops execute when the condition is true.
        // The script loops once and echoes "once"; STDOUT must be "once\n".
        // If the loop body is skipped, STDOUT would be empty.
        var script = @"for ((i=0; i<1; i++)); do echo once; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("once\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticForCommand_AllowsZeroIterations()
    {
        // This test verifies that arithmetic for loops allow zero iterations.
        // The script uses a false condition and must emit no output with exit code 0.
        // If the loop executes anyway, STDOUT would contain "never\n".
        var script = @"for ((i=0; i<0; i++)); do echo never; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticForCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of arithmetic for loops.
        // The script "! for ((i=0; i<1; i++)); do :; done" must emit no output and exit with code 1.
        // If negation is ignored, the exit code would be 0.
        var script = @"! for ((i=0; i<1; i++)); do :; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("case yes in yes) printf ok;; ${MISSING:?bad}) printf no;; esac; printf after", "okafter")]
    [InlineData("case yes in yes|${MISSING:?bad}) printf ok;; esac; printf after", "okafter")]
    [InlineData("case yes in yes) printf ok;; $(printf unexpected >&2)) printf no;; esac", "ok")]
    [InlineData("case yes in $(printf no)) printf wrong;; $(printf yes)|$(printf unexpected >&2)) printf ok;; esac", "ok")]
    public async Task Case_ExpandsOnlyPatternsNeededToFindTheFirstMatch(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData(false, "'7'", "373A6D697373")]
    [InlineData(false, "$'\\377'", "FF7C373A6D697373")]
    [InlineData(true, "'7'", "373A6D697373")]
    [InlineData(true, "$'\\377'", "FF7C373A6D697373")]
    public async Task Case_KeepsSubjectBytesAndPublishesPatternEffects(bool prepared, string wanted, string expectedHex)
    {
        var script = "subject=$'\\377'; wanted=" + wanted + "\n"
            + "case \"$subject\" in ${mark:=miss}|$((subject=7))|\"$wanted\") printf '%s|' \"$wanted\";; esac\n"
            + "printf '%s:%s' \"$subject\" \"$mark\"\n";
        await using var host = new TestHost();
        var status = prepared
            ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
            : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

        Assert.Equal(0, status);
        Assert.Equal(Convert.FromHexString(expectedHex), host.StandardOutput.Content.ToArray());
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Case_KeepsTheSubjectAndPendingPatternThroughCancellation()
    {
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        await using var host = new TestHost { MemoryBudget = owner };
        host.AddFile("/pattern", "tail\n");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterReadOpen = async path =>
        {
            Assert.Equal("/pattern", path.Path);
            entered.TrySetResult();
            await release.Task;
        };

        var state = new Evaluation.Evaluation(Parser.Parse(
            """case "$subject" in "$prefix$(<pattern)") printf selected;; esac; printf after"""));
        const int width = 16 * 1024;
        state.SetVariable("subject", ShellValue.FromText(new string('a', width) + "tail"));
        state.SetVariable("prefix", ShellValue.FromText(new string('a', width)));
        var running = new Engine().RunAsync(state, host, owner, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.Equal(1, host.OpenFileCount);
            var retainedValues = Variables.EstimateValueBytes(width + 4) + Variables.EstimateValueBytes(width);
            Assert.True(state.Memory.Workspace.Bytes >= retainedValues + 2L * width);
        }
        finally
        {
            release.TrySetResult();
        }

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(0, host.OpenFileCount);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("case ${MISSING:?bad} in $(printf unexpected >&2)) printf no;; esac; printf after")]
    [InlineData("case yes in ${MISSING:?bad}|$(printf unexpected >&2)) printf no;; esac; printf after")]
    [InlineData("! case yes in no) printf wrong;; ${MISSING:?bad}) printf no;; esac; printf after")]
    public async Task Case_StopsOnFatalExpansionInTheSubjectOrAReachedPattern(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("MISSING: bad\n", result.StdErr);
    }
}

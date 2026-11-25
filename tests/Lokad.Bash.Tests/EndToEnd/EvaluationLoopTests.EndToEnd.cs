using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationLoopTestsEndToEnd
{
    [Theory]
    [InlineData("break", "x", false)]
    [InlineData("break", "x", true)]
    [InlineData("continue", "xy", false)]
    [InlineData("continue", "xy", true)]
    public async Task ChildLoopFlowUsesOnlyLoopsEnteredInTheChild(string flow, string innerOutput, bool pipeline)
    {
        var child = flow + "; printf 'start'; "
            + "for inner in x y; do printf '%s' \"$inner\"; " + flow + " 2; printf unexpected; done; "
            + "printf 'end';";
        var invocation = pipeline ? "{ " + child + " } | { read text; printf '%s' \"$text\"; }" : "( " + child + " )";
        var script = "for outer in a b; do " + invocation + "; printf '<%s:%s>' \"$outer\" \"$?\"; done";
        var result = await EvaluationTestHelper.RunAsync(script);
        var diagnostic = "line 1: " + flow + ": only meaningful in a `for', `while', or `until' loop\n";

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"start{innerOutput}end<a:0>start{innerOutput}end<b:0>", result.StdOut);
        Assert.Equal(diagnostic + diagnostic, result.StdErr);
    }

    public static IEnumerable<object[]> ConditionRoutes()
    {
        foreach (var kind in new[] { "while", "until" })
            foreach (var flow in new[] { "break", "continue" })
                foreach (var route in new[] { "direct", "group", "eval", "source" })
                    foreach (var prepared in new[] { false, true })
                        yield return [kind, flow, route, prepared];
    }

    [Theory]
    [MemberData(nameof(ConditionRoutes))]
    public async Task ConditionFlowIsConsumedByItsOwningLoop(string kind, string flow, string route, bool prepared)
    {
        var initial = route == "source" ? "printf '%s' '" + flow + "; printf skipped' >action; " : "";
        var action = route switch
        {
            "direct" => flow,
            "group" => "{ " + flow + "; }",
            "eval" => "eval '" + flow + "; printf skipped'",
            "source" => "source ./action",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        var script = initial + "i=0; for outer in a b; do " + kind
            + " { ((i++)); if ((i>=3)); then break; fi; " + action
            + "; }; do printf wrong; done; printf '<%s>' \"$outer\"; done; printf ':after:%s:%s' \"$?\" \"$i\"";
        var memory = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RunResult result;
        using (var owner = memory.CreateScope())
            result = prepared
                ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(script)), host, owner, deadline.Token)
                : await host.RunAsync(script, deadline.Token);

        var count = kind == "while" && flow == "continue" ? 4 : 2;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<a><b>:after:0:" + count, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("while", "break", false, false, 0, 2)]
    [InlineData("while", "! break", false, false, 1, 2)]
    [InlineData("while", "continue", false, false, 0, 3)]
    [InlineData("while", "! continue", false, false, 1, 2)]
    [InlineData("until", "break", false, false, 1, 2)]
    [InlineData("until", "! break", false, false, 1, 2)]
    [InlineData("until", "continue", false, false, 1, 2)]
    [InlineData("until", "! continue", false, false, 1, 3)]
    [InlineData("while", "! break", true, false, 0, 2)]
    [InlineData("until", "break", true, false, 0, 2)]
    [InlineData("until", "! break", true, false, 1, 2)]
    [InlineData("until", "! continue", true, false, 1, 3)]
    [InlineData("while", "break 0", true, false, 0, 2)]
    [InlineData("until", "break 0", true, false, 1, 2)]
    [InlineData("while", "break", false, true, 1, 2)]
    [InlineData("until", "break", false, true, 0, 2)]
    public async Task ConditionGatingSelectsTheCorrectLoopStatus(
        string kind, string flow, bool successfulBody, bool negated, int status, int iterations)
    {
        var first = kind == "while" ? "true" : "false";
        var stop = kind == "while" ? "false" : "true";
        var script = "i=0; " + (negated ? "! " : "") + kind
            + " { ((i++)); if ((i==1)); then " + first + "; elif ((i==2)); then " + flow + "; else " + stop
            + "; fi; }; do printf B; " + (successfulBody ? "true" : "false")
            + "; done; printf ':after:%s:%s' \"$?\" \"$i\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("B:after:" + status + ":" + iterations, result.StdOut);
        if (flow.EndsWith(" 0", StringComparison.Ordinal))
            Assert.Contains("loop count out of range", result.StdErr);
        else
            Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("while", "! break", false)]
    [InlineData("until", "break", false)]
    [InlineData("while", "! continue", true)]
    [InlineData("until", "continue", true)]
    public async Task OutwardFlowRetainsTheTestedOriginOfTheLastBody(string kind, string flow, bool arithmetic)
    {
        var header = arithmetic ? "for ((outer=0;outer<1;outer++))" : "for outer in one";
        var first = kind == "while" ? "true" : "false";
        var script = "set -e; " + header + "; do i=0; " + kind
            + " { ((i++)); if ((i==1)); then " + first + "; else " + flow
            + " 2; fi; }; do false && :; done; printf skipped; done; printf ':after:%s' \"$?\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(":after:1", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("while")]
    [InlineData("until")]
    public async Task AConditionBreakDoesNotEscapeItsEnclosingFunction(string kind)
    {
        var result = await EvaluationTestHelper.RunAsync("f(){ " + kind
            + " break; do printf wrong; done; printf after; }; f; printf ':%s' \"$?\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after:0", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("while", 4096)]
    [InlineData("until", 1)]
    public async Task RepeatedConditionFlowReusesCommandWorkspace(string kind, int expected)
    {
        var memory = new MemoryGovernor(64 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var script = "i=0; " + kind + " { ((i++)); if ((i>=4096)); then break; fi; continue; }; do printf wrong; done; printf '%s' \"$i\"";
        var result = await host.RunAsync(script, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected.ToString(), host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_ExecutesSingleIteration()
    {
        // This test verifies that a while-loop executes its body when the condition succeeds.
        // The script sets i=0 and loops while i==0, echoing once and then setting i=1 to stop.
        // A correct implementation must emit "once\n", leave STDERR empty, and exit with code 0.
        // If the loop never executes or never terminates, output and exit code would differ.
        var script = @"i=0; while [ $i -eq 0 ]; do echo once; i=1; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("once\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_BreakExitsLoop()
    {
        // This test verifies that "break" exits a while-loop immediately.
        // The script echoes "first", breaks, then attempts to echo "after" inside the loop and
        // "tail" after the loop; only "first" and "tail" should appear.
        // If break is ignored, STDOUT would include "after\n".
        var script = @"while true; do echo first; break; echo after; done; echo tail";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("first\ntail\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_ContinueSkipsBody()
    {
        // This test verifies that "continue" skips the remainder of the loop body.
        // The script increments i, continues on the first iteration, and echoes "after" only on
        // the second iteration; a correct run must emit "after\n" once with exit code 0.
        // If continue is ignored, STDOUT would include an extra line.
        var script = @"i=0; while [ $i -lt 2 ]; do i=$((i+1)); if [ $i -eq 1 ]; then continue; fi; echo after; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("after\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_BreakInvalidArgumentReturnsFailure()
    {
        var script = @"while true; do break nope; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: break: nope: numeric argument required\n", result.StdErr);
        Assert.Equal(128, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_AllowsZeroIterations()
    {
        // This test verifies that a while-loop with a failing condition runs zero iterations.
        // The script "while false; do echo never; done" must emit no output and exit with code 0.
        // If the body executes incorrectly, STDOUT would contain "never\n".
        var script = @"while false; do echo never; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileRead_WithInputRedirectionConsumesFile()
    {
        var script = @"while IFS= read value; do printf '%s\n' ""$value""; done < /input";
        var result = await EvaluationTestHelper.RunAsync(script, standardInput: string.Empty);

        Assert.Equal("input\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UntilCommand_ExecutesSingleIteration()
    {
        // This test verifies that an until-loop runs the body until the condition succeeds.
        // The script starts with i=0 and runs until i==1, echoing once and then setting i=1.
        // A correct implementation must emit "once\n", leave STDERR empty, and exit 0.
        // If until is inverted incorrectly, the body would not execute.
        var script = @"i=0; until [ $i -eq 1 ]; do echo once; i=1; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("once\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a while-loop.
        // The script "! while false; do echo never; done" normally exits 0; negation must return 1.
        // If negation is ignored, the exit code would be 0.
        var script = @"! while false; do echo never; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}

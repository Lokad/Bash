using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ControlBuiltinStatusTests
{
    [Theory]
    [InlineData("break", "-- -1", "--")]
    [InlineData("continue", "-- 0", "--")]
    [InlineData("break", "-1", "-1")]
    public async Task OutOfRangeLoopCountReportsTheFirstOperand(string command, string arguments, string operand)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("for n in one two; do " + command + " " + arguments
            + "; printf unexpected; done; printf '<%s>' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1>", host.StandardOutput.TextContent);
        Assert.Equal($"{command}: {operand}: loop count out of range\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("exit 2 3", 1, "exit: too many arguments\n")]
    [InlineData("exit bad 3", 2, "exit: bad: numeric argument required\n")]
    [InlineData("exit 7 $'\\377'", 1, "exit: too many arguments\n")]
    [InlineData("for n in one two; do break 1 $'\\377'; done", 1, "break: too many arguments\n")]
    [InlineData("for n in one two; do continue 1 $'\\377'; done", 1, "continue: too many arguments\n")]
    [InlineData("for n in one two; do break bad; done", 128, "break: bad: numeric argument required\n")]
    [InlineData("for n in one two; do continue bad; done", 128, "continue: bad: numeric argument required\n")]
    public async Task OperandErrorsStopTheRestOfTheCommandUnit(string command, int status, string diagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(command + "; printf unexpected", CancellationToken.None);
        Assert.Equal(status, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal(diagnostic, host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("return 1 2", "return", false)]
    [InlineData("return 1 2", "return", true)]
    [InlineData("exit 1 2", "exit", false)]
    [InlineData("exit 1 2", "exit", true)]
    [InlineData("shift -- 0 extra", "shift", false)]
    [InlineData("shift -- 0 extra", "shift", true)]
    [InlineData("for n in x; do break 1 2; done", "break", false)]
    [InlineData("for n in x; do break 1 2; done", "break", true)]
    [InlineData("for n in x; do continue 1 2; done", "continue", false)]
    [InlineData("for n in x; do continue 1 2; done", "continue", true)]
    public async Task ExtraArgumentsDiscardTheCurrentRootUnitUnderErrexit(
        string invocation, string command, bool prepared)
    {
        var script = "set -e\n" + invocation + """
            ; printf unexpected
            printf '<%s:%s>' "$?" "${PIPESTATUS[*]}"
            """;
        await using var host = new TestHost();
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, CancellationToken.None)
            : await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:1>", host.StandardOutput.TextContent);
        Assert.Equal($"{command}: too many arguments\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardRetainsTheLastFailedPipelineStatusAtEndOfInput(bool prepared)
    {
        const string script = """
            trap 'printf "<%s:%s>" "$?" "${PIPESTATUS[*]}"' EXIT
            : | (exit 7)
            return 1 2; printf unexpected
            """;
        await using var host = new TestHost();
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, CancellationToken.None)
            : await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("<7:0 7>", host.StandardOutput.TextContent);
        Assert.Equal("return: too many arguments\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("exit")]
    [InlineData("shift")]
    public async Task DiscardInExitActionPreservesThePendingExitStatus(string command)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("trap '" + command + " 1 2; printf unexpected\nprintf unexpected' EXIT\n(exit 7)",
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal($"{command}: too many arguments\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("( (exit 7); return 1 2; printf unexpected )", "<7:7>")]
    [InlineData("{ (exit 7); return 1 2; printf unexpected; } | :", "<7:7 0>")]
    [InlineData("f(){ (exit 7); return 1 2; printf unexpected; }; f | :", "<7:7 0>")]
    [InlineData("(exit 7); return 1 2 | :", "<1:1 0>")]
    [InlineData("value=`(exit 7)\nreturn 1 2\nprintf unexpected`", "<1:1>")]
    public async Task DiscardUsesTheReachedChildBoundary(string invocation, string status)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(invocation + """
            ; printf '<%s:%s>%s' "$?" "${PIPESTATUS[*]}" "$value"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(status, host.StandardOutput.TextContent);
        Assert.Equal("return: too many arguments\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task DiscardFromEvalResumesAfterRestoringTheFunctionAndClosingItsRedirection()
    {
        const string script = """
            visible=outer
            f(){ local -; set -e; local visible=inner; eval 'return 1 2
            printf unexpected'; printf unexpected; }
            f 2>/errors; printf unexpected
            printf '<%s:%s:%s>' "$?" "${PIPESTATUS[*]}" "$visible"
            false
            printf '<%s>' "$?"
            """;
        var governor = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:1:outer><1>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("return: too many arguments\n", host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("exit -1", 255, "inner", "")]
    [InlineData("exit bad", 2, "inner", "exit: bad: numeric argument required\n")]
    [InlineData("exit 2 3", 1, "outer", "exit: too many arguments\n")]
    [InlineData("for x in a; do break bad; done", 128, "outer", "break: bad: numeric argument required\n")]
    public async Task ExitTrapUsesTheCorrectScopeAfterOperandFailure(
        string command, int status, string visible, string diagnostic)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync("x=outer; trap 'printf \"trap:%s:%s\" \"$?\" \"$x\"' EXIT; "
            + "f(){ local x=inner; " + command + "; }; f 2>/errors; printf unexpected", CancellationToken.None);
        Assert.Equal(status, result.ExitCode);
        Assert.Equal($"trap:{status}:{visible}", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(diagnostic, host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }
}

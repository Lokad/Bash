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
    [InlineData("break", ":", "bad", 128, false)]
    [InlineData("continue", "false", "-- bad extra", 129, true)]
    [InlineData("break", ": | (exit 7)", "bad", 135, true)]
    [InlineData("continue", "(exit 201)", "bad", 201, false)]
    [InlineData("break", "(exit 7)", "\"$(printf bad; exit 11)\"", 139, false)]
    public async Task InvalidLoopCountPublishesTheReachedFailure(
        string command, string preceding, string operand, int status, bool prepared)
    {
        var script = "trap 'printf \"<%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\"' EXIT\n"
            + "for n in one; do " + preceding + "; " + command + " " + operand + "; printf unexpected; done\n"
            + "printf unexpected\n";
        await using var host = new TestHost();
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, CancellationToken.None)
            : await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal($"<{status}:{status}>", host.StandardOutput.TextContent);
        Assert.Equal($"{command}: bad: numeric argument required\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("local -; ", "off")]
    [InlineData("", "on")]
    public async Task InvalidLoopCountRestoresOnlySavedOptionsBeforeExitAction(string saveOptions, string flag)
    {
        var script = """
            trap 'status=$? pipes="${PIPESTATUS[*]}"; case $- in *e*) flag=on;; *) flag=off;; esac; printf "<%s:%s:%s>" "$status" "$pipes" "$flag"' EXIT
            """ + "\nf(){ " + saveOptions
            + "set -e; for n in one; do (exit 7) || eval 'break bad'; done; }\nf 2>errors\nprintf unexpected";
        var governor = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(135, result.ExitCode);
        Assert.Equal($"<135:135:{flag}>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("break: bad: numeric argument required\n", host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("false", "exit", 7)]
    [InlineData("true", "exit --", 0)]
    [InlineData("false", "exit --", 1)]
    [InlineData("(exit 5)", "exit --", 5)]
    [InlineData("false", "exit -- 4", 4)]
    public async Task ExitActionSelectsTheStatusForTheReachedExitForm(string preceding, string command, int status)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "trap '" + preceding + "; " + command + "; printf unexpected' EXIT\nexit 7", CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
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
    [InlineData("exit 1 2", "exit: too many arguments", 1)]
    [InlineData("for x in a; do break 1 2; done", "break: too many arguments", 1)]
    [InlineData("for x in a; do continue 1 2; done", "continue: too many arguments", 1)]
    [InlineData("shift 1 2", "shift: too many arguments", 1)]
    public async Task ControlFailureReportsTheSourcedLineBeforeUnwinding(string invocation, string diagnostic, int status)
    {
        await using var host = new TestHost();
        host.AddFile("/body", $"# Leading line\n{invocation}\nprintf unexpected\n");
        var result = await host.RunAsync("source ./body\nprintf '<%s>' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<{status}>", host.StandardOutput.TextContent);
        Assert.Equal($"./body: line 2: {diagnostic}\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task RedirectedControlDiagnosticUsesTheOperandLineInRetainedSource()
    {
        const string script = "source ./body\nf; printf '<%s>' \"$?\"";
        await using var host = new TestHost();
        host.AddFile("/body", "# Leading line\nf(){\nshift \\\nbad \\\n2>error\n}\n");
        var result = await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("./body: line 4: shift: bad: numeric argument required\n", host.ReadFile("/error"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ControlDiagnosticKeepsUnnamedFunctionContextInsideSource()
    {
        const string script = "f(){ shift bad; }; source ./body; printf '<%s>' \"$?\"";
        await using var host = new TestHost();
        host.AddFile("/body", "# Leading line\nf\n");
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1>", host.StandardOutput.TextContent);
        Assert.Equal("shift: bad: numeric argument required\n", host.StandardError.TextContent);
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

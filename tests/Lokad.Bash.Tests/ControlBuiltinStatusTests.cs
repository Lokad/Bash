using Lokad.Bash.Memory;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ControlBuiltinStatusTests
{
    [Theory]
    [InlineData("exit 2 3", 1, "exit: too many arguments\n")]
    [InlineData("exit bad 3", 2, "exit: bad: numeric argument required\n")]
    [InlineData("exit 7 $'\\377'", 1, "exit: too many arguments\n")]
    [InlineData("for n in one two; do break 1 $'\\377'; done", 1, "break: too many arguments\n")]
    [InlineData("for n in one two; do continue 1 $'\\377'; done", 1, "continue: too many arguments\n")]
    [InlineData("for n in one two; do break bad; done", 128, "break: bad: numeric argument required\n")]
    [InlineData("for n in one two; do continue bad; done", 128, "continue: bad: numeric argument required\n")]
    public async Task FatalOperandErrorsStopLaterEffects(string command, int status, string diagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(command + "; printf unexpected", CancellationToken.None);
        Assert.Equal(status, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal(diagnostic, host.StandardError.TextContent);
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

using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class BuiltinUsageDiagnosticTests
{
    [Theory]
    [InlineData("printf -v bad-name '%s' sample")]
    [InlineData("printf -z sample")]
    [InlineData("printf -v")]
    [InlineData("mapfile -z")]
    [InlineData("readarray -z")]
    [InlineData("read -z")]
    [InlineData("getopts -z option")]
    public async Task ShellClosedStderrPreservesUsageStatus(string command)
    {
        var result = await EndToEndHarness.RunScriptAsync(command + " 2>&-; printf '<%s>' \"$?\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("mapfile -z")]
    [InlineData("read -z")]
    [InlineData("getopts -z option")]
    public async Task HostUsageDiagnosticFailureStillReturnsOne(string command)
    {
        var state = new Eval(Parser.Parse(command));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Synthetic write failure\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }
}

using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ErrexitTests
{
    [Theory]
    [InlineData("-e", "+e")]
    [InlineData("-e", "+o errexit")]
    [InlineData("-o errexit", "+e")]
    [InlineData("-o errexit", "+o errexit")]
    public async Task OptionSpellingsEnableAndDisableTheSameState(string enable, string disable)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"set {enable}; printf '%s' \"$-\"; set {disable}; false; printf ':continued:%s' \"${{-}}\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("e:continued:", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task InspectionReportsSupportedStateWithoutReplacingPositionals()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "set -- keep; set -o; set -e; set +o; set +e; set -o; printf '%s' \"$1\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("errexit\toff\nset -o errexit\nerrexit\toff\nkeep", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("-u")]
    [InlineData("+x")]
    [InlineData("-o unknown")]
    [InlineData("+o pipefail")]
    public async Task UnsupportedOptionsReturnAnErrorAndDiagnostic(string option)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("set " + option, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.StartsWith("set: ", host.StandardError.TextContent);
        Assert.Contains("option", host.StandardError.TextContent);
    }

    [Fact]
    public async Task ErrexitWaitsForDescriptorCleanupAndRunsExitTrapOnce()
    {
        var memory = new MemoryGovernor(64 * 1024);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = memory,
            RecordAppends = true,
            BeforeDescriptorClose = _ => closeEntered.SetResult()
        };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueCloseDescriptorTask(closeComplete.Task);
        var running = BashScript.RunAsync(host,
            "set -e; exec 3>/out; trap 'printf trap:%s \"$?\"' EXIT; false; printf wrong",
            EvaluationOptions.Default, CancellationToken.None);
        try
        {
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes > 0);
            var output = Assert.Single(host.Appends);
            Assert.Equal(FileDescriptor.StdOut, output.Descriptor);
            Assert.Equal("trap:1"u8.ToArray(), output.Content);
        }
        finally
        {
            closeComplete.TrySetResult();
        }

        Assert.Equal(1, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Single(host.Appends);
        Assert.Equal(0, memory.ReservedBytes);
    }
}

using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed partial class TildePrefixTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SelectedPathUsesTheSharedBorrowAndCleanupLifetime(bool prepared, bool canceled, bool suffix)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new PendingWriteHost { MemoryBudget = owner };
        var opened = new List<string>();
        host.BeforeWritableOpen = path => opened.Add(path.Path);
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var script = "HOME=home; printf data > ~\"" + (suffix ? "x" : "") + "\"; printf tail";
        var running = prepared
            ? new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancellation.Token)
            : BashScript.RunAsync(host, script, EvaluationOptions.Default, cancellation.Token);
        Exception? failure = null;
        RunResult? result = null;
        try
        {
            var content = await host.WriteEntered.Task.WaitAsync(deadline.Token);
            Assert.Equal("/~" + (suffix ? "x" : ""), Assert.Single(opened));
            Assert.Equal("data"u8.ToArray(), content.ToArray());
            Assert.False(running.IsCompleted);
            Assert.Equal(1, host.OpenFileCount);
            Assert.True(governor.ReservedBytes > 0);
            if (canceled) cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.Equal("data"u8.ToArray(), content.ToArray());
            host.ReleaseWrite.TrySetResult();
            await host.CloseEntered.Task.WaitAsync(deadline.Token);
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
            Assert.Equal(1, host.OpenFileCount);
        }
        finally
        {
            host.ReleaseWrite.TrySetResult();
            host.ReleaseClose.TrySetResult();
            try { result = await running.WaitAsync(deadline.Token); }
            catch (Exception error) { failure = error; }
        }

        if (canceled)
        {
            var error = Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(0, Assert.IsType<RunResult>(result).ExitCode);
            Assert.Equal("tail", host.StandardOutput.TextContent);
        }
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(1, host.Closes);
        Assert.Equal(0, host.OpenFileCount);
        var entry = Assert.Single(host.CaptureEntries());
        Assert.Equal("~" + (suffix ? "x" : ""), entry.Key);
        Assert.Equal((FilePathKind.File, "64617461"), entry.Value);
        if (!prepared) Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class PendingWriteHost : TestHost
    {
        private FileDescriptor? _held;
        public TaskCompletionSource<ReadOnlyMemory<byte>> WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Closes { get; private set; }

        public override async ValueTask<DescriptorWriteResult> WriteAsync(
            FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancel)
        {
            if (_held == null && descriptor != FileDescriptor.StdOut && descriptor != FileDescriptor.StdErr)
            {
                _held = descriptor;
                WriteEntered.TrySetResult(content);
                await ReleaseWrite.Task;
                Assert.Equal("data"u8.ToArray(), content.ToArray());
                // This host completes the pending write even after cancellation. Earlier
                // effects remain visible; the engine still owes the original cancellation.
                return await base.WriteAsync(descriptor, content, CancellationToken.None);
            }
            return await base.WriteAsync(descriptor, content, cancel);
        }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            Closes++;
            CloseEntered.TrySetResult();
            await ReleaseClose.Task;
            return await base.CloseDescriptorAsync(descriptor);
        }

        public override async ValueTask DisposeAsync()
        {
            ReleaseWrite.TrySetResult();
            ReleaseClose.TrySetResult();
            await base.DisposeAsync();
        }
    }
}

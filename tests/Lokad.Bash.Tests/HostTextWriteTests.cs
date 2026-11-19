using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class HostTextWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextWriteKeepsEncodedBytesChargedThroughHostFailure(bool withLf)
    {
        var text = new string('é', 4096);
        var expected = Encoding.UTF8.GetBytes(withLf ? text + "\n" : text);
        var memory = new MemoryGovernor(expected.Length);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory };
        var started = new TaskCompletionSource<(FileDescriptor Descriptor, ReadOnlyMemory<byte> Bytes)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeAppend = (descriptor, bytes) => started.SetResult((descriptor, bytes));
        host.EnqueueAppendTask(settle.Task);
        var descriptor = new FileDescriptor(71);
        var failure = DescriptorWriteResult.Failure(new OperationError("sample write failure\n"));

        var write = (withLf
            ? host.WriteLineAsync(descriptor, text, CancellationToken.None)
            : host.WriteAsync(descriptor, text, CancellationToken.None)).AsTask();
        DescriptorWriteResult result;
        try
        {
            var append = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(descriptor, append.Descriptor);
            Assert.False(write.IsCompleted);
            Assert.True(append.Bytes.Span.SequenceEqual(expected));
            Assert.Equal(expected.Length, memory.ReservedBytes);
        }
        finally
        {
            settle.SetResult(failure);
            result = await write;
        }

        Assert.Equal(failure, result);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedTextWriteLeavesShortPreEvaluationDiagnosticAvailable(bool withLf)
    {
        var memory = new MemoryGovernor(0);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        var text = new string('é', 128) + (withLf ? "" : "x");

        await Assert.ThrowsAsync<MemoryBudgetExceededException>(() => (withLf
            ? host.WriteLineAsync(FileDescriptor.StdErr, text, CancellationToken.None)
            : host.WriteAsync(FileDescriptor.StdErr, text, CancellationToken.None)).AsTask());
        Assert.Empty(host.Appends);

        var result = await BashScript.RunAsync(host, "true", EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("shell: shared memory budget exhausted\n"u8.ToArray(), Assert.Single(host.Appends).Content);
        Assert.Equal(0, memory.PeakBytes);
    }

    [Fact]
    public async Task CancellationRefundsOnlyAfterHostWriteSettles()
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new MemoryGovernor(4096);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeAppend = (_, _) => started.SetResult();
        host.EnqueueAppendTask(settle.Task);
        var write = host.WriteAsync(FileDescriptor.StdErr, new string('x', 4096), cancellation.Token).AsTask();

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(write.IsCompleted);
            Assert.Equal(4096, memory.ReservedBytes);
        }
        finally
        {
            settle.SetCanceled(cancellation.Token);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }

        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task LineWriteKeepsReplacementEncodingAndAppendsLf()
    {
        var memory = new MemoryGovernor(0);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);
        const string text = "x\uD800\r\n";

        await Assert.ThrowsAsync<EncoderFallbackException>(() => host.WriteAsync(FileDescriptor.StdErr, text, CancellationToken.None).AsTask());
        Assert.Empty(host.Appends);
        Assert.Equal(DescriptorWriteResult.Closed, await host.WriteLineAsync(FileDescriptor.StdErr, text, CancellationToken.None));
        Assert.Equal("x�\r\n\n"u8.ToArray(), Assert.Single(host.Appends).Content);
        Assert.Equal(0, memory.PeakBytes);
    }
}

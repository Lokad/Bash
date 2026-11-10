using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class WriteOperationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Bad file descriptor\n")]
    [InlineData("Input/output error\n")]
    public async Task OperationRetainsKnownAndUnknownFailureCauses(string? message)
    {
        var completion = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new WriteOperation(completion, new(71), "value"u8.ToArray());
        var host = new TestOperationVisitor([]);
        var result = DescriptorWriteResult.Failure(new OperationError(message ?? "Write failed\n"));
        host.EnqueueAppendResult(result);
        host.BeforeAppend = (descriptor, content) =>
        {
            Assert.Equal(new FileDescriptor(71), descriptor);
            Assert.Equal("value"u8.ToArray(), content.ToArray());
        };

        await ShellOperationDispatcher.DispatchAsync(operation, host, CancellationToken.None);

        Assert.Equal(result, await completion.Task);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShellOperationDispatcher.DispatchAsync(operation, host, CancellationToken.None));
    }

    [Fact]
    public async Task OperationPreservesSuccessfulDownstreamClosure()
    {
        var completion = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new WriteOperation(completion, new(71), "value"u8.ToArray());
        var host = new TestOperationVisitor([]);
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);

        await ShellOperationDispatcher.DispatchAsync(operation, host, CancellationToken.None);

        Assert.Equal(DescriptorWriteResult.Closed, await completion.Task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionOrCancellationIsNotConvertedToAnAppendFailure(bool canceled)
    {
        var completion = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new WriteOperation(completion, new(71), "value"u8.ToArray());
        var host = new TestOperationVisitor([]);
        Exception failure = canceled
            ? new OperationCanceledException(new CancellationToken(true)) : new IOException("write failed");
        host.BeforeAppend = (_, _) => throw failure;

        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => ShellOperationDispatcher.DispatchAsync(operation, host, CancellationToken.None)));
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
        else
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => completion.Task));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShellOperationDispatcher.DispatchAsync(operation, host, CancellationToken.None));
    }
}

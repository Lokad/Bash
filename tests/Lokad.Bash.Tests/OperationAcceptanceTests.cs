using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class OperationAcceptanceTests
{
    [Fact]
    public async Task Operation_DispatchAsync_RejectsRepeatedHostDispatch()
    {
        var completion = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new WriteOperation(completion, FileDescriptor.StdOut, "value"u8.ToArray());
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);

        await ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None));

        Assert.Equal("An evaluation operation can be accepted only once.", exception.Message);
        Assert.Equal(DescriptorWriteResult.Open, await completion.Task);
    }

    [Fact]
    public async Task ReadLinesOperation_RejectsRepeatedDispatchAfterCancellation()
    {
        var completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new ReadLineOperation(completion, FileDescriptor.StdIn, 1, destinationBudget: null);
        var visitor = new TestOperationVisitor([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var dispatchError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, cancellation.Token));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None));

        Assert.Equal("An evaluation operation can be accepted only once.", exception.Message);
        var completionError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
        Assert.Equal(cancellation.Token, dispatchError.CancellationToken);
        Assert.Equal(cancellation.Token, completionError.CancellationToken);
    }

    [Fact]
    public async Task ReadFailureCannotBeDispatchedAgain()
    {
        var completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new ReadLineOperation(completion, FileDescriptor.StdIn, 1, destinationBudget: null);
        var visitor = new TestOperationVisitor([]);
        var failure = new IOException("read failed");
        visitor.EnqueueReadTask(Task.FromException<ReadResult>(failure));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None));

        Assert.Equal("An evaluation operation can be accepted only once.", exception.Message);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => completion.Task));
    }

    [Fact]
    public async Task PendingDispatchRejectsOverlapWithoutConsumingAnotherRead()
    {
        var completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRead = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new ReadLineOperation(completion, FileDescriptor.StdIn, 1, destinationBudget: null);
        var visitor = new TestOperationVisitor([]);
        var firstResult = ReadResult.Success("first\n"u8.ToArray(), [], reachedEoF: false);
        var nextResult = ReadResult.Success("next\n"u8.ToArray(), [], reachedEoF: true);
        visitor.EnqueueReadTask(pendingRead.Task);
        visitor.EnqueueReadResult(nextResult);

        var dispatch = ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None);
        try
        {
            Assert.False(dispatch.IsCompleted);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None));

            Assert.Equal("An evaluation operation can be accepted only once.", exception.Message);
            Assert.False(completion.Task.IsCompleted);
        }
        finally
        {
            pendingRead.TrySetResult(firstResult);
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(firstResult.Content.ToArray(), (await completion.Task).Content.ToArray());
        var nextCompletion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ShellOperationDispatcher.DispatchAsync(
            new ReadLineOperation(nextCompletion, FileDescriptor.StdIn, 1, destinationBudget: null), visitor, CancellationToken.None);
        Assert.Equal(nextResult.Content.ToArray(), (await nextCompletion.Task).Content.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseCapturesHostFailureOrCancellationButRejectsRepeatedDispatch(bool canceled)
    {
        var completion = new TaskCompletionSource<OperationError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = new FileDescriptor(10);
        var operation = new CloseDescriptorOperation(completion, descriptor);
        var visitor = new TestOperationVisitor([]);
        var failure = new IOException("close failed");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        visitor.EnqueueCloseDescriptorTask(canceled
            ? Task.FromCanceled(cancellation.Token)
            : Task.FromException(failure));

        await ShellOperationDispatcher.DispatchAsync(operation, visitor, cancellation.Token);
        if (canceled)
        {
            var completionError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
            Assert.Equal(cancellation.Token, completionError.CancellationToken);
        }
        else
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => completion.Task));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None));

        Assert.Equal("An evaluation operation can be accepted only once.", exception.Message);
        Assert.Equal(new[] { descriptor }, visitor.ClosedDescriptors);
    }

    [Theory]
    [InlineData(TaskStatus.RanToCompletion)]
    [InlineData(TaskStatus.Faulted)]
    [InlineData(TaskStatus.Canceled)]
    public async Task LateHostFailurePreservesSettledOutcome(TaskStatus settledStatus)
    {
        var completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRead = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new ReadLineOperation(completion, FileDescriptor.StdIn, 1, destinationBudget: null);
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueReadTask(pendingRead.Task);
        var dispatch = ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None);
        Assert.False(dispatch.IsCompleted);

        var firstFailure = new IOException("request failed");
        var token = new CancellationToken(canceled: true);
        switch (settledStatus)
        {
            case TaskStatus.RanToCompletion:
                operation.SetResult(ReadResult.EndOfFile);
                break;
            case TaskStatus.Faulted:
                Assert.True(operation.TrySetException(firstFailure));
                break;
            case TaskStatus.Canceled:
                Assert.True(operation.TrySetCanceled(token));
                break;
        }

        var hostFailure = new IOException("late host failure");
        pendingRead.SetException(hostFailure);
        Assert.Same(hostFailure, await Assert.ThrowsAsync<IOException>(() => dispatch));
        Assert.False(operation.TrySetException(hostFailure));
        Assert.False(operation.TrySetCanceled(token));
        Assert.Equal(settledStatus, completion.Task.Status);
        switch (settledStatus)
        {
            case TaskStatus.RanToCompletion:
                Assert.Equal(ReadResult.EndOfFile, await completion.Task);
                break;
            case TaskStatus.Faulted:
                Assert.Same(firstFailure, await Assert.ThrowsAsync<IOException>(() => completion.Task));
                break;
            case TaskStatus.Canceled:
                var completionError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);
                Assert.Equal(token, completionError.CancellationToken);
                break;
        }
    }
}

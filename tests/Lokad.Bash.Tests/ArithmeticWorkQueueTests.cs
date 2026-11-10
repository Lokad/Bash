using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticWorkQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedEvaluation_PropagatesDeferredFailureAndAllowsTheNextOperand(bool canceled)
    {
        var work = new ArithmeticWorkQueue();
        var selected = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = work.RunAsync(() => work.Enqueue(() => selected.Task));
        Assert.False(pending.IsCompleted);

        if (canceled)
        {
            selected.SetCanceled();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            var failure = new InvalidOperationException("Selected reference failed");
            selected.SetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5))));
        }

        Assert.Equal(9, await work.RunAsync(() => Task.FromResult<long?>(9)).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ExecutionLifetimeTests
{
    [Theory]
    [InlineData("printf '%s' {-2147483648..2147483647} >/out", false)]
    [InlineData("{ printf '%s' {-2147483648..2147483647}; } 2>/errors", true)]
    public async Task RepresentationOverflowReportsOnceBeforeAllocatingTheExpansion(string script, bool redirected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        var diagnostic = redirected ? host.ReadFile("/errors") : host.StandardError.TextContent;

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("CLR array length limit", diagnostic);
        Assert.Single(diagnostic.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (redirected)
            Assert.Empty(host.StandardError.Content.ToArray());
        else
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(20000)]
    public async Task CompletedIterationsReuseLiveMemory(int iterations)
    {
        var governor = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync(
            $"for ((i=0; i<{iterations}; i++)); do :; done; printf 'done:%s' \"$i\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"done:{iterations}", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
        Assert.InRange(governor.PeakBytes, 1, governor.LimitBytes);
    }

    [Fact]
    public async Task CpuOnlyExpansionObservesCancellationAndRefundsItsOwner()
    {
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(64 * 1024 * 1024);
        var memory = new CountingMemoryBudget(governor);
        var admissions = 0;
        memory.AdmissionSucceeded += () =>
        {
            if (++admissions == 16)
                cancellation.Cancel();
        };
        await using var host = new TestHost { MemoryBudget = memory };

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RunAsync("x=a; while :; do x=$x$x; done; printf unexpected", cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.True(admissions >= 16);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ArithmeticObservesCancellationDuringSustainedBinding()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var expression = ArithmeticOperand.FromValue(ShellValue.FromText(
            string.Join('+', Enumerable.Repeat("value", 10000))));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ArithmeticEvaluation.EvaluateAsync<string>(expression, Bind,
                (_, _, _) => throw new InvalidOperationException("Unexpected assignment"), cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(500, reads);

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            if (++reads == 500)
                cancellation.Cancel();
            return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Bound("value", 1));
        }
    }

    [Theory]
    [InlineData("echo borrowed >/out")]
    [InlineData("echo borrowed >/out\n)\n")]
    public async Task CancellationRetainsBorrowedOutputUntilWriteAndCloseSettle(string source)
    {
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(64 * 1024);
        var writeEntered = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeComplete = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = governor,
            BeforeAppend = (_, content) => writeEntered.SetResult(content),
            BeforeDescriptorClose = _ => closeEntered.SetResult()
        };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendTask(writeComplete.Task);
        host.EnqueueCloseDescriptorTask(closeComplete.Task);
        var running = BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);
        try
        {
            var borrowed = await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
            Assert.Equal("borrowed\n"u8.ToArray(), borrowed.ToArray());

            writeComplete.SetResult(DescriptorWriteResult.Open);
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
        }
        finally
        {
            writeComplete.TrySetResult(DescriptorWriteResult.Open);
            closeComplete.TrySetResult();
        }

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Equal(0, governor.ReservedBytes);
    }
}

using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DescriptorBookkeepingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipeAndErrorStagePreparationUsesConstantWorkspaceForExplicitRedirections(bool compound)
    {
        Assert.Equal(await MeasureDispatchWorkspaceAsync(1), await MeasureDispatchWorkspaceAsync(1024));

        async Task<long> MeasureDispatchWorkspaceAsync(int redirections)
        {
            var script = (compound ? "{ :; }" : ":") + string.Concat(Enumerable.Repeat(" 1>&1", redirections)) + " |& :";
            var evaluation = new Eval(Parser.Parse(script));
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            evaluation.Memory.Budget = owner;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var pipe = Assert.IsType<CreatePipeOperation>(await evaluation.GetNextStep(deadline.Token));
            pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));
            var pipeline = Assert.IsType<RunPipelineEvaluations>(await evaluation.GetNextStep(deadline.Token));
            var workspace = evaluation.Memory.Workspace.Bytes;
            pipeline.SetResult([0, 0]);

            Assert.Null(await evaluation.GetNextStep(deadline.Token));
            await evaluation.Completion;
            Assert.Equal(0, evaluation.LastExitCode);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return workspace;
        }
    }

    [Fact]
    public async Task PipelineReturnsUnusedParentCapacityBeforePublishingTheStages()
    {
        var ordinary = await MeasureDispatchAsync(extraCapacity: 0);
        Assert.Equal(ordinary, await MeasureDispatchAsync(extraCapacity: 16 * 1024));

        async Task<(long Accounted, long Live)> MeasureDispatchAsync(int extraCapacity)
        {
            var evaluation = new Eval(Parser.Parse(": | :"));
            var governor = new MemoryGovernor(64 * 1024);
            using var owner = governor.CreateScope();
            evaluation.Memory.Budget = owner;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var pipe = Assert.IsType<CreatePipeOperation>(await evaluation.GetNextStep(deadline.Token));
            var workspace = evaluation.Memory.Workspace;
            var before = workspace.Bytes;
            if (extraCapacity != 0)
            {
                workspace.Grow(extraCapacity);
                workspace.Resize(before);
            }
            pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));
            var pipeline = Assert.IsType<RunPipelineEvaluations>(await evaluation.GetNextStep(deadline.Token));
            var usage = (Accounted: governor.ReservedBytes, Live: workspace.Bytes);
            pipeline.SetResult([0, 0]);

            Assert.Null(await evaluation.GetNextStep(deadline.Token));
            await evaluation.Completion;
            Assert.Equal(0, evaluation.LastExitCode);
            Assert.True(usage.Accounted >= usage.Live);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return usage;
        }
    }

    [Fact]
    public async Task FailedPipelinePublicationClosesTheCreatedPipesInReverseOrder()
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = new IOException("pipeline publication failed");
        var pipesCreated = 0;
        var closed = new List<FileDescriptor>();
        var evaluation = new Eval(Parser.Parse(": | : | :"), publishNextStep: operation =>
        {
            switch (operation)
            {
                case CreatePipeOperation pipe:
                    var first = 10 + pipesCreated++ * 2;
                    pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(first, first + 1)));
                    break;
                case RunPipelineEvaluations:
                    throw failure;
                case CloseDescriptorOperation close:
                    closed.Add(close.Descriptor);
                    close.SetResult(null);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected pipeline preparation operation.");
            }
        });
        evaluation.Memory.Budget = owner;

        var observed = await Assert.ThrowsAsync<IOException>(() => evaluation.EnsureRunning(deadline.Token));

        Assert.Same(failure, observed);
        Assert.Equal(2, pipesCreated);
        Assert.Equal(new FileDescriptor[] { new(13), new(12), new(11), new(10) }, closed);
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedRepeatedOpensAdmitTrackingWhileNoOpAliasesStayBounded(bool opens)
    {
        const int Count = 1024;
        var source = "set -e; :" + string.Concat(Enumerable.Repeat(opens ? " >out" : " 1>&1", Count)) + "; printf ready";
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var opened = 0;
        host.BeforeWritableOpen = _ => opened++;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, deadline.Token);

        Assert.Equal(opens ? 1 : 0, result.ExitCode);
        Assert.Equal(opens ? "" : "ready", host.StandardOutput.TextContent);
        Assert.Equal(opens, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
        if (opens)
        {
            Assert.InRange(opened, 1, Count - 1);
            Assert.Empty(host.ReadFile("/out"));
        }
        else
            Assert.Equal(0, opened);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CleanupMetadataStaysChargedAfterWorkspaceRetiresUntilTheCloseCompletes()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(":"));
        evaluation.Memory.Budget = owner;
        evaluation.TrackOwnedDescriptor(new FileDescriptor(42));
        await evaluation.EnsureRunning(CancellationToken.None);
        var before = governor.ReservedBytes;
        await using var host = new DelayedCloseHost { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new Engine().RunAsync(evaluation, host, owner, deadline.Token);
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes >= before);
            Assert.Equal(new FileDescriptor(42), Assert.Single(evaluation.GetDescriptorsForCleanup()));
        }
        finally
        {
            host.Release.TrySetResult();
            await running;
        }

        Assert.Equal(0, (await running).ExitCode);
        Assert.Equal(1, host.Closes);
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
        Assert.True(governor.ReservedBytes < before);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefusedHandoffsSettleEveryChildAndPreserveEarlierCancellation(bool cancel, bool closeFails)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(": | : | :"));
        evaluation.Memory.Budget = owner;
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 2; index++)
        {
            var pipe = Assert.IsType<CreatePipeOperation>(await evaluation.GetNextStep(cancellation.Token));
            pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10 + 2 * index, 11 + 2 * index)));
        }
        var pipeline = Assert.IsType<RunPipelineEvaluations>(await evaluation.GetNextStep(cancellation.Token));
        pipeline.Stages[0].Evaluation.TrackOwnedDescriptor(new FileDescriptor(100));
        pipeline.Stages[1].Evaluation.TrackOwnedDescriptor(new FileDescriptor(200));
        // Let PIPESTATUS publication use prepaid workspace. Only receiving the child's
        // cleanup index must acquire new admission when the shared allowance is full.
        var workspace = evaluation.Memory.Workspace;
        var logicalBytes = workspace.Bytes;
        workspace.Grow(4096);
        workspace.Resize(logicalBytes);
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        if (cancel)
        {
            cancellation.Cancel();
            Assert.True(pipeline.TrySetCanceled(cancellation.Token));
        }
        else
            pipeline.SetResult([0, 0, 0]);
        var close = Assert.IsType<CloseDescriptorOperation>(await evaluation.GetNextStep(deadline.Token));
        Assert.Equal(new FileDescriptor(100), close.Descriptor);
        var visitor = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        var acknowledgment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        visitor.EnqueueCloseDescriptorTask(acknowledgment.Task);
        visitor.EnqueueCloseDescriptor();
        if (!cancel) visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        var dispatch = ShellOperationDispatcher.DispatchAsync(close, visitor, CancellationToken.None);
        try
        {
            Assert.False(dispatch.IsCompleted);
            Assert.False(evaluation.Completion.IsCompleted);
            Assert.Equal(governor.LimitBytes, governor.ReservedBytes);
            Assert.Equal(new FileDescriptor(100), Assert.Single(pipeline.Stages[0].Evaluation.GetDescriptorsForCleanup()));
        }
        finally
        {
            if (closeFails) acknowledgment.TrySetException(new IOException("secondary close failure"));
            else acknowledgment.TrySetResult();
        }
        await dispatch;
        try
        {
            while (await evaluation.GetNextStep(deadline.Token) is { } operation)
                await ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None);
        }
        catch (OperationCanceledException error) when (cancel)
        {
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation.Completion);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(visitor.Appends);
        }
        else
        {
            await evaluation.Completion;
            Assert.Equal(1, evaluation.LastExitCode);
            Assert.Contains(visitor.Appends, append => System.Text.Encoding.UTF8.GetString(append.Content).Contains("shared memory budget exhausted", StringComparison.Ordinal));
        }
        // This test owns the manual operation stream. Drain any successfully handed-off
        // handle without starting a second consumer after the terminal marker was read.
        foreach (var pending in evaluation.GetDescriptorsForCleanup())
        {
            _ = await ShellDescriptorCleanup.CloseAsync(visitor, pending, earlierException: null);
            evaluation.MarkDescriptorCleanupCompleted(pending);
        }
        Assert.Equal(new[] { new FileDescriptor(100), new FileDescriptor(200) }, visitor.ClosedDescriptors);
        Assert.All(pipeline.Stages, stage => Assert.Empty(stage.Evaluation.GetDescriptorsForCleanup()));
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
        blocking.Dispose();
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class DelayedCloseHost : TestHost
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Closes { get; private set; }
        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            Closes++;
            Entered.TrySetResult();
            await Release.Task;
            return null;
        }
        public override async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await base.DisposeAsync();
        }
    }
}

using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DescriptorCloseBatchTests
{
    [Fact]
    public void CleanupBatchAdmissionCanRefuseBeforeAcquiringOrPublishingHandles()
    {
        var governor = new MemoryGovernor(96 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(":"));
        evaluation.Memory.Budget = owner;

        Assert.Throws<MemoryBudgetExceededException>(() => evaluation.PrepareOwnedDescriptors(1024));

        Assert.Empty(evaluation.GetDescriptorsForCleanup());
        evaluation.ReleaseDescriptorCleanupMemory();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefusedSetupClosesEveryAcquisitionWhileTheAllowanceIsFull(bool cancel, bool closeFails)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var counted = new CountingMemoryBudget(governor);
        using var owner = counted.CreateScope();
        using var cancellation = new CancellationTokenSource();
        await using var host = new RefusingSetupHost(governor, cancellation, cancel, closeFails) { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var source = "set -e; :" + string.Concat(Enumerable.Repeat(" >out", 32)) + "; printf unreached";
        long? closeCalls = null;
        host.BeforeClose = () =>
        {
            closeCalls ??= counted.AdmissionCalls;
            Assert.Equal(closeCalls, counted.AdmissionCalls);
        };

        var running = new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token);
        if (cancel)
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(deadline.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        else Assert.Equal(1, (await running.WaitAsync(deadline.Token)).ExitCode);

        // Already admitted spare capacity remains usable after the host fills the allowance.
        Assert.InRange(host.Opened.Count, 8, 31);
        Assert.Equal(host.Opened.AsEnumerable().Reverse(), host.Closed);
        Assert.Equal("", host.StandardOutput.TextContent);
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        host.ReleaseAllowance();
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrowingOwnershipIncludesItsCleanupBatchWhileBoundedHandlesReuseIt(bool bounded)
    {
        const int Count = 32;
        var suffix = bounded ? " exec {fd}>&-;" : new string(' ', " exec {fd}>&-;".Length);
        var source = $"fd=0; set -e; for ((i=0;i<{Count};i++)); do : {{fd}}>out;" + suffix + " done; printf ready";
        var governor = new MemoryGovernor(24 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var opened = 0;
        host.BeforeWritableOpen = _ => opened++;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, deadline.Token);

        Assert.Equal(bounded ? 0 : 1, result.ExitCode);
        Assert.Equal(bounded ? "ready" : "", host.StandardOutput.TextContent);
        Assert.Equal(!bounded, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
        if (bounded) Assert.Equal(Count, opened);
        else Assert.InRange(opened, 1, Count - 1);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class RefusingSetupHost(
        MemoryGovernor governor, CancellationTokenSource cancellation, bool cancel, bool closeFails) : TestHost, IBashHost
    {
        private IMemoryReservation? _blocking;
        public List<FileDescriptor> Opened { get; } = [];
        public List<FileDescriptor> Closed { get; } = [];
        public Action? BeforeClose { get; set; }

        async Task<FileOpenResult> IBashHost.OpenWritableFileAsync(
            FilePathLookup path, WritableFileOpenMode mode, FileCreationMode creationMode,
            FileWriteRetention retention, CancellationToken cancellationToken)
        {
            var result = await base.OpenWritableFileAsync(path, mode, creationMode, retention, cancellationToken);
            if (result.FileDescriptor is { } descriptor)
            {
                Opened.Add(descriptor);
                if (Opened.Count == 8)
                    _blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
            }
            return result;
        }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            BeforeClose?.Invoke();
            Closed.Add(descriptor);
            if (cancel && Closed.Count == 1) cancellation.Cancel();
            var result = await base.CloseDescriptorAsync(descriptor);
            if (closeFails) throw new IOException("secondary synthetic close failure");
            return result;
        }

        public void ReleaseAllowance()
        {
            _blocking?.Dispose();
            _blocking = null;
        }

        public override async ValueTask DisposeAsync()
        {
            ReleaseAllowance();
            await base.DisposeAsync();
        }
    }
}

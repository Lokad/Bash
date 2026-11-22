using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DescriptorMemoryTests
{
    [Fact]
    public void SparseBorrowedMapsAdmitCompactedBackingBeforeTheWrite()
    {
        var bindings = Bindings(256);
        for (var number = 10; number < 266; number++)
            Assert.True(bindings.Remove(new LogicalDescriptor(number)));
        var inherited = bindings.Capture();
        var child = DescriptorTable.Snapshot.FromInheritedBindings(inherited).Fork();
        var governor = new MemoryGovernor(2048);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        child.Set(new LogicalDescriptor(300), FileDescriptor.DevNull, memory);
        Assert.Equal(FileDescriptor.DevNull, child.Resolve(new LogicalDescriptor(300)));
        Assert.False(inherited.HasMapping(new LogicalDescriptor(300)));
        Assert.True(child.OwnedMemoryBytes <= governor.ReservedBytes);
        Assert.InRange(child.OwnedMemoryBytes, 1, 2048);
    }

    [Fact]
    public void AdmissionPreparationDoesNotAcquireOrPublishBorrowedBacking()
    {
        var inherited = Bindings(128).Capture();
        var table = DescriptorTable.Snapshot.FromInheritedBindings(inherited).Fork();
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var number = new LogicalDescriptor(10);

        table.PrepareSet(number, FileDescriptor.DevNull, memory.Workspace);
        Assert.True(governor.ReservedBytes > 0);
        Assert.Equal(0, table.OwnedMemoryBytes);
        Assert.Equal(FileDescriptor.StdOut, table.Resolve(number));
        Assert.Equal(FileDescriptor.StdOut, inherited.Resolve(number));

        // Refusal at a later receiver leaves no newly allocated tree to retain.
        memory.Workspace.Resize(0);
        memory.ReleaseUnusedCapacity();
        Assert.Equal(0, governor.ReservedBytes);
        using var blocker = owner.Reserve(governor.LimitBytes);
        Assert.Throws<MemoryBudgetExceededException>(() =>
            table.PrepareSet(number, FileDescriptor.DevNull, memory.Workspace));
        Assert.Equal(0, table.OwnedMemoryBytes);
        Assert.Equal(FileDescriptor.StdOut, table.Resolve(number));
    }

    [Fact]
    public void AdmittedChangedPathsReuseTheirOwnersReplacementHeadroom()
    {
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var parent = new DescriptorTable();
        for (var index = 0; index < 128; index++)
            parent.Set(new LogicalDescriptor(10 + index), new FileDescriptor(1000 + index), memory);
        var inherited = parent.Capture();
        var child = inherited.Fork();
        var number = new LogicalDescriptor(10);
        child.Set(number, new FileDescriptor(2000), memory);
        var retained = parent.OwnedMemoryBytes + child.OwnedMemoryBytes;
        memory.Workspace.Resize(retained);
        Assert.True(governor.ReservedBytes >= retained);
        using var blocker = owner.Reserve(governor.LimitBytes - governor.ReservedBytes);

        var failure = Record.Exception(() =>
        {
            for (var index = 0; index < 1024; index++)
            {
                // Only this operation's old/new paths overlap; discarded snapshots
                // have no pending user. Parent backing has its separate live owner.
                _ = child.Capture();
                child.Set(number, new FileDescriptor(3000 + index), memory);
            }
        });
        Assert.Null(failure);
        Assert.Equal(retained, parent.OwnedMemoryBytes + child.OwnedMemoryBytes);
        Assert.Equal(new FileDescriptor(1000), inherited.Resolve(number));
        Assert.Equal(new FileDescriptor(4023), child.Resolve(number));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctPersistentMapsConsumeAdmissionWhileBoundedBindingsReuseIt(bool prepared)
    {
        // Measure a short bounded run so startup bindings and parser overhead do not set the cutoff.
        // The longer run has the same descriptor shape and a small allowance for its command values.
        var boundedPeak = await RunAsync(dense: false, iterations: 16, limit: 128 * 1024);
        foreach (var dense in new[] { false, true })
            await RunAsync(dense, iterations: 1024, limit: boundedPeak + 1024);

        async Task<long> RunAsync(bool dense, int iterations, long limit)
        {
            var script = $"set -e; for ((i=0;i<{iterations};i++)); do eval \"exec $((10+i*"
                + (dense ? "1" : "0") + "))>&1\"; done; printf ready";
            var governor = new MemoryGovernor(limit);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, deadline.Token)
                : await BashScript.RunAsync(host, script, EvaluationOptions.Default, deadline.Token);

            Assert.Equal(dense ? 1 : 0, result.ExitCode);
            Assert.Equal(dense ? "" : "ready", host.StandardOutput.TextContent);
            Assert.Equal(dense, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
            Assert.Equal(0, host.OpenFileCount);
            if (!prepared)
                Assert.Equal(0, governor.ReservedBytes);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return governor.PeakBytes;
        }
    }

    [Fact]
    public void BorrowedMappingsAndUnchangedWritesDoNotAcquireTheirParentsStorage()
    {
        var bindings = Bindings(4096);
        var snapshot = bindings.Capture();
        var child = DescriptorTable.Snapshot.FromInheritedBindings(snapshot).Fork();
        var governor = new MemoryGovernor(0);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        child.Set(new LogicalDescriptor(10), FileDescriptor.StdOut, memory);
        child.Set(LogicalDescriptor.StdOut, FileDescriptor.DevNull, memory);
        Assert.Equal(0, child.OwnedMemoryBytes);
        Assert.Equal(0, governor.ReservedBytes);
        Assert.Throws<MemoryBudgetExceededException>(() => child.Set(new LogicalDescriptor(9), FileDescriptor.StdOut, memory));
        Assert.False(child.HasMapping(new LogicalDescriptor(9)));
        Assert.Equal(FileDescriptor.StdOut, snapshot.StdOut);
        Assert.Equal(FileDescriptor.StdOut, snapshot.Resolve(new LogicalDescriptor(10)));
    }

    [Fact]
    public void RefusedCapacityGrowthLeavesThePublishedBindingsIntact()
    {
        var governor = new MemoryGovernor(4096);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var table = new DescriptorTable();
        var accepted = new List<LogicalDescriptor>();
        var refused = false;
        for (var number = 10; number < 1000; number++)
        {
            var descriptor = new LogicalDescriptor(number);
            try { table.Set(descriptor, FileDescriptor.StdOut, memory); }
            catch (MemoryBudgetExceededException)
            {
                Assert.False(table.HasMapping(descriptor));
                refused = true;
                break;
            }
            accepted.Add(descriptor);
        }

        Assert.True(refused);
        Assert.NotEmpty(accepted);
        Assert.All(accepted, descriptor => Assert.Equal(FileDescriptor.StdOut, table.Resolve(descriptor)));
        Assert.True(governor.ReservedBytes >= table.OwnedMemoryBytes);
        memory.Workspace.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void PersistentRefusalPreparesAllReceiversBeforePublishingAnyAlias()
    {
        var root = DescriptorTable.Snapshot.FromInheritedBindings(Bindings(4096).Capture()).Fork();
        var middle = new DescriptorTable();
        var current = new DescriptorTable();
        var number = new LogicalDescriptor(10);
        middle.Set(number, FileDescriptor.StdOut);
        current.Set(number, FileDescriptor.StdOut);
        _ = middle.Capture();
        _ = current.Capture();
        var state = new RedirectionState(root);
        state.Push(middle, new DescriptorHandleOwner(), new HashSet<LogicalDescriptor>(), null);
        state.Push(current, new DescriptorHandleOwner(), new HashSet<LogicalDescriptor>(), null);
        var governor = new MemoryGovernor(4096);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        var metadata = state.OwnedMemoryBytes - root.OwnedMemoryBytes - middle.OwnedMemoryBytes - current.OwnedMemoryBytes;
        Assert.Throws<MemoryBudgetExceededException>(() => state.SetPersistentDescriptor(number, FileDescriptor.DevNull, memory, acquired: false));

        Assert.Equal(FileDescriptor.StdOut, root.Resolve(number));
        Assert.Equal(FileDescriptor.StdOut, middle.Resolve(number));
        Assert.Equal(FileDescriptor.StdOut, current.Resolve(number));
        Assert.Equal(metadata + root.OwnedMemoryBytes + middle.OwnedMemoryBytes + current.OwnedMemoryBytes, state.OwnedMemoryBytes);
    }

    [Fact]
    public void PreserveRefusalLeavesTheScopeActiveForOrdinaryRestoration()
    {
        var root = DescriptorTable.Snapshot.FromInheritedBindings(Bindings(4096).Capture()).Fork();
        var child = new DescriptorTable();
        var number = new LogicalDescriptor(10);
        child.Set(number, FileDescriptor.DevNull);
        var opened = new[] { new FileDescriptor(123) };
        var state = new RedirectionState(root);
        state.Push(child, new DescriptorHandleOwner(opened), new HashSet<LogicalDescriptor> { number }, null);
        using var owner = new MemoryGovernor(0).CreateScope();

        var before = state.OwnedMemoryBytes;
        Assert.Throws<MemoryBudgetExceededException>(() => state.Preserve(new EvaluationMemory(owner), null, null));
        Assert.Equal(before, state.OwnedMemoryBytes);
        Assert.Same(child, state.CurrentTable);
        Assert.Equal(FileDescriptor.StdOut, root.Resolve(number));
        Assert.Same(opened, state.Pop());
        Assert.Same(root, state.CurrentTable);
        Assert.True(state.OwnedMemoryBytes > root.OwnedMemoryBytes); // The retained stack buffer still belongs to this state.
    }

    [Fact]
    public void PartialPreservationTransfersEarlierHandlesAndClosesOnlyUnreferencedOnes()
    {
        var root = DescriptorTable.Snapshot.FromInheritedBindings(Bindings(100).Capture()).Fork();
        var child = new DescriptorTable();
        var numbers = Enumerable.Range(1000, 256).Select(number => new LogicalDescriptor(number)).ToArray();
        var opened = numbers.Select(number => new FileDescriptor(number.Number)).ToArray();
        for (var index = 0; index < numbers.Length; index++)
            child.Set(numbers[index], opened[index]);
        var state = new RedirectionState(root);
        state.Push(child, new DescriptorHandleOwner(opened), numbers.ToHashSet(), null);
        using var owner = new MemoryGovernor(64 * 1024).CreateScope();

        var metadata = state.OwnedMemoryBytes - root.OwnedMemoryBytes - child.OwnedMemoryBytes;
        Assert.Throws<MemoryBudgetExceededException>(() => state.Preserve(new EvaluationMemory(owner), null, null));
        Assert.Same(child, state.CurrentTable);
        var beforePop = state.OwnedMemoryBytes;
        var retained = opened.Where((_, index) => !root.Resolve(numbers[index]).IsClosed).ToArray();
        Assert.NotEmpty(retained);
        Assert.True(retained.Length < opened.Length);
        Assert.Same(opened, state.Pop());
        var owned = typeof(RedirectionState).GetField("_openedDescriptors",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(state);
        var rootOwner = Assert.IsType<DescriptorHandleOwner>(owned);
        Assert.Equal(metadata + root.OwnedMemoryBytes + child.OwnedMemoryBytes + rootOwner.OwnedMemoryBytes, beforePop);
        var batch = new OrderedDescriptorSet();
        batch.PrepareAdditional(opened.Length, null);
        var closing = state.TransferHandles(opened, null, batch);

        Assert.Equal(retained, state.OpenedDescriptors);
        Assert.Equal(opened.Except(retained).Reverse(), closing);
        Assert.DoesNotContain(closing, descriptor => root.References(descriptor));
    }

    [Fact]
    public async Task MapCopyRefusalDiagnosesBeforeEarlierRedirectionsClose()
    {
        const string Script = ": 2>error 9>effect; printf '|%s|tail' \"$?\"";
        var bindings = Bindings(4096);
        var snapshot = bindings.Capture();
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        host.AddFile("/effect", "prior");
        var opens = new List<string>();
        host.BeforeWritableOpen = path => opens.Add(path.Path);
        var evaluation = new Eval(Parser.Parse(Script),
            new EvaluationDescriptorSetup(DescriptorTable.Snapshot.FromInheritedBindings(snapshot), []),
            null, null, EvaluationOptions.Default, owner, null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("|1|tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(new[] { "/error", "/effect" }, opens);
        Assert.Empty(host.ReadFile("/effect")); // The reached open/truncation remains an earlier effect.
        Assert.Contains("shared memory budget exhausted", host.ReadFile("/error"));
        Assert.False(snapshot.HasMapping(new LogicalDescriptor(9)));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubshellsKeepSavedParentMapsAdmittedThroughPendingHostQueries(bool cancel)
    {
        var script = string.Join(";", Enumerable.Range(10, 128).Select(number => "exec " + number + ">&1"))
            + "; [[ -e /outer ]]; ( [[ -e /inner ]] ); printf ready";
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var evaluation = new Eval(Parser.Parse(script));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long parent = 0;
        long child = 0;
        host.BeforeGetPathInfo = path =>
        {
            if (path.Path == "/outer") parent = evaluation.RetainedDescriptorMemoryBytes;
        };
        host.AfterGetPathInfo = async (path, _) =>
        {
            if (path.Path != "/inner") return;
            child = evaluation.RetainedDescriptorMemoryBytes;
            entered.TrySetResult();
            await release.Task;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new Engine().RunAsync(evaluation, host, owner, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(parent > 0);
            Assert.Equal(parent, child);
            Assert.True(governor.ReservedBytes >= parent);
            if (cancel) deadline.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes >= parent);
        }
        finally { release.TrySetResult(); }

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        else
        {
            Assert.Equal(0, (await running).ExitCode);
            Assert.Equal("ready", host.StandardOutput.TextContent);
        }
        Assert.Equal(parent, evaluation.RetainedDescriptorMemoryBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RefusalAfterAPersistentAllocationKeepsItsHandleThroughPendingCleanup()
    {
        const string Script = ": 2>error {first}>one {second}>$(printf two); printf '|%s|' \"$?\"; printf kept >&$first; printf tail";
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(Script),
            new EvaluationDescriptorSetup(DescriptorTable.Snapshot.FromInheritedBindings(Bindings(4096).Capture()), []),
            null, null, EvaluationOptions.Default, owner, null);
        await using var host = new RefusingOpenHost(governor, evaluation.Memory) { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new Engine().RunAsync(evaluation, host, owner, deadline.Token);
        try
        {
            await host.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(evaluation.RetainedDescriptorMemoryBytes > 0);
            Assert.True(governor.ReservedBytes >= evaluation.RetainedDescriptorMemoryBytes);
        }
        finally
        {
            host.Release.TrySetResult();
            await running;
        }

        Assert.Equal(0, (await running).ExitCode);
        Assert.Equal("|1|tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("kept", host.ReadFile("/one"));
        Assert.Empty(host.ReadFile("/two"));
        Assert.Contains("shared memory budget exhausted", host.ReadFile("/error"));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RefusalAfterAMoveRetainsTheSourceCloseAndRetiresItsHandle()
    {
        // The open of "two" exhausts the remaining allowance before the here-string is built.
        const string script = """
            exec 3>one
            : 2>error 4>&3- 3500>two 5<<<text
            printf '|%s|' "$?"
            printf after >&3
            printf ':%s:' "$?"
            """;
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(script),
            new EvaluationDescriptorSetup(DescriptorTable.Snapshot.FromInheritedBindings(Bindings(4096).Capture()), []),
            null, null, EvaluationOptions.Default, owner, null);
        await using var host = new RefusingOpenHost(governor, evaluation.Memory) { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new Engine().RunAsync(evaluation, host, owner, deadline.Token);
        try
        {
            await host.Closing.Task.WaitAsync(deadline.Token);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            host.Release.TrySetResult();
        }

        Assert.Equal(0, (await running.WaitAsync(deadline.Token)).ExitCode);
        Assert.Equal("|1|:1:", host.StandardOutput.TextContent);
        Assert.Equal("3: Bad file descriptor\n", host.StandardError.TextContent);
        Assert.Empty(host.ReadFile("/one"));
        Assert.Empty(host.ReadFile("/two"));
        Assert.Contains("shared memory budget exhausted", host.ReadFile("/error"));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class RefusingOpenHost : TestHost
    {
        private IMemoryReservation? _blocking;
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RefusingOpenHost(MemoryGovernor governor, EvaluationMemory memory)
        {
            BeforeWritableOpen = path =>
            {
                if (path.Path == "/two")
                {
                    memory.ReleaseUnusedCapacity();
                    _blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
                }
            };
        }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            if (_blocking != null)
            {
                _blocking.Dispose();
                _blocking = null;
                Closing.TrySetResult();
                await Release.Task;
            }
            return await base.CloseDescriptorAsync(descriptor);
        }

        public override async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            _blocking?.Dispose();
            _blocking = null;
            await base.DisposeAsync();
        }
    }

    private static DescriptorBindings Bindings(int count)
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        for (var number = 10; number < 10 + count; number++)
            bindings.Set(new LogicalDescriptor(number), FileDescriptor.StdOut);
        return bindings;
    }
}

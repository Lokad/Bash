using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

[Collection(ComplexityRegressionCollection.Name)]
public sealed partial class ComplexityRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DescriptorWritesReusePathsUntilAnotherCapture(bool capture)
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        for (var index = 0; index < 1024; index++)
            bindings.Set(new LogicalDescriptor(10 + index), new FileDescriptor(1000 + index));
        var snapshot = capture ? bindings.Capture() : DescriptorBindings.Snapshot.Standard;
        var number = new LogicalDescriptor(10);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1024; index++)
            bindings.Set(number, new FileDescriptor(10000 + index));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 8192,
            $"Writes after {(capture ? "one capture" : "no capture")} allocated {allocated:N0} bytes; writable paths were not reused.");
        Assert.Equal(new FileDescriptor(11023), bindings.Resolve(number));
        if (capture) Assert.Equal(new FileDescriptor(1000), snapshot.Resolve(number));
    }

    [Fact]
    public void CapturedDescriptorWritesCopyChangedPathsInsteadOfWholeMaps()
    {
        const int Writes = 1024;
        _ = Measure(64, 16);
        var narrow = Measure(64, Writes);
        var wide = Measure(1024, Writes);
        Assert.True(wide < 2 * 1024 * 1024,
            $"1,024 captured writes allocated {wide:N0} bytes; descriptor maps were likely copied in full.");
        Assert.True(wide <= 3 * narrow,
            $"Captured writes allocated {narrow:N0} at width 64 and {wide:N0} at width 1,024.");

        static long Measure(int width, int writes)
        {
            var bindings = DescriptorBindings.Snapshot.Standard.Fork();
            for (var index = 0; index < width; index++)
                bindings.Set(new LogicalDescriptor(10 + index), new FileDescriptor(1000 + index));
            var snapshots = new DescriptorBindings.Snapshot[writes];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < writes; index++)
            {
                snapshots[index] = bindings.Capture();
                bindings.Set(new LogicalDescriptor(10), new FileDescriptor(10000 + index));
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            for (var index = 0; index < writes; index++)
                Assert.Equal(new FileDescriptor(index == 0 ? 1000 : 9999 + index),
                    snapshots[index].Resolve(new LogicalDescriptor(10)));
            GC.KeepAlive(snapshots);
            return allocated;
        }
    }

    [Fact]
    public void SequentialPersistentHandlesDoNotCopyGrowingOwnershipPrefixes()
    {
        const int Count = 1024;
        using var owner = new MemoryGovernor(64 * 1024 * 1024).CreateScope();
        var memory = new EvaluationMemory(owner);
        var root = new DescriptorTable();
        var state = new RedirectionState(root);
        var batch = new FileDescriptor[1];
        var closing = new OrderedDescriptorSet();
        // Warm generic index and transfer paths outside the measured owner.
        var warmup = new RedirectionState(new DescriptorTable());
        warmup.SetPersistentDescriptor(LogicalDescriptor.StdOut, new FileDescriptor(9), memory, acquired: true);
        Assert.Empty(warmup.TransferHandles(new[] { new FileDescriptor(9) }, memory, closing));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Count; index++)
        {
            batch[0] = new FileDescriptor(1000 + index);
            state.SetPersistentDescriptor(LogicalDescriptor.StdOut, batch[0], memory, acquired: true);
            Assert.True(state.TransferHandles(batch, memory, closing).Count == 0);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Count, state.OpenedDescriptors.Count);
        Assert.True(allocated <= 2048L * Count, $"Sequential ownership publication allocated {allocated:N0} bytes.");
        Assert.Equal(new FileDescriptor(1000 + Count - 1), root.StdOut);
    }

    [Fact]
    public async Task LiteralRedirectionOverheadDoesNotScaleWithVisibleBindings()
    {
        var narrowOverhead = await MeasureOverhead(0);
        var wideOverhead = await MeasureOverhead(4_000);

        Assert.True(wideOverhead <= narrowOverhead + 1024 * 1024,
            $"Literal redirections added {narrowOverhead:N0} bytes without bindings and "
            + $"{wideOverhead:N0} bytes with 4,000 bindings; the variable store was likely copied.");

        static async Task<long> MeasureOverhead(int bindingCount)
        {
            var prefix = string.Concat(Enumerable.Range(0, bindingCount).Select(index => $"v{index}=x;"));
            var withoutRedirection = prefix + "for ((i=0;i<50;i++)); do cat; done";
            var withRedirection = prefix + "for ((i=0;i<50;i++)); do cat </dev/null; done";
            return await Measure(withRedirection) - await Measure(withoutRedirection);

            static async Task<long> Measure(string script)
            {
                // Warm each path; measure across continuations with this collection running alone.
                await EvaluationTestHelper.RunAsync(script, standardInput: null);
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var result = await EvaluationTestHelper.RunAsync(script, standardInput: null);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Assert.Equal(0, result.ExitCode);
                Assert.Empty(result.StdOut);
                Assert.Empty(result.StdErr);
                return allocated;
            }
        }
    }

    [Fact]
    public void RedirectionDescriptorTrackingDoesNotRebuildScopeStack()
    {
        var state = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        var descriptor = new FileDescriptor(20);
        var outer = new DescriptorTable();
        outer.Set(new LogicalDescriptor(10), descriptor);
        var outerOpened = new List<FileDescriptor>();
        state.PushRedirectionTable(outer, new DescriptorHandleOwner(outerOpened), new HashSet<LogicalDescriptor>(), null);
        Assert.Same(outerOpened, state.CurrentRedirectionOpenedDescriptors);
        state.PushRedirectionTable(new DescriptorTable(), new DescriptorHandleOwner(), new HashSet<LogicalDescriptor>(), null);

        IReadOnlyList<FileDescriptor> batch = [descriptor];
        var closing = new OrderedDescriptorSet();
        var queuedForClose = 0;
        for (var iteration = 0; iteration < 20_000; iteration++)
            Transfer();

        AllocationAssert.SustainedReuse(Transfer, iterations: 10_000, maximumBytes: 1_024);

        Assert.Equal(0, queuedForClose);
        Assert.Empty(state.PopRedirectionTable());
        Assert.Equal(descriptor, Assert.Single(state.CurrentRedirectionOpenedDescriptors));
        Assert.Empty(outerOpened);

        void Transfer() => queuedForClose += state.TransferRedirectionDescriptors(batch, null, closing).Count;
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void RedirectionDescriptorBatchesUseLinearOwnershipStorage(int count)
    {
        var table = new DescriptorTable();
        var handles = Enumerable.Range(0, count).Select(index => new FileDescriptor(1000 + index)).ToArray();
        for (var index = 0; index < count; index++)
            table.Set(new LogicalDescriptor(10 + index), handles[index]);
        var script = Parser.Parse(":");
        var warmup = new EvaluationState(script, ShellPath.Root, table);
        var batch = new OrderedDescriptorSet();
        Assert.Empty(warmup.TransferRedirectionDescriptors(handles, null, batch));
        var state = new EvaluationState(script, ShellPath.Root, table);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var closing = state.TransferRedirectionDescriptors(handles, null, batch);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(closing);
        Assert.Equal(handles, state.CurrentRedirectionOpenedDescriptors);
        // Indexed encounter order has wider entries than a packed list. Storage
        // stays linear instead of copying an owner's prefix for each new handle.
        Assert.True(allocated <= 128L * count + 4096,
            $"Transferring {count} handles allocated {allocated:N0} bytes; ownership lists were likely copied per handle.");
    }

    [Fact]
    public void DescriptorBatchTransfersPreserveEachFramesOrderAndBorrowedOwnership()
    {
        var inherited = new FileDescriptor(20);
        var rootHandle = new FileDescriptor(21);
        var middleHandle = new FileDescriptor(22);
        var currentHandle = new FileDescriptor(23);
        var firstClosed = new FileDescriptor(24);
        var secondClosed = new FileDescriptor(25);
        var rootTable = new DescriptorTable();
        rootTable.Set(new LogicalDescriptor(10), inherited);
        var owned = new List<FileDescriptor> { inherited };
        var parent = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        parent.PushRedirectionTable(rootTable, new DescriptorHandleOwner(owned), new HashSet<LogicalDescriptor>(), null);
        using var memory = new MemoryGovernor(1024).CreateScope();
        var child = parent.CloneForSubshell(new EvaluationMemory(memory));
        child.SetPersistentDescriptor(new LogicalDescriptor(11), rootHandle, null, acquired: false);
        var middleTable = new DescriptorTable();
        middleTable.Set(new LogicalDescriptor(12), middleHandle);
        child.PushRedirectionTable(middleTable, new DescriptorHandleOwner(), new HashSet<LogicalDescriptor>(), null);
        var currentTable = new DescriptorTable();
        currentTable.Set(new LogicalDescriptor(13), currentHandle);
        child.PushRedirectionTable(currentTable, new DescriptorHandleOwner(), new HashSet<LogicalDescriptor>(), null);

        var batch = new OrderedDescriptorSet();
        batch.PrepareAdditional(2, null);
        var closing = child.TransferRedirectionDescriptors(
            [inherited, rootHandle, middleHandle, currentHandle, firstClosed, secondClosed, rootHandle], null, batch);

        Assert.Equal(new[] { secondClosed, firstClosed }, closing);
        Assert.Equal(new[] { currentHandle }, child.PopRedirectionTable());
        Assert.Equal(new[] { middleHandle }, child.PopRedirectionTable());
        Assert.Equal(new[] { inherited, rootHandle }, child.CurrentRedirectionOpenedDescriptors);
        Assert.Same(owned, parent.CurrentRedirectionOpenedDescriptors);
        Assert.Equal(new[] { inherited }, owned);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComplexityRegressionCollection
{
    public const string Name = "Complexity regressions";
}

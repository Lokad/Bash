using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

[Collection(ComplexityRegressionCollection.Name)]
public sealed partial class ComplexityRegressionTests
{
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
        for (var iteration = 0; iteration < 20_000; iteration++)
            Assert.True(state.TransferRedirectionDescriptors(batch, null, closing).Count == 0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10_000; iteration++)
            Assert.True(state.TransferRedirectionDescriptors(batch, null, closing).Count == 0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated <= 1_024,
            $"Repeated descriptor tracking allocated {allocated:N0} bytes; the scope stack was likely copied.");
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

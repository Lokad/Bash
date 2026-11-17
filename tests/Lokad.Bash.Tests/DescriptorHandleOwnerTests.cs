using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class DescriptorHandleOwnerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(128)]
    public void PublicationSettlementKeepsTransferOrderWithoutNewAdmission(int count)
    {
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        var counted = new CountingMemoryBudget(governor);
        using var owner = counted.CreateScope();
        using var memory = new MemoryCapacity(owner, 0);
        var handles = new DescriptorHandleOwner();
        handles.PrepareAdditional(1, memory);
        handles.AddPrepared(new FileDescriptor(40));
        for (var index = count - 1; index >= 0; index--)
        {
            handles.PreparePublication(memory);
            handles.PublishPrepared(new FileDescriptor(100 + index));
        }
        var retained = handles.OwnedMemoryBytes;
        var calls = counted.AdmissionCalls;
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        handles.SettlePrepared(new FileDescriptor(40)); // Previously settled ownership keeps its position.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
            handles.SettlePrepared(new FileDescriptor(100 + index));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(calls, counted.AdmissionCalls);
        Assert.True(allocated <= 1024, $"Settlement allocated {allocated} bytes.");
        Assert.Equal(new[] { new FileDescriptor(40) }.Concat(Enumerable.Range(100, count).Select(i => new FileDescriptor(i))), handles.Handles);
        Assert.True(handles.OwnedMemoryBytes <= retained);
        if (count == 128) Assert.True(handles.OwnedMemoryBytes < retained);
        Assert.Same(handles.Handles, handles.Capture().Handles);
    }

    [Fact]
    public void RefusedDetachmentLeavesTheBorrowedHandlesUnchanged()
    {
        var borrowed = Enumerable.Range(0, 1024).Select(i => new FileDescriptor(100 + i)).ToArray();
        var handles = new DescriptorHandleOwner(borrowed);
        using var owner = new MemoryGovernor(0).CreateScope();
        using var capacity = new MemoryCapacity(owner, 0);

        Assert.Throws<MemoryBudgetExceededException>(() => handles.PrepareAdditional(1, capacity));

        Assert.Same(borrowed, handles.Handles);
        Assert.Equal(0, handles.OwnedMemoryBytes);
        Assert.Equal(borrowed, handles.Handles);
    }

    [Fact]
    public void ForksBorrowCompletedStorageUntilTheirOwnAdmittedWrite()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var counted = new CountingMemoryBudget(governor);
        using var owner = counted.CreateScope();
        using var capacity = new MemoryCapacity(owner, 0);
        var parent = new DescriptorHandleOwner();
        parent.PrepareAdditional(2, capacity);
        parent.AddPrepared(new FileDescriptor(40));
        parent.AddPrepared(new FileDescriptor(41));
        var calls = counted.AdmissionCalls;
        var child = parent.Capture();
        Assert.Same(parent.Handles, child.Handles);
        Assert.Equal(0, child.OwnedMemoryBytes);
        child.AddPrepared(new FileDescriptor(40)); // Existing entries do not mutate shared storage.
        Assert.Equal(calls, counted.AdmissionCalls);

        child.PrepareAdditional(1, capacity);
        child.AddPrepared(new FileDescriptor(42));
        parent.PrepareAdditional(0, capacity);
        Assert.True(parent.RemovePrepared(new FileDescriptor(40)));

        Assert.Equal(new[] { new FileDescriptor(41) }, parent.Handles);
        Assert.Equal(new[] { new FileDescriptor(40), new FileDescriptor(41), new FileDescriptor(42) }, child.Handles);
        Assert.NotSame(parent.Handles, child.Handles);
        Assert.True(parent.OwnedMemoryBytes > 0 && child.OwnedMemoryBytes > 0);
    }

    [Fact]
    public void RefusedParentWriteDoesNotAlterAnExistingFork()
    {
        var parent = new DescriptorHandleOwner();
        parent.PrepareAdditional(1, null);
        parent.AddPrepared(new FileDescriptor(40));
        var child = parent.Capture();
        var retained = parent.OwnedMemoryBytes;
        using var owner = new MemoryGovernor(0).CreateScope();
        using var capacity = new MemoryCapacity(owner, 0);

        Assert.Throws<MemoryBudgetExceededException>(() => parent.PrepareAdditional(1, capacity));

        Assert.Same(parent.Handles, child.Handles);
        Assert.Equal(retained, parent.OwnedMemoryBytes);
        Assert.Equal(new[] { new FileDescriptor(40) }, child.Handles);
    }
}

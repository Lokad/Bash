using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class OrderedDescriptorSetTests
{
    [Fact]
    public void ReversalPreservesReusedSlotsAndSubsequentEncounterOrder()
    {
        var descriptors = new OrderedDescriptorSet();
        descriptors.PrepareAdditional(4, null);
        for (var id = 10; id < 14; id++) descriptors.AddPrepared(new FileDescriptor(id));
        descriptors.Remove(new FileDescriptor(11));

        descriptors.Reverse();
        descriptors.AddPrepared(new FileDescriptor(14));

        Assert.Equal(new[] { 13, 12, 10, 14 }, descriptors.Select(d => d.Id));
        descriptors.Remove(new FileDescriptor(10));
        descriptors.Reverse();
        Assert.Equal(new[] { 14, 12, 13 }, descriptors.Select(d => d.Id));
        Assert.True(descriptors.TryPeekFirst(out var first));
        Assert.Equal(new FileDescriptor(14), first);
    }

    [Fact]
    public void ConsumedBatchesClearAndReverseWithoutFurtherAllocationOrAdmission()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var counted = new CountingMemoryBudget(governor);
        using var owner = counted.CreateScope();
        using var memory = new MemoryCapacity(owner, 0);
        var descriptors = new OrderedDescriptorSet();
        descriptors.PrepareAdditional(128, memory);
        var calls = counted.AdmissionCalls;
        var retained = descriptors.EstimatedMemoryBytes;
        Cycle();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1000; iteration++) Cycle();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 1024, $"Batch reuse allocated {allocated} bytes.");
        Assert.Equal(calls, counted.AdmissionCalls);
        Assert.Equal(retained, descriptors.EstimatedMemoryBytes);
        Assert.Empty(descriptors);

        void Cycle()
        {
            for (var id = 10; id < 138; id++) descriptors.AddPrepared(new FileDescriptor(id));
            descriptors.Reverse();
            descriptors.Clear();
        }
    }

    [Fact]
    public void ReactivationAndRemovalMatchAnIndependentEncounterOrderModel()
    {
        var descriptors = new OrderedDescriptorSet();
        var expected = new List<FileDescriptor>();
        var random = new Random(173);
        using var owner = new MemoryGovernor(1024 * 1024).CreateScope();
        using var memory = new MemoryCapacity(owner, 0);
        for (var iteration = 0; iteration < 4000; iteration++)
        {
            var descriptor = new FileDescriptor(10 + random.Next(31));
            if (random.Next(2) == 0)
            {
                var adding = !expected.Contains(descriptor);
                if (adding)
                {
                    descriptors.PrepareAdditional(1, memory);
                    expected.Add(descriptor);
                }
                Assert.Equal(adding, descriptors.AddPrepared(descriptor));
            }
            else
                Assert.Equal(expected.Remove(descriptor), descriptors.Remove(descriptor));
            Assert.Equal(expected, descriptors.ToArray());
            Assert.Equal(expected.Count != 0, descriptors.TryPeekFirst(out var first));
            if (expected.Count != 0)
                Assert.Equal(expected[0], first);
        }
    }

    [Fact]
    public void RefusedGrowthDoesNotAllocateMajorBuffersOrChangeExistingOrder()
    {
        var descriptors = new OrderedDescriptorSet();
        descriptors.PrepareAdditional(3, null); // Caller-owned preparation is independent.
        descriptors.AddPrepared(new FileDescriptor(11));
        descriptors.AddPrepared(new FileDescriptor(12));
        var expected = descriptors.ToArray();
        var retained = descriptors.EstimatedMemoryBytes;
        using var owner = new MemoryGovernor(0).CreateScope();
        using var memory = new MemoryCapacity(owner, 0);
        Assert.Throws<MemoryBudgetExceededException>(() => descriptors.PrepareAdditional(1000, memory));

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<MemoryBudgetExceededException>(() => descriptors.PrepareAdditional(65536, memory));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 32768, $"Refused synchronous preparation allocated {allocated} bytes.");
        Assert.Equal(retained, descriptors.EstimatedMemoryBytes);
        Assert.Equal(expected, descriptors.ToArray());
    }

    [Fact]
    public void BoundedLiveHandlesReuseSlotsWithoutAllocationOrAdditionalAdmission()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var counted = new CountingMemoryBudget(governor);
        using var owner = counted.CreateScope();
        using var memory = new MemoryCapacity(owner, 0);
        var descriptors = new OrderedDescriptorSet();
        descriptors.PrepareAdditional(3, memory);
        descriptors.AddPrepared(new FileDescriptor(10));
        descriptors.AddPrepared(new FileDescriptor(11));
        descriptors.AddPrepared(new FileDescriptor(12));
        for (var iteration = 0; iteration < 1000; iteration++) Cycle();
        var retained = descriptors.EstimatedMemoryBytes;
        var calls = counted.AdmissionCalls;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 20000; iteration++) Cycle();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 1024, $"Bounded synchronous reuse allocated {allocated} bytes.");
        Assert.Equal(calls, counted.AdmissionCalls);
        Assert.Equal(retained, descriptors.EstimatedMemoryBytes);
        Assert.Equal(new[] { new FileDescriptor(11), new FileDescriptor(12), new FileDescriptor(10) }, descriptors.ToArray());

        void Cycle()
        {
            descriptors.Remove(new FileDescriptor(10));
            descriptors.PrepareAdditional(1, memory);
            descriptors.AddPrepared(new FileDescriptor(10));
        }
    }
}

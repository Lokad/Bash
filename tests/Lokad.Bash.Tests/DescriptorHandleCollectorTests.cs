using Lokad.Bash.Helpers;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class DescriptorHandleCollectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public void RuntimeCollectorRefusalLeavesCallerStorageAndMembershipUnchanged(int count)
    {
        var handles = Enumerable.Range(10, count).Select(id => new FileDescriptor(id)).ToList();
        var before = handles.ToArray();
        var capacity = handles.Capacity;
        using var owner = new MemoryGovernor(0).CreateScope();
        using var memory = new MemoryCapacity(owner, 0);
        var collector = new DescriptorHandleCollector(handles, memory);

        Assert.Throws<MemoryBudgetExceededException>(() => collector.Add(new FileDescriptor(5000)));

        Assert.Equal(before, handles);
        Assert.Equal(capacity, handles.Capacity);
        Assert.Equal(0, memory.Bytes);
    }

    [Fact]
    public void CompletedCollectorReusesItsIndexWithoutAdmissionAllocationOrCopy()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var counted = new CountingMemoryBudget(governor);
        using var owner = counted.CreateScope();
        using var memory = new MemoryCapacity(owner, 100);
        var handles = new List<FileDescriptor>();
        var collector = new DescriptorHandleCollector(handles, memory);
        var values = Enumerable.Range(10, 1024).Select(id => new FileDescriptor(id)).ToArray();
        collector.AddRange(values);
        var index = collector.GetIndex();
        var calls = counted.AdmissionCalls;
        var bytes = memory.Bytes;
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            Assert.Same(index, collector.GetIndex());
            collector.Add(values[0]);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 1024, $"Completed-index reuse allocated {allocated} bytes.");
        Assert.Equal(calls, counted.AdmissionCalls);
        Assert.Equal(bytes, memory.Bytes);
        Assert.True(bytes >= 100 + 4L * handles.Capacity + 16L * index.EnsureCapacity(0));
        Assert.Equal(values, handles);
        Assert.Equal(values, index.OrderBy(d => d.Id));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(256)]
    public void MultipleSourcesPreserveEncounterOrderAndDeduplicateHandles(int count)
    {
        var source = Enumerable.Range(10, count).Select(id => new FileDescriptor(id)).ToArray();
        var last = new FileDescriptor(10 + count);
        var handles = new List<FileDescriptor> { FileDescriptor.Closed };
        var collector = new DescriptorHandleCollector(handles);

        collector.Add(FileDescriptor.DevNull);
        collector.AddRange(source);
        collector.AddRange(source.Reverse().ToArray());
        collector.Add(FileDescriptor.Closed);
        collector.Add(last);

        Assert.Equal(new[] { FileDescriptor.Closed, FileDescriptor.DevNull }.Concat(source).Append(last), handles);
    }
}

using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DescriptorTableTests
{
    [Fact]
    public void InheritedMappingsSkipOccupiedNumbersAndReuseClosedNumbers()
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        var alias = new FileDescriptor(900);
        bindings.Set(new LogicalDescriptor(10), alias);
        bindings.Set(new LogicalDescriptor(11), FileDescriptor.DevNull);
        bindings.Set(new LogicalDescriptor(13), FileDescriptor.Closed);
        bindings.Set(new LogicalDescriptor(int.MaxValue), new FileDescriptor(901));
        var captured = bindings.Capture();
        var child = DescriptorTable.Snapshot.FromInheritedBindings(captured).Fork();

        Assert.True(child.TryFindAvailableDescriptor(out var available));
        Assert.Equal(new LogicalDescriptor(12), available);
        child.Set(available, alias);
        Assert.True(child.TryFindAvailableDescriptor(out var next));
        Assert.Equal(new LogicalDescriptor(13), next);

        bindings.Set(new LogicalDescriptor(10), FileDescriptor.Closed);
        Assert.Equal(alias, child.Resolve(new LogicalDescriptor(10)));
        child.Set(new LogicalDescriptor(10), FileDescriptor.Closed);
        Assert.True(child.TryFindAvailableDescriptor(out var reused));
        Assert.Equal(new LogicalDescriptor(10), reused);
        Assert.Equal(alias, captured.Resolve(new LogicalDescriptor(10)));
    }

    [Fact]
    public void AllocationSkipsBindingsReusesClosedNumbersAndLeavesSnapshotsIndependent()
    {
        var table = new DescriptorTable();
        table.Set(new LogicalDescriptor(10), new FileDescriptor(900));
        table.Set(new LogicalDescriptor(12), FileDescriptor.DevNull);
        table.Set(new LogicalDescriptor(int.MaxValue), new FileDescriptor(901));
        Assert.True(table.TryFindAvailableDescriptor(out var first));
        Assert.Equal(new LogicalDescriptor(11), first);
        table.Set(first, new FileDescriptor(902));
        var captured = table.Capture();
        var child = captured.Fork();

        Assert.True(table.TryFindAvailableDescriptor(out var next));
        Assert.Equal(new LogicalDescriptor(13), next);
        table.Set(new LogicalDescriptor(10), FileDescriptor.Closed);
        Assert.True(table.TryFindAvailableDescriptor(out var reused));
        Assert.Equal(new LogicalDescriptor(10), reused);
        Assert.True(child.TryFindAvailableDescriptor(out var childNumber));
        Assert.Equal(new LogicalDescriptor(13), childNumber);
        Assert.Equal(new FileDescriptor(900), captured.Resolve(new LogicalDescriptor(10)));
        Assert.Equal(FileDescriptor.Closed, captured.Resolve(new LogicalDescriptor(900)));
    }

    [Fact]
    public void RebindingStreamsRetainsCapturedMappingsAndAllocationCursor()
    {
        var alias = new FileDescriptor(20);
        var table = new DescriptorTable();
        table.Set(new LogicalDescriptor(10), alias);
        var captured = table.Capture();
        var rebound = captured.WithStandardStreams(alias, FileDescriptor.DevNull, FileDescriptor.Closed);
        var child = rebound.Fork();

        table.Set(new LogicalDescriptor(10), FileDescriptor.Closed);
        child.Set(new LogicalDescriptor(11), new FileDescriptor(21));
        child.Set(LogicalDescriptor.StdOut, new FileDescriptor(22));

        Assert.Equal(FileDescriptor.StdIn, captured.StdIn);
        Assert.Equal(FileDescriptor.DevNull, rebound.StdOut);
        Assert.Equal(alias, child.StdIn);
        Assert.Equal(alias, child.Resolve(new LogicalDescriptor(10)));
        Assert.Equal(alias, rebound.Resolve(new LogicalDescriptor(10)));
        Assert.Equal(FileDescriptor.Closed, rebound.Resolve(new LogicalDescriptor(11)));
        Assert.True(table.TryFindAvailableDescriptor(out var parentNumber));
        Assert.Equal(new LogicalDescriptor(10), parentNumber);
        Assert.True(child.TryFindAvailableDescriptor(out var childNumber));
        Assert.Equal(new LogicalDescriptor(12), childNumber);
    }
}

using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class DescriptorBindingsTests
{
    [Fact]
    public void RemovingAdditionalBindingsPreservesSnapshotsAndOtherAliases()
    {
        var number = new LogicalDescriptor(10);
        var alias = new LogicalDescriptor(11);
        var handle = new FileDescriptor(20);
        var table = new DescriptorBindings(FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);
        table.Set(number, handle);
        table.Set(alias, handle);
        var captured = table.Capture();
        var sibling = captured.Fork();

        Assert.True(table.Remove(number));
        Assert.False(table.Remove(number));
        Assert.False(table.HasMapping(number));
        Assert.Equal(FileDescriptor.Closed, table.Resolve(number));
        Assert.Equal(handle, table.Resolve(alias));
        Assert.Equal(handle, captured.Resolve(number));
        Assert.True(captured.HasMapping(number));
        Assert.Equal(handle, sibling.Resolve(number));

        Assert.True(sibling.Remove(alias));
        Assert.Equal(handle, table.Resolve(alias));
        Assert.Equal(handle, captured.Resolve(alias));
        Assert.Equal(FileDescriptor.Closed, sibling.Resolve(alias));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RemovingStandardBindingsLeavesExplicitClosedStreams(int number)
    {
        var table = new DescriptorBindings(FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);
        var descriptor = new LogicalDescriptor(number);
        var captured = table.Capture();

        Assert.True(table.Remove(descriptor));
        Assert.False(table.Remove(descriptor));
        Assert.True(table.HasMapping(descriptor));
        Assert.Equal(FileDescriptor.Closed, table.Resolve(descriptor));
        Assert.Equal(new FileDescriptor(number), captured.Resolve(descriptor));
    }

    [Fact]
    public void StandardMappingsRemainExplicitAfterReplacement()
    {
        var table = new DescriptorBindings(new FileDescriptor(20), new FileDescriptor(21), new FileDescriptor(22));
        table.Set(LogicalDescriptor.StdIn, FileDescriptor.StdIn);
        table.Set(LogicalDescriptor.StdOut, FileDescriptor.StdOut);
        table.Set(LogicalDescriptor.StdErr, FileDescriptor.StdErr);

        Assert.Equal(FileDescriptor.StdIn, table.StdIn);
        Assert.Equal(FileDescriptor.StdOut, table.StdOut);
        Assert.Equal(FileDescriptor.StdErr, table.StdErr);
    }

    [Fact]
    public void References_TracksSharedResolvedDescriptorsAcrossReplacementAndClone()
    {
        var shared = new FileDescriptor(20);
        var replacement = new FileDescriptor(21);
        var table = new DescriptorBindings(FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);
        table.Set(new LogicalDescriptor(10), shared);
        table.Set(new LogicalDescriptor(11), shared);

        table.Set(new LogicalDescriptor(10), replacement);
        Assert.True(table.References(shared));
        Assert.True(table.References(replacement));

        table.Set(new LogicalDescriptor(11), replacement);
        Assert.False(table.References(shared));

        var clone = table.Capture().Fork();
        table.Set(new LogicalDescriptor(10), shared);
        Assert.True(table.References(shared));
        Assert.False(clone.References(shared));
        Assert.True(clone.References(replacement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedBindingsStayIndependentOfParentAndSiblingWrites(bool includeAdditionalMapping)
    {
        var number = new LogicalDescriptor(9);
        var original = includeAdditionalMapping ? new FileDescriptor(20) : FileDescriptor.Closed;
        var table = new DescriptorBindings(FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);
        if (includeAdditionalMapping)
            table.Set(number, original);
        var captured = table.Capture();
        var first = captured.Fork();
        var second = captured.Fork();

        table.Set(number, new FileDescriptor(21));
        table.Set(LogicalDescriptor.StdIn, new FileDescriptor(24));
        first.Set(number, new FileDescriptor(22));
        second.Set(LogicalDescriptor.StdOut, new FileDescriptor(23));
        second.Set(new LogicalDescriptor(10), original);

        Assert.Equal(original, captured.Resolve(number));
        Assert.Equal(FileDescriptor.StdIn, captured.Resolve(LogicalDescriptor.StdIn));
        Assert.Equal(FileDescriptor.StdIn, first.StdIn);
        Assert.Equal(FileDescriptor.StdIn, second.StdIn);
        Assert.Equal(includeAdditionalMapping, captured.HasMapping(number));
        Assert.Equal(original, second.Resolve(number));
        Assert.Equal(new FileDescriptor(21), table.Resolve(number));
        Assert.Equal(new FileDescriptor(22), first.Resolve(number));
        Assert.Equal(FileDescriptor.StdOut, table.StdOut);
        Assert.Equal(FileDescriptor.StdOut, first.StdOut);
        Assert.Equal(new FileDescriptor(23), second.StdOut);
        Assert.False(captured.HasMapping(new LogicalDescriptor(10)));
        Assert.False(first.HasMapping(new LogicalDescriptor(10)));
    }

    [Fact]
    public void RebindingStandardStreamsPreservesAdditionalAliasesAndDeduplicatesHandles()
    {
        var alias = new FileDescriptor(20);
        var table = new DescriptorBindings(FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);
        table.Set(new LogicalDescriptor(9), alias);
        table.Set(new LogicalDescriptor(10), alias);
        var captured = table.Capture();
        var rebound = captured.WithStandardStreams(alias, FileDescriptor.DevNull, FileDescriptor.Closed);
        var handles = new List<FileDescriptor> { alias };
        var collector = new DescriptorHandleCollector(handles);
        rebound.AddReferencedHandlesTo(ref collector);

        Assert.Equal(FileDescriptor.StdIn, table.StdIn);
        Assert.Equal(alias, rebound.Resolve(LogicalDescriptor.StdIn));
        Assert.Equal(alias, rebound.Resolve(new LogicalDescriptor(9)));
        Assert.Equal(alias, rebound.Resolve(new LogicalDescriptor(10)));
        Assert.Equal(new[] { alias, FileDescriptor.DevNull, FileDescriptor.Closed }, handles);
    }
}

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

    [Fact]
    public void VacantSlotsAndCaptureCompactionPreserveHandleEncounterOrder()
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        bindings.Set(new LogicalDescriptor(30), new FileDescriptor(300));
        bindings.Set(new LogicalDescriptor(10), new FileDescriptor(100));
        bindings.Set(new LogicalDescriptor(20), new FileDescriptor(200));
        Assert.True(bindings.Remove(new LogicalDescriptor(10)));
        bindings.Set(new LogicalDescriptor(40), new FileDescriptor(400));
        var captured = bindings.Capture();
        AssertOrder(captured, 300, 400, 200);

        // A changed write after capture used to compact Dictionary's vacant
        // slots. Parent and sibling writes must retain that observable order.
        bindings.Remove(new LogicalDescriptor(30));
        bindings.Remove(new LogicalDescriptor(20));
        var sparse = bindings.Capture();
        var sibling = sparse.Fork();
        bindings.Set(new LogicalDescriptor(50), new FileDescriptor(500));
        sibling.Set(new LogicalDescriptor(60), new FileDescriptor(600));
        AssertOrder(captured, 300, 400, 200);
        AssertOrder(sparse, 400);
        AssertOrder(bindings.Capture(), 400, 500);
        AssertOrder(sibling.Capture(), 400, 600);

        static void AssertOrder(DescriptorBindings.Snapshot snapshot, params int[] expected)
        {
            var handles = new List<FileDescriptor>();
            snapshot.AddReferencedHandlesTo(handles);
            Assert.Equal(new[] { 0, 1, 2 }.Concat(expected), handles.Select(handle => handle.Id));
        }
    }

    [Fact]
    public void CapturedSparseMapsBoundAbandonedSlotsAcrossRepeatedRemovalAndInsertion()
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        for (var index = 0; index < 16; index++)
            bindings.Set(new LogicalDescriptor(10 + index), new FileDescriptor(100 + index));
        var original = bindings.Capture();
        for (var index = 0; index < 1024; index++)
        {
            var descriptor = new LogicalDescriptor(10 + index % 16);
            Assert.True(bindings.Remove(descriptor));
            _ = bindings.Capture();
            bindings.Set(descriptor, new FileDescriptor(1000 + index));
            // The estimate follows live slot backing, not the number of updates.
            Assert.True(bindings.AdditionalMemoryBytes <= DescriptorMap.EstimateMemoryBytes(33));
        }
        for (var index = 0; index < 16; index++)
            Assert.Equal(new FileDescriptor(100 + index), original.Resolve(new LogicalDescriptor(10 + index)));
    }

    [Fact]
    public async Task CapturedBindingsAllowConcurrentIndependentReadersAndWriters()
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        for (var index = 0; index < 128; index++)
            bindings.Set(new LogicalDescriptor(10 + index), new FileDescriptor(1000 + index));
        var snapshot = bindings.Capture();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var local = snapshot.Fork();
            for (var index = 0; index < 128; index++)
            {
                var number = new LogicalDescriptor(10 + index);
                Assert.Equal(new FileDescriptor(1000 + index), snapshot.Resolve(number));
                local.Set(number, new FileDescriptor(10000 + worker * 128 + index));
                Assert.Equal(new FileDescriptor(10000 + worker * 128 + index), local.Resolve(number));
                Assert.True(local.References(new FileDescriptor(10000 + worker * 128 + index)));
                Assert.False(local.References(new FileDescriptor(1000 + index)));
                Assert.True(snapshot.Fork().References(new FileDescriptor(1000 + index)));
            }
        })));
        for (var index = 0; index < 128; index++)
            Assert.Equal(new FileDescriptor(1000 + index), snapshot.Resolve(new LogicalDescriptor(10 + index)));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(97)]
    public void MixedMutationsPreserveDictionaryEncounterOrderAcrossCaptureBoundaries(int seed)
    {
        var random = new Random(seed);
        var actual = DescriptorBindings.Snapshot.Standard.Fork();
        var expected = new Dictionary<LogicalDescriptor, FileDescriptor>();
        var captured = false;
        for (var batch = 0; batch < 128; batch++)
        {
            for (var operation = 0; operation < 8; operation++)
            {
                var descriptor = new LogicalDescriptor(10 + random.Next(16));
                if (random.Next(3) == 0)
                {
                    var present = expected.ContainsKey(descriptor);
                    if (present && captured) Detach();
                    Assert.Equal(expected.Remove(descriptor), actual.Remove(descriptor));
                }
                else
                {
                    var handle = random.Next(5) == 0 ? FileDescriptor.Closed : new FileDescriptor(100 + random.Next(32));
                    var changed = !expected.TryGetValue(descriptor, out var old) || old != handle;
                    if (changed && captured) Detach();
                    expected[descriptor] = handle;
                    Assert.Equal(changed, actual.Set(descriptor, handle));
                }
            }
            var snapshot = actual.Capture();
            captured = true;
            var handles = new List<FileDescriptor>();
            snapshot.AddReferencedHandlesTo(handles);
            Assert.Equal(new[] { FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr }
                .Concat(expected.Values).Distinct(), handles);
            for (var index = 0; index < 16; index++)
            {
                var descriptor = new LogicalDescriptor(10 + index);
                Assert.Equal(expected.ContainsKey(descriptor), snapshot.HasMapping(descriptor));
                Assert.Equal(expected.GetValueOrDefault(descriptor, FileDescriptor.Closed), snapshot.Resolve(descriptor));
            }
            var capturedBindings = snapshot.Fork();
            foreach (var handle in Enumerable.Range(100, 33).Select(id => new FileDescriptor(id)).Append(FileDescriptor.Closed))
            {
                var present = expected.Values.Contains(handle);
                Assert.Equal(present, actual.References(handle));
                Assert.Equal(present, capturedBindings.References(handle));
            }
        }

        void Detach()
        {
            // An independent Dictionary model pins the previous observable slot
            // order, including free-slot reuse and compaction after a capture.
            var copy = new Dictionary<LogicalDescriptor, FileDescriptor>();
            foreach (var entry in expected) copy.Add(entry.Key, entry.Value);
            expected = copy;
            captured = false;
        }
    }
}

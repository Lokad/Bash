using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class IndexedArrayRangeTests
{
    [Fact]
    public void CanceledDisjointBatchStopsBeforeGrowingTheIndex()
    {
        const int count = 1024;
        var array = IndexedArray.FromSequential(new ShellValue[count]);
        var budget = new MemoryGovernor(0);
        var memory = new EvaluationMemory(budget);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            array.SetSequential(4096, new ShellValue[count], memory, cancellation.Token));

        Assert.Equal(count, array.Count);
        Assert.Equal(count - 1L, array.MaxIndex);
        Assert.Equal(0, budget.PeakBytes);
        memory.Finish(retainVariables: false);
    }

    [Theory]
    [InlineData(1U, 2L, 3L, 5000L, long.MaxValue, 6)]
    [InlineData(3U, 4L, 5L, 6L, 7L, 5)]
    [InlineData(uint.MaxValue - 1, (long)uint.MaxValue, 0L, 5L, long.MaxValue, 6)]
    public void OverlappingSequentialBatchUsesExistingTableCapacity(
        uint origin, long first, long second, long third, long fourth, int expectedCount)
    {
        var array = new IndexedArray(isInitialized: true, capacity: 4);
        var saved = ShellValue.FromText("saved");
        var priorPositions = new[] { first, second, third, fourth };
        foreach (var position in priorPositions)
        {
            Assert.True(IndexedArrayIndex.TryCreate(position, out var index));
            array.Set(index, position == first ? null : saved);
        }
        ShellValue[] values = [ShellValue.FromText("a"), ShellValue.FromText("b"), ShellValue.Empty, ShellValue.FromBytes(new byte[] { 0xff })];
        var budget = new MemoryGovernor(0);
        var memory = new EvaluationMemory(budget);

        array.SetSequential(origin, values, memory, CancellationToken.None);

        Assert.Equal(expectedCount, array.Count);
        var written = new HashSet<long>();
        for (var i = 0; i < values.Length; i++)
        {
            var position = unchecked(origin + (uint)i);
            written.Add(position);
            Assert.True(array.TryGet(IndexedArrayIndex.FromUnsigned(position), out var result));
            Assert.Equal(values[i], result);
        }
        foreach (var position in priorPositions)
        {
            if (written.Contains(position))
                continue;
            Assert.True(IndexedArrayIndex.TryCreate(position, out var index));
            Assert.True(array.TryGet(index, out var result));
            Assert.Equal(saved, result);
        }
        Assert.Equal(0, budget.PeakBytes);
        memory.Finish(retainVariables: false);
    }

    [Fact]
    public void WrappedBatchChargesOnlyItsNewPositions()
    {
        var array = new IndexedArray(isInitialized: true, capacity: 7);
        array.Set(IndexedArrayIndex.FromUnsigned(uint.MaxValue), null);
        array.Set(IndexedArrayIndex.FromUnsigned(1), ShellValue.FromText("old"));
        ShellValue[] values = [ShellValue.FromText("a"), ShellValue.FromText("b"),
            ShellValue.FromText("c"), ShellValue.FromText("d")];
        var required = 2 * IndexedArray.ElementMemoryBytes;
        var budget = new MemoryGovernor(required);
        using var scope = budget.CreateScope();
        var memory = new EvaluationMemory(scope);

        array.SetSequential(uint.MaxValue - 1, values, memory, CancellationToken.None, prepaidEntryBytes: 0);

        Assert.Equal(required, memory.Workspace.Bytes);
        Assert.Equal(4, array.Count);
        Assert.Equal(new[] { values[2], values[3], values[0], values[1] }, array.PresentValues);
    }

    [Fact]
    public void SequentialBatchRefusalLeavesOverlappingEntriesUnchanged()
    {
        var array = new IndexedArray(isInitialized: true, capacity: 7);
        var value = ShellValue.FromText("original");
        for (uint index = 1; index <= 13; index += 2)
            array.Set(IndexedArrayIndex.FromUnsigned(index), value);
        var budget = new MemoryGovernor(0);
        var memory = new EvaluationMemory(budget);
        ShellValue[] values = [ShellValue.FromText("replacement"), ShellValue.FromText("new")];

        Assert.Throws<MemoryBudgetExceededException>(() => array.SetSequential(1, values, memory, CancellationToken.None));

        Assert.Equal(7, array.Count);
        Assert.Equal(13, array.MaxIndex);
        Assert.Equal(Enumerable.Repeat(value, 7), array.PresentValues);
        Assert.False(array.TryGet(IndexedArrayIndex.FromUnsigned(2), out _));
        Assert.Equal(0, budget.ReservedBytes);
        memory.Finish(retainVariables: false);
    }

    [Fact]
    public void ClonesKeepTheirOwnCapacityEstimate()
    {
        var original = IndexedArray.FromSequential(new ShellValue[4]);
        var originalBytes = original.EstimatedMemoryBytes;
        var clone = original.Clone();
        Assert.Equal(originalBytes, clone.EstimatedMemoryBytes);
        Assert.True(IndexedArrayIndex.TryCreate(4, out var next));
        clone.Remove(default);
        Assert.Equal(originalBytes, clone.EstimatedMemoryBytes);
        clone.Set(next, ShellValue.Empty);
        Assert.Equal(4, clone.Count);
        Assert.Equal(originalBytes, clone.EstimatedMemoryBytes);
        Assert.True(original.TryGet(default, out _));
    }

    [Fact]
    public void RemovalPreservesSparseOrderMaximumAndCloneIndependence()
    {
        var array = new IndexedArray(isInitialized: false);
        Assert.True(IndexedArrayIndex.TryCreate(5, out var five));
        Assert.True(IndexedArrayIndex.TryCreate(long.MaxValue, out var maximum));
        array.Set(five, ShellValue.FromText("before"));
        array.Set(default, ShellValue.FromText("zero"));
        array.Set(maximum, ShellValue.FromText("last"));
        array.Set(five, ShellValue.FromText("five"));
        var clone = array.Clone();

        clone.Remove(maximum);
        clone.Remove(maximum);
        Assert.Equal(5, clone.MaxIndex);
        Assert.Equal(new[] { ShellValue.FromText("0"), ShellValue.FromText("5") }, clone.PresentKeys);
        Assert.Equal(new[] { ShellValue.FromText("zero"), ShellValue.FromText("five") }, clone.PresentValues);
        clone.Remove(default);
        Assert.Equal(5, clone.MaxIndex);
        clone.Remove(five);
        Assert.Null(clone.MaxIndex);
        Assert.Equal(0, clone.Count);
        Assert.True(clone.IsInitialized);
        clone.Set(default, ShellValue.FromText("again"));
        Assert.Equal(0, clone.MaxIndex);
        Assert.Single(clone.PresentValues);
        Assert.Equal(long.MaxValue, array.MaxIndex);
        Assert.Equal(3, array.Count);
        ShellValue[] expectedKeys = [ShellValue.FromText("0"), ShellValue.FromText("5"), ShellValue.FromText("9223372036854775807")];
        Assert.Equal(expectedKeys, array.PresentKeys);
        Assert.Equal(expectedKeys, array.Entries.Select(entry => entry.Key));
    }

    [Fact]
    public void RemovingHighestIndicesUpdatesMaximumAndCountUntilEmpty()
    {
        var array = new IndexedArray(isInitialized: false);
        var indices = new IndexedArrayIndex[4096];
        for (var index = 0; index < indices.Length; index++)
        {
            Assert.True(IndexedArrayIndex.TryCreate(index, out indices[index]));
            array.Set(indices[index], ShellValue.Empty);
        }
        for (var index = indices.Length - 1; index >= 0; index--)
        {
            Assert.Equal((long)index, array.MaxIndex);
            array.Remove(indices[index]);
            Assert.Equal(index, array.Count);
        }

        Assert.Null(array.MaxIndex);
        Assert.Equal(0, array.Count);
    }

    [Theory]
    [InlineData(0, 3, "7A65726F,,FF")]
    [InlineData(1, 1, "")]
    [InlineData(2, 2, ",FF")]
    [InlineData(3, 2, "FF")]
    [InlineData(long.MaxValue, 1, "FF")]
    [InlineData(0, 0, null)]
    [InlineData(3, 0, null)]
    public void ValuesFrom_CountsPresentValuesWithoutMaterializingHoles(long first, int count, string? expected)
    {
        var array = new IndexedArray(isInitialized: false);
        Assert.True(IndexedArrayIndex.TryCreate(2, out var two));
        Assert.True(IndexedArrayIndex.TryCreate(long.MaxValue, out var maximum));
        array.Set(default, ShellValue.FromText("zero"));
        array.Set(two, ShellValue.Empty);
        array.Set(maximum, ShellValue.FromBytes(new byte[] { 0xff }));
        Assert.True(IndexedArrayIndex.TryCreate(first, out var start));

        var actual = array.ValuesFrom(start, count).ToArray();

        var expectedValues = expected == null ? [] : expected.Split(',').Select(value =>
            ShellValue.FromBytes(Convert.FromHexString(value))).ToArray();
        Assert.Equal(expectedValues, actual);
        Assert.Equal(3, array.Count);
        Assert.Equal(long.MaxValue, array.MaxIndex);
        Assert.Equal(array.ValuesFrom(default, array.Count), array.PresentValues);
    }

    [Fact]
    public void ValuesFrom_DefersReadingUntilEnumeration()
    {
        var array = new IndexedArray(isInitialized: false);
        Assert.True(IndexedArrayIndex.TryCreate(5, out var five));
        Assert.True(IndexedArrayIndex.TryCreate(2, out var two));
        array.Set(five, ShellValue.FromText("five"));
        var selected = array.ValuesFrom(two, 2);
        array.Set(two, ShellValue.FromText("two"));

        Assert.Equal(
            new[] { ShellValue.FromText("two"), ShellValue.FromText("five") },
            selected);
        Assert.Empty(new IndexedArray(isInitialized: false).ValuesFrom(default, 2));
    }

    [Fact]
    public void ValuesFrom_SingletonIncludesAnUnassignedPositionUntilItIsRemoved()
    {
        var array = new IndexedArray(isInitialized: false);
        Assert.True(IndexedArrayIndex.TryCreate(2, out var two));
        Assert.True(IndexedArrayIndex.TryCreate(8, out var eight));
        var last = ShellValue.FromText("last");
        array.Set(eight, last);

        var selected = array.ValuesFrom(two, 1);
        array.Set(two, null);
        Assert.Equal(ShellValue.Empty, Assert.Single(selected));

        array.Remove(two);
        Assert.Equal(last, Assert.Single(selected));
    }

    [Theory]
    [InlineData(0, -1, "")]
    [InlineData(0, 0, "")]
    [InlineData(4, 1, "five")]
    [InlineData(5, int.MaxValue, "five,twelve")]
    [InlineData(6, int.MaxValue, "twelve")]
    [InlineData(13, 1, "")]
    [InlineData(long.MaxValue, int.MaxValue, "")]
    public void ValuesFrom_HandlesEmptyAndOpenEndedWindows(long first, int count, string expected)
    {
        var array = new IndexedArray(isInitialized: true);
        Assert.True(IndexedArrayIndex.TryCreate(5, out var five));
        Assert.True(IndexedArrayIndex.TryCreate(12, out var twelve));
        Assert.True(IndexedArrayIndex.TryCreate(first, out var start));
        array.Set(default, ShellValue.FromText("zero"));
        array.Set(five, ShellValue.FromText("five"));
        array.Set(twelve, ShellValue.FromText("twelve"));

        Assert.Equal(expected, string.Join(",", array.ValuesFrom(start, count).Select(value => value.ToUtf8String())));
    }

    [Fact]
    public void ValuesFrom_LateSmallWindowsPreserveSparseOrderAfterRemovals()
    {
        var array = new IndexedArray(isInitialized: false);
        const int entries = 8192;
        for (var index = 0; index < entries; index++)
        {
            Assert.True(IndexedArrayIndex.TryCreate(1_000_000L * index, out var position));
            array.Set(position, ShellValue.FromText(index.ToString()));
        }
        for (var index = entries - 1; index >= entries - 128; index--)
        {
            Assert.True(IndexedArrayIndex.TryCreate(1_000_000L * index - 1, out var start));
            Assert.True(IndexedArrayIndex.TryCreate(1_000_000L * index, out var position));
            Assert.Equal(index.ToString(), Assert.Single(array.ValuesFrom(start, 1)).ToUtf8String());
            array.Remove(position);
            Assert.Empty(array.ValuesFrom(start, 1));
        }
        Assert.Equal(entries - 128, array.Count);
    }
}

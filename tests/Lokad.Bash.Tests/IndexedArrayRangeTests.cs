using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class IndexedArrayRangeTests
{
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
        Assert.Equal(new[] { "0", "5" }, clone.PresentKeys);
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

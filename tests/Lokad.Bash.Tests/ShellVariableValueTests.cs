using System;
using System.Globalization;
using System.Linq;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

[Collection(CollectionName)]
public sealed class ShellVariableValueTests
{
    private const string CollectionName = "Shell variable value regressions";

    [Fact]
    public void IndexedArrayIndex_ValidatesNonNegativeDecimalPositions()
    {
        Assert.False(IndexedArrayIndex.TryCreate(-1, out _));
        Assert.True(IndexedArrayIndex.TryParse(ShellValue.FromText(long.MaxValue.ToString(CultureInfo.InvariantCulture)).Bytes.Span, out var maximum));
        Assert.Equal(long.MaxValue, maximum.Value);
        Assert.False(IndexedArrayIndex.TryParse("+1"u8, out _));
        Assert.False(IndexedArrayIndex.TryParse(" 1"u8, out _));
    }

    [Fact]
    public void AssociativeArrayKey_RejectsEmptyKeys()
    {
        Assert.False(AssociativeArrayKey.TryCreate(ShellValue.Empty, out _));
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("key"), out var key));
        Assert.Equal(ShellValue.FromText("key"), key.Value);
    }

    [Fact]
    public void CompatibilityViews_AreDenseAndOrdinal()
    {
        Assert.True(IndexedArrayIndex.TryCreate(10, out var ten));
        Assert.True(IndexedArrayIndex.TryCreate(2, out var two));
        var indexed = new IndexedArray(isInitialized: false);
        indexed.Set(ten, ShellValue.FromText("ten"));
        indexed.Set(two, ShellValue.FromText("two"));

        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("z"), out var z));
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("a"), out var a));
        var associative = new AssociativeArray(isInitialized: false);
        associative.Set(z, ShellValue.FromText("last"));
        associative.Set(a, ShellValue.FromText("first"));

        var dense = indexed.CaptureDenseValues();
        var map = associative.CaptureValues();
        Assert.Equal(11, dense.Count);
        Assert.Equal(ShellValue.FromText("two"), dense[2]);
        Assert.Equal(ShellValue.FromText("ten"), dense[10]);
        Assert.Equal([ShellValue.FromText("a"), ShellValue.FromText("z")], map.Keys);
        Assert.Equal([ShellValue.FromText("first"), ShellValue.FromText("last")], map.Values);
    }

    [Fact]
    public void Clone_PreservesSparseArrayOwnership()
    {
        Assert.True(IndexedArrayIndex.TryCreate(2, out var index));
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("key"), out var key));
        var indexed = new IndexedArray(isInitialized: false);
        var associative = new AssociativeArray(isInitialized: false);
        indexed.Set(index, ShellValue.FromText("before"));
        associative.Set(key, ShellValue.FromText("before"));

        var indexedClone = indexed.Clone();
        var associativeClone = associative.Clone();
        indexedClone.Set(index, ShellValue.FromText("after"));
        associativeClone.Set(key, ShellValue.FromText("after"));

        Assert.True(indexed.TryGet(index, out var indexedValue));
        Assert.True(associative.TryGet(key, out var associativeValue));
        Assert.Equal(ShellValue.FromText("before"), indexedValue);
        Assert.Equal(ShellValue.FromText("before"), associativeValue);
    }

    [Fact]
    public void SparseMaximumIndexCloneDoesNotMaterializeHoles()
    {
        Assert.True(IndexedArrayIndex.TryCreate(long.MaxValue, out var index));
        var array = new IndexedArray(isInitialized: false);
        array.Set(index, ShellValue.FromText("edge"));

        _ = array.Clone();
        var allocated = MeasureAllocation(() =>
        {
            var clone = array.Clone();
            Assert.Equal(long.MaxValue, clone.MaxIndex);
            Assert.True(IndexedArrayIndex.TryCreate(0, out var zero));
            Assert.False(clone.TryGet(zero, out _));
            Assert.True(clone.TryGet(index, out var value));
            Assert.Equal(ShellValue.FromText("edge"), value);
        });

        Assert.True(
            allocated < 256 * 1024,
            $"Cloning one high-index element allocated {allocated:N0} bytes; holes must remain absent.");
    }

    [Fact]
    public void SequentialIndexedArrayUsesLinearStorage()
    {
        const int Count = 20_000;
        var values = Enumerable.Repeat(ShellValue.FromText("value"), Count).ToArray();

        _ = IndexedArray.FromSequential(values);
        var allocated = MeasureAllocation(() =>
        {
            var array = IndexedArray.FromSequential(values);
            Assert.Equal(Count, array.CaptureDenseValues().Count);
        });

        Assert.True(
            allocated < 8 * 1024 * 1024,
            $"Storing {Count:N0} sequential elements allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Variables_HighIndexedElementUsesSparseStorage()
    {
        var variables = Variables.CreateEmpty();
        var indexText = long.MaxValue.ToString(CultureInfo.InvariantCulture);
        _ = variables.TrySetArrayElement("warmup", ShellValue.FromText("1"), ShellValue.FromText("value"), ArrayKind.Indexed);

        var allocated = MeasureAllocation(() =>
        {
            Assert.True(variables.TrySetArrayElement("sparse", ShellValue.FromText(indexText), ShellValue.FromText("edge"), ArrayKind.Indexed));
            var clone = variables.Clone();
            var array = clone.IndexedArrays["sparse"];
            Assert.Single(array.PresentValues);
            Assert.Equal(long.MaxValue, array.MaxIndex);
            Assert.True(IndexedArrayIndex.TryParse(ShellValue.FromText(indexText).Bytes.Span, out var index));
            Assert.True(IndexedArrayIndex.TryCreate(0, out var zero));
            Assert.False(array.TryGet(zero, out _));
            Assert.True(array.TryGet(index, out var value));
            Assert.Equal(ShellValue.FromText("edge"), value);
        });

        Assert.True(
            allocated < 256 * 1024,
            $"Setting and cloning one high-index variable allocated {allocated:N0} bytes.");
    }

    private static long MeasureAllocation(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

[CollectionDefinition("Shell variable value regressions", DisableParallelization = true)]
public sealed class ShellVariableValueTestCollection;

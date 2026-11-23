using System;
using System.Globalization;
using System.Linq;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

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
    public void IndexedNullEntriesPreservePresenceAcrossCloningAndReplacement()
    {
        Assert.True(IndexedArrayIndex.TryCreate(7, out var seven));
        Assert.True(IndexedArrayIndex.TryCreate(12, out var twelve));
        var array = new IndexedArray(isInitialized: false);
        array.Set(seven, null);
        array.Set(twelve, ShellValue.FromText("kept"));
        var clone = array.Clone();

        Assert.Equal(2, clone.Count);
        Assert.Equal(12, clone.MaxIndex);
        Assert.Equal(new[] { ShellValue.FromText("7"), ShellValue.FromText("12") }, clone.PresentKeys);
        Assert.Equal(new ShellValue?[] { null, ShellValue.FromText("kept") }, clone.Entries.Select(entry => entry.Value));
        Assert.False(clone.TryGet(seven, out _));
        Assert.Equal(ShellValue.Empty, clone.CaptureDenseValues()[7]);
        var retained = clone.EstimatedMemoryBytes;
        clone.Set(seven, ShellValue.Empty);
        Assert.True(clone.TryGet(seven, out var assigned));
        Assert.Equal(ShellValue.Empty, assigned);
        Assert.Equal(retained, clone.EstimatedMemoryBytes);
        Assert.False(array.TryGet(seven, out _));
        clone.Remove(twelve);
        Assert.Equal(7, clone.MaxIndex);
        clone.Remove(seven);
        Assert.Empty(clone.PresentKeys);
        Assert.Null(clone.MaxIndex);
        Assert.Equal(2, array.Count);
    }

    [Fact]
    public void AssociativeNullEntriesPreservePresenceAcrossCloningAndReplacement()
    {
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("key"), out var key));
        var array = new AssociativeArray(isInitialized: false);
        array.Set(key, null);
        var clone = array.Clone();

        Assert.Equal(1, clone.Count);
        Assert.Equal(new[] { key.Value }, clone.PresentKeys);
        Assert.Null(Assert.Single(clone.Entries).Value);
        Assert.False(clone.TryGet(key, out _));
        Assert.Equal(ShellValue.Empty, clone.CaptureValues()[key.Value]);
        var retained = clone.EstimatedMemoryBytes;
        clone.Set(key, ShellValue.Empty);
        Assert.True(clone.TryGet(key, out var assigned));
        Assert.Equal(ShellValue.Empty, assigned);
        Assert.Equal(retained, clone.EstimatedMemoryBytes);
        Assert.False(array.TryGet(key, out _));
        clone.Remove(key);
        Assert.Equal(0, clone.Count);
        Assert.Empty(clone.Entries);
        Assert.Equal(1, array.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssociativeRangesFollowByteOrderThroughUpdatesAndRemovals(bool reverse)
    {
        var array = new AssociativeArray(isInitialized: false);
        var expected = new SortedDictionary<ShellValue, ShellValue?>(ShellValue.ByteOrder);
        var keys = Enumerable.Range(1, 192).Select(index =>
        {
            Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromBytes(new[] { (byte)index }), out var key));
            return key;
        }).ToArray();
        if (reverse)
            Array.Reverse(keys);

        foreach (var key in keys)
            Set(key, key.Value);
        var clone = array.Clone();
        var cloneEntries = expected.ToArray();
        var random = new Random(112);
        for (var step = 0; step < 1200; step++)
        {
            var key = keys[random.Next(keys.Length)];
            if (step % 4 == 0)
                Set(key, null);
            else if (step % 4 == 1)
                Set(key, ShellValue.Empty);
            else if (step % 4 == 2)
                Set(key, ShellValue.FromText("replacement"));
            else
            {
                array.Remove(key);
                expected.Remove(key.Value);
            }

            var first = random.Next(expected.Count + 1);
            var count = random.Next(6);
            Assert.Equal(expected.Values.Skip(first).Take(count).Select(value => value.GetValueOrDefault()),
                array.ValuesFrom(first, count));
            Assert.Equal(expected.Count, array.Count);
            var assigned = expected.TryGetValue(key.Value, out var stored) && stored.HasValue;
            Assert.Equal(assigned, array.TryGet(key, out var actual));
            Assert.Equal(stored.GetValueOrDefault(), actual);
        }

        Assert.Equal(expected.ToArray(), array.Entries);
        Assert.Equal(cloneEntries, clone.Entries);
        foreach (var key in keys)
            array.Remove(key);
        Assert.True(array.IsInitialized);
        Assert.Equal(0, array.EstimatedMemoryBytes);
        Assert.Empty(array.ValuesFrom(0, int.MaxValue));

        void Set(AssociativeArrayKey key, ShellValue? value)
        {
            array.Set(key, value);
            expected[key.Value] = value;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void SingleEntryAssociativeRangeSeeksWhenEnumerated(int count)
    {
        var array = new AssociativeArray(isInitialized: false);
        foreach (var text in new[] { "c", "a", "d", "b" })
        {
            var value = ShellValue.FromText(text);
            Assert.True(AssociativeArrayKey.TryCreate(value, out var key));
            array.Set(key, value);
        }
        var range = array.ValuesFrom(2, count);
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("a"), out var removed));
        array.Remove(removed);

        Assert.Equal(ShellValue.FromText("d"), Assert.Single(range));
        Assert.Empty(array.ValuesFrom(3, count));
    }

    [Fact]
    public void AssociativeRefusedInsertionAndReplacementKeepExistingEntries()
    {
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("a"), out var a));
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("b"), out var b));
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("c"), out var c));
        var array = new AssociativeArray(isInitialized: false);
        array.Set(a, a.Value);
        array.Set(c, c.Value);
        var before = array.EstimatedMemoryBytes;
        var budget = new MemoryGovernor(0);
        using var owner = budget.CreateScope();
        var memory = new EvaluationMemory(owner);

        Assert.Throws<MemoryBudgetExceededException>(() => array.Set(b, b.Value, memory));
        Assert.Throws<MemoryBudgetExceededException>(() => array.Set(a, ShellValue.FromText("longer"), memory));

        Assert.Equal(new[] { a.Value, c.Value }, array.ValuesFrom(0, 2));
        Assert.Equal(before, array.EstimatedMemoryBytes);
        Assert.Equal(0, budget.ReservedBytes);
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

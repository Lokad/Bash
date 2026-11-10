using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>A validated, immutable, non-negative indexed-array position.</summary>
internal readonly record struct IndexedArrayIndex
{
    private IndexedArrayIndex(long value)
    {
        Value = value;
    }

    /// <summary>Gets the non-negative numeric position.</summary>
    public long Value { get; }

    /// <summary>Creates an index when <paramref name="value"/> is non-negative.</summary>
    public static bool TryCreate(long value, out IndexedArrayIndex index)
    {
        if (value < 0)
        {
            index = default;
            return false;
        }

        index = new IndexedArrayIndex(value);
        return true;
    }

    /// <summary>Parses a non-negative decimal indexed-array position.</summary>
    public static bool TryParse(ReadOnlySpan<byte> text, out IndexedArrayIndex index)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            index = default;
            return false;
        }

        return TryCreate(value, out index);
    }
}

/// <summary>A validated, immutable, non-empty associative-array key.</summary>
internal sealed record AssociativeArrayKey
{
    private AssociativeArrayKey(ShellValue value)
    {
        Value = value;
    }

    /// <summary>Gets the non-empty key bytes, which need not form valid UTF-8.</summary>
    public ShellValue Value { get; }

    /// <summary>Creates a key unless <paramref name="value"/> is empty.</summary>
    public static bool TryCreate(
        ShellValue value,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AssociativeArrayKey? key)
    {
        if (value.IsEmpty)
        {
            key = null;
            return false;
        }

        key = new AssociativeArrayKey(value);
        return true;
    }
}

/// <summary>Stores only present indexed-array positions in ascending order.</summary>
internal sealed class IndexedArray
{
    // Dictionary capacity and resize overlap, sorted-set nodes, and collection headers.
    internal const int ElementMemoryBytes = 192;
    private readonly Dictionary<long, ShellValue> _elements;
    // SortedDictionary cannot find the last key without enumeration. Keep an ordered
    // position index for sparse traversal and logarithmic maximum removal.
    private readonly SortedSet<long> _positions;
    private long? _maximumIndex;
    private long _valueBytes;
    private int _entryCapacity;

    // mapfile entries borrow batch slices; removing entries need not release their backing arrays.
    internal long RetainedBackingBytes { get; set; }
    internal long EstimatedMemoryBytes => (long)_entryCapacity * ElementMemoryBytes + _valueBytes + RetainedBackingBytes;

    /// <summary>Creates an empty array, retaining whether it has received an assignment.</summary>
    public IndexedArray(bool isInitialized)
    {
        _elements = [];
        _positions = [];
        IsInitialized = isInitialized;
    }

    private IndexedArray(
        Dictionary<long, ShellValue> elements,
        SortedSet<long> positions,
        long? maximumIndex,
        bool isInitialized)
    {
        _elements = elements;
        _positions = positions;
        _maximumIndex = maximumIndex;
        IsInitialized = isInitialized;
    }

    /// <summary>Distinguishes a declaration-only array from an assigned array, including an empty one.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Records an assignment even when a compound append has no elements.</summary>
    public void MarkInitialized() => IsInitialized = true;

    /// <summary>Gets the greatest present position in constant time.</summary>
    public long? MaxIndex => _maximumIndex;

    /// <summary>Enumerates present values by index without copying; consume before mutating.</summary>
    public IEnumerable<ShellValue> PresentValues => _positions.Select(position => _elements[position]);

    /// <summary>Gets the number of present entries, excluding holes.</summary>
    public int Count => _elements.Count;

    /// <summary>Enumerates at most count present entries starting at an index, without materializing holes.</summary>
    public IEnumerable<ShellValue> ValuesFrom(IndexedArrayIndex first, int count)
    {
        foreach (var position in _positions)
        {
            if (count <= 0)
                yield break;
            if (position < first.Value)
                continue;
            yield return _elements[position];
            count--;
        }
    }
    /// <summary>Enumerates index text for shell metadata expansion without a dense view.</summary>
    public IEnumerable<string> PresentKeys => _positions.Select(Integers.FormatNumber);

    /// <summary>Gets one present position without allocating.</summary>
    public bool TryGet(
        IndexedArrayIndex index,
        out ShellValue value) =>
        _elements.TryGetValue(index.Value, out value);

    /// <summary>Adds or replaces one validated position.</summary>
    public void Set(IndexedArrayIndex index, ShellValue value)
    {
        _elements.TryGetValue(index.Value, out var previous);
        var valueBytes = checked(_valueBytes - Variables.EstimateValueBytes(previous) + Variables.EstimateValueBytes(value));
        if (_elements.TryAdd(index.Value, value))
            _positions.Add(index.Value);
        else
            _elements[index.Value] = value;
        _valueBytes = valueBytes;
        _entryCapacity = Math.Max(_entryCapacity, _elements.Count);
        MarkInitialized();
        if (!_maximumIndex.HasValue || index.Value > _maximumIndex.Value)
            _maximumIndex = index.Value;
    }

    /// <summary>Removes one position, updating the maximum only when its entry was removed.</summary>
    public void Remove(IndexedArrayIndex index)
    {
        if (!_elements.Remove(index.Value, out var previous))
            return;
        _valueBytes -= Variables.EstimateValueBytes(previous);
        _positions.Remove(index.Value);
        if (_maximumIndex == index.Value)
            _maximumIndex = _positions.Count == 0 ? null : _positions.Max;
    }

    /// <summary>Creates an owned array at contiguous positions starting from zero.</summary>
    public static IndexedArray FromSequential(IReadOnlyList<ShellValue> values)
    {
        var array = new IndexedArray(isInitialized: true);
        for (var index = 0; index < values.Count; index++)
        {
            if (!IndexedArrayIndex.TryCreate(index, out var validated))
                throw new InvalidOperationException("A sequential array position cannot be negative.");

            array.Set(validated, values[index]);
        }

        return array;
    }

    /// <summary>
    /// Captures the legacy public inspection view in which holes are empty values.
    /// </summary>
    /// <remarks>
    /// Allocation grows with the maximum index, not the number of present elements.
    /// Use sparse lookup or cloning when a dense compatibility view is not required.
    /// </remarks>
    public List<ShellValue> CaptureDenseValues()
    {
        if (MaxIndex is not { } maximum)
            return [];
        if (maximum >= int.MaxValue)
        {
            throw new InvalidOperationException(
                "A sparse indexed array is too large for the legacy dense view.");
        }

        var count = checked((int)maximum + 1);
        var values = new List<ShellValue>(count);
        for (var index = 0; index < count; index++)
            values.Add(ShellValue.Empty);

        foreach (var entry in _elements)
            values[checked((int)entry.Key)] = entry.Value;

        return values;
    }

    /// <summary>Creates an independent sparse array with the same present positions.</summary>
    public IndexedArray Clone()
    {
        return new IndexedArray(new Dictionary<long, ShellValue>(_elements),
            new SortedSet<long>(_positions), _maximumIndex, IsInitialized)
        {
            _valueBytes = _valueBytes,
            _entryCapacity = _elements.Count,
            RetainedBackingBytes = RetainedBackingBytes
        };
    }
}

/// <summary>Stores associative-array entries in deterministic byte order, not Bash hash-table order.</summary>
internal sealed class AssociativeArray
{
    private readonly SortedDictionary<ShellValue, ShellValue> _elements;
    private long _valueBytes;
    internal long EstimatedMemoryBytes => (long)_elements.Count * IndexedArray.ElementMemoryBytes + _valueBytes;

    /// <summary>Creates an empty array, retaining whether it has received an assignment.</summary>
    public AssociativeArray(bool isInitialized)
    {
        _elements = new SortedDictionary<ShellValue, ShellValue>(ShellValue.ByteOrder);
        IsInitialized = isInitialized;
    }

    private AssociativeArray(SortedDictionary<ShellValue, ShellValue> elements, bool isInitialized)
    {
        _elements = elements;
        IsInitialized = isInitialized;
    }

    /// <summary>Distinguishes a declaration-only array from an assigned array, including an empty one.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Records an assignment even when a compound append has no elements.</summary>
    public void MarkInitialized() => IsInitialized = true;

    /// <summary>Gets one validated key without allocating.</summary>
    public bool TryGet(
        AssociativeArrayKey key,
        out ShellValue value) =>
        _elements.TryGetValue(key.Value, out value);

    /// <summary>Adds or replaces one validated non-empty key.</summary>
    public void Set(AssociativeArrayKey key, ShellValue value)
    {
        if (_elements.TryGetValue(key.Value, out var previous))
            _valueBytes -= Variables.EstimateValueBytes(previous);
        else
            _valueBytes = checked(_valueBytes + Variables.EstimateValueBytes(key.Value));
        _valueBytes = checked(_valueBytes + Variables.EstimateValueBytes(value));
        _elements[key.Value] = value;
        MarkInitialized();
    }

    /// <summary>Removes one key without changing the array's declaration or initialization state.</summary>
    public void Remove(AssociativeArrayKey key)
    {
        if (_elements.TryGetValue(key.Value, out var previous) && _elements.Remove(key.Value))
            _valueBytes -= Variables.EstimateValueBytes(key.Value) + Variables.EstimateValueBytes(previous);
    }

    /// <summary>Enumerates values by byte-ordered key without copying; consume before mutating.</summary>
    public IEnumerable<ShellValue> PresentValues => _elements.Values;

    /// <summary>Gets the number of present entries.</summary>
    public int Count => _elements.Count;

    /// <summary>Enumerates keys in the same deterministic order as their values.</summary>
    public IEnumerable<ShellValue> PresentKeys => _elements.Keys;

    /// <summary>Captures a detached dictionary whose enumeration follows byte key order.</summary>
    public Dictionary<ShellValue, ShellValue> CaptureValues() => new(_elements);

    /// <summary>Creates an independent associative array with the same entries.</summary>
    public AssociativeArray Clone() =>
        new(new SortedDictionary<ShellValue, ShellValue>(_elements, ShellValue.ByteOrder), IsInitialized) { _valueBytes = _valueBytes };
}

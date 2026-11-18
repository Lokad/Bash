using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
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

    internal static IndexedArrayIndex FromUnsigned(uint value) => new(value);

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
    private const long DictionarySlotBytes = 48;
    private const long PositionNodeBytes = 64;
    private readonly Dictionary<long, ShellValue?> _elements;
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
        Dictionary<long, ShellValue?> elements,
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
    public IEnumerable<ShellValue> PresentValues => _positions.Select(position => _elements[position].GetValueOrDefault());

    /// <summary>Enumerates sparse positions and values together, including unassigned positions.</summary>
    internal IEnumerable<KeyValuePair<ShellValue, ShellValue?>> Entries =>
        _positions.Select(position => KeyValuePair.Create(ShellValue.FromText(Integers.FormatNumber(position)), _elements[position]));

    /// <summary>Gets the number of present entries, excluding holes.</summary>
    public int Count => _elements.Count;

    /// <summary>Enumerates at most count present entries starting at an index, without materializing holes.</summary>
    public IEnumerable<ShellValue> ValuesFrom(IndexedArrayIndex first, int count)
    {
        if (count <= 0 || _maximumIndex is not { } maximum || first.Value > maximum)
            yield break;
        // Seek into the existing position tree. Do not ask the subset for Count:
        // that would enumerate its entire range before a small window can be read.
        foreach (var position in _positions.GetViewBetween(first.Value, maximum))
        {
            yield return _elements[position].GetValueOrDefault();
            if (--count == 0)
                yield break;
        }
    }
    /// <summary>Enumerates index text for shell metadata expansion without a dense view.</summary>
    public IEnumerable<string> PresentKeys => _positions.Select(Integers.FormatNumber);

    /// <summary>Gets one present position without allocating.</summary>
    public bool TryGet(
        IndexedArrayIndex index,
        out ShellValue value)
    {
        if (_elements.TryGetValue(index.Value, out var assigned) && assigned is { } stored)
        {
            value = stored;
            return true;
        }
        value = ShellValue.Empty;
        return false;
    }

    /// <summary>Adds or replaces one validated position.</summary>
    public void Set(IndexedArrayIndex index, ShellValue? value) => Set(index, value, memory: null);

    internal void Set(IndexedArrayIndex index, ShellValue? value, EvaluationMemory? memory)
    {
        var present = _elements.TryGetValue(index.Value, out var previous);
        var valueDelta = Variables.EstimateValueBytes(value.GetValueOrDefault())
            - Variables.EstimateValueBytes(previous.GetValueOrDefault());
        var valueBytes = checked(_valueBytes + valueDelta);
        var (replacementCapacity, overlap) = memory != null && !present
            ? EstimateDictionaryGrowth(_elements.EnsureCapacity(0), checked(_elements.Count + 1), prepaidBytes: 0)
            : (0, 0L);
        memory?.Charge(checked(Math.Max(0, valueDelta) + (present ? 0 : ElementMemoryBytes) + overlap));
        if (replacementCapacity != 0)
            _elements.EnsureCapacity(replacementCapacity);
        _elements[index.Value] = value;
        if (!present)
            _positions.Add(index.Value);
        _valueBytes = valueBytes;
        _entryCapacity = Math.Max(_entryCapacity, _elements.Count);
        MarkInitialized();
        if (!_maximumIndex.HasValue || index.Value > _maximumIndex.Value)
            _maximumIndex = index.Value;
        if (overlap != 0 && memory != null)
            memory.Workspace.Resize(memory.Workspace.Bytes - overlap);
    }

    /// <summary>Removes one position, updating the maximum only when its entry was removed.</summary>
    public void Remove(IndexedArrayIndex index)
    {
        if (!_elements.Remove(index.Value, out var previous))
            return;
        _valueBytes -= Variables.EstimateValueBytes(previous.GetValueOrDefault());
        _positions.Remove(index.Value);
        if (_maximumIndex == index.Value)
            _maximumIndex = _positions.Count == 0 ? null : _positions.Max;
    }

    /// <summary>Creates an owned array at contiguous positions starting from zero.</summary>
    public static IndexedArray FromSequential(IReadOnlyList<ShellValue> values)
    {
        var array = new IndexedArray(isInitialized: true);
        array.SetSequential(0, values, memory: null, CancellationToken.None);
        return array;
    }

    /// <summary>Publishes admitted records after checking dictionary growth for the whole batch.</summary>
    internal void SetSequential(
        uint origin, IReadOnlyList<ShellValue> values, EvaluationMemory? memory, CancellationToken cancellationToken)
    {
        if (memory != null)
        {
            var capacity = _elements.EnsureCapacity(0);
            if (capacity != 0 && values.Count > capacity - _elements.Count)
            {
                var added = 0;
                for (var index = 0; index < values.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_elements.ContainsKey(unchecked(origin + (uint)index)))
                        added++;
                }

                // Keep the new position nodes covered by the existing per-record admission.
                var prepaid = (long)values.Count * ElementMemoryBytes - added * PositionNodeBytes;
                var (replacementCapacity, overlap) = EstimateDictionaryGrowth(
                    capacity, checked(_elements.Count + added), prepaid);
                if (replacementCapacity != 0)
                {
                    memory.Charge(overlap);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _elements.EnsureCapacity(replacementCapacity);
                    }
                    finally
                    {
                        if (overlap != 0)
                            memory.Workspace.Resize(memory.Workspace.Bytes - overlap);
                    }
                }
            }
        }

        for (var index = 0; index < values.Count; index++)
            Set(IndexedArrayIndex.FromUnsigned(unchecked(origin + (uint)index)), values[index]);
        MarkInitialized();
    }

    private (int Capacity, long Overlap) EstimateDictionaryGrowth(int capacity, int required, long prepaidBytes)
    {
        if (required <= capacity)
            return (0, 0);

        var replacementCapacity = (int)Math.Min(int.MaxValue, Math.Max(required, Math.Max(3L, 2L * capacity)));
        // Reuse the part of the retained estimate above the live table and nodes.
        var liveStorage = 128 + DictionarySlotBytes * capacity + PositionNodeBytes * _elements.Count;
        var headroom = Math.Max(0, (long)_entryCapacity * ElementMemoryBytes - liveStorage);
        var replacement = 128 + DictionarySlotBytes * (2L * replacementCapacity + 1);
        return (replacementCapacity, Math.Max(0, replacement - headroom - prepaidBytes));
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
            values[checked((int)entry.Key)] = entry.Value.GetValueOrDefault();

        return values;
    }

    /// <summary>Creates an independent sparse array with the same present positions.</summary>
    public IndexedArray Clone()
    {
        return new IndexedArray(new Dictionary<long, ShellValue?>(_elements),
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
    private readonly SortedDictionary<ShellValue, ShellValue?> _elements;
    private long _valueBytes;
    internal long EstimatedMemoryBytes => (long)_elements.Count * IndexedArray.ElementMemoryBytes + _valueBytes;

    /// <summary>Creates an empty array, retaining whether it has received an assignment.</summary>
    public AssociativeArray(bool isInitialized)
    {
        _elements = new SortedDictionary<ShellValue, ShellValue?>(ShellValue.ByteOrder);
        IsInitialized = isInitialized;
    }

    private AssociativeArray(SortedDictionary<ShellValue, ShellValue?> elements, bool isInitialized)
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
        out ShellValue value)
    {
        if (_elements.TryGetValue(key.Value, out var assigned) && assigned is { } stored)
        {
            value = stored;
            return true;
        }
        value = ShellValue.Empty;
        return false;
    }

    /// <summary>Adds or replaces one validated non-empty key.</summary>
    public void Set(AssociativeArrayKey key, ShellValue? value) => Set(key, value, memory: null);

    internal void Set(AssociativeArrayKey key, ShellValue? value, EvaluationMemory? memory)
    {
        var present = _elements.TryGetValue(key.Value, out var previous);
        var keyBytes = present ? 0 : Variables.EstimateValueBytes(key.Value);
        var valueDelta = Variables.EstimateValueBytes(value.GetValueOrDefault())
            - Variables.EstimateValueBytes(previous.GetValueOrDefault());
        var valueBytes = checked(_valueBytes + keyBytes + valueDelta);
        memory?.Charge(checked(Math.Max(0, valueDelta) + (present ? 0 : IndexedArray.ElementMemoryBytes + keyBytes)));
        _elements[key.Value] = value;
        _valueBytes = valueBytes;
        MarkInitialized();
    }

    /// <summary>Removes one key without changing the array's declaration or initialization state.</summary>
    public void Remove(AssociativeArrayKey key)
    {
        if (_elements.TryGetValue(key.Value, out var previous) && _elements.Remove(key.Value))
            _valueBytes -= Variables.EstimateValueBytes(key.Value) + Variables.EstimateValueBytes(previous.GetValueOrDefault());
    }

    /// <summary>Enumerates values by byte-ordered key without copying; consume before mutating.</summary>
    public IEnumerable<ShellValue> PresentValues => _elements.Values.Select(value => value.GetValueOrDefault());

    /// <summary>Enumerates keys and values together in byte order, including unassigned keys.</summary>
    internal IEnumerable<KeyValuePair<ShellValue, ShellValue?>> Entries => _elements;

    /// <summary>Gets the number of present entries.</summary>
    public int Count => _elements.Count;

    /// <summary>Enumerates keys in the same deterministic order as their values.</summary>
    public IEnumerable<ShellValue> PresentKeys => _elements.Keys;

    /// <summary>Captures a detached dictionary whose enumeration follows byte key order.</summary>
    public Dictionary<ShellValue, ShellValue> CaptureValues() =>
        _elements.ToDictionary(entry => entry.Key, entry => entry.Value.GetValueOrDefault());

    /// <summary>Creates an independent associative array with the same entries.</summary>
    public AssociativeArray Clone() =>
        new(new SortedDictionary<ShellValue, ShellValue?>(_elements, ShellValue.ByteOrder), IsInitialized) { _valueBytes = _valueBytes };
}

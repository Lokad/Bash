using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

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
    internal static AssociativeArrayKey Zero { get; } = new(ShellValue.FromText("0"));

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
    // Recovery creates an assigned empty array with immutable contents. Each binding owns its attributes.
    internal static IndexedArray EmptyRecoveryPipelineStatus { get; } = new(isInitialized: true);
    private Dictionary<long, ShellValue?> _elements;
    // SortedDictionary cannot find the last key without enumeration. Keep an ordered
    // position index for sparse traversal and logarithmic maximum removal.
    private SortedSet<long> _positions;
    // A live directory view uses the stack's dense positions and retained value bytes.
    internal EvaluationState? DirectoryStack { get; private set; }
    private readonly Variables? _callerOwner;
    private readonly CallerArrayKind _callerKind;
    private bool _editedCallerNames;
    private long _positionOffset;
    internal bool IsCallerArray => _callerOwner != null;
    internal bool IsFunctionNames => _callerOwner != null && _callerKind == CallerArrayKind.FunctionName;
    internal bool UsesSparseCallerNames => !IsCallerArray || _editedCallerNames;
    private bool NamesAreVisible => !IsFunctionNames || _callerOwner?.CallerNamesVisible == true;
    private int? DenseCount => DirectoryStack?.DirectoryStackCount
        ?? (!NamesAreVisible ? 0 : _editedCallerNames ? null : _callerOwner?.CallerCount);
    private long? _maximumIndex;
    private long _valueBytes;
    private int _entryCapacity;

    internal long EstimatedMemoryBytes => (long)_entryCapacity * ElementMemoryBytes + _valueBytes;

    /// <summary>Creates an empty array, retaining whether it has received an assignment.</summary>
    public IndexedArray(bool isInitialized) : this(isInitialized, capacity: 0) { }

    /// <summary>Creates an empty array with capacity for already admitted entries.</summary>
    internal IndexedArray(bool isInitialized, int capacity)
    {
        _elements = new(capacity);
        _positions = [];
        IsInitialized = isInitialized;
    }

    internal IndexedArray(EvaluationState directoryStack)
    {
        DirectoryStack = directoryStack;
        // Projected operations never modify the shared empty sparse storage.
        _elements = EmptyRecoveryPipelineStatus._elements;
        _positions = EmptyRecoveryPipelineStatus._positions;
        IsInitialized = true;
    }

    internal IndexedArray(Variables callerOwner, CallerArrayKind kind)
    {
        _callerOwner = callerOwner;
        _callerKind = kind;
        _elements = EmptyRecoveryPipelineStatus._elements;
        _positions = EmptyRecoveryPipelineStatus._positions;
        IsInitialized = true;
    }

    internal IndexedArray ForCallerOwner(Variables owner)
    {
        var view = new IndexedArray(owner, _callerKind);
        if (_editedCallerNames)
        {
            view._elements = new Dictionary<long, ShellValue?>(_elements);
            view._positions = new SortedSet<long>(_positions);
            view._maximumIndex = _maximumIndex;
            view._positionOffset = _positionOffset;
            view._valueBytes = _valueBytes;
            view._entryCapacity = _elements.Count;
            view._editedCallerNames = true;
        }
        return view;
    }

    internal void RebindDirectoryStack(EvaluationState directoryStack) => DirectoryStack = directoryStack;

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
    private bool _initialized;
    public bool IsInitialized
    {
        get => IsFunctionNames ? NamesAreVisible && CallerNameCount != 0 : _initialized;
        private set => _initialized = value;
    }
    internal bool IsRecoveryPipelineStatus => ReferenceEquals(this, EmptyRecoveryPipelineStatus);
    internal int CallerNameCount => IsFunctionNames && !_editedCallerNames ? _callerOwner?.CallerCount ?? 0 : _elements.Count;

    internal bool TryReadCallerName(IndexedArrayIndex index, out ShellValue value)
    {
        if (!IsFunctionNames)
            return TryGet(index, out value);
        if (!_editedCallerNames && index.Value < CallerNameCount)
        {
            value = ReadDense((int)index.Value);
            return true;
        }
        return TryGetSparse(index.Value, out value);
    }

    /// <summary>Records an assignment even when a compound append has no elements.</summary>
    public void MarkInitialized()
    {
        if (!IsInitialized)
            IsInitialized = true;
    }

    /// <summary>Hides retained entries until the next assignment.</summary>
    public void HideEntries()
    {
        if (!DenseCount.HasValue)
            IsInitialized = false;
    }

    /// <summary>Gets the greatest stored position for arithmetic indexing, including hidden metadata.</summary>
    public long? MaxIndex => DenseCount is { } count ? count > 0 ? count - 1L : null : _maximumIndex + _positionOffset;

    /// <summary>Enumerates present values by index without copying; consume before mutating.</summary>
    public IEnumerable<ShellValue> PresentValues =>
        DenseCount.HasValue ? ValuesFrom(default, int.MaxValue)
            : IsInitialized ? _positions.Select(position => _elements[position].GetValueOrDefault()) : [];

    /// <summary>Enumerates sparse positions and values together, including unassigned positions.</summary>
    internal IEnumerable<KeyValuePair<ShellValue, ShellValue?>> Entries => DenseCount.HasValue
        ? EntriesByPosition.Select(static entry => KeyValuePair.Create(ShellValue.FromInteger(entry.Key), entry.Value))
        : IsInitialized ? _positions.Select(
            position => KeyValuePair.Create(ShellValue.FromInteger(position + _positionOffset), _elements[position])) : [];

    /// <summary>Enumerates stored entries by numeric position, including unassigned entries.</summary>
    internal IEnumerable<KeyValuePair<long, ShellValue?>> EntriesByPosition => DenseCount is { } count
        ? Enumerable.Range(0, count).Select(
            position => KeyValuePair.Create((long)position, (ShellValue?)ReadDense(position)))
        : IsInitialized ? _positions.Select(position => KeyValuePair.Create(position + _positionOffset, _elements[position])) : [];

    /// <summary>Gets the number of visible entries, excluding holes.</summary>
    public int Count => DenseCount ?? (IsInitialized ? _elements.Count : 0);

    private ShellValue ReadDense(int position) => DirectoryStack is { } stack
        ? stack.GetDirectoryStackEntry(position).Value
        : (_callerOwner ?? throw new InvalidOperationException("Expected a dense array owner.")).GetCallerEntry(position, _callerKind);

    /// <summary>Enumerates at most count present entries starting at an index, without materializing holes.</summary>
    public IEnumerable<ShellValue> ValuesFrom(IndexedArrayIndex first, int count)
    {
        if (DenseCount is { } total)
        {
            if (first.Value < total)
            {
                var start = (int)first.Value;
                var end = start + Math.Min(count, total - start);
                for (var position = start; position < end; position++)
                    yield return ReadDense(position);
            }
            yield break;
        }
        if (!IsInitialized || count <= 0 || _maximumIndex is not { } maximum || first.Value > maximum + _positionOffset)
            yield break;
        var startPosition = first.Value - _positionOffset;
        if (count == 1 && _elements.TryGetValue(startPosition, out var exact))
        {
            yield return exact.GetValueOrDefault();
            yield break;
        }
        // Seek into the existing position tree. Do not ask the subset for Count:
        // that would enumerate its entire range before a small window can be read.
        var positions = first.Value == 0 ? _positions : _positions.GetViewBetween(startPosition, maximum);
        foreach (var position in positions)
        {
            yield return _elements[position].GetValueOrDefault();
            if (--count == 0)
                yield break;
        }
    }
    /// <summary>Enumerates index bytes in numeric order without a dense view.</summary>
    public IEnumerable<ShellValue> PresentKeys => DenseCount is { } count
        ? Enumerable.Range(0, count).Select(position => ShellValue.FromInteger(position))
        : IsInitialized ? _positions.Select(position => ShellValue.FromInteger(position + _positionOffset)) : [];

    /// <summary>Gets one present position without allocating.</summary>
    public bool TryGet(
        IndexedArrayIndex index,
        out ShellValue value)
    {
        if (DenseCount is { } count)
        {
            var exists = index.Value < count;
            value = exists ? ReadDense((int)index.Value) : ShellValue.Empty;
            return exists;
        }
        if (IsInitialized)
            return TryGetSparse(index.Value, out value);
        value = ShellValue.Empty;
        return false;
    }

    private bool TryGetSparse(long index, out ShellValue value)
    {
        if (_maximumIndex is { } maximum && index <= maximum + _positionOffset
            && _elements.TryGetValue(index - _positionOffset, out var assigned) && assigned is { } stored)
        {
            value = stored;
            return true;
        }
        value = ShellValue.Empty;
        return false;
    }

    /// <summary>Adds or replaces one validated position.</summary>
    public void Set(IndexedArrayIndex index, ShellValue? value) => Set(index, value, memory: null);

    internal void Set(IndexedArrayIndex index, ShellValue? value, EvaluationMemory? memory) =>
        Set(index, value, memory, prepaidBytes: 0);

    internal void Set(IndexedArrayIndex index, ShellValue? value, EvaluationMemory? memory, long prepaidBytes)
    {
        if (DirectoryStack is { } stack)
        {
            if (value is { } assigned)
                stack.SetSavedDirectory(index, assigned, memory);
            return;
        }
        if (!IsCallerArray)
            Set(index.Value - _positionOffset, value, memory, prepaidBytes);
    }

    private void Set(long position, ShellValue? value, EvaluationMemory? memory, long prepaidBytes)
    {
        if (IsRecoveryPipelineStatus || DirectoryStack != null)
            return;
        var present = _elements.TryGetValue(position, out var previous);
        var valueDelta = Variables.EstimateValueBytes(value.GetValueOrDefault())
            - Variables.EstimateValueBytes(previous.GetValueOrDefault());
        var valueBytes = checked(_valueBytes + valueDelta);
        var growthBytes = checked(Math.Max(0, valueDelta) + (present ? 0 : ElementMemoryBytes));
        var (replacementCapacity, overlap) = (0, 0L);
        if (memory != null && !present)
        {
            var capacity = _elements.EnsureCapacity(0);
            // Prepaid input already covers the first table when there is no old backing.
            if (capacity != 0 || prepaidBytes == 0)
                (replacementCapacity, overlap) = EstimateDictionaryGrowth(
                    capacity, checked(_elements.Count + 1), Math.Max(0, prepaidBytes - growthBytes));
        }
        memory?.Charge(checked(Math.Max(0, growthBytes - prepaidBytes) + overlap));
        if (replacementCapacity != 0)
            _elements.EnsureCapacity(replacementCapacity);
        _elements[position] = value;
        if (!present)
            _positions.Add(position);
        _valueBytes = valueBytes;
        _entryCapacity = Math.Max(_entryCapacity, _elements.Count);
        MarkInitialized();
        if (!_maximumIndex.HasValue || position > _maximumIndex.Value)
            _maximumIndex = position;
        if (overlap != 0 && memory != null)
            memory.Workspace.Resize(memory.Workspace.Bytes - overlap);
    }

    /// <summary>Removes one position, updating the maximum only when its entry was removed.</summary>
    public void Remove(IndexedArrayIndex index)
    {
        if (IsRecoveryPipelineStatus || DirectoryStack != null || IsCallerArray)
            return;
        RemovePosition(index.Value - _positionOffset);
    }

    internal void RemoveFunctionName(IndexedArrayIndex index, EvaluationMemory memory)
    {
        if (!_editedCallerNames)
        {
            if (!TryGet(index, out _))
                return;
            var count = CallerNameCount;
            memory.Charge((long)count * ElementMemoryBytes);
            var snapshot = new IndexedArray(isInitialized: true, capacity: count);
            for (var position = 0; position < count; position++)
            {
                memory.CancellationToken.ThrowIfCancellationRequested();
                snapshot.Set(position, ReadDense(position), memory, prepaidBytes: ElementMemoryBytes);
            }
            _elements = snapshot._elements;
            _positions = snapshot._positions;
            _valueBytes = snapshot._valueBytes;
            _entryCapacity = snapshot._entryCapacity;
            _maximumIndex = snapshot._maximumIndex;
            _editedCallerNames = true;
        }
        RemovePosition(index.Value - _positionOffset);
    }

    internal void PushCallerName(ShellValue name, EvaluationMemory memory)
    {
        // The existing elements move right by one logical position. Only the new head needs storage.
        var offset = _positionOffset + 1;
        Set(-offset, name, memory, prepaidBytes: 0);
        _positionOffset = offset;
    }

    internal void PopCallerName()
    {
        if (_positions.Count == 0)
            return;
        RemovePosition(_positions.Min);
        _positionOffset = _positions.Count == 0 ? 0 : _positionOffset - 1;
    }

    private void RemovePosition(long position)
    {
        if (!_elements.Remove(position, out var previous))
            return;
        _valueBytes -= Variables.EstimateValueBytes(previous.GetValueOrDefault());
        _positions.Remove(position);
        if (_maximumIndex == position)
            _maximumIndex = _positions.Count == 0 ? null : _positions.Max;
    }

    /// <summary>Creates an owned array at contiguous positions starting from zero.</summary>
    public static IndexedArray FromSequential(IReadOnlyList<ShellValue> values)
    {
        var array = new IndexedArray(isInitialized: true);
        array.SetSequential(0, values, memory: null, CancellationToken.None);
        return array;
    }

    /// <summary>Replaces foreground status values using the caller's admitted result backing.</summary>
    internal static IndexedArray FromForegroundStatuses(
        IReadOnlyList<int> statuses, IndexedArray? previous, CancellationToken cancellationToken)
    {
        var array = new IndexedArray(isInitialized: true);
        var index = 0;
        // When the old array fits, Bash updates its existing elements in index order before
        // appending at ordinal positions. A shorter status list rebuilds from position zero.
        if (previous != null && previous._elements.Count <= statuses.Count && previous._maximumIndex >= previous._elements.Count)
        {
            foreach (var position in previous._positions)
                array.Set(position + previous._positionOffset, FormatStatusAt(index++), memory: null, prepaidBytes: 0);
        }
        else if (statuses.Count > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            array._elements.EnsureCapacity(statuses.Count);
        }
        for (; index < statuses.Count; index++)
            array.Set(index, FormatStatusAt(index), memory: null, prepaidBytes: 0);
        array.IsInitialized = previous?.IsInitialized ?? true;
        return array;

        ShellValue FormatStatusAt(int offset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ShellValue.FromInteger(statuses[offset]);
        }
    }

    /// <summary>Publishes admitted records after checking dictionary growth for the whole batch.</summary>
    internal void SetSequential(
        uint origin, IReadOnlyList<ShellValue> values, EvaluationMemory? memory, CancellationToken cancellationToken) =>
        SetSequential(origin, values, memory, cancellationToken, (long)values.Count * ElementMemoryBytes);

    /// <summary>Admits uncovered entry storage and dictionary growth before changing any record.</summary>
    internal void SetSequential(uint origin, IReadOnlyList<ShellValue> values, EvaluationMemory? memory,
        CancellationToken cancellationToken, long prepaidEntryBytes)
    {
        if (DirectoryStack is { } stack)
        {
            stack.SetSavedDirectories(origin, values, memory, cancellationToken);
            return;
        }
        if (IsRecoveryPipelineStatus || IsCallerArray)
            return;
        var count = values.Count;
        var capacity = _elements.EnsureCapacity(0);
        var mayGrow = capacity != 0 && count > capacity - _elements.Count;
        if (memory != null && (mayGrow || prepaidEntryBytes < (long)count * ElementMemoryBytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var added = CountNewPositions();
            var newEntryBytes = (long)added * ElementMemoryBytes;
            // New position nodes must still fit alongside the replacement dictionary.
            var dictionaryCredit = Math.Max(prepaidEntryBytes, newEntryBytes) - added * PositionNodeBytes;
            var (replacement, overlap) = mayGrow
                ? EstimateDictionaryGrowth(capacity, checked(_elements.Count + added), dictionaryCredit)
                : (capacity == 0 && count > 1 ? count : 0, 0L);
            var charge = Math.Max(0, newEntryBytes - prepaidEntryBytes) + overlap;
            if (charge > 0)
                memory.Charge(charge);
            var release = charge;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (replacement != 0)
                    _elements.EnsureCapacity(replacement);
                release = overlap;
            }
            finally
            {
                if (release != 0)
                    memory.Workspace.Resize(memory.Workspace.Bytes - release);
            }
        }
        else if (capacity == 0 && count > 1)
        {
            _elements.EnsureCapacity(count);
        }

        for (var index = 0; index < count; index++)
            Set(IndexedArrayIndex.FromUnsigned(unchecked(origin + (uint)index)), values[index]);
        MarkInitialized();

        int CountNewPositions()
        {
            var minimum = _positions.Min;
            var maximum = _maximumIndex.GetValueOrDefault(-1);
            var contiguous = maximum - minimum == _elements.Count - 1L;
            var beforeWrap = (int)Math.Min(count, (long)uint.MaxValue + 1 - origin);
            return count - CountExisting(origin, beforeWrap) - CountExisting(0, count - beforeWrap);

            int CountExisting(uint first, int length)
            {
                var start = Math.Max(minimum, (long)first - _positionOffset);
                var last = Math.Min(maximum, (long)first + length - 1 - _positionOffset);
                if (start > last)
                    return 0;
                if (contiguous)
                    return (int)(last - start + 1);
                if (start == last)
                    return _elements.ContainsKey(start) ? 1 : 0;

                var positions = start == minimum && last == maximum ? _positions : _positions.GetViewBetween(start, last);
                var existing = 0;
                foreach (var _ in positions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    existing++;
                }
                return existing;
            }
        }
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
        if (DenseCount.HasValue)
            return new List<ShellValue>(PresentValues);
        if (!IsInitialized || MaxIndex is not { } maximum)
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
            values[checked((int)(entry.Key + _positionOffset))] = entry.Value.GetValueOrDefault();

        return values;
    }

    /// <summary>Copies sparse storage. A directory view keeps its shell owner until rebind at a fork.</summary>
    public IndexedArray Clone()
    {
        if (_callerOwner != null)
            return _editedCallerNames ? ForCallerOwner(_callerOwner) : this;
        if (IsRecoveryPipelineStatus || DirectoryStack != null)
            return this;
        return new IndexedArray(new Dictionary<long, ShellValue?>(_elements),
            new SortedSet<long>(_positions), _maximumIndex, IsInitialized)
        {
            _valueBytes = _valueBytes,
            _entryCapacity = _elements.Count,
            _positionOffset = _positionOffset
        };
    }

    /// <summary>Copies a reached directory view for a detached variable scope.</summary>
    internal IndexedArray CaptureDirectoryStack(EvaluationMemory? memory, CancellationToken cancellationToken)
    {
        var count = Count;
        memory?.Charge((long)count * ElementMemoryBytes);
        var snapshot = new IndexedArray(isInitialized: true, capacity: count);
        for (var position = 0; position < count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGet(IndexedArrayIndex.FromUnsigned((uint)position), out var value))
                snapshot.Set(IndexedArrayIndex.FromUnsigned((uint)position), value, memory, prepaidBytes: ElementMemoryBytes);
        }
        return snapshot;
    }
}

/// <summary>Stores associative-array entries in deterministic byte order, not Bash hash-table order.</summary>
internal sealed class AssociativeArray
{
    // Subtree sizes let slices seek by ordinal without walking the preceding keys.
    private Entry? _root;
    private long _valueBytes;
    internal long EstimatedMemoryBytes => (long)(_root?.Count ?? 0) * IndexedArray.ElementMemoryBytes + _valueBytes;

    /// <summary>Creates an empty array, retaining whether it has received an assignment.</summary>
    public AssociativeArray(bool isInitialized)
    {
        IsInitialized = isInitialized;
    }

    /// <summary>Distinguishes a declaration-only array from an assigned array, including an empty one.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Records an assignment even when a compound append has no elements.</summary>
    public void MarkInitialized() => IsInitialized = true;

    /// <summary>Hides retained entries until the next assignment.</summary>
    public void HideEntries() => IsInitialized = false;

    /// <summary>Gets one validated key without allocating.</summary>
    public bool TryGet(
        AssociativeArrayKey key,
        out ShellValue value)
    {
        for (var entry = IsInitialized ? _root : null; entry != null;)
        {
            var order = ShellValue.ByteOrder.Compare(key.Value, entry.Key);
            if (order == 0)
            {
                value = entry.Value.GetValueOrDefault();
                return entry.Value.HasValue;
            }
            entry = order < 0 ? entry.Left : entry.Right;
        }
        value = ShellValue.Empty;
        return false;
    }

    /// <summary>Adds or replaces one validated non-empty key.</summary>
    public void Set(AssociativeArrayKey key, ShellValue? value) => Set(key, value, memory: null);

    internal void Set(AssociativeArrayKey key, ShellValue? value, EvaluationMemory? memory)
    {
        _root = Insert(_root);
        MarkInitialized();

        Entry Insert(Entry? entry)
        {
            if (entry == null)
            {
                var added = checked(Variables.EstimateValueBytes(key.Value) + Variables.EstimateValueBytes(value.GetValueOrDefault()));
                var nextBytes = checked(_valueBytes + added);
                memory?.Charge(checked(IndexedArray.ElementMemoryBytes + added));
                var inserted = new Entry(key.Value, value);
                _valueBytes = nextBytes;
                return inserted;
            }

            var order = ShellValue.ByteOrder.Compare(key.Value, entry.Key);
            if (order == 0)
            {
                var delta = Variables.EstimateValueBytes(value.GetValueOrDefault())
                    - Variables.EstimateValueBytes(entry.Value.GetValueOrDefault());
                var nextBytes = checked(_valueBytes + delta);
                memory?.Charge(Math.Max(0, delta));
                entry.Value = value;
                _valueBytes = nextBytes;
                return entry;
            }

            if (order < 0)
                entry.Left = Insert(entry.Left);
            else
                entry.Right = Insert(entry.Right);
            return Balance(entry);
        }
    }

    /// <summary>Removes one key without changing the array's declaration or initialization state.</summary>
    public void Remove(AssociativeArrayKey key)
    {
        _root = Delete(_root);

        Entry? Delete(Entry? entry)
        {
            if (entry == null)
                return null;
            var order = ShellValue.ByteOrder.Compare(key.Value, entry.Key);
            if (order < 0)
                entry.Left = Delete(entry.Left);
            else if (order > 0)
                entry.Right = Delete(entry.Right);
            else
            {
                _valueBytes -= Variables.EstimateValueBytes(entry.Key) + Variables.EstimateValueBytes(entry.Value.GetValueOrDefault());
                if (entry.Left == null)
                    return entry.Right;
                if (entry.Right == null)
                    return entry.Left;
                var right = DetachFirst(entry.Right, out var successor);
                successor.Left = entry.Left;
                successor.Right = right;
                return Balance(successor);
            }
            return Balance(entry);
        }

        static Entry? DetachFirst(Entry entry, out Entry first)
        {
            if (entry.Left == null)
            {
                first = entry;
                return entry.Right;
            }
            entry.Left = DetachFirst(entry.Left, out first);
            return Balance(entry);
        }
    }

    /// <summary>Enumerates values by byte-ordered key without copying; consume before mutating.</summary>
    public IEnumerable<ShellValue> PresentValues => ValuesFrom(0, int.MaxValue);

    /// <summary>Seeks to a zero-based ordinal and yields a bounded byte-ordered range without copying.</summary>
    internal IEnumerable<ShellValue> ValuesFrom(int first, int count) =>
        Enumerate(first, count).Select(static entry => entry.Value.GetValueOrDefault());

    /// <summary>Enumerates keys and values together in byte order, including unassigned keys.</summary>
    internal IEnumerable<KeyValuePair<ShellValue, ShellValue?>> Entries =>
        Enumerate(0, int.MaxValue).Select(static entry => KeyValuePair.Create(entry.Key, entry.Value));

    /// <summary>Gets the number of present entries.</summary>
    public int Count => IsInitialized ? _root?.Count ?? 0 : 0;

    /// <summary>Enumerates keys in the same deterministic order as their values.</summary>
    public IEnumerable<ShellValue> PresentKeys => Enumerate(0, int.MaxValue).Select(static entry => entry.Key);

    /// <summary>Captures a detached dictionary whose enumeration follows byte key order.</summary>
    public Dictionary<ShellValue, ShellValue> CaptureValues()
    {
        var values = new Dictionary<ShellValue, ShellValue>(Count);
        foreach (var entry in Enumerate(0, int.MaxValue))
            values.Add(entry.Key, entry.Value.GetValueOrDefault());
        return values;
    }

    /// <summary>Creates an independent associative array with the same entries.</summary>
    public AssociativeArray Clone() => new(IsInitialized) { _root = _root?.Clone(), _valueBytes = _valueBytes };

    private IEnumerable<Entry> Enumerate(int first, int count)
    {
        if (!IsInitialized || _root is not { } root || count <= 0 || first >= root.Count)
            yield break;

        first = Math.Max(0, first);
        var pending = Math.Min(count, root.Count - first) == 1 ? null : new Stack<Entry>(root.Height);
        Entry? selected = root;
        while (selected != null)
        {
            var leftCount = selected.Left?.Count ?? 0;
            if (first == leftCount)
                break;
            if (first > leftCount)
            {
                first -= leftCount + 1;
                selected = selected.Right;
            }
            else
            {
                pending?.Push(selected);
                selected = selected.Left;
            }
        }

        if (pending == null)
        {
            if (selected != null)
                yield return selected;
            yield break;
        }
        if (selected != null)
            pending.Push(selected);

        while (pending.TryPop(out var next))
        {
            yield return next;
            if (--count == 0)
                yield break;
            for (var entry = next.Right; entry != null; entry = entry.Left)
                pending.Push(entry);
        }
    }

    private static Entry Balance(Entry entry)
    {
        entry.Refresh();
        var difference = Height(entry.Left) - Height(entry.Right);
        if (difference > 1 && entry.Left is { } left)
        {
            if (left.Right is { } inner && inner.Height > Height(left.Left))
                left = RotateLeft(left, inner);
            return RotateRight(entry, left);
        }
        if (difference < -1 && entry.Right is { } right)
        {
            if (right.Left is { } inner && inner.Height > Height(right.Right))
                right = RotateRight(right, inner);
            return RotateLeft(entry, right);
        }
        return entry;

        static Entry RotateLeft(Entry entry, Entry right)
        {
            entry.Right = right.Left;
            right.Left = entry;
            entry.Refresh();
            right.Refresh();
            return right;
        }

        static Entry RotateRight(Entry entry, Entry left)
        {
            entry.Left = left.Right;
            left.Right = entry;
            entry.Refresh();
            left.Refresh();
            return left;
        }
    }

    private static int Height(Entry? entry) => entry?.Height ?? 0;

    private sealed class Entry(ShellValue key, ShellValue? value)
    {
        public ShellValue Key { get; } = key;
        public ShellValue? Value { get; set; } = value;
        public Entry? Left { get; set; }
        public Entry? Right { get; set; }
        public int Height { get; private set; } = 1;
        public int Count { get; private set; } = 1;

        public void Refresh()
        {
            Height = 1 + Math.Max(AssociativeArray.Height(Left), AssociativeArray.Height(Right));
            Count = 1 + (Left?.Count ?? 0) + (Right?.Count ?? 0);
        }

        public Entry Clone() => new(Key, Value) { Left = Left?.Clone(), Right = Right?.Clone(), Height = Height, Count = Count };
    }
}

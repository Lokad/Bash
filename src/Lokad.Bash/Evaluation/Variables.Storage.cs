using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    private const int BindingTableEntryBytes = 48;

    // One entry owns both the binding kind and its attributes. Views below never own values.
    private readonly Dictionary<BindingName, VariableSlot> _bindings = new();
    // Command launches enumerate exports, not every shell local. StoreBinding maintains this derived index.
    private readonly HashSet<BindingName> _exportedIndex = new();

    [Flags]
    internal enum BindingAttributes
    {
        None = 0, Readonly = 1, Exported = 2, Temporary = 4, Integer = 8,
        Capitalize = 16, Lowercase = 32, Uppercase = 64,
        Case = Capitalize | Lowercase | Uppercase,
        // Rejected local declarations can retain -n on an array without a reference target.
        ArrayNameref = 128
    }

    internal readonly record struct CaseAttributeChange(BindingAttributes Set, BindingAttributes Clear)
    {
        internal bool IsEmpty => (Set | Clear) == BindingAttributes.None;

        internal CaseAttributeChange Include(BindingAttributes attribute, bool remove) => remove
            ? this with { Clear = Clear | attribute }
            : new(Set | attribute, Clear | (BindingAttributes.Case & ~attribute));
    }

    private abstract class VariableSlot
    {
        public BindingAttributes Attributes { get; set; }
        public bool IsArrayNameref => (Attributes & BindingAttributes.ArrayNameref) != 0;

        /// <summary>Copies mutable containers and attributes, sharing only immutable name/value bytes.</summary>
        public VariableSlot Clone()
        {
            var copy = CloneValue();
            copy.Attributes = Attributes;
            return copy;
        }

        /// <summary>Returns a new slot of the same kind, with independently owned mutable value storage.</summary>
        protected abstract VariableSlot CloneValue();
    }

    // An export/readonly attribute alone does not declare a scalar.
    private sealed class AttributeSlot : VariableSlot
    {
        protected override VariableSlot CloneValue() => new AttributeSlot();
    }

    private sealed class UnassignedSlot : VariableSlot
    {
        protected override VariableSlot CloneValue() => new UnassignedSlot();
    }

    private class ScalarSlot(ShellValue value) : VariableSlot
    {
        public virtual ShellValue Value { get; set; } = value;
        protected override VariableSlot CloneValue() => new ScalarSlot(Value);
    }

    private sealed class LineNumberSlot(int line) : ScalarSlot(ShellValue.Empty)
    {
        private int _line = line;
        private ShellValue _cachedValue;
        public int Line
        {
            get => _line;
            set
            {
                if (_line == value)
                    return;
                _line = value;
                _cachedValue = ShellValue.Empty;
            }
        }

        public override ShellValue Value
        {
            get => _cachedValue.IsEmpty
                ? _cachedValue = ShellValue.FromBytes(NumberFormatting.FormatNumber(Line))
                : _cachedValue;
            set => Line = Integers.TryParseNumber(Integers.TrimWhitespace(value.Bytes.Span), out var number)
                ? unchecked((int)number) : 0;
        }

        protected override VariableSlot CloneValue() => new LineNumberSlot(Line);
    }

    private sealed class IndexedSlot(IndexedArray value) : VariableSlot
    {
        public IndexedArray Value { get; } = value;
        protected override VariableSlot CloneValue() => new IndexedSlot(Value.Clone());
    }

    private sealed class AssociativeSlot(AssociativeArray value) : VariableSlot
    {
        public AssociativeArray Value { get; } = value;
        protected override VariableSlot CloneValue() => new AssociativeSlot(Value.Clone());
    }

    private sealed class NamerefSlot(NamerefTarget value) : VariableSlot
    {
        public NamerefTarget Value { get; } = value;
        protected override VariableSlot CloneValue() => new NamerefSlot(Value);
    }

    /// <summary>Replaces the value while borrowing its declaration attributes.</summary>
    private void ReplaceValue(BindingName name, VariableSlot replacement)
    {
        if (TryGetBinding(name, detachBorrowed: false, out var previous))
        {
            if (previous is IndexedSlot { Value.IsRecoveryPipelineStatus: true })
                return;
            replacement.Attributes |= previous.Attributes & ~BindingAttributes.ArrayNameref;
        }
        StoreBinding(name, replacement);
        ResetGetoptsPosition(name);
    }

    private void StoreBinding(BindingName name, VariableSlot slot) => StoreBinding(name, slot, slot.Attributes);

    private void StoreBinding(BindingName name, VariableSlot slot, BindingAttributes attributes)
    {
        if (slot is AttributeSlot && attributes == BindingAttributes.None)
        {
            ClearBinding(name);
            return;
        }
        var exported = (attributes & BindingAttributes.Exported) != 0;
        PreparePrefixTableEntry(name);
        if (exported && _commandSource?.PrefixCollectionMemory is { } collections)
            collections.PrepareSetCapacity(_exportedIndex, name, BindingNameSetEntryBytes);
        using var memory = TrackBindingMemory(name);
        slot.Attributes = attributes;
        _bindings[name] = slot;
        _bindingCapacity = Math.Max(_bindingCapacity, _bindings.Count);
        if (exported)
            _exportedIndex.Add(name);
        else
            _exportedIndex.Remove(name);
    }

    private void ClearBinding(BindingName name)
    {
        ClearBindingSlot(name);
        _exportedIndex.Remove(name);
    }

    private void ClearBindingSlot(BindingName name)
    {
        PreparePrefixTableEntry(name);
        using var memory = TrackBindingMemory(name);
        ResetGetoptsPosition(name);
        if (_commandSource == null)
            _bindings.Remove(name);
        else
        {
            _bindings[name] = new MissingSlot();
            _bindingCapacity = Math.Max(_bindingCapacity, _bindings.Count);
        }
    }

    private void PreparePrefixTableEntry(BindingName name)
    {
        if (_commandSource?.PrefixCollectionMemory is { } collections
            && _bindings.Count == _bindings.EnsureCapacity(0) && !_bindings.ContainsKey(name))
            collections.PrepareDictionaryCapacity(_bindings, BindingTableEntryBytes);
    }

    /// <summary>Clears export attributes while preserving the fork's values and other attributes.</summary>
    internal void ClearExportAttributes(EvaluationMemory memory, CancellationToken cancellationToken)
    {
        if (_commandSource != null)
        {
            // Detaching borrowed bindings changes the export view. Own its keys through mutation.
            using var scratch = new MemoryCapacity(memory.Budget, 0);
            var names = new List<BindingName>();
            foreach (var name in ExportedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                memory.PrepareListCapacity(scratch, names, 1);
                names.Add(name);
            }
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetExported(name, false);
            }
            return;
        }

        if (_exportedIndex.Count != 0)
            _exportedIndex.RemoveWhere(ClearOwnedExport);

        bool ClearOwnedExport(BindingName name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = _bindings[name];
            slot.Attributes &= ~BindingAttributes.Exported;
            if (slot is AttributeSlot && slot.Attributes == BindingAttributes.None)
                ClearBindingSlot(name);
            return true;
        }
    }

    private void DeclareUnassigned(BindingName name)
    {
        if (!HasBinding(name))
            ReplaceValue(name, new UnassignedSlot());
    }

    private bool HasAttribute(BindingName name, BindingAttributes attribute) =>
        TryGetBinding(name, detachBorrowed: false, out var slot) && (slot.Attributes & attribute) != 0;

    internal readonly record struct ArrayMetadata(ArrayKind Kind, int Count, bool IsInitialized, long? MaxIndex);

    /// <summary>Reads array metadata through the visible slot without retaining its mutable backing.</summary>
    internal ArrayMetadata? GetArrayMetadata(BindingName name) =>
        TryGetBinding(name, detachBorrowed: false, out var slot) ? slot switch
        {
            IndexedSlot indexed => new(ArrayKind.Indexed, indexed.Value.Count, indexed.Value.IsInitialized, indexed.Value.MaxIndex),
            AssociativeSlot map => new(ArrayKind.Associative, map.Value.Count, map.Value.IsInitialized, null),
            _ => null
        } : null;

    /// <summary>Selects an array for expansion under the active command scope.</summary>
    /// <remarks>Immediate consumers can enumerate the borrowed source before running another operand.</remarks>
    internal (IndexedArray? Indexed, AssociativeArray? Associative) SelectExpansionArray(BindingName name, bool detachBorrowed) =>
        TryGetBinding(name, detachBorrowed, out var slot) ? slot switch
        {
            IndexedSlot indexed => (indexed.Value, null),
            AssociativeSlot map => (null, map.Value),
            _ => default
        } : default;

    internal bool TryReadArrayElement(BindingName name, IndexedArrayIndex index, out ShellValue value)
    {
        if (TryGetBinding(name, detachBorrowed: false, out var slot) && slot is IndexedSlot indexed)
            return indexed.Value.TryGet(index, out value);
        value = ShellValue.Empty;
        return false;
    }

    internal bool TryReadArrayElement(BindingName name, AssociativeArrayKey key, out ShellValue value)
    {
        if (TryGetBinding(name, detachBorrowed: false, out var slot) && slot is AssociativeSlot map)
            return map.Value.TryGet(key, out value);
        value = ShellValue.Empty;
        return false;
    }

    internal BindingAttributes GetAssignmentAttributes(BindingName name) =>
        TryGetBinding(name, detachBorrowed: false, out var slot) ? slot.Attributes : BindingAttributes.None;

    internal void SetCaseAttributes(BindingName name, CaseAttributeChange change) =>
        UpdateBindingAttributes(name, change.Set, change.Clear);

    internal void UpdateBindingAttributes(BindingName name, BindingAttributes set, BindingAttributes clear)
    {
        if ((set | clear) == BindingAttributes.None || name.IsEmpty)
            return;
        if (!TryGetBinding(name, out var slot))
        {
            if ((set & ~clear) == 0)
                return;
            slot = new AttributeSlot();
        }
        var attributes = (slot.Attributes | set) & ~clear;
        if (slot.Attributes == attributes)
            return;
        StoreBinding(name, slot, attributes);
    }

    internal ShellValue ConvertAssignmentCase(BindingName name, ShellValue value, EvaluationMemory? memory) =>
        ConvertAssignmentCase(GetAssignmentAttributes(name), value, memory);

    internal static ShellValue ConvertAssignmentCase(BindingAttributes attributes, ShellValue value, EvaluationMemory? memory)
    {
        if ((attributes & BindingAttributes.Case) == 0 || (attributes & BindingAttributes.Integer) != 0)
            return value;
        var conversion = (attributes & BindingAttributes.Capitalize) != 0 ? CaseTransformation.Capitalize
            : (attributes & BindingAttributes.Uppercase) != 0 ? CaseTransformation.Uppercase : CaseTransformation.Lowercase;
        return WordExpansion.ChangeAssignmentCase(value, conversion, memory);
    }

    internal bool IsTemporaryBinding(BindingName name) => HasAttribute(name, BindingAttributes.Temporary);

    private void SetAttribute(BindingName name, BindingAttributes attribute, bool enabled)
    {
        if (!TryGetBinding(name, out var slot))
        {
            if (!enabled) return;
            slot = new AttributeSlot();
        }
        StoreBinding(name, slot, enabled ? slot.Attributes | attribute : slot.Attributes & ~attribute);
    }

    /// <summary>Removes a value kind, retaining attributes that belong to the name.</summary>
    private bool RemoveValue<TSlot>(BindingName name) where TSlot : VariableSlot
    {
        if (!TryGetBinding(name, out var slot) || slot is not TSlot)
            return false;
        StoreBinding(name, new AttributeSlot { Attributes = slot.Attributes });
        ResetGetoptsPosition(name);
        return true;
    }

    private static bool IsAssigned(VariableSlot slot) => slot switch
    {
        ScalarSlot => true,
        IndexedSlot indexed => indexed.Value.IsInitialized,
        AssociativeSlot associative => associative.Value.IsInitialized,
        NamerefSlot reference => !reference.Value.IsUnset,
        _ => false
    };

    /// <summary>Stages indexed input so callbacks and arithmetic can see the preceding records.</summary>
    internal sealed class IndexedInputAssignment : IDisposable
    {
        private readonly Variables _owner;
        private readonly BindingName _name;
        private readonly VariableSlot? _previous;
        private readonly GetoptsCursor _previousCursor;
        private bool _committed;

        internal long SavedBytes { get; }

        internal IndexedInputAssignment(Variables owner, BindingName name, bool preserve, bool resetAttributes,
            ShellValue? namerefValue, EvaluationMemory memory)
        {
            _owner = owner;
            _name = name;
            owner.TryGetBinding(name, out _previous);
            _previousCursor = owner._getoptsCursor;
            if (_previous != null)
            {
                SavedBytes = EstimateSlotBytes(name, _previous);
                memory.Charge(SavedBytes);
            }
            else
                memory.PrepareRetainedStorage(BindingOverheadBytes + (long)ValueCapacityMultiplier * name.ByteLength);
            var staged = preserve && _previous is IndexedSlot indexed
                ? indexed.Value.Clone() : new IndexedArray(isInitialized: true);
            if (preserve && _previous is ScalarSlot scalar)
                staged.Set(default, scalar.Value);
            else if (preserve && namerefValue is { } raw)
                staged.Set(default, raw);
            staged.MarkInitialized();
            if (resetAttributes)
                owner.ClearBinding(name);
            owner.ReplaceValue(name, new IndexedSlot(staged));
        }

        internal void Commit() => _committed = true;

        /// <summary>Promotes a borrowed record without retaining the source batch.</summary>
        internal bool TryStoreBorrowedRecord(IndexedArrayIndex index, ShellValue value, EvaluationMemory memory) =>
            _owner.TrySetResolvedArrayElement(_name, index, value, memory, prepaidBytes: 0,
                savedInputBytes: SavedBytes, replaceAssociative: false);

        public void Dispose()
        {
            if (_committed)
                return;
            if (_previous != null)
                _owner.StoreBinding(_name, _previous);
            else
                _owner.ClearBinding(_name);
            if (_name.CachedText == "OPTIND")
                _owner._getoptsCursor = _previousCursor;
        }
    }

    /// <summary>Live typed lookup. Membership is read-only; value access can detach mutable storage.</summary>
    private sealed class BindingView<TSlot, TValue>(
        Variables owner, Func<TSlot, TValue> select, bool detachBorrowedValues)
        : IReadOnlyDictionary<BindingName, TValue> where TSlot : VariableSlot
    {
        public int Count => owner.EnumerateBindings().Count(entry => entry.Value is TSlot);
        public IEnumerable<BindingName> Keys => owner.EnumerateBindings().Where(entry => entry.Value is TSlot).Select(entry => entry.Key);
        public IEnumerable<TValue> Values => this.Select(entry => entry.Value);
        public TValue this[BindingName key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        public bool ContainsKey(BindingName key) => owner.TryGetBinding(key, detachBorrowed: false, out var slot) && slot is TSlot;

        public bool TryGetValue(BindingName key, [MaybeNullWhen(false)] out TValue value)
        {
            if (owner.TryGetBinding(key, detachBorrowedValues, out var slot) && slot is TSlot typed)
            {
                value = select(typed);
                return true;
            }
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<BindingName, TValue>> GetEnumerator()
        {
            foreach (var entry in owner.EnumerateBindings())
                if (entry.Value is TSlot && TryGetValue(entry.Key, out var value))
                    yield return new(entry.Key, value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Live attribute inspection; membership remains owned by each binding.</summary>
    private sealed class AttributeView(Variables owner, BindingAttributes attribute)
        : IReadOnlyCollection<BindingName>
    {
        public int Count => owner.EnumerateBindings().Count(entry => (entry.Value.Attributes & attribute) != 0);

        public IEnumerator<BindingName> GetEnumerator()
        {
            foreach (var entry in owner.EnumerateBindings())
                if ((entry.Value.Attributes & attribute) != 0)
                    yield return entry.Key;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

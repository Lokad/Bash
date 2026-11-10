using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    // One entry owns both the binding kind and its attributes. Views below never own values.
    private readonly Dictionary<BindingName, VariableSlot> _bindings = new();
    // Command launches enumerate exports, not every shell local. StoreBinding maintains this derived index.
    private readonly HashSet<BindingName> _exportedIndex = new();

    [Flags]
    private enum BindingAttributes { None = 0, Readonly = 1, Exported = 2 }

    private abstract class VariableSlot
    {
        public BindingAttributes Attributes { get; set; }

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

    private sealed class ScalarSlot(ShellValue value) : VariableSlot
    {
        public ShellValue Value { get; set; } = value;
        protected override VariableSlot CloneValue() => new ScalarSlot(Value);
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

    /// <summary>Changes the value kind without losing declaration attributes.</summary>
    private void ReplaceValue(BindingName name, VariableSlot replacement)
    {
        if (_bindings.TryGetValue(name, out var previous))
            replacement.Attributes = previous.Attributes;
        StoreBinding(name, replacement);
    }

    private void StoreBinding(BindingName name, VariableSlot slot)
    {
        if (slot is AttributeSlot && slot.Attributes == BindingAttributes.None)
        {
            ClearBinding(name);
            return;
        }
        using var memory = TrackBindingMemory(name);
        _bindings[name] = slot;
        _bindingCapacity = Math.Max(_bindingCapacity, _bindings.Count);
        if ((slot.Attributes & BindingAttributes.Exported) != 0)
            _exportedIndex.Add(name);
        else
            _exportedIndex.Remove(name);
    }

    private void ClearBinding(BindingName name)
    {
        using var memory = TrackBindingMemory(name);
        _bindings.Remove(name);
        _exportedIndex.Remove(name);
    }

    private void DeclareUnassigned(BindingName name)
    {
        if (!HasBinding(name))
            ReplaceValue(name, new UnassignedSlot());
    }

    private bool HasAttribute(BindingName name, BindingAttributes attribute) =>
        _bindings.TryGetValue(name, out var slot) && (slot.Attributes & attribute) != 0;

    private void SetAttribute(BindingName name, BindingAttributes attribute, bool enabled)
    {
        if (!_bindings.TryGetValue(name, out var slot))
        {
            if (!enabled) return;
            slot = new AttributeSlot();
        }
        slot.Attributes = enabled ? slot.Attributes | attribute : slot.Attributes & ~attribute;
        StoreBinding(name, slot);
    }

    /// <summary>Removes a value kind, retaining attributes that belong to the name.</summary>
    private bool RemoveValue<TSlot>(BindingName name) where TSlot : VariableSlot
    {
        if (!_bindings.TryGetValue(name, out var slot) || slot is not TSlot)
            return false;
        StoreBinding(name, new AttributeSlot { Attributes = slot.Attributes });
        return true;
    }

    private static bool IsAssigned(VariableSlot slot) => slot switch
    {
        ScalarSlot => true,
        IndexedSlot indexed => indexed.Value.IsInitialized,
        AssociativeSlot associative => associative.Value.IsInitialized,
        NamerefSlot reference => !reference.Value.IsEmpty,
        _ => false
    };

    /// <summary>Live typed lookup; counts and enumeration scan bindings without copying their values.</summary>
    private sealed class BindingView<TSlot, TValue>(
        IReadOnlyDictionary<BindingName, VariableSlot> bindings, Func<TSlot, TValue> select)
        : IReadOnlyDictionary<BindingName, TValue> where TSlot : VariableSlot
    {
        public int Count => bindings.Count(entry => entry.Value is TSlot);
        public IEnumerable<BindingName> Keys => bindings.Where(entry => entry.Value is TSlot).Select(entry => entry.Key);
        public IEnumerable<TValue> Values => this.Select(entry => entry.Value);
        public TValue this[BindingName key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        public bool ContainsKey(BindingName key) => bindings.TryGetValue(key, out var slot) && slot is TSlot;

        public bool TryGetValue(BindingName key, [MaybeNullWhen(false)] out TValue value)
        {
            if (bindings.TryGetValue(key, out var slot) && slot is TSlot typed)
            {
                value = select(typed);
                return true;
            }
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<BindingName, TValue>> GetEnumerator()
        {
            foreach (var entry in bindings)
                if (entry.Value is TSlot typed)
                    yield return new(entry.Key, select(typed));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Live attribute inspection; membership remains owned by each binding.</summary>
    private sealed class AttributeView(IReadOnlyDictionary<BindingName, VariableSlot> bindings, BindingAttributes attribute)
        : IReadOnlyCollection<BindingName>
    {
        public int Count => bindings.Count(entry => (entry.Value.Attributes & attribute) != 0);

        public IEnumerator<BindingName> GetEnumerator()
        {
            foreach (var entry in bindings)
                if ((entry.Value.Attributes & attribute) != 0)
                    yield return entry.Key;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

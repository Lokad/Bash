using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Whether a kind-only local declaration discards an existing scalar value.</summary>
internal enum ScalarArrayPromotion { RetainValue, DiscardValue }
internal enum NamerefAssignmentFailure { None, InvalidTarget, IndirectUnsetTarget }
internal enum BindingResolution { Referent, LastNameref }
internal readonly record struct VariableBinding(VariableReference Reference, Variables Owner)
{
    public BindingName Name => Reference.Name;
    public bool IsUnsetNameref => Reference.Kind == VariableReferenceKind.Name
        && Owner.TryGetNameref(Name, out var target) && target.IsEmpty;
}

/// <summary>Declarations include attributes/unset names; assignment listings include assigned values only.</summary>
internal enum VariableDisplayStyle { Declaration, DeclarationName, Assignment }

/// <summary>
/// Encapsulates shell variable state (scalars, arrays, namerefs, export/readonly flags, and positional parameters).
/// </summary>
public sealed partial class Variables
{
    private const int ValueHeaderBytes = 32;
    private const int ValueCapacityMultiplier = 2;
    private const int BindingOverheadBytes = 128;
    private const int PositionalEntryBytes = 32;

    /// <summary>Retains bytes and parsed selectors; a null reference denotes an unset nameref.</summary>
    private readonly record struct NamerefTarget(ShellValue Value, VariableReference? Reference, BindingName? LiteralName)
    {
        public bool IsEmpty => Value.IsEmpty;
    }
    /// <summary>
    /// Holds the shell name used for $0 expansion.
    /// </summary>
    private ShellValue _argumentZero = ShellValue.Empty;
    private int _bindingCapacity;
    private long _bindingBytes;

    // Account bindings as a coarse owner. Shared values can be counted twice; no object identities
    // are registered. Callers keep saved bindings charged through restoration.
    internal static long EstimateValueBytes(ShellValue value) => value.IsEmpty ? 0
        : ValueHeaderBytes + (long)ValueCapacityMultiplier * value.Bytes.Length;

    internal long EstimatedMemoryBytes
    {
        get
        {
            // The command source already owns borrowed positional/name/IFS bytes. Include it
            // for full child-snapshot admission, without charging that borrowed frame twice.
            var inherited = _commandSource?.Variables.EstimatedMemoryBytes
                ?? checked(PositionalMemoryBytes + EstimateValueBytes(_argumentZero)
                    + EstimateValueBytes(ShellValue.FromBytes(CachedFieldSeparators.Bytes)));
            return checked(_bindingBytes + inherited
                + (long)BindingOverheadBytes * (_bindingCapacity - _bindings.Count));
        }
    }

    // Storage replacements and in-place value mutations each update the affected binding once.
    // Command boundaries read a constant-time total; arrays already cache their element estimates.
    internal BindingMemoryChange TrackBindingMemory(BindingName name) => new(this, name);

    internal long EstimateBindingBytes(BindingName name)
    {
        return TryGetBinding(name, out var slot) ? EstimateSlotBytes(name, slot) : 0;
    }

    private long EstimateOwnedBindingBytes(BindingName name) =>
        _bindings.TryGetValue(name, out var slot) ? EstimateSlotBytes(name, slot) : 0;

    private static long EstimateSlotBytes(BindingName name, VariableSlot slot)
    {
        var overhead = BindingOverheadBytes + (long)ValueCapacityMultiplier * name.ByteLength;
        return overhead + (slot switch
        {
            ScalarSlot scalar => EstimateValueBytes(scalar.Value),
            IndexedSlot indexed => indexed.Value.EstimatedMemoryBytes,
            AssociativeSlot associative => associative.Value.EstimatedMemoryBytes,
            NamerefSlot reference => EstimateValueBytes(reference.Value.Value),
            _ => 0
        });
    }

    internal readonly struct BindingMemoryChange(Variables owner, BindingName name) : IDisposable
    {
        private readonly long _before = owner.EstimateOwnedBindingBytes(name);
        public void Dispose() => owner._bindingBytes = checked(owner._bindingBytes + owner.EstimateOwnedBindingBytes(name) - _before);
    }

    internal IReadOnlyDictionary<BindingName, ShellValue> Scalars { get; }
    internal IReadOnlyDictionary<BindingName, IndexedArray> IndexedArrays { get; }
    internal IReadOnlyDictionary<BindingName, AssociativeArray> AssociativeArrays { get; }
    private IReadOnlyDictionary<BindingName, NamerefTarget> NamerefTargets { get; }
    internal IReadOnlyDictionary<BindingName, ShellValue> Namerefs =>
        NamerefTargets.ToDictionary(entry => entry.Key, entry => entry.Value.Value);
    internal IEnumerable<BindingName> BindingNames =>
        EnumerateBindings().Where(entry => entry.Value is not AttributeSlot).Select(entry => entry.Key);
    internal IEnumerable<BindingName> AssignedNames =>
        EnumerateBindings().Where(entry => IsAssigned(entry.Value)).Select(entry => entry.Key);

    internal bool HasBinding(BindingName name) =>
        TryGetBinding(name, out var slot) && slot is not AttributeSlot;

    internal bool IsUnassignedScalar(BindingName name) =>
        TryGetBinding(name, out var slot) && slot is UnassignedSlot;

    /// <summary>Checks the binding's own attribute, without following a nameref.</summary>
    internal bool IsReadonlyBinding(BindingName name) => HasAttribute(name, BindingAttributes.Readonly);

    internal bool TryDeclareName(BindingName name)
    {
        if (!name.IsIdentifier)
            return false;
        DeclareUnassigned(name);
        return true;
    }

    internal IReadOnlyCollection<BindingName> ExportedNames { get; }
    internal IEnumerable<VariableBinding> ExportedBindings => ExportedNames.Select(name => new VariableBinding(VariableReference.ForName(name), this));
    internal IReadOnlyCollection<BindingName> ReadonlyNames { get; }
    internal ShellValue ArgumentZero { get => _argumentZero; set => _argumentZero = value; }

    /// <summary>Immutable membership for the last resolved IFS; lookups validate its value identity.</summary>
    internal FieldSeparators CachedFieldSeparators { get; set; } = FieldSeparators.Default;

    private Variables()
    {
        _positionalView = new PositionalParametersView(this);
        Scalars = new BindingView<ScalarSlot, ShellValue>(this, slot => slot.Value);
        IndexedArrays = new BindingView<IndexedSlot, IndexedArray>(this, slot => slot.Value);
        AssociativeArrays = new BindingView<AssociativeSlot, AssociativeArray>(this, slot => slot.Value);
        NamerefTargets = new BindingView<NamerefSlot, NamerefTarget>(this, slot => slot.Value);
        ExportedNames = new ExportedNameView(this);
        ReadonlyNames = new AttributeView(this, BindingAttributes.Readonly);
    }

    /// <summary>Creates empty variable state.</summary>
    public static Variables CreateEmpty() => new();

    /// <summary>Copies initial bindings and positional values into owned state.</summary>
    public static Variables CreateInitial(
        IReadOnlyDictionary<BindingName, ShellValue> scalars,
        IReadOnlyList<ShellValue> positionalParameters)
    {
        var variables = new Variables();
        foreach (var entry in scalars)
            variables.StoreBinding(entry.Key, new ScalarSlot(entry.Value));
        variables.ReplacePositionalParameters(positionalParameters);
        return variables;
    }

    /// <summary>
    /// Copies bindings and collection ownership, sharing immutable value bytes.
    /// Use a clone when evaluation must observe or perform writes that should not affect the caller scope.
    /// Used for child evaluation and explicit snapshots. Prefix preparation detaches reached bindings instead.
    /// Caller-side expansion effects are handled by ScopedEvaluation, not inferred from clone differences.
    /// </summary>
    public Variables Clone()
    {
        var clone = new Variables();
        clone.CachedFieldSeparators = CachedFieldSeparators;
        foreach (var entry in EnumerateBindings())
            clone.StoreBinding(entry.Key, entry.Value.Clone());
        clone.ReplacePositionalParameters(PositionalParameters);
        clone._argumentZero = _argumentZero;

        return clone;
    }

    /// <summary>
    /// Installs prepared prefixes for an in-process command, restoring only those bindings on exit.
    /// Other writes go directly to the caller; no whole-store snapshot or value comparison is needed.
    /// </summary>
    internal sealed class CommandBindings : IDisposable
    {
        private readonly Variables _owner;
        private readonly HashSet<BindingName> _prefixes;
        private readonly Variables _saved;
        private readonly Variables _retained;
        private readonly HashSet<BindingName> _retainedNames;

        public CommandBindings(Variables owner, Variables prepared, IReadOnlySet<BindingName> prefixes)
        {
            _owner = owner;
            _prefixes = new HashSet<BindingName>(prefixes);
            _saved = new Variables();
            _retained = new Variables();
            _retainedNames = new HashSet<BindingName>();
            foreach (var name in prefixes)
            {
                _saved.CopyBindingFrom(owner, name);
                owner.CopyBindingFrom(prepared, name);
            }
        }

        private CommandBindings(Variables owner, CommandBindings source)
        {
            _owner = owner;
            _prefixes = new HashSet<BindingName>(source._prefixes);
            _saved = source._saved.Clone();
            // Child contexts never restore the caller's pending output assignments.
            _retained = new Variables();
            _retainedNames = new HashSet<BindingName>();
        }

        public bool Contains(BindingName name) => _prefixes.Contains(name);
        internal Variables SavedVariables => _saved;
        internal IReadOnlySet<BindingName> PrefixNames => _prefixes;
        internal CommandBindings Fork(Variables owner) => new(owner, this);

        /// <summary>A declaration reached the caller after unset; that binding no longer belongs to this prefix.</summary>
        internal void ExposeBinding(BindingName name, Variables visible)
        {
            if (!_prefixes.Remove(name))
                return;
            if (!ReferenceEquals(_owner, visible))
                _owner.CopyBindingFrom(visible, name);
            _saved.ClearBinding(name);
            _retained.ClearBinding(name);
            _retainedNames.Remove(name);
        }

        /// <summary>Direct outputs preserve caller bindings; indirect writes use the command scope.</summary>
        public bool TryApplyOutputAssignment(
            VariableReference reference, ShellValue value, EvaluationMemory memory)
        {
            var destination = SelectOutputVariables(reference.Name);
            if (!destination.TryApplyAssignment(reference, value, memory))
                return false;
            if (ReferenceEquals(destination, _owner))
                return true;

            // Arithmetic and getopts read subsequent results from their temporary scope.
            if (!_owner.TryApplyAssignment(reference, value, memory))
                return false;

            _retainedNames.Add(reference.Name);
            return true;
        }

        /// <summary>Direct outputs select the caller's saved kind; indirect outputs keep the active binding.</summary>
        internal Variables SelectOutputVariables(BindingName name)
        {
            if (!_prefixes.Contains(name) || _saved.NamerefTargets.ContainsKey(name))
                return _owner;
            if (!_retainedNames.Contains(name))
                _retained.CopyBindingFrom(_saved, name);
            return _retained;
        }

        internal void CompleteOutput(BindingName name, Variables destination)
        {
            if (ReferenceEquals(destination, _retained))
                _retainedNames.Add(name);
        }

        /// <summary>Selects storage for one selector write, preserving prior outputs and untouched caller elements.</summary>
        internal Variables? RetainSelectorBinding(BindingName name)
        {
            if (!_prefixes.Contains(name))
                return null;
            if (_retainedNames.Add(name))
                _retained.CopyBindingFrom(_saved, name);
            return _retained;
        }

        /// <summary>Retains an explicit builtin write even when its target was a temporary prefix.</summary>
        public void Retain(BindingName name)
        {
            if (VariableReference.TryParse(ShellSource.FromBytes(name.ToValue().Bytes), out var reference)
                && _owner.TryResolveWholeName(reference.Name, out var resolved)
                && VariableReference.TryParse(ShellSource.FromBytes(resolved.ToValue().Bytes), out var target))
                RetainBinding(target.Name);
        }

        /// <summary>Captures a promoted binding before function-local restoration can replace it.</summary>
        public void RetainBinding(BindingName name)
        {
            if (!_prefixes.Contains(name))
                return;

            _retainedNames.Add(name);
            if (_owner.Scalars.TryGetValue(name, out var value))
            {
                // A scalar promotion updates element zero of a caller array, not its whole binding.
                _retained.CopyBindingFrom(_saved, name);
                _retained.SetResolvedValuePreservingArray(name, value);
                _retained.SetAttribute(name, BindingAttributes.Readonly, _owner.IsReadonlyBinding(name));
                _retained.SetExported(name, _owner.IsExported(name));
            }
            else
            {
                _retained.CopyBindingFrom(_owner, name);
            }
        }

        public void Dispose()
        {
            foreach (var name in _prefixes)
                _owner.CopyBindingFrom(_retainedNames.Contains(name) ? _retained : _saved, name);
        }
    }

    /// <summary>
    /// Tracks local declaration identity and the function or temporary command scope that owns it.
    /// </summary>
    internal sealed class LocalBindings(Variables owner, LocalBindings? parent, CommandBindings? prefixes)
    {
        private readonly Variables _owner = owner;
        private readonly LocalBindings? _parent = parent;
        // Borrow live prefix ownership so an exposed caller no longer supplies prefix exports.
        private readonly CommandBindings? _prefixes = prefixes;
        private readonly HashSet<BindingName> _names = new();
        private readonly Variables _saved = new();
        private readonly HashSet<BindingName> _unsetNames = new();
        private readonly HashSet<BindingName> _declaredPrefixes = new();
        private readonly HashSet<BindingName> _promotedPrefixes = new();

        internal long SavedMemoryBytes => _saved.EstimatedMemoryBytes;

        public bool Owns(BindingName name) => _parent == null && _names.Contains(name);

        internal bool OwnsDeclaration(BindingName name) => ReferenceEquals(DeclarationOwner(name), this) && Declares(name);

        /// <summary>A local nameref can shadow an unset global reference with an ordinary local value.</summary>
        internal void ShadowNamerefTarget(BindingName name, Variables active, EvaluationMemory memory)
        {
            var owner = DeclarationOwner(name);
            owner.RememberDeclaration(name, active, memory);
            active.ClearBinding(name);
            if (owner.IsScriptPrefix(name)) owner._declaredPrefixes.Add(name);
        }

        internal Variables? ExportSnapshot => _parent == null ? _saved : null;
        internal IReadOnlySet<BindingName> SavedNames => _names;
        internal IReadOnlySet<BindingName> PromotedPrefixes => _promotedPrefixes;

        internal Variables HiddenOwner(BindingName name, Variables visible) => _parent != null
            ? _parent.HiddenOwner(name, visible) : _names.Contains(name) ? _saved : visible;

        internal void Publish(BindingName name, Variables command)
        {
            if (!ReferenceEquals(_owner, command))
                _owner.CopyBindingFrom(command, name);
        }

        public bool Declares(BindingName name)
        {
            var owner = DeclarationOwner(name);
            return owner._names.Contains(name)
                && (!owner.IsScriptPrefix(name) || owner._declaredPrefixes.Contains(name));
        }

        public bool IsDeclaredPrefix(BindingName name)
        {
            var owner = DeclarationOwner(name);
            return owner._prefixes?.Contains(name) == true && owner.Declares(name);
        }

        /// <summary>Records unset locals and prefixes so redeclaration does not revive their caller promotion.</summary>
        public void RecordUnset(BindingName name)
        {
            var owner = DeclarationOwner(name);
            owner._promotedPrefixes.Remove(name);
            if (owner._names.Contains(name) || owner._prefixes?.Contains(name) == true)
                owner._unsetNames.Add(name);
        }

        public bool BlocksPromotion(BindingName name)
        {
            var owner = DeclarationOwner(name);
            return owner._names.Contains(name) && owner._unsetNames.Contains(name);
        }

        /// <summary>A fresh attribute can promote a recreated temporary binding, but not a recreated local.</summary>
        public void RecordAttributePromotion(BindingName name)
        {
            var owner = DeclarationOwner(name);
            if (!owner.Declares(name))
                owner._unsetNames.Remove(name);
        }

        /// <summary>A nested command's promoted prefix replaces the enclosing binding's canceled lifetime.</summary>
        public void RecordPrefixPromotion(BindingName name)
        {
            var owner = DeclarationOwner(name);
            owner._unsetNames.Remove(name);
            owner._promotedPrefixes.Add(name);
        }

        private LocalBindings DeclarationOwner(BindingName name) =>
            _parent != null && !IsScriptPrefix(name) ? _parent.DeclarationOwner(name) : this;

        private bool IsScriptPrefix(BindingName name) => _parent != null && _prefixes?.Contains(name) == true;

        public bool TryDeclare(
            Variables active,
            Variables command,
            VariableOperand operand,
            IReadOnlySet<BindingName>? prefixes,
            ArrayAssignmentKind arrayKind,
            bool isNameref,
            EvaluationMemory memory)
        {
            var hasAssignment = operand.Kind == VariableOperandKind.Assignment;
            var reference = operand.Reference;
            if (hasAssignment && reference.IsAggregate)
                return false;

            var name = reference.Name;
            // Adding -n without an initializer changes only the temporary prefix binding.
            if (isNameref && !hasAssignment && prefixes != null && prefixes.Contains(name))
                return TryUpdateBinding(command);

            // A new local shadows an outer nameref. Redeclaring a local nameref
            // instead declares its referent in this frame, preserving caller bindings.
            var declarationOwner = DeclarationOwner(name);
            if (!isNameref && declarationOwner._names.Contains(name) && command.NamerefTargets.ContainsKey(name))
            {
                if (!command.TryResolveBinding(name, selectOwner: null, out var binding))
                    return false;
                var resolved = binding.Name;
                if (binding.IsUnsetNameref)
                {
                    if (arrayKind != ArrayAssignmentKind.Auto)
                    {
                        // Unlike Bash 5.2's invalid nameref/array hybrid, conversion removes the reference.
                        if (!command.TryRemoveNamerefAttribute(name))
                            return false;
                    }
                    else
                    {
                        // An unset nameref has no referent to localize: initialize the reference itself.
                        if (hasAssignment && operand.Value.IsEmpty)
                            return false;
                        isNameref = true;
                    }
                }
                else
                {
                    if (binding.Reference.IsAggregate)
                        return false;
                    if (binding.Reference.Kind != VariableReferenceKind.Name)
                        resolved = BindingNameExpansion.FromValue(binding.Reference.ToValue(memory), memory);
                    // A declaration localizes the complete target spelling (even "a[1]"),
                    // not an element of the array "a". Only the operand supplies a selector.
                    name = resolved;
                    // A function-local nameref can target a surrounding eval/source prefix.
                    declarationOwner = DeclarationOwner(name);
                }
            }

            var inheritPrefixExport = !command.HasBinding(name) && !declarationOwner.Declares(name)
                && declarationOwner._prefixes?.Contains(name) == true;
            var savedBinding = active;
            List<CommandBindings>? unsetPrefixes = null;
            // The first local after an unset eval/source prefix belongs to its enclosing scope.
            while (declarationOwner.IsScriptPrefix(name)
                && declarationOwner._unsetNames.Contains(name) && !declarationOwner._declaredPrefixes.Contains(name)
                && declarationOwner._parent is { } enclosing && declarationOwner._prefixes is { } scriptPrefixes)
            {
                (unsetPrefixes ??= new()).Add(scriptPrefixes);
                savedBinding = scriptPrefixes.SavedVariables;
                declarationOwner = enclosing.DeclarationOwner(name);
            }

            var updated = command;
            if (declarationOwner._names.Contains(name))
            {
                // A helper can unset this local without ending its namespace. An explicit
                // redeclaration starts a fresh local lifetime rather than reviving its promotion.
                if (!command.HasBinding(name))
                    declarationOwner._promotedPrefixes.Remove(name);
                if (unsetPrefixes != null)
                {
                    updated = new Variables();
                    updated.CopyBindingFrom(prefixes?.Contains(name) == true ? command : savedBinding, name);
                }
                if (!TryUpdateBinding(updated))
                    return false;
            }
            else
            {
                // Shadow the binding itself, not the target of an outer nameref.
                if (command.IsReadonlyBinding(name) || unsetPrefixes != null && savedBinding.IsReadonlyBinding(name))
                    return false;

                var local = new Variables();
                if (prefixes != null && prefixes.Contains(name))
                    local.CopyBindingFrom(command, name);
                else if (savedBinding.HasAttribute(name, BindingAttributes.Exported))
                    local.SetAttribute(name, BindingAttributes.Exported, true);
                if (!TryUpdateBinding(local))
                    return false;

                declarationOwner.RememberDeclaration(name, savedBinding, memory);
                updated = local;
            }

            // A first declaration inherits the prefix export; an unset local retains its own export state.
            if (inheritPrefixExport)
                updated.SetExported(name, true);

            if (!ReferenceEquals(updated, command))
                command.CopyBindingFrom(updated, name);
            if (unsetPrefixes != null)
                foreach (var prefix in unsetPrefixes)
                    prefix.ExposeBinding(name, command);

            // A prefix on local/declare itself does not contain the declaration.
            // eval/source prefix lifetimes are represented by WithTemporaryPrefixes instead.
            if (!ReferenceEquals(active, command))
                active.CopyBindingFrom(command, name);
            if (declarationOwner.IsScriptPrefix(name))
                declarationOwner._declaredPrefixes.Add(name);
            return true;

            bool TryUpdateBinding(Variables variables)
            {
                if (isNameref)
                {
                    if (reference.Kind != VariableReferenceKind.Name
                        || arrayKind != ArrayAssignmentKind.Auto)
                        return false;

                    if (!hasAssignment && variables.NamerefTargets.ContainsKey(name))
                        return true;
                    if (variables.IndexedArrays.ContainsKey(name) || variables.AssociativeArrays.ContainsKey(name))
                        return false;

                    var target = hasAssignment ? operand.Value
                        : variables.Scalars.GetValueOrDefault(name, ShellValue.Empty);
                    memory.Charge(checked(4L * target.Bytes.Length));
                    if (!variables.TrySetNameref(name, target, memory))
                        return false;
                    return true;
                }

                if (!variables.TryResolveWholeName(name, out var resolved) || resolved.IsEmpty)
                    return false;

                var existingKind = variables.IndexedArrays.ContainsKey(resolved)
                    ? ArrayAssignmentKind.Indexed
                    : variables.AssociativeArrays.ContainsKey(resolved)
                        ? ArrayAssignmentKind.Associative
                        : ArrayAssignmentKind.Auto;
                if (arrayKind != ArrayAssignmentKind.Auto
                    && existingKind != ArrayAssignmentKind.Auto && arrayKind != existingKind)
                    return false;

                var kind = arrayKind == ArrayAssignmentKind.Auto ? existingKind : arrayKind;
                if (kind == ArrayAssignmentKind.Auto && reference.Kind != VariableReferenceKind.Name)
                    kind = ArrayAssignmentKind.Indexed;
                if (!hasAssignment && (kind == ArrayAssignmentKind.Auto || kind == existingKind))
                {
                    variables.DeclareUnassigned(resolved);
                    return true;
                }

                if (hasAssignment && variables.IsReadonly(name))
                    return false;

                var index = reference.Kind == VariableReferenceKind.Element ? reference.Subscript.ToValue() : ShellValue.FromText("0");
                if (kind == ArrayAssignmentKind.Auto)
                {
                    variables.SetResolvedScalar(resolved, operand.Value);
                    return true;
                }
                // Without an initializer, a selector declares the array type but is not evaluated.
                if (hasAssignment && (kind == ArrayAssignmentKind.Indexed
                        ? !IndexedArrayIndex.TryParse(index.Bytes.Span, out _)
                        : !AssociativeArrayKey.TryCreate(index, out _)))
                    return false;

                if (existingKind == ArrayAssignmentKind.Auto
                    && !variables.TryDeclareResolvedArray(resolved,
                        kind == ArrayAssignmentKind.Indexed ? ArrayKind.Indexed : ArrayKind.Associative,
                        ScalarArrayPromotion.DiscardValue))
                    return false;

                return !hasAssignment || variables.TrySetResolvedArrayElement(resolved, index, operand.Value,
                    kind == ArrayAssignmentKind.Indexed ? ArrayKind.Indexed : ArrayKind.Associative);
            }
        }

        private void RememberDeclaration(BindingName name, Variables active, EvaluationMemory memory)
        {
            if (_names.Contains(name))
                return;
            memory.Charge(active.EstimateBindingBytes(name));
            _saved.CopyBindingFrom(active, name);
            _names.Add(name);
        }

        /// <summary>Scopes declaration identity for eval/source; their values already share the caller's store.</summary>
        public LocalBindings WithTemporaryPrefixes(CommandBindings prefixes)
        {
            var bindings = new LocalBindings(_owner, this, prefixes);
            // Existing prefix bindings can be redeclared, but must not become function locals.
            bindings._names.UnionWith(prefixes.PrefixNames);
            return bindings;
        }

        /// <summary>Copies local state, reconnecting borrowed script scopes to their cloned owner and prefixes.</summary>
        public LocalBindings Fork(Variables variables, LocalBindings? enclosing, CommandBindings? prefixes)
        {
            var child = new LocalBindings(variables, _parent == null ? null : enclosing, prefixes);
            child._names.UnionWith(_names);
            child._unsetNames.UnionWith(_unsetNames);
            child._declaredPrefixes.UnionWith(_declaredPrefixes);
            child._promotedPrefixes.UnionWith(_promotedPrefixes);
            foreach (var name in _names)
                child._saved.CopyBindingFrom(_saved, name);
            return child;
        }

        /// <summary>Called only when the owning function returns, never on a discarded child fork.</summary>
        public void Restore()
        {
            foreach (var name in _names)
                if (!_promotedPrefixes.Contains(name) || !_owner.HasBinding(name))
                    _owner.CopyBindingFrom(_saved, name);
        }
    }

    /// <summary>
    /// Attempts to get a scalar value, resolving namerefs when present.
    /// </summary>
    public bool TryGetScalar(BindingName name, out ShellValue value)
    {
        if (name.IsEmpty)
        {
            value = ShellValue.Empty;
            return false;
        }

        if (!TryResolveWholeName(name, out var resolved))
        {
            value = ShellValue.Empty;
            return false;
        }

        if (Scalars.TryGetValue(resolved, out var stored))
        {
            value = stored;
            return true;
        }

        value = ShellValue.Empty;
        return false;
    }

    /// <summary>Reads a scalar or array element zero for expansion, after nameref resolution.</summary>
    internal bool TryGetExpansionValue(BindingName name, out ShellValue value)
    {
        if (TryResolveWholeName(name, out var resolved) && !resolved.IsEmpty)
        {
            if (Scalars.TryGetValue(resolved, out var scalar))
            {
                value = scalar;
                return true;
            }
            if (IndexedArrays.TryGetValue(resolved, out var indexed)
                && IndexedArrayIndex.TryCreate(0, out var index)
                && indexed.TryGet(index, out var indexedValue))
            {
                value = indexedValue;
                return true;
            }
            if (AssociativeArrays.TryGetValue(resolved, out var associative)
                && AssociativeArrayKey.TryCreate(ShellValue.FromText("0"), out var key)
                && associative.TryGet(key, out var associativeValue))
            {
                value = associativeValue;
                return true;
            }
        }
        value = ShellValue.Empty;
        return false;
    }

    /// <summary>Expansion assignments update element zero without replacing an existing array.</summary>
    /// <remarks>The expansion caller checks readonly status before applying or forwarding the write.</remarks>
    internal void SetExpansionValueIgnoringReadonly(BindingName name, ShellValue value, EvaluationMemory memory)
    {
        if (!TryResolveWholeName(name, out var resolved))
            return;

        if (resolved.IsEmpty)
        {
            _ = TrySetScalar(name, value, memory);
            return;
        }
        SetResolvedValuePreservingArray(resolved, value);
    }

    /// <summary>Writes a resolved scalar or array element zero; callers own readonly checks.</summary>
    internal void SetResolvedValuePreservingArray(BindingName resolved, ShellValue value)
    {
        if (IndexedArrays.ContainsKey(resolved))
            _ = TrySetResolvedArrayElement(resolved, ShellValue.FromText("0"), value, ArrayKind.Indexed);
        else if (AssociativeArrays.ContainsKey(resolved))
            _ = TrySetResolvedArrayElement(resolved, ShellValue.FromText("0"), value, ArrayKind.Associative);
        else
            SetResolvedScalar(resolved, value);
    }

    /// <summary>
    /// Attempts to set a scalar value, resolving namerefs and enforcing readonly constraints.
    /// </summary>
    public bool TrySetScalar(BindingName name, ShellValue value) => TrySetScalar(name, value, memory: null);

    internal bool TrySetScalar(BindingName name, ShellValue value, EvaluationMemory? memory)
    {
        if (!TryResolveWritableName(name, out var resolved))
            return false;

        if (resolved.IsEmpty)
        {
            if (!NamerefTargets.TryGetValue(name, out var target) || !target.IsEmpty || value.IsEmpty)
                return false;
            memory?.Charge(checked(4L * value.Bytes.Length));
            return TrySetNameref(name, value, memory);
        }
        SetResolvedScalar(resolved, value);
        return true;
    }

    /// <summary>
    /// Sets a scalar value while bypassing readonly checks, still honoring nameref resolution.
    /// </summary>
    internal void SetScalarIgnoringReadonly(BindingName name, ShellValue value)
    {
        if (name.IsEmpty)
            return;

        if (!TryResolveWholeName(name, out var resolved))
            return;

        SetResolvedScalar(resolved, value);
    }

    internal bool TrySetResolvedScalar(BindingName name, ShellValue value, EvaluationMemory memory)
    {
        if (name.IsEmpty || IsReadonly(name))
            return false;
        if (NamerefTargets.TryGetValue(name, out var target) && target.IsEmpty)
            return TrySetScalar(name, value, memory);
        SetResolvedScalar(name, value);
        return true;
    }

    /// <summary>Shadows a command-prefix name after its selected destination passed readonly validation.</summary>
    internal void SetTemporaryScalar(BindingName name, ShellValue value)
    {
        ClearBinding(name);
        SetResolvedScalar(name, value);
    }

    /// <summary>Replaces a resolved binding; callers own name and readonly validation.</summary>
    private void SetResolvedScalar(BindingName resolved, ShellValue value)
    {
        if (TryGetBinding(resolved, out var slot) && slot is ScalarSlot scalar)
        {
            using var memory = TrackBindingMemory(resolved);
            scalar.Value = value;
        }
        else
            ReplaceValue(resolved, new ScalarSlot(value));
    }

    /// <summary>
    /// Attempts to unset a name, removing scalar/array/nameref as dictated by flags.
    /// </summary>
    public bool TryUnset(BindingName name) => TryUnset(name, UnsetFlags.None);

    public bool TryUnset(BindingName name, UnsetFlags flags)
    {
        if (name.IsEmpty)
            return false;

        if (flags == UnsetFlags.NamerefOnly)
        {
            if (IsReadonlyBinding(name))
                return false;
            if (RemoveValue<NamerefSlot>(name))
                SetAttribute(name, BindingAttributes.Exported, false);
            return true;
        }

        // Follow a referent when possible; an unbound or cyclic nameref removes the named binding.
        // Element selectors require scoped evaluation, not deletion of the alias itself.
        if (TryResolveBinding(name, selectOwner: null, out var binding)
            && binding.Reference.Kind != VariableReferenceKind.Name)
            return false;
        if (TryResolveWholeName(name, out var resolved) && !resolved.IsEmpty)
            name = resolved;

        if (IsReadonly(name))
            return false;

        if (flags == UnsetFlags.None)
            ClearBinding(name);
        else
        {
            SetAttribute(name, BindingAttributes.Exported, false);
            if (TryGetBinding(name, out var slot) && (slot switch
                {
                    ScalarSlot or UnassignedSlot => flags.HasFlag(UnsetFlags.ScalarOnly),
                    IndexedSlot or AssociativeSlot => flags.HasFlag(UnsetFlags.ArrayOnly),
                    NamerefSlot => flags.HasFlag(UnsetFlags.NamerefOnly),
                    _ => false
                }))
                ClearBinding(name);
        }

        return true;
    }

    /// <summary>
    /// Gets a detached legacy array view after resolving namerefs. An indexed
    /// array beyond the dense view's range returns false without materializing holes.
    /// </summary>
    /// <remarks>Indexed views allocate through the maximum present index, not just present elements.</remarks>
    public bool TryGetArray(BindingName name, out ArrayValue value)
    {
        value = default;
        if (name.IsEmpty)
            return false;

        if (!TryResolveWholeName(name, out var resolved))
            return false;

        if (IndexedArrays.TryGetValue(resolved, out var indexed))
        {
            if (indexed.MaxIndex >= int.MaxValue)
                return false;

            value = new ArrayValue(ArrayKind.Indexed, indexed.CaptureDenseValues(), null);
            return true;
        }

        if (AssociativeArrays.TryGetValue(resolved, out var associative))
        {
            value = new ArrayValue(
                ArrayKind.Associative,
                null,
                associative.CaptureValues());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Captures the legacy dense shape used by the public evaluation inspection API.
    /// </summary>
    internal Dictionary<BindingName, List<ShellValue>> CaptureDenseIndexedArrays()
    {
        var result = new Dictionary<BindingName, List<ShellValue>>();
        foreach (var entry in IndexedArrays)
            result.Add(entry.Key, entry.Value.CaptureDenseValues());

        return result;
    }

    /// <summary>
    /// Captures detached associative arrays; mutating the dictionaries does not change stored bindings.
    /// </summary>
    internal Dictionary<BindingName, Dictionary<ShellValue, ShellValue>> CaptureAssociativeArrays()
    {
        var result = new Dictionary<BindingName, Dictionary<ShellValue, ShellValue>>();
        foreach (var entry in AssociativeArrays)
            result.Add(entry.Key, entry.Value.CaptureValues());

        return result;
    }

    /// <summary>
    /// Attempts to set an element by byte key or non-negative decimal indexed position.
    /// </summary>
    public bool TrySetArrayElement(BindingName name, ShellValue index, ShellValue value, ArrayKind kind)
    {
        return TryResolveWritableName(name, out var resolved)
            && TrySetResolvedArrayElement(resolved, index, value, kind);
    }

    private bool TrySetResolvedArrayElement(BindingName resolved, ShellValue index, ShellValue value, ArrayKind kind) =>
        kind == ArrayKind.Indexed
            ? IndexedArrayIndex.TryParse(index.Bytes.Span, out var position)
                && TrySetResolvedArrayElement(resolved, position, value)
            : AssociativeArrayKey.TryCreate(index, out var key) && TrySetResolvedArrayElement(resolved, key, value);

    /// <summary>
    /// Attempts to assign an entire array from a sequence of values.
    /// </summary>
    public bool TryAssignArray(BindingName name, IReadOnlyList<ShellValue> values, ArrayKind kind)
    {
        if (!TryResolveWritableName(name, out var resolved))
            return false;

        return TryAssignResolvedArray(resolved, values, kind);
    }

    /// <summary>Replaces an already selected array binding without following its nameref again.</summary>
    internal bool TryAssignResolvedArray(BindingName resolved, IReadOnlyList<ShellValue> values, ArrayKind kind)
    {
        if (resolved.IsEmpty || IsReadonly(resolved))
            return false;

        if (kind == ArrayKind.Indexed)
            return TryAssignResolvedIndexedArray(resolved, IndexedArray.FromSequential(values));

        var array = new AssociativeArray(isInitialized: true);
        for (var index = 0; index < values.Count; index += 2)
        {
            if (!AssociativeArrayKey.TryCreate(values[index], out var key))
                return false;

            var value = index + 1 < values.Count ? values[index + 1] : ShellValue.Empty;
            array.Set(key, value);
        }

        ReplaceValue(resolved, new AssociativeSlot(array));
        return true;
    }

    /// <summary>Publishes an already admitted indexed array without copying its storage.</summary>
    internal bool TryAssignResolvedIndexedArray(BindingName resolved, IndexedArray array)
    {
        if (resolved.IsEmpty || IsReadonly(resolved))
            return false;
        ReplaceValue(resolved, new IndexedSlot(array));
        return true;
    }

    /// <summary>
    /// Applies a parsed operand or an already selected literal binding used by builtin output.
    /// </summary>
    internal bool TryApplyAssignment(VariableReference reference, ShellValue value, EvaluationMemory? memory)
    {
        if (reference.Kind != VariableReferenceKind.Name)
            return TryAssignArrayElement(reference.Name, reference.Subscript.ToValue(), value, ArrayAssignmentKind.Auto);

        if (!TryResolveBinding(reference.Name, selectOwner: null, out var binding)
            || binding.Reference.Kind != VariableReferenceKind.Name
            || IsReadonly(reference.Name) || binding.Owner.IsReadonly(binding.Name))
            return false;
        if (binding.IsUnsetNameref)
            return TrySetScalar(reference.Name, value, memory);
        binding.Owner.SetResolvedValuePreservingArray(binding.Name, value);
        return true;
    }

    /// <summary>
    /// Attempts to assign a single array element for the given base name and index.
    /// </summary>
    internal bool TryAssignArrayElement(BindingName baseName, ShellValue index, ShellValue value, ArrayAssignmentKind kind)
    {
        if (kind == ArrayAssignmentKind.Auto)
        {
            var resolved = ResolveNamerefName(baseName);
            // Existing bindings own their kind, even when an associative key happens to be numeric.
            kind = AssociativeArrays.ContainsKey(resolved) ? ArrayAssignmentKind.Associative
                : IndexedArrays.ContainsKey(resolved) || IndexedArrayIndex.TryParse(index.Bytes.Span, out _)
                    ? ArrayAssignmentKind.Indexed : ArrayAssignmentKind.Associative;
        }

        return TrySetArrayElement(
            baseName,
            index,
            value,
            kind == ArrayAssignmentKind.Indexed ? ArrayKind.Indexed : ArrayKind.Associative);
    }

    /// <summary>Writes a validated indexed position, retaining a promoted scalar at position zero.</summary>
    internal bool TrySetArrayElement(BindingName name, IndexedArrayIndex index, ShellValue value)
    {
        return TryResolveWritableName(name, out var resolved) && TrySetResolvedArrayElement(resolved, index, value);
    }

    internal bool TrySetResolvedArrayElement(BindingName resolved, IndexedArrayIndex index, ShellValue value)
    {
        if (resolved.IsEmpty || AssociativeArrays.ContainsKey(resolved))
            return false;

        var array = GetOrCreateIndexedArray(resolved);
        using var memory = TrackBindingMemory(resolved);
        array.Set(index, value);
        return true;
    }

    private IndexedArray GetOrCreateIndexedArray(BindingName resolved)
    {
        if (!IndexedArrays.TryGetValue(resolved, out var array))
        {
            array = new IndexedArray(isInitialized: false);
            if (Scalars.TryGetValue(resolved, out var scalar))
                array.Set(default, scalar);
            ReplaceValue(resolved, new IndexedSlot(array));
        }

        return array;
    }

    /// <summary>Writes a validated associative key without changing an existing array's kind.</summary>
    internal bool TrySetArrayElement(BindingName name, AssociativeArrayKey key, ShellValue value)
    {
        return TryResolveWritableName(name, out var resolved) && TrySetResolvedArrayElement(resolved, key, value);
    }

    internal bool TrySetResolvedArrayElement(BindingName resolved, AssociativeArrayKey key, ShellValue value)
    {
        if (resolved.IsEmpty || IndexedArrays.ContainsKey(resolved))
            return false;

        if (!AssociativeArrays.TryGetValue(resolved, out var associative))
        {
            associative = new AssociativeArray(isInitialized: false);
            ReplaceValue(resolved, new AssociativeSlot(associative));
        }

        using var memory = TrackBindingMemory(resolved);
        associative.Set(key, value);
        return true;
    }

    private bool TryResolveWritableName(BindingName name, out BindingName resolved)
    {
        resolved = BindingName.Empty;
        return name.IsIdentifier && TryResolveWholeName(name, out resolved)
            && !IsReadonly(resolved) && !IsReadonly(name);
    }

    /// <summary>
    /// Returns true when a scalar variable exists with the provided name after nameref resolution.
    /// </summary>
    internal bool ContainsScalar(BindingName name) =>
        TryResolveWholeName(name, out var resolved) && Scalars.ContainsKey(resolved);

    /// <summary>
    /// Resolves whole bindings; returns empty for cycles, unset references or elements requiring scoped selection.
    /// </summary>
    internal BindingName ResolveNamerefName(BindingName name)
    {
        if (name.IsEmpty)
            return BindingName.Empty;

        return TryResolveWholeName(name, out var resolved) ? resolved : BindingName.Empty;
    }

    /// <summary>Renders the raw binding, without following namerefs or changing attributes.</summary>
    /// <remarks>False leaves an incomplete row; the caller must discard it.</remarks>
    internal bool TryAppendVariable(BindingName name, ByteMemoryBuilder output, VariableDisplayStyle style)
    {
        TryGetBinding(name, out var slot);
        if (style == VariableDisplayStyle.Assignment && slot is not (null or AttributeSlot) && !IsAssigned(slot))
            return true;
        if (style != VariableDisplayStyle.Assignment)
        {
            if (!output.TryAppend("declare -"u8))
                return false;
            var flagsStart = output.Length;
            var attributes = slot?.Attributes ?? BindingAttributes.None;
            if (slot is IndexedSlot && !output.TryAppend((byte)'a')
                || slot is AssociativeSlot && !output.TryAppend((byte)'A')
                || slot is NamerefSlot && !output.TryAppend((byte)'n')
                || (attributes & BindingAttributes.Readonly) != 0 && !output.TryAppend((byte)'r')
                || (attributes & BindingAttributes.Exported) != 0 && !output.TryAppend((byte)'x'))
                return false;
            if (flagsStart == output.Length && !output.TryAppend((byte)'-'))
                return false;
            if (!output.TryAppend((byte)' '))
                return false;
        }
        if (!output.CanAppend(name.ByteLength)
            || !output.TryAppend(name.ToValue().Bytes.Span))
            return false;

        var valueStyle = style == VariableDisplayStyle.Declaration ? ShellQuoteStyle.DoubleQuoted : ShellQuoteStyle.AssignmentValue;
        if (style != VariableDisplayStyle.DeclarationName)
        {
            var appended = slot switch
            {
                ScalarSlot scalar => AppendValue(scalar.Value, valueStyle),
                NamerefSlot reference when !reference.Value.IsEmpty => AppendValue(reference.Value.Value, valueStyle),
                IndexedSlot { Value.IsInitialized: true } indexed =>
                    AppendArray(indexed.Value.PresentKeys.Select(ShellValue.FromText), indexed.Value.PresentValues, ArrayKind.Indexed),
                AssociativeSlot { Value.IsInitialized: true } associative =>
                    AppendArray(associative.Value.PresentKeys, associative.Value.PresentValues, ArrayKind.Associative),
                _ => true
            };
            if (!appended)
                return false;
        }
        return output.TryAppend((byte)'\n');

        bool AppendValue(ShellValue value, ShellQuoteStyle quoteStyle) => output.TryAppend((byte)'=')
            && ShellArgumentEscaping.TryAppendQuoted(output, value.Bytes.Span, quoteStyle);

        bool AppendArray(IEnumerable<ShellValue> keys, IEnumerable<ShellValue> values, ArrayKind kind)
        {
            if (!output.TryAppend("=("u8))
                return false;
            var first = true;
            // Both live views share their container's ordering; no dense materialization or second value lookup.
            foreach (var (key, value) in keys.Zip(values))
            {
                if (!first && kind == ArrayKind.Indexed && !output.TryAppend((byte)' '))
                    return false;
                first = false;
                if (!output.TryAppend((byte)'[')
                    || !ShellArgumentEscaping.TryAppendQuoted(output, key.Bytes.Span,
                        kind == ArrayKind.Indexed ? ShellQuoteStyle.Backslash : ShellQuoteStyle.AssociativeKey)
                    || !output.TryAppend((byte)']') || !AppendValue(value, ShellQuoteStyle.DoubleQuoted))
                    return false;
                if (kind == ArrayKind.Associative && !output.TryAppend((byte)' '))
                    return false;
            }
            return output.TryAppend((byte)')');
        }
    }

    /// <summary>Copies one complete binding, including its value kind and attributes.</summary>
    internal void CopyBindingFrom(Variables source, BindingName name)
    {
        if (source.TryGetBinding(name, out var slot))
            StoreBinding(name, slot.Clone());
        else
            ClearBinding(name);
    }

    /// <summary>
    /// Stores an unset or syntactically valid target without evaluating its selector.
    /// </summary>
    public bool TrySetNameref(BindingName name, ShellValue target) => TrySetNameref(name, target, memory: null);

    internal bool TrySetNameref(BindingName name, ShellValue target, EvaluationMemory? memory)
    {
        if (!name.IsIdentifier)
            return false;

        // Rebinding changes the reference, not its previous target.
        if (HasAttribute(name, BindingAttributes.Readonly) || IndexedArrays.ContainsKey(name) || AssociativeArrays.ContainsKey(name))
            return false;

        VariableReference? reference = null;
        BindingName? literalName = null;
        if (!target.IsEmpty)
        {
            if (!VariableReference.TryParse(ShellSource.FromBytes(target.Bytes), memory, out var parsed))
                return false;
            reference = parsed;
            if (parsed.Kind != VariableReferenceKind.Name)
            {
                // Local declarations can create a binding whose literal name includes the selector.
                memory?.Charge(checked(4L * target.Bytes.Length));
                literalName = BindingName.FromValue(target);
            }
        }

        ReplaceValue(name, new NamerefSlot(new NamerefTarget(target, reference, literalName)));
        return true;
    }

    internal bool TryRemoveNamerefAttribute(BindingName name) =>
        !HasAttribute(name, BindingAttributes.Readonly) && RemoveValue<NamerefSlot>(name);

    internal void SetReadonlyBinding(BindingName name) => SetAttribute(name, BindingAttributes.Readonly, true);

    internal bool TryResolveBinding(BindingName name, Func<BindingName, Variables>? selectOwner, out VariableBinding binding)
        => TryResolveBinding(name, selectOwner, BindingResolution.Referent, out binding);

    internal bool TryResolveBinding(BindingName name, Func<BindingName, Variables>? selectOwner,
        BindingResolution resolution, out VariableBinding binding)
    {
        binding = new VariableBinding(VariableReference.ForName(name), this);
        if (name.IsEmpty)
            return false;
        HashSet<VariableBinding>? visited = null;
        while (binding.Reference.Kind == VariableReferenceKind.Name
            && binding.Owner.NamerefTargets.TryGetValue(binding.Name, out var next) && next.Reference is { } reference)
        {
            visited ??= [];
            if (!visited.Add(binding))
                return false;
            var owner = selectOwner?.Invoke(reference.Name) ?? this;
            if (next.LiteralName is { } literalName)
            {
                var literalOwner = selectOwner?.Invoke(literalName) ?? this;
                if (literalOwner.HasBinding(literalName))
                {
                    reference = VariableReference.ForName(literalName);
                    owner = literalOwner;
                }
            }
            var nextBinding = new VariableBinding(reference, owner);
            if (resolution == BindingResolution.LastNameref
                && (reference.Kind != VariableReferenceKind.Name || !nextBinding.Owner.NamerefTargets.ContainsKey(reference.Name)))
                break;
            binding = nextBinding;
        }
        return true;
    }

    internal NamerefAssignmentFailure GetNamerefAssignmentFailure(
        BindingName sourceName, BindingName selectedName, ShellValue value, EvaluationMemory memory)
    {
        if (!TryResolveBinding(selectedName, selectOwner: null, out var binding)
            || !binding.IsUnsetNameref)
            return NamerefAssignmentFailure.None;
        if (binding.Name != sourceName)
            return NamerefAssignmentFailure.IndirectUnsetTarget;
        // Plain names need only byte validation; selectors also require structural parsing.
        var valid = !value.Bytes.Span.Contains((byte)'[')
            ? VariableNames.IsValidVariableName(value.Bytes.Span)
            : VariableReference.TryParse(ShellSource.FromBytes(value.Bytes), memory, out _);
        return valid
            ? NamerefAssignmentFailure.None : NamerefAssignmentFailure.InvalidTarget;
    }

    /// <summary>An explicit array kind replaces the original reference when its chain ends in an unset nameref.</summary>
    internal VariableBinding SelectArrayDeclarationBinding(BindingName name, VariableBinding resolved) =>
        resolved.IsUnsetNameref
            ? new VariableBinding(VariableReference.ForName(name), this) : resolved;

    /// <summary>Changes a declaration's kind without replacing an incompatible array.</summary>
    internal bool TryDeclareArray(BindingName name, ArrayKind kind, ScalarArrayPromotion promotion)
    {
        if (!TryResolveBinding(name, selectOwner: null, out var binding))
            return false;
        var resolved = SelectArrayDeclarationBinding(name, binding).Name;
        if (NamerefTargets.ContainsKey(resolved))
            ClearBinding(resolved);
        return TryDeclareResolvedArray(resolved, kind, promotion);
    }

    /// <summary>Changes the selected binding's array kind, removing its nameref attribute.</summary>
    internal bool TryDeclareResolvedArray(BindingName resolved, ArrayKind kind, ScalarArrayPromotion promotion)
    {
        if (resolved.IsEmpty
            || (kind == ArrayKind.Indexed ? AssociativeArrays.ContainsKey(resolved) : IndexedArrays.ContainsKey(resolved)))
            return false;
        if (NamerefTargets.ContainsKey(resolved) && !TryRemoveNamerefAttribute(resolved))
            return false;
        var discardScalar = promotion == ScalarArrayPromotion.DiscardValue && Scalars.ContainsKey(resolved);
        if (kind == ArrayKind.Indexed)
        {
            if (discardScalar)
                ReplaceValue(resolved, new IndexedSlot(new IndexedArray(isInitialized: true)));
            else
                _ = GetOrCreateIndexedArray(resolved);
        }
        else
        {
            if (discardScalar)
                ReplaceValue(resolved, new AssociativeSlot(new AssociativeArray(isInitialized: true)));
            else if (!AssociativeArrays.ContainsKey(resolved))
            {
                var array = new AssociativeArray(isInitialized: false);
                if (Scalars.TryGetValue(resolved, out var scalar) && AssociativeArrayKey.TryCreate(ShellValue.FromText("0"), out var key))
                    array.Set(key, scalar);
                ReplaceValue(resolved, new AssociativeSlot(array));
            }
        }
        return true;
    }

    /// <summary>Replaces a reference and its attributes when a declaration explicitly creates an array.</summary>
    internal void RecreateDeclarationArray(BindingName name, ArrayKind kind)
    {
        ClearBinding(name);
        _ = TryDeclareResolvedArray(name, kind, ScalarArrayPromotion.DiscardValue);
    }

    /// <summary>
    /// Attempts to get a nameref mapping.
    /// </summary>
    public bool TryGetNameref(BindingName name, out ShellValue target)
    {
        if (name.IsEmpty)
        {
            target = ShellValue.Empty;
            return false;
        }

        if (NamerefTargets.TryGetValue(name, out var stored))
        {
            target = stored.Value;
            return true;
        }

        target = ShellValue.Empty;
        return false;
    }

    /// <summary>
    /// Builds the exported environment list.
    /// </summary>
    public IReadOnlyList<EnvironmentVariable> BuildExportedEnvironment() => BuildExportedEnvironment(ExportedBindings);

    /// <summary>Selects the first assigned export of each name, from innermost to outermost scope.</summary>
    internal static IReadOnlyList<EnvironmentVariable> BuildExportedEnvironment(
        IEnumerable<VariableBinding> bindings)
    {
        var results = new List<EnvironmentVariable>();
        var selected = new HashSet<BindingName>();
        foreach (var binding in bindings)
        {
            var name = binding.Name;
            var scope = binding.Owner;
            if (selected.Contains(name) || !scope.TryGetBinding(name, out var slot)) continue;
            ShellValue value;
            switch (slot)
            {
                case ScalarSlot scalar:
                    value = scalar.Value;
                    break;
                case NamerefSlot reference when !reference.Value.IsEmpty:
                    value = reference.Value.Value;
                    break;
                case IndexedSlot or AssociativeSlot:
                    // Assigned arrays shadow outer exports, but cannot themselves enter the environment.
                    if (IsAssigned(slot)) selected.Add(name);
                    continue;
                default:
                    continue;
            }
            selected.Add(name);
            results.Add(new EnvironmentVariable(name, value));
        }

        return results;
    }

    /// <summary>
    /// Returns true when a name is readonly (after nameref resolution).
    /// </summary>
    public bool IsReadonly(BindingName name)
    {
        if (name.IsEmpty)
            return false;

        if (HasAttribute(name, BindingAttributes.Readonly))
            return true;

        return TryResolveBinding(name, selectOwner: null, out var binding) && HasAttribute(binding.Name, BindingAttributes.Readonly);
    }

    /// <summary>
    /// Marks a name as readonly.
    /// </summary>
    public void SetReadonly(BindingName name)
    {
        if (name.IsEmpty)
            return;

        if (TryResolveWholeName(name, out var resolved))
        {
            SetAttribute(resolved, BindingAttributes.Readonly, true);
            return;
        }

        SetAttribute(name, BindingAttributes.Readonly, true);
    }

    /// <summary>
    /// Returns true when a name is exported.
    /// </summary>
    public bool IsExported(BindingName name) => !name.IsEmpty && HasAttribute(name, BindingAttributes.Exported);

    /// <summary>
    /// Marks or unmarks a name as exported.
    /// </summary>
    public void SetExported(BindingName name, bool exported)
    {
        if (name.IsEmpty)
            return;

        SetAttribute(name, BindingAttributes.Exported, exported);
    }

    /// <summary>
    /// Resolves whole bindings, leaving element selection to the scoped evaluator.
    /// </summary>
    private bool TryResolveWholeName(BindingName name, out BindingName resolved)
    {
        resolved = name;
        if (!TryResolveBinding(name, selectOwner: null, out var binding))
            return false;
        if (binding.Reference.Kind != VariableReferenceKind.Name)
            return false;
        resolved = binding.IsUnsetNameref ? BindingName.Empty : binding.Name;
        return true;
    }
}

/// <summary>
/// Describes the kind of array stored.
/// </summary>
public enum ArrayKind
{
    Indexed,
    Associative
}

/// <summary>
/// Holds an array value (indexed or associative).
/// </summary>
public readonly record struct ArrayValue(
    ArrayKind Kind,
    IReadOnlyList<ShellValue>? Indexed,
    IReadOnlyDictionary<ShellValue, ShellValue>? Associative);

/// <summary>
/// Flags controlling unset behavior.
/// </summary>
[Flags]
public enum UnsetFlags
{
    None = 0,
    ScalarOnly = 1,
    ArrayOnly = 2,
    NamerefOnly = 4
}

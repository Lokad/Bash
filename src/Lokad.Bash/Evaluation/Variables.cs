using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Whether a kind-only local declaration discards an existing scalar value.</summary>
internal enum ScalarArrayPromotion { RetainValue, DiscardValue }
internal enum NamerefAssignmentFailure { None, InvalidTarget, IndirectUnsetTarget }
internal enum ForAssignmentFailure { None, InvalidTarget, Readonly, IncompatibleAttributes }
internal enum BindingResolution { Referent, LastNameref }
internal readonly record struct VariableBinding(VariableReference Reference, Variables Owner)
{
    public BindingName Name => Reference.Name;
    public bool IsUnsetNameref => Reference.Kind == VariableReferenceKind.Name
        && Owner.TryGetNameref(Name, out var target) && target.IsEmpty;
}
internal readonly record struct VariableUnsetTarget(BindingName Name, bool RequiresSelector, BindingName ReadonlyName);

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
    private const int BindingNameSetEntryBytes = 40;
    // Four backing positions per selected name cover hash-table growth and replacement overlap.
    internal const int BindingNameSelectionBytes = 4 * BindingNameSetEntryBytes;
    private const int PositionalEntryBytes = 32;
    internal static readonly BindingName LastArgumentName = "_";
    private static readonly BindingName LineNumberName = "LINENO";

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
    private LocalBindings? _savedLocalBindings;

    // Account bindings as a coarse owner. Shared values can be counted twice; no object identities
    // are registered. Callers keep saved bindings charged through restoration.
    internal static long EstimateValueBytes(ShellValue value) => EstimateValueBytes(value.Bytes.Length);

    internal static long EstimateValueBytes(int length) => length == 0 ? 0
        : ValueHeaderBytes + (long)ValueCapacityMultiplier * length;

    internal long EstimatedMemoryBytes
    {
        get
        {
            // The command source already owns borrowed positional/name/IFS bytes. Include it
            // for full child-snapshot admission, without charging that borrowed frame twice.
            var inherited = _commandSource?.Variables.EstimatedMemoryBytes
                ?? checked(PositionalMemoryBytes + EstimateValueBytes(_argumentZero));
            var separators = _commandSource is { } source
                && ReferenceEquals(CachedFieldSeparators, source.Variables.CachedFieldSeparators)
                    ? 0 : CachedFieldSeparators.EstimatedMemoryBytes;
            return checked(_bindingBytes + inherited + separators
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
            LineNumberSlot => EstimateValueBytes(11),
            ScalarSlot scalar => EstimateValueBytes(scalar.Value),
            IndexedSlot indexed => indexed.Value.EstimatedMemoryBytes,
            AssociativeSlot associative => associative.Value.EstimatedMemoryBytes,
            NamerefSlot reference => EstimateValueBytes(reference.Value.Value),
            _ => 0
        });
    }

    internal void InitializeLineNumber() => StoreBinding(LineNumberName, new LineNumberSlot(1));

    internal LineNumberScope EnterLineNumber(int line)
    {
        if (TryGetBinding(LineNumberName, out var slot) && slot is LineNumberSlot dynamic)
        {
            var previous = dynamic.Line;
            dynamic.Line = line;
            return new LineNumberScope(this, previous);
        }
        return default;
    }

    internal readonly struct LineNumberScope(Variables? owner, int previous) : IDisposable
    {
        public void Dispose()
        {
            if (owner != null && owner.TryGetBinding(LineNumberName, out var slot) && slot is LineNumberSlot dynamic)
                dynamic.Line = previous;
        }
    }

    internal readonly struct BindingMemoryChange(Variables owner, BindingName name) : IDisposable
    {
        private readonly long _before = owner.EstimateOwnedBindingBytes(name);
        public void Dispose()
        {
            owner._bindingBytes = checked(owner._bindingBytes + owner.EstimateOwnedBindingBytes(name) - _before);
            owner._savedLocalBindings?.RefreshSavedMemory();
        }
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

    internal bool IsIntegerBinding(BindingName name) => HasAttribute(name, BindingAttributes.Integer);

    internal void SetIntegerBinding(BindingName name, bool enabled) =>
        SetAttribute(name, BindingAttributes.Integer, enabled);

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
    internal ShellValue ArgumentZero
    {
        get => _argumentZero;
        set
        {
            _argumentZero = value;
            _savedLocalBindings?.RefreshSavedMemory();
        }
    }

    /// <summary>Immutable snapshot of the last resolved IFS; completed membership belongs to this variable owner.</summary>
    private FieldSeparators _fieldSeparators = FieldSeparators.Default;
    internal FieldSeparators CachedFieldSeparators
    {
        get => _fieldSeparators;
        set
        {
            _fieldSeparators = value;
            _savedLocalBindings?.RefreshSavedMemory();
        }
    }

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
        clone._getoptsCursor = _getoptsCursor;

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
        private long _retainedCopyCapacity;

        public CommandBindings(Variables owner, Variables prepared, IReadOnlySet<BindingName> prefixes,
            EvaluationMemory memory, CancellationToken cancellationToken)
        {
            // The originals remain live when subsequent commands account only for the
            // visible store. Admit the complete saved set before changing any binding.
            var bytes = checked(384 + (BindingOverheadBytes + (long)BindingNameSetEntryBytes) * prefixes.Count);
            foreach (var name in prefixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes = checked(bytes + owner.EstimateBindingBytes(name));
            }
            memory.Charge(bytes);
            cancellationToken.ThrowIfCancellationRequested();

            _owner = owner;
            _prefixes = new HashSet<BindingName>(prefixes);
            _saved = new Variables();
            _retained = new Variables();
            _retainedNames = new HashSet<BindingName>();
            foreach (var name in prefixes)
            {
                var savedCursor = owner._getoptsCursor;
                var preparedCursor = prepared._getoptsCursor;
                _saved.TransferBindingFrom(owner, name);
                owner.TransferBindingFrom(prepared, name);
                if (name.CachedText == "OPTIND")
                {
                    _saved._getoptsCursor = savedCursor;
                    owner._getoptsCursor = preparedCursor;
                }
                owner.SetAttribute(name, BindingAttributes.Temporary, true);
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
        internal long ForkMemoryBytes => checked(384 + _saved.EstimatedMemoryBytes
            + (long)BindingNameSetEntryBytes * _prefixes.EnsureCapacity(0));
        internal CommandBindings Fork(Variables owner) => new(owner, this);

        /// <summary>Exposes a selected binding and ends this prefix's restoration responsibility.</summary>
        internal void ExposeBinding(BindingName name, Variables visible)
        {
            if (!_prefixes.Contains(name))
                return;
            if (ReferenceEquals(visible, _saved))
                _owner.TransferBindingFrom(_saved, name);
            else
            {
                if (!ReferenceEquals(_owner, visible))
                    _owner.CopyBindingFrom(visible, name);
                _saved.ClearBinding(name);
            }
            _prefixes.Remove(name);
            _retained.ClearBinding(name);
            _retainedNames.Remove(name);
        }

        /// <summary>Direct outputs preserve caller bindings; indirect writes use the command scope.</summary>
        public bool TryApplyOutputAssignment(
            VariableReference reference, ShellValue value, EvaluationMemory memory, CancellationToken cancellationToken)
        {
            var destination = SelectOutputVariables(reference.Name, memory, cancellationToken);
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

        internal bool CanRetainOutput(BindingName name) => _prefixes.Contains(name) && !_saved.NamerefTargets.ContainsKey(name);

        /// <summary>Direct outputs select the caller's saved kind; indirect outputs keep the active binding.</summary>
        internal Variables SelectOutputVariables(BindingName name, EvaluationMemory memory, CancellationToken cancellationToken)
        {
            if (!CanRetainOutput(name))
                return _owner;
            if (!_retainedNames.Contains(name))
                StageRetainedBinding(_saved, name, memory, cancellationToken);
            return _retained;
        }

        internal void CompleteOutput(BindingName name, Variables destination)
        {
            if (ReferenceEquals(destination, _retained))
                _retainedNames.Add(name);
        }

        /// <summary>Selects storage for one selector write, preserving prior outputs and untouched caller elements.</summary>
        internal Variables? RetainSelectorBinding(BindingName name, EvaluationMemory memory, CancellationToken cancellationToken)
        {
            if (!_prefixes.Contains(name))
                return null;
            if (!_retainedNames.Contains(name))
            {
                StageRetainedBinding(_saved, name, memory, cancellationToken);
                _retainedNames.Add(name);
            }
            return _retained;
        }

        /// <summary>Retains an explicit builtin write even when its target was a temporary prefix.</summary>
        public void Retain(BindingName name, EvaluationMemory memory, CancellationToken cancellationToken)
        {
            memory.Charge(checked(4L * name.ByteLength));
            if (VariableReference.TryParse(ShellSource.FromBytes(name.ToValue().Bytes), memory, out var reference)
                && _owner.TryResolveWholeName(reference.Name, out var resolved))
            {
                memory.Charge(checked(4L * resolved.ByteLength));
                if (VariableReference.TryParse(ShellSource.FromBytes(resolved.ToValue().Bytes), memory, out var target))
                    RetainBinding(target.Name, memory, cancellationToken);
            }
        }

        /// <summary>Captures a promoted binding before function-local restoration can replace it.</summary>
        public void RetainBinding(BindingName name, EvaluationMemory memory, CancellationToken cancellationToken)
        {
            if (!_prefixes.Contains(name))
                return;

            if (_owner.Scalars.TryGetValue(name, out var value))
            {
                // A scalar promotion updates element zero of a caller array, not its whole binding.
                var previousCursor = _retained._getoptsCursor;
                _retained.TryGetBinding(name, out var previous);
                StageRetainedBinding(_saved, name, memory, cancellationToken);
                try
                {
                    _retained.SetResolvedValuePreservingArray(name, value, memory);
                }
                catch (MemoryBudgetExceededException)
                {
                    // The staged replacement was never selected. Preserve an earlier output.
                    if (previous != null)
                        _retained.StoreBinding(name, previous);
                    else
                        _retained.ClearBinding(name);
                    _retained._getoptsCursor = previousCursor;
                    throw;
                }
                _retained.SetAttribute(name, BindingAttributes.Readonly, _owner.IsReadonlyBinding(name));
                _retained.SetExported(name, _owner.IsExported(name));
            }
            else
            {
                StageRetainedBinding(_owner, name, memory, cancellationToken);
            }
            _retainedNames.Add(name);
        }

        private void StageRetainedBinding(Variables source, BindingName name,
            EvaluationMemory memory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The surrounding command owns this store and the overlapping replacement.
            // Reuse admitted capacity when a later write stages a binding of the same size.
            var required = checked(_retained.EstimatedMemoryBytes + source.EstimateBindingBytes(name) + BindingNameSetEntryBytes);
            if (required > _retainedCopyCapacity)
            {
                memory.Charge(required - _retainedCopyCapacity);
                _retainedCopyCapacity = required;
            }
            cancellationToken.ThrowIfCancellationRequested();
            _retained.CopyBindingFrom(source, name);
        }

        public void Dispose()
        {
            foreach (var name in _prefixes)
            {
                var retained = _retainedNames.Contains(name);
                if (retained)
                    _retained.SetAttribute(name, BindingAttributes.Temporary, _saved.IsTemporaryBinding(name));
                var cursor = _owner._getoptsCursor;
                // Pending users have settled. The selected store can give its slot back to the caller.
                _owner.TransferBindingFrom(retained ? _retained : _saved, name);
                (retained ? _saved : _retained).ClearBinding(name);
                // A temporary OPTIND changes the visible index. The current bundle belongs to the scan.
                if (name.CachedText == "OPTIND")
                    _owner._getoptsCursor = cursor with { ArgumentIndex = 0 };
            }
            _prefixes.Clear();
            _retainedNames.Clear();
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
        private long _trackedSavedBytes;

        internal EvaluationMemory.SavedLocalMemory? SavedMemoryTracker { get; private set; }

        internal void TrackSavedMemory(EvaluationMemory.SavedLocalMemory tracker)
        {
            SavedMemoryTracker = tracker;
            _saved._savedLocalBindings = this;
            RefreshSavedMemory();
        }

        internal void RefreshSavedMemory()
        {
            if (SavedMemoryTracker is not { } tracker)
                return;
            var currentBytes = SavedMemoryBytes;
            tracker.Bytes = checked(tracker.Bytes + (currentBytes - _trackedSavedBytes));
            _trackedSavedBytes = currentBytes;
        }

        internal void RetireSavedMemory()
        {
            if (SavedMemoryTracker is not { } tracker)
                return;
            tracker.Bytes -= _trackedSavedBytes;
            _trackedSavedBytes = 0;
            _saved._savedLocalBindings = null;
            SavedMemoryTracker = null;
        }

        internal long SavedMemoryBytes => _saved.EstimatedMemoryBytes;
        internal long ForkMemoryBytes => checked(256 + SavedMemoryBytes + (long)BindingNameSetEntryBytes
            * (_names.EnsureCapacity(0) + (long)_unsetNames.EnsureCapacity(0)
                + _declaredPrefixes.EnsureCapacity(0) + _promotedPrefixes.EnsureCapacity(0)));

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

        internal Variables? SavedBindings => _parent == null ? _saved : null;
        internal bool IsTemporaryPrefixScope => _parent != null;
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
            bool inherit,
            CaseAttributeChange caseChange,
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
                if (!command.HasBinding(name) || command.IsUnassignedScalar(name))
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
                var temporary = prefixes?.Contains(name) == true || command.IsTemporaryBinding(name);
                var inherited = temporary ? command : inherit ? savedBinding : null;
                if (inherited != null)
                {
                    memory.Charge(inherited.EstimateBindingBytes(name));
                    local.CopyBindingFrom(inherited, name);
                    if (inherit)
                        local.ResetGetoptsPosition(name);
                    if ((inherit || command.IsTemporaryBinding(name))
                        && local.NamerefTargets.TryGetValue(name, out var inheritedReference))
                    {
                        if (inherit && inheritedReference.IsEmpty)
                            local.ReplaceValue(name, new UnassignedSlot());
                        else
                            local.SetResolvedScalar(name, inheritedReference.Value);
                    }
                    if (prefixes?.Contains(name) == true)
                        local.SetAttribute(name, BindingAttributes.Temporary, false);
                }
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
            {
                var stagedCursor = updated._getoptsCursor;
                if (command.TransferBindingFrom(updated, name) && name.CachedText == "OPTIND")
                    command._getoptsCursor = stagedCursor;
            }
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
                    variables.SetCaseAttributes(name, caseChange);
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

                variables.SetCaseAttributes(resolved, caseChange);
                var index = reference.Kind == VariableReferenceKind.Element ? reference.Subscript.ToValue() : ShellValue.FromText("0");
                if (kind == ArrayAssignmentKind.Auto)
                {
                    variables.SetAssignedScalar(resolved, operand.Value, memory);
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
                    kind == ArrayAssignmentKind.Indexed ? ArrayKind.Indexed : ArrayKind.Associative, memory);
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
            child._names.EnsureCapacity(_names.Count);
            child._unsetNames.EnsureCapacity(_unsetNames.Count);
            child._declaredPrefixes.EnsureCapacity(_declaredPrefixes.Count);
            child._promotedPrefixes.EnsureCapacity(_promotedPrefixes.Count);
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
            var savedCursor = _saved._getoptsCursor;
            foreach (var name in _names)
            {
                if (!_promotedPrefixes.Contains(name) || !_owner.HasBinding(name))
                {
                    _owner.TransferBindingFrom(_saved, name);
                    if (name.CachedText == "OPTIND")
                        _owner._getoptsCursor = savedCursor;
                }
                else
                    _saved.ClearBinding(name);
            }
            _names.Clear();
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

        if (!TryResolveWholeName(name, out var resolved) || resolved.IsEmpty)
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
    internal ShellValue SetExpansionValueIgnoringReadonly(BindingName name, ShellValue value, EvaluationMemory memory)
    {
        if (!TryResolveWholeName(name, out var resolved))
            return value;

        if (resolved.IsEmpty)
        {
            _ = TrySetScalar(name, value, memory);
            return value;
        }
        return SetResolvedValuePreservingArray(resolved, value, memory);
    }

    /// <summary>Writes a resolved scalar or array element zero; callers own readonly checks.</summary>
    internal ShellValue SetResolvedValuePreservingArray(BindingName resolved, ShellValue value, EvaluationMemory? memory)
    {
        if (IndexedArrays.ContainsKey(resolved))
            _ = TrySetResolvedArrayElement(resolved, default(IndexedArrayIndex), value, memory);
        else if (AssociativeArrays.ContainsKey(resolved))
            _ = TrySetResolvedArrayElement(resolved, ShellValue.FromText("0"), value, ArrayKind.Associative, memory);
        else
            return SetAssignedScalar(resolved, value, memory);
        return HasAttribute(resolved, BindingAttributes.Case) && TryGetExpansionValue(resolved, out var stored)
            ? stored : value;
    }

    /// <summary>
    /// Attempts to set a scalar value, resolving namerefs and enforcing readonly constraints.
    /// </summary>
    public bool TrySetScalar(BindingName name, ShellValue value) => TrySetScalar(name, value, memory: null);

    internal bool TrySetScalar(BindingName name, ShellValue value, EvaluationMemory? memory, bool commandPrefix = false)
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
        if (commandPrefix && TryGetBinding(resolved, out var slot) && slot is LineNumberSlot)
            ReplaceValue(resolved, new ScalarSlot(value));
        else
            SetAssignedScalar(resolved, value, memory);
        return true;
    }

    /// <summary>
    /// Sets a resolved scalar value while bypassing readonly checks.
    /// </summary>
    internal void SetScalarIgnoringReadonly(BindingName name, ShellValue value)
    {
        if (name.IsEmpty)
            return;

        if (!TryResolveWholeName(name, out var resolved) || resolved.IsEmpty)
            return;

        SetResolvedScalar(resolved, value);
    }

    internal bool TrySetResolvedScalar(BindingName name, ShellValue value, EvaluationMemory memory)
    {
        if (name.IsEmpty || IsReadonly(name))
            return false;
        if (NamerefTargets.TryGetValue(name, out var target) && target.IsEmpty)
            return TrySetScalar(name, value, memory);
        SetAssignedScalar(name, value, memory);
        return true;
    }

    /// <summary>Records a completed command's argument for ordinary scalar bindings.</summary>
    internal bool TrySetLastArgumentScalar(ShellValue value)
    {
        if (TryGetBinding(LastArgumentName, out var slot)
            && (slot is IndexedSlot or AssociativeSlot or NamerefSlot
                || (slot.Attributes & (BindingAttributes.Integer | BindingAttributes.Readonly)) != 0))
            return false;
        SetResolvedScalar(LastArgumentName, value);
        if ((slot?.Attributes & BindingAttributes.Exported) == BindingAttributes.Exported)
            SetExported(LastArgumentName, false);
        return true;
    }

    /// <summary>Publishes an unassigned scalar while retaining its declaration attributes and unset alias.</summary>
    internal bool TrySetResolvedUnassigned(BindingName name)
    {
        if (name.IsEmpty || IsReadonly(name))
            return false;
        if (!NamerefTargets.ContainsKey(name))
            ReplaceValue(name, new UnassignedSlot());
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
        TryGetBinding(resolved, out var slot);
        SetResolvedScalar(resolved, slot, value);
    }

    private ShellValue SetAssignedScalar(BindingName resolved, ShellValue value, EvaluationMemory? memory)
    {
        TryGetBinding(resolved, out var slot);
        value = ConvertAssignmentCase(slot?.Attributes ?? BindingAttributes.None, value, memory);
        SetResolvedScalar(resolved, slot, value);
        return value;
    }

    private void SetResolvedScalar(BindingName resolved, VariableSlot? slot, ShellValue value)
    {
        if (slot is ScalarSlot scalar)
        {
            using var memory = TrackBindingMemory(resolved);
            scalar.Value = value;
            ResetGetoptsPosition(resolved);
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

        VariableBinding? referent = flags != UnsetFlags.NamerefOnly
            && TryResolveBinding(name, selectOwner: null, out var binding) ? binding : null;
        return TryUnsetSelected(SelectUnsetTarget(name, referent, checkUninitializedTerminal: true), flags);
    }

    internal VariableUnsetTarget SelectUnsetTarget(BindingName name, VariableBinding? referent, bool checkUninitializedTerminal)
    {
        // An absent resolution (including a cycle) or an unset nameref keeps the operand's name.
        if (referent is not { } binding)
            return new(name, false, ReadonlyName(name));
        var requiresSelector = binding.Reference.Kind != VariableReferenceKind.Name;
        if (!requiresSelector && !binding.IsUnsetNameref)
            return new(binding.Name, false, ReadonlyName(binding.Name));
        var readonlyName = ReadonlyName(name);
        if (readonlyName.IsEmpty && (requiresSelector || checkUninitializedTerminal))
            readonlyName = ReadonlyName(binding.Name);
        return new(name, requiresSelector, readonlyName);

        BindingName ReadonlyName(BindingName selected) => IsReadonlyBinding(selected) ? selected : BindingName.Empty;
    }

    internal bool TryUnsetSelected(VariableUnsetTarget target, UnsetFlags flags)
    {
        if (target.Name.IsEmpty || target.RequiresSelector || !target.ReadonlyName.IsEmpty)
            return false;

        var name = target.Name;
        if (flags == UnsetFlags.NamerefOnly)
        {
            if (RemoveValue<NamerefSlot>(name))
                SetAttribute(name, BindingAttributes.Exported, false);
            return true;
        }

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

        if (!TryResolveWholeName(name, out var resolved) || resolved.IsEmpty)
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
    public bool TrySetArrayElement(BindingName name, ShellValue index, ShellValue value, ArrayKind kind) =>
        TrySetArrayElement(name, index, value, kind, memory: null);

    private bool TrySetArrayElement(BindingName name, ShellValue index, ShellValue value, ArrayKind kind, EvaluationMemory? memory)
    {
        return TryResolveWritableName(name, out var resolved)
            && TrySetResolvedArrayElement(resolved, index, value, kind, memory);
    }

    private bool TrySetResolvedArrayElement(BindingName resolved, ShellValue index, ShellValue value, ArrayKind kind, EvaluationMemory? memory) =>
        kind == ArrayKind.Indexed
            ? IndexedArrayIndex.TryParse(index.Bytes.Span, out var position)
                && TrySetResolvedArrayElement(resolved, position, value, memory)
            : AssociativeArrayKey.TryCreate(index, out var key) && TrySetResolvedArrayElement(resolved, key, value, memory);

    /// <summary>
    /// Attempts to assign an entire array from a sequence of values.
    /// </summary>
    public bool TryAssignArray(BindingName name, IReadOnlyList<ShellValue> values, ArrayKind kind)
    {
        if (!TryResolveWritableName(name, out var resolved))
            return false;

        return TryAssignResolvedArray(resolved, values, kind, initializeReadonly: false);
    }

    /// <summary>Replaces an already selected array binding without following its nameref again.</summary>
    /// <remarks>The declaring initializer can populate a binding it just marked readonly.</remarks>
    internal bool TryAssignResolvedArray(BindingName resolved, IReadOnlyList<ShellValue> values, ArrayKind kind,
        bool initializeReadonly)
    {
        if (resolved.IsEmpty || !initializeReadonly && IsReadonly(resolved))
            return false;

        if (kind == ArrayKind.Indexed)
        {
            ReplaceValue(resolved, new IndexedSlot(IndexedArray.FromSequential(values)));
            return true;
        }

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

    /// <summary>Applies admitted records to the selected sparse array after input has settled.</summary>
    internal bool TryUpdateResolvedIndexedArray(
        BindingName resolved, uint origin, IReadOnlyList<ShellValue> values,
        EvaluationMemory memory, CancellationToken cancellationToken)
    {
        if (resolved.IsEmpty || IsReadonly(resolved) || AssociativeArrays.ContainsKey(resolved))
            return false;
        if (IndexedArrays.TryGetValue(resolved, out var array))
        {
            using var bindingMemory = TrackBindingMemory(resolved);
            array.SetSequential(origin, values, memory, cancellationToken);
        }
        else
        {
            var promoted = CreatePromotedIndexedArray(resolved, memory: null);
            promoted.SetSequential(origin, values, memory: null, CancellationToken.None);
            ReplaceValue(resolved, new IndexedSlot(promoted));
        }
        return true;
    }

    /// <summary>
    /// Applies a parsed operand or an already selected literal binding used by builtin output.
    /// </summary>
    internal bool TryApplyAssignment(VariableReference reference, ShellValue value, EvaluationMemory? memory)
    {
        if (reference.Kind != VariableReferenceKind.Name)
            return TryAssignArrayElement(reference.Name, reference.Subscript.ToValue(), value, ArrayAssignmentKind.Auto, memory);

        if (!TryResolveBinding(reference.Name, selectOwner: null, out var binding)
            || binding.Reference.Kind != VariableReferenceKind.Name
            || IsReadonly(reference.Name) || binding.Owner.IsReadonly(binding.Name))
            return false;
        if (binding.IsUnsetNameref)
            return TrySetScalar(reference.Name, value, memory);
        binding.Owner.SetResolvedValuePreservingArray(binding.Name, value, memory);
        return true;
    }

    /// <summary>
    /// Attempts to assign a single array element for the given base name and index.
    /// </summary>
    internal bool TryAssignArrayElement(BindingName baseName, ShellValue index, ShellValue value, ArrayAssignmentKind kind, EvaluationMemory? memory)
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
            kind == ArrayAssignmentKind.Indexed ? ArrayKind.Indexed : ArrayKind.Associative,
            memory);
    }

    /// <summary>Writes a validated indexed position, retaining a promoted scalar at position zero.</summary>
    internal bool TrySetArrayElement(BindingName name, IndexedArrayIndex index, ShellValue value)
    {
        return TryResolveWritableName(name, out var resolved) && TrySetResolvedArrayElement(resolved, index, value, memory: null);
    }

    internal bool TrySetResolvedArrayElement(BindingName resolved, IndexedArrayIndex index, ShellValue? value, EvaluationMemory? memory)
        => TrySetResolvedArrayElement(resolved, index, value, memory, prepaidBytes: 0);

    internal bool TrySetResolvedArrayElement(
        BindingName resolved, IndexedArrayIndex index, ShellValue? value, EvaluationMemory? memory, long prepaidBytes) =>
        TrySetResolvedArrayElement(resolved, index, value, memory, prepaidBytes, savedInputBytes: null);

    private bool TrySetResolvedArrayElement(
        BindingName resolved, IndexedArrayIndex index, ShellValue? value, EvaluationMemory? memory, long prepaidBytes,
        long? savedInputBytes)
    {
        if (resolved.IsEmpty)
            return false;

        TryGetBinding(resolved, out var slot);
        if (slot is AssociativeSlot)
            return false;
        if (value is { } assigned)
        {
            var converted = ConvertAssignmentCase(slot?.Attributes ?? BindingAttributes.None, assigned, memory);
            if (savedInputBytes is { } saved)
            {
                if (converted.IsEmpty)
                    converted = ShellValue.Empty;
                else
                {
                    prepaidBytes = EstimateValueBytes(converted);
                    memory?.PrepareRetainedStorage(checked(saved + prepaidBytes));
                    // A changed value already has its own backing from the converter.
                    if (converted.Bytes.Equals(assigned.Bytes))
                        converted = ShellValue.FromBytes(assigned.Bytes.ToArray());
                }
                memory?.CancellationToken.ThrowIfCancellationRequested();
            }
            value = converted;
        }
        if (slot is IndexedSlot indexed)
        {
            using var change = TrackBindingMemory(resolved);
            indexed.Value.Set(index, value, memory, prepaidBytes);
        }
        else
        {
            memory?.Charge(BindingOverheadBytes);
            var array = CreatePromotedIndexedArray(resolved, memory);
            array.Set(index, value, memory, prepaidBytes);
            ReplaceValue(resolved, new IndexedSlot(array));
        }
        if (index.Value == 0)
            ResetGetoptsPosition(resolved);
        return true;
    }

    private IndexedArray GetOrCreateIndexedArray(BindingName resolved)
    {
        if (!IndexedArrays.TryGetValue(resolved, out var array))
        {
            array = CreatePromotedIndexedArray(resolved, memory: null);
            ReplaceValue(resolved, new IndexedSlot(array));
        }

        return array;
    }

    private IndexedArray CreatePromotedIndexedArray(BindingName resolved, EvaluationMemory? memory)
    {
        var array = new IndexedArray(isInitialized: false);
        if (Scalars.TryGetValue(resolved, out var scalar))
            array.Set(default, scalar, memory);
        return array;
    }

    /// <summary>Writes a validated associative key without changing an existing array's kind.</summary>
    internal bool TrySetArrayElement(BindingName name, AssociativeArrayKey key, ShellValue value)
    {
        return TryResolveWritableName(name, out var resolved) && TrySetResolvedArrayElement(resolved, key, value, memory: null);
    }

    internal bool TrySetResolvedArrayElement(BindingName resolved, AssociativeArrayKey key, ShellValue? value, EvaluationMemory? memory)
    {
        if (resolved.IsEmpty)
            return false;

        TryGetBinding(resolved, out var slot);
        if (slot is IndexedSlot)
            return false;
        if (value is { } assigned)
            value = ConvertAssignmentCase(slot?.Attributes ?? BindingAttributes.None, assigned, memory);
        if (slot is AssociativeSlot associative)
        {
            using var change = TrackBindingMemory(resolved);
            associative.Value.Set(key, value, memory);
        }
        else
        {
            memory?.Charge(BindingOverheadBytes);
            var array = new AssociativeArray(isInitialized: false);
            array.Set(key, value, memory);
            ReplaceValue(resolved, new AssociativeSlot(array));
        }
        if (resolved.CachedText == "OPTIND" && key.Value.Bytes.Span.SequenceEqual("0"u8))
            ResetGetoptsPosition(resolved);
        return true;
    }

    private bool TryResolveWritableName(BindingName name, out BindingName resolved)
    {
        resolved = BindingName.Empty;
        return name.IsIdentifier && TryResolveWholeName(name, out resolved)
            && !IsReadonly(resolved) && !IsReadonly(name);
    }

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
            Span<byte> flags = stackalloc byte[8];
            var count = WriteDeclarationAttributes(slot, flags);
            if (!output.TryAppend("declare -"u8)
                || !output.TryAppend(count == 0 ? "-"u8 : flags[..count])
                || !output.TryAppend((byte)' '))
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
                _ => TryAppendArrayAssignment(slot, output, CancellationToken.None)
            };
            if (!appended)
                return false;
        }
        return output.TryAppend((byte)'\n');

        bool AppendValue(ShellValue value, ShellQuoteStyle quoteStyle) => output.TryAppend((byte)'=')
            && ShellArgumentEscaping.TryAppendQuoted(output, value.Bytes.Span, quoteStyle);

    }

    internal ShellValue GetDeclarationAttributes(BindingName name)
    {
        TryGetBinding(name, out var slot);
        Span<byte> flags = stackalloc byte[8];
        var count = WriteDeclarationAttributes(slot, flags);
        return count == 0 ? ShellValue.Empty : ShellValue.FromBytes(flags[..count].ToArray());
    }

    private static int WriteDeclarationAttributes(VariableSlot? slot, Span<byte> flags)
    {
        var length = 0;
        var kind = slot switch
        {
            IndexedSlot => (byte)'a',
            AssociativeSlot => (byte)'A',
            _ => (byte)0
        };
        var attributes = slot?.Attributes ?? BindingAttributes.None;
        if (kind != 0)
            flags[length++] = kind;
        if ((attributes & BindingAttributes.Integer) != 0)
            flags[length++] = (byte)'i';
        if (slot is NamerefSlot)
            flags[length++] = (byte)'n';
        if ((attributes & BindingAttributes.Readonly) != 0)
            flags[length++] = (byte)'r';
        if ((attributes & BindingAttributes.Exported) != 0)
            flags[length++] = (byte)'x';
        if ((attributes & BindingAttributes.Capitalize) != 0)
            flags[length++] = (byte)'c';
        if ((attributes & BindingAttributes.Lowercase) != 0)
            flags[length++] = (byte)'l';
        if ((attributes & BindingAttributes.Uppercase) != 0)
            flags[length++] = (byte)'u';
        return length;
    }

    internal bool TryAppendArrayAssignment(BindingName name, ByteMemoryBuilder output, CancellationToken cancellationToken)
    {
        TryGetBinding(name, out var slot);
        return TryAppendArrayAssignment(slot, output, cancellationToken);
    }

    private static bool TryAppendArrayAssignment(
        VariableSlot? slot, ByteMemoryBuilder output, CancellationToken cancellationToken)
    {
        return slot switch
        {
            IndexedSlot { Value.IsInitialized: true } indexed => AppendArray(indexed.Value.Entries, ArrayKind.Indexed),
            AssociativeSlot { Value.IsInitialized: true } associative => AppendArray(associative.Value.Entries, ArrayKind.Associative),
            _ => true
        };

        bool AppendArray(IEnumerable<KeyValuePair<ShellValue, ShellValue?>> entries, ArrayKind kind)
        {
            if (!output.TryAppend("=("u8))
                return false;
            var first = true;
            foreach (var (key, value) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!first && kind == ArrayKind.Indexed && !output.TryAppend((byte)' '))
                    return false;
                first = false;
                if (!output.TryAppend((byte)'[')
                    || !ShellArgumentEscaping.TryAppendQuoted(output, key.Bytes.Span,
                        kind == ArrayKind.Indexed ? ShellQuoteStyle.Backslash : ShellQuoteStyle.AssociativeKey)
                    || !output.TryAppend((byte)']') || !output.TryAppend((byte)'=')
                    || value is { } assigned && !ShellArgumentEscaping.TryAppendQuoted(output, assigned.Bytes.Span, ShellQuoteStyle.DoubleQuoted))
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
        if (name.CachedText == "OPTIND")
            _getoptsCursor = source._getoptsCursor;
    }

    /// <summary>Moves a private store's slot, or clears the destination if the slot is absent.</summary>
    private bool TransferBindingFrom(Variables source, BindingName name)
    {
        if (!source._bindings.TryGetValue(name, out var slot) || slot is MissingSlot)
        {
            ClearBinding(name);
            return false;
        }
        StoreBinding(name, slot);
        source.ClearBinding(name);
        return true;
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

        target = ConvertAssignmentCase(name, target, memory);
        VariableReference? reference = null;
        if (!target.IsEmpty)
        {
            if (!VariableReference.TryParse(ShellSource.FromBytes(target.Bytes), memory, out var parsed))
                return false;
            reference = parsed;
        }

        SetNamerefTarget(name, target, reference, memory);
        return true;
    }

    internal ForAssignmentFailure AssignForIteration(BindingName name, ShellValue item, EvaluationMemory memory)
    {
        if (NamerefTargets.ContainsKey(name))
        {
            // A loop selects a new referent. Validate it before checking the reference's readonly attribute.
            if (item.IsEmpty || !VariableReference.TryParse(ShellSource.FromBytes(item.Bytes), memory, out var target))
                return ForAssignmentFailure.InvalidTarget;
            if (IsReadonlyBinding(name))
                return ForAssignmentFailure.Readonly;
            if (IsIntegerBinding(name))
                return ForAssignmentFailure.IncompatibleAttributes;
            SetNamerefTarget(name, item, target, memory);
        }
        else
        {
            if (IsReadonlyBinding(name))
                return ForAssignmentFailure.Readonly;
            SetResolvedValuePreservingArray(name, item, memory);
        }

        return ForAssignmentFailure.None;
    }

    private void SetNamerefTarget(BindingName name, ShellValue target, VariableReference? reference, EvaluationMemory? memory)
    {
        BindingName? literalName = null;
        if (reference is { Kind: not VariableReferenceKind.Name })
        {
            // Local declarations can create a binding whose literal name includes the selector.
            memory?.Charge(checked(4L * target.Bytes.Length));
            literalName = BindingName.FromValue(target);
        }

        ReplaceValue(name, new NamerefSlot(new NamerefTarget(target, reference, literalName)));
        if (IsIntegerBinding(name))
            SetIntegerBinding(name, false);
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
    public IReadOnlyList<EnvironmentVariable> BuildExportedEnvironment() =>
        BuildExportedEnvironment(ExportedBindings, memory: null, selectionMemory: null, CancellationToken.None);

    /// <summary>Selects the first assigned export of each name, from innermost to outermost scope.</summary>
    internal static IReadOnlyList<EnvironmentVariable> BuildExportedEnvironment(
        IEnumerable<VariableBinding> bindings, EvaluationMemory? memory, MemoryCapacity? selectionMemory,
        CancellationToken cancellationToken)
    {
        const long EnvironmentEntryBytes = 40;
        const long ArrayHeaderBytes = 32;
        var results = new List<EnvironmentVariable>();
        var selected = new HashSet<BindingName>();
        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    if (IsAssigned(slot)) Select(name);
                    continue;
                default:
                    continue;
            }
            Select(name);
            if (memory != null && results.Count == results.Capacity)
            {
                var previous = results.Capacity;
                var capacity = previous == 0 ? 4 : checked(2 * previous);
                memory.Charge(ArrayHeaderBytes + EnvironmentEntryBytes * capacity);
                results.Capacity = capacity;
                if (previous != 0)
                    memory.Workspace.Resize(memory.Workspace.Bytes - (ArrayHeaderBytes + EnvironmentEntryBytes * previous));
            }
            results.Add(new EnvironmentVariable(name, value));
        }

        return results;

        void Select(BindingName name)
        {
            selectionMemory?.Grow(BindingNameSelectionBytes);
            selected.Add(name);
        }
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
    /// Marks the selected whole binding as readonly, including an uninitialized nameref.
    /// </summary>
    /// <remarks>A circular reference or an element reference selects the original name.</remarks>
    public void SetReadonly(BindingName name)
    {
        if (name.IsEmpty)
            return;

        var target = TryResolveBinding(name, selectOwner: null, out var binding)
            && binding.Reference.Kind == VariableReferenceKind.Name ? binding.Name : name;
        SetReadonlyBinding(target);
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
    /// <remarks>An uninitialized nameref resolves successfully with an empty scalar or array destination.</remarks>
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

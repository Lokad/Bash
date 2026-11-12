using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    private CommandSource? _commandSource;

    /// <summary>The surrounding command owns this borrow and all reached-binding copies.</summary>
    private sealed record CommandSource(Variables Variables, EvaluationMemory Memory, CancellationToken CancellationToken);

    /// <summary>An absent command binding hides the caller's binding without changing the caller.</summary>
    private sealed class MissingSlot : VariableSlot
    {
        protected override VariableSlot CloneValue() => new MissingSlot();
    }

    /// <summary>
    /// Borrows caller state for prefix preparation or isolated redirection expansion. Each reached
    /// binding is admitted and detached once. Positional values stay borrowed; this scope must not
    /// escape the surrounding command. A full Clone still creates independent child state.
    /// </summary>
    internal static Variables CreateCommandScope(Variables caller, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        memory.Charge(1024);
        cancellationToken.ThrowIfCancellationRequested();
        return new Variables
        {
            _commandSource = new CommandSource(caller, memory, cancellationToken),
            _positional = caller._positional,
            _argumentZero = caller._argumentZero,
            CachedFieldSeparators = caller.CachedFieldSeparators
        };
    }

    private bool TryGetBinding(BindingName name, [NotNullWhen(true)] out VariableSlot? slot)
    {
        if (_bindings.TryGetValue(name, out slot))
            return slot is not MissingSlot;
        if (_commandSource is not { } source || !source.Variables.TryGetBinding(name, out slot))
        {
            slot = null;
            return false;
        }
        source.CancellationToken.ThrowIfCancellationRequested();
        source.Memory.Charge(source.Variables.EstimateBindingBytes(name));
        source.CancellationToken.ThrowIfCancellationRequested();
        slot = slot.Clone();
        source.CancellationToken.ThrowIfCancellationRequested();
        StoreBinding(name, slot);
        return true;
    }

    /// <summary>Enumerates visible slots without copying unrelated containers merely to list names.</summary>
    private IEnumerable<KeyValuePair<BindingName, VariableSlot>> EnumerateBindings()
    {
        foreach (var entry in _bindings)
            if (entry.Value is not MissingSlot)
                yield return entry;
        if (_commandSource is not { } source)
            yield break;
        foreach (var entry in source.Variables.EnumerateBindings())
            if (!_bindings.ContainsKey(entry.Key))
                yield return entry;
    }

    private IEnumerable<BindingName> EnumerateExportedNames()
    {
        foreach (var name in _exportedIndex)
            yield return name;
        if (_commandSource is not { } source)
            yield break;
        foreach (var name in source.Variables.ExportedNames)
            if (!_bindings.ContainsKey(name))
                yield return name;
    }

    /// <summary>Visits exported names only, including borrowed names not hidden by command bindings.</summary>
    private sealed class ExportedNameView(Variables owner) : IReadOnlyCollection<BindingName>
    {
        public int Count => owner._commandSource == null ? owner._exportedIndex.Count : owner.EnumerateExportedNames().Count();
        public IEnumerator<BindingName> GetEnumerator() => owner.EnumerateExportedNames().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

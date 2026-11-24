using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text.Unicode;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Classifies expansion failures by their unwind and recovery behavior.</summary>
internal enum ExpansionFailureKind
{
    Arithmetic,
    /// <summary>A generated word fails substitution parsing and discards its command unit.</summary>
    WordSyntax,
    /// <summary>A reached parenthesized substitution has invalid rendered syntax.</summary>
    CommandSubstitutionSyntax,
    /// <summary>Rereading rendered capture or array syntax terminates the current reader.</summary>
    ReaderSyntax,
    Parameter,
    /// <summary>A failed indirect name read or write discards the current command unit.</summary>
    IndirectParameter,
    /// <summary>A reached error-if-unset parameter expansion.</summary>
    RequiredParameter,
    /// <summary>A present value reached an invalid parameter transformation.</summary>
    InvalidParameterTransform,
    /// <summary>An errexit-enabled file substitution could not open its input.</summary>
    InputSubstitution,
    Assignment,
    /// <summary>An integer binding's operand failed arithmetic evaluation.</summary>
    IntegerAssignment,
    /// <summary>Arithmetic selection of an indexed array element failed.</summary>
    ArrayIndex,
    /// <summary>A compound initializer rejected an array selector with errexit active.</summary>
    CompoundArrayIndex,
    /// <summary>A reached parameter-default write failed after its operand expanded.</summary>
    ParameterDefaultAssignment,
    /// <summary>A parameter-default assignment rejected an unset nameref target (status 2).</summary>
    NamerefDefault,
    /// <summary>A direct write failed; eval/source can return control to their caller.</summary>
    AssignmentTarget
}

internal readonly record struct ExpansionFailure(ExpansionFailureKind Kind, int ExitCode)
{
    // A local option scope may be restored before the loaded script reader handles the failure.
    internal bool CanResumeUntestedUnit { get; init; }

    // Argument expansion in a direct pipeline stage precedes the child boundary that resets EXIT.
    internal bool UseInheritedPipelineExitAction { get; init; }
}

internal enum ExpansionErrorKind { General, HereDocumentSyntax, HereDocumentParameter }

[Flags]
internal enum FunctionAttributes
{
    None = 0, Readonly = 1, Exported = 2,
    Capitalize = (int)Variables.BindingAttributes.Capitalize,
    Lowercase = (int)Variables.BindingAttributes.Lowercase,
    Uppercase = (int)Variables.BindingAttributes.Uppercase
}

internal enum FunctionDisplayStyle { Name, Attributes, Definition, DefinitionWithAttributes }

/// <summary>Immutable runtime binding; child evaluations may share it until an attribute or definition changes.</summary>
internal sealed record FunctionBinding
{
    public FunctionDefinition Definition { get; }
    public SourceMemory? SourceMemory { get; }
    internal CommandCoordinates Coordinates { get; init; }
    public FunctionAttributes Attributes { get; init; }

    // Attribute copies and child evaluations share this cell, including before its first publication.
    private readonly ExportCache _exportCache;

    public FunctionBinding(FunctionDefinition definition, FunctionAttributes attributes, SourceMemory? sourceMemory,
        EvaluationMemory memory)
    {
        Definition = definition;
        SourceMemory = sourceMemory;
        Attributes = attributes;
        _exportCache = new ExportCache(memory);
    }

    /// <summary>Acquires namespace or command ownership while another owner is live, or for a new binding.</summary>
    public void Retain()
    {
        SourceMemory?.Retain();
        _exportCache.Retain();
    }

    public void Release()
    {
        _exportCache.Release();
        SourceMemory?.Release();
    }

    /// <summary>Retains the selected binding through preparation, execution and cleanup.</summary>
    public readonly struct Borrow : IDisposable
    {
        private readonly FunctionBinding? _binding;
        public Borrow(FunctionBinding? binding)
        {
            _binding = binding;
            binding?.Retain();
        }
        public void Dispose() => _binding?.Release();
    }

    /// <summary>Retains a bounded serialization under its creator's owner, shared by attribute copies.</summary>
    /// <remarks>The caller holds namespace or borrow ownership through consumption of the returned bytes.</remarks>
    public bool TryGetExport(int maximumBytes, out ExportedFunction export)
    {
        export = default;
        var cached = Volatile.Read(ref _exportCache.Value);
        if (cached == null)
        {
            var memory = _exportCache.Memory.Budget.CreateScope();
            var published = false;
            try
            {
                _ = memory.Reserve(192); // Cache/scope/result metadata and backing-array headers.
                using var output = new BoundedByteMemoryBuilder(0, Math.Min(maximumBytes, Parser.MaximumScriptBytes), memory);
                if (!FunctionDefinitionFormatter.TryAppendExport(Definition, output)) return false;
                var rendered = new CachedExport(new ExportedFunction(output.ToMemory()), memory);
                // Concurrent first readers may render independently; no evaluator waits for another one.
                cached = Interlocked.CompareExchange(ref _exportCache.Value, rendered, null);
                if (cached == null)
                {
                    _ = output.DetachSnapshot();
                    cached = rendered;
                    published = true;
                }
            }
            finally { if (!published) memory.Dispose(); }
        }
        if (cached.Export.Source.Length > maximumBytes) return false;
        export = cached.Export;
        return true;
    }

    private sealed class ExportCache(EvaluationMemory memory)
    {
        // Resolve Budget at publication: Engine installs it after host-provided definitions are imported.
        public EvaluationMemory Memory => memory;
        public CachedExport? Value;
        private int _references;
        public void Retain() => Interlocked.Increment(ref _references);
        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                Interlocked.Exchange(ref Value, null)?.Memory.Dispose();
        }
    }

    private sealed record CachedExport(ExportedFunction Export, IMemoryScope Memory);

    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public bool TryAppend(ByteMemoryBuilder output, FunctionDisplayStyle style)
    {
        if (style is FunctionDisplayStyle.Definition or FunctionDisplayStyle.DefinitionWithAttributes)
        {
            if (!FunctionDefinitionFormatter.TryAppend(Definition, output)) return false;
            if (style == FunctionDisplayStyle.Definition || Attributes == FunctionAttributes.None) return true;
        }
        if (style != FunctionDisplayStyle.Name
            && (!output.TryAppend("declare -f"u8)
                || Attributes.HasFlag(FunctionAttributes.Readonly) && !output.TryAppend((byte)'r')
                || Attributes.HasFlag(FunctionAttributes.Exported) && !output.TryAppend((byte)'x')
                || Attributes.HasFlag(FunctionAttributes.Capitalize) && !output.TryAppend((byte)'c')
                || Attributes.HasFlag(FunctionAttributes.Lowercase) && !output.TryAppend((byte)'l')
                || Attributes.HasFlag(FunctionAttributes.Uppercase) && !output.TryAppend((byte)'u')
                || !output.TryAppend((byte)' ')))
            return false;
        return output.TryAppend(Definition.Name.Value.Bytes.Span) && output.TryAppend((byte)'\n');
    }
}

/// <summary>
/// Holds mutable evaluation state that is independent of operation execution.
/// </summary>
internal sealed class EvaluationState
{
    /// <summary>
    /// Stores the pending expansion exit code and its origin until preparation handles the failure.
    /// </summary>
    private (int ExitCode, ExpansionErrorKind Kind)? _pendingExpansionError;

    /// <summary>Stops preparation before later operands or command effects after an expansion failure.</summary>
    public bool HasPendingExpansionError => _pendingExpansionError.HasValue;

    /// <summary>Fatal noninteractive expansion failures cannot be consumed by an OR-list or negation.</summary>
    public ExpansionFailure? FatalExpansion { get; set; }

    /// <summary>An exec invocation ended the shell; cleanup has run or been discarded, and enclosing timers cannot print.</summary>
    public bool ExitedThroughExec { get; set; }

    // Embedded and kept private so mutable scope state cannot escape as a value copy.
    private RedirectionState _redirections;

    /// <summary>
    /// Stores entries in reverse logical order, with the current directory at the end.
    /// </summary>
    private readonly List<DirectoryStackEntry> _directoryStack;
    private long _ownedDirectoryPathBytes;

    internal readonly struct DirectoryStackEntry
    {
        // Both forms use a path reference and an ownership estimate.
        private readonly object _path;

        private DirectoryStackEntry(object path, long ownedBytes)
        {
            _path = path;
            OwnedBytes = ownedBytes;
        }

        internal ShellPath? Path => _path as ShellPath;
        internal ShellValue DeferredPath => ShellValue.FromBytes((byte[])_path);
        internal long OwnedBytes { get; }

        internal static DirectoryStackEntry Resolved(ShellPath path) => new(path, 48L + 2L * path.Path.Length);
        internal static DirectoryStackEntry Deferred(byte[] path, long ownedBytes) => new(path, ownedBytes);
        internal DirectoryStackEntry Borrow() => new(_path, 0);
    }

    /// <summary>One function namespace for invocation, attributes and inspection; independent of variables.</summary>
    private readonly Dictionary<ShellValue, FunctionBinding> _functions = [];
    private int _functionCapacity;
    private int _exportedFunctionCount;
    private FunctionBinding[]? _exportedFunctions;
    private IMemoryReservation? _exportedFunctionsMemory;

    // Capacity covers dictionary growth/overlap; live entries cover binding/cache-cell metadata.
    internal long EstimatedFunctionMemoryBytes => 128L * _functionCapacity + 128L * _functions.Count;

    public EvaluationState(
        ParsedScript script,
        ShellPath currentDirectory,
        DescriptorTable? inheritedDescriptorTable)
        : this(script, new RedirectionState(inheritedDescriptorTable), new List<DirectoryStackEntry>(1) { DirectoryStackEntry.Resolved(currentDirectory) })
    {
        _ownedDirectoryPathBytes = _directoryStack[^1].OwnedBytes;
    }

    private EvaluationState(ParsedScript script, RedirectionState redirections, List<DirectoryStackEntry> directoryStack)
    {
        Script = script;
        _redirections = redirections;
        _directoryStack = directoryStack;
    }

    /// <summary> Stores the parsed script for the lifetime of the evaluation. </summary>
    public ParsedScript Script { get; }

    /// <summary>
    /// Last shell-visible status, including completed substitutions used by later expansions; null before execution.
    /// </summary>
    public int? LastExitCode { get; set; }
    internal int FunctionBraceLine { get; set; }

    /// <summary>The shell's errexit option; tested execution contexts suppress its effect without changing it.</summary>
    public bool ErrexitEnabled { get; set; }
    internal ShellForkKind? ForkKind { get; private init; }
    internal bool KeepsLocalsOnErrexit => ForkKind is ShellForkKind.Subshell or ShellForkKind.PipelineSimpleCommand;

    /// <summary>
    /// Assignment status from the last substitution in the current simple command; null means none ran.
    /// Reset at command entry and do not copy into nested evaluation state.
    /// </summary>
    public int? LastCommandSubstitutionExitCode { get; set; }

    /// <summary>
    /// Stores the current working directory; initialized to "/" and updated by directory-changing builtins.
    /// </summary>
    public ShellPath CurrentDirectory => _directoryStack[^1].Path
        ?? throw new InvalidOperationException("The working directory must be resolved.");

    internal int DirectoryStackCount => _directoryStack.Count;
    internal DirectoryStackEntry GetDirectoryStackEntry(int index) => _directoryStack[DirectoryStackSlot(index)];
    internal long RetainedDirectoryStackBytes => checked(EstimateDirectoryStackBytes(_directoryStack.Capacity)
        + _ownedDirectoryPathBytes);

    private static long EstimateDirectoryStackBytes(int capacity) => 32L + 16L * capacity;
    private int DirectoryStackSlot(int index) => _directoryStack.Count - 1 - index;

    public IReadOnlyDictionary<ShellValue, FunctionBinding> Functions => _functions;

    /// <summary>Borrows the current export selection in function namespace order.</summary>
    internal ReadOnlySpan<FunctionBinding> GetExportedFunctions(EvaluationMemory memory)
    {
        if (_exportedFunctionCount == 0)
            return [];
        if (_exportedFunctions is { } cached)
            return cached;

        var reservation = memory.Budget.Reserve(MemoryCapacity.ArrayBackingBytes<FunctionBinding>(_exportedFunctionCount));
        try
        {
            var selected = new FunctionBinding[_exportedFunctionCount];
            var index = 0;
            foreach (var binding in EnumerateExportedFunctions(CancellationToken.None))
                selected[index++] = binding;
            _exportedFunctionsMemory = reservation;
            _exportedFunctions = selected;
            return selected;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private IEnumerable<FunctionBinding> EnumerateExportedFunctions(CancellationToken cancellationToken)
    {
        foreach (var binding in _functions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.Attributes.HasFlag(FunctionAttributes.Exported))
                yield return binding;
        }
    }

    /// <summary>Clears the exported subset while an in-process replacement is in flight.</summary>
    internal FunctionExportClear? BeginClearFunctionExports(EvaluationMemory memory, CancellationToken cancellationToken)
    {
        if (_exportedFunctionCount == 0)
            return null;

        // Both versions can still be held after a nested command publishes an edit.
        const int bindingPairBytes = 96;
        var staging = memory.Budget.Reserve(checked(64L
            + MemoryCapacity.ArrayBackingBytes<(FunctionBinding, FunctionBinding)>(_exportedFunctionCount)
            + (long)bindingPairBytes * _exportedFunctionCount));
        FunctionExportClear? clear = null;
        try
        {
            var changes = new (FunctionBinding Original, FunctionBinding Cleared)[_exportedFunctionCount];
            var index = 0;
            foreach (var binding in _exportedFunctions ?? EnumerateExportedFunctions(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                changes[index++] = (binding, binding with { Attributes = binding.Attributes & ~FunctionAttributes.Exported });
            }

            cancellationToken.ThrowIfCancellationRequested();
            clear = new FunctionExportClear(this, changes, staging);
            clear.Apply();
            return clear;
        }
        catch
        {
            if (clear == null)
                staging.Dispose();
            else
                clear.Dispose();
            throw;
        }
    }

    /// <summary>Owns both binding versions until the replacement completes or unwinds.</summary>
    internal sealed class FunctionExportClear(EvaluationState owner,
        (FunctionBinding Original, FunctionBinding Cleared)[] changes, IMemoryReservation staging) : IDisposable
    {
        private int _applied;
        private bool _committed;

        internal void Apply()
        {
            for (; _applied < changes.Length; _applied++)
            {
                var (original, cleared) = changes[_applied];
                original.Retain();
                try { owner.SetFunctionBinding(cleared); }
                catch { original.Release(); throw; }
            }
        }

        internal void Commit() => _committed = true;

        public void Dispose()
        {
            try
            {
                while (_applied > 0)
                {
                    var (original, cleared) = changes[--_applied];
                    try
                    {
                        // Earlier commands in an eval can publish edits before a later refusal.
                        var name = original.Definition.Name.Value;
                        if (!_committed && owner._functions.TryGetValue(name, out var current) && ReferenceEquals(current, cleared))
                            owner.SetFunctionBinding(original);
                    }
                    finally { original.Release(); }
                }
            }
            finally { staging.Dispose(); }
        }
    }

    internal void ReleaseExportedFunctionSelection()
    {
        _exportedFunctions = null;
        _exportedFunctionsMemory?.Dispose();
        _exportedFunctionsMemory = null;
    }

    public ShellTraps? Traps { get; private set; }

    public void SetTrap(TrapCondition condition, ShellValue action, EvaluationMemory memory)
    {
        var traps = Traps ?? new ShellTraps();
        traps.Set(condition, action, memory);
        Traps = traps;
    }

    public void RetireEmptyTraps()
    {
        if (Traps?.CanRetire == true)
            Traps = null;
    }

    public DescriptorTable? CurrentRedirectionTable => _redirections.CurrentTable;

    internal long RetainedDescriptorMemoryBytes => _redirections.OwnedMemoryBytes;

    public IReadOnlyCollection<FileDescriptor> CurrentRedirectionOpenedDescriptors => _redirections.OpenedDescriptors;

    /// <summary>
    /// Creates an isolated shell-state copy for subshell evaluation.
    /// </summary>
    public EvaluationState CloneForSubshell(EvaluationMemory memory)
    {
        // Keep the parent's saved handlers admitted through restoration if the child clears them.
        memory.Charge(checked(EstimateForkBytes() + (Traps?.EstimatedMemoryBytes ?? 0)));
        var clone = CloneShellState(Script, _redirections.ForkForSubshell(), ShellForkKind.Subshell);
        clone._pendingExpansionError = _pendingExpansionError;
        clone.FatalExpansion = FatalExpansion;
        return clone;
    }

    /// <summary>
    /// Forks shell-visible state for a substitution or pipeline stage, with independent descriptor mappings.
    /// </summary>
    public EvaluationState CloneForNestedEvaluation(
        ParsedScript script,
        EvaluationDescriptorSetup descriptors,
        ShellForkKind kind,
        EvaluationMemory memory)
    {
        memory.Charge(EstimateForkBytes());
        return CloneShellState(script, _redirections.ForkForNestedEvaluation(descriptors.Bindings), kind);
    }

    private long EstimateForkBytes() => checked(EstimatedFunctionMemoryBytes
        + EstimateDirectoryStackBytes(_directoryStack.Count) + (Traps?.EstimateForkBackingBytes() ?? 0));

    private EvaluationState CloneShellState(ParsedScript script, RedirectionState redirections, ShellForkKind kind)
    {
        // The parent outlives the child, so its path text remains admitted to the parent.
        var childStack = new List<DirectoryStackEntry>(_directoryStack.Count);
        foreach (var entry in _directoryStack)
            childStack.Add(entry.Borrow());
        var clone = new EvaluationState(script, redirections, childStack)
        {
            LastExitCode = LastExitCode,
            FunctionBraceLine = FunctionBraceLine,
            ErrexitEnabled = kind != ShellForkKind.CommandSubstitution && ErrexitEnabled,
            ForkKind = kind,
            Traps = Traps?.Fork(kind)
        };

        foreach (var function in _functions)
            clone.SetFunctionBinding(function.Value);

        return clone;
    }

    /// <summary>Borrows redirection handles in scope order; the consumer selects ownership and deduplication.</summary>
    public IEnumerable<FileDescriptor> EnumerateRedirectionHandles() => _redirections.EnumerateHandles();

    public void PushRedirectionTable(
        DescriptorTable table,
        DescriptorHandleOwner openedDescriptors,
        IReadOnlySet<LogicalDescriptor> changedDescriptors,
        EvaluationMemory? memory) =>
        _redirections.Push(table, openedDescriptors, changedDescriptors, memory);

    public IReadOnlyCollection<FileDescriptor> PopRedirectionTable() => _redirections.Pop();

    /// <summary>Commits the current scope's changed bindings through their temporary restoration boundaries.</summary>
    public IReadOnlyCollection<FileDescriptor> PreserveRedirectionTable(
        EvaluationMemory? memory, OrderedDescriptorSet? pending, Stack<HashSet<FileDescriptor>>? borrowed) =>
        _redirections.Preserve(memory, pending, borrowed);

    /// <summary>Persists a binding through enclosing scopes up to its temporary restoration boundary.</summary>
    public void SetPersistentDescriptor(LogicalDescriptor descriptor, DescriptorBinding binding, EvaluationMemory? memory, bool acquired) =>
        _redirections.SetPersistentDescriptor(descriptor, binding, memory, acquired);

    /// <summary>Transfers handles to their longest-lived referencing scope; returns unreferenced handles in reverse order.</summary>
    public IReadOnlyCollection<FileDescriptor> TransferRedirectionDescriptors(
        IReadOnlyCollection<FileDescriptor> descriptors, EvaluationMemory? memory, OrderedDescriptorSet closing) =>
        _redirections.TransferHandles(descriptors, memory, closing);

    public bool ReferencesDescriptor(FileDescriptor descriptor) => _redirections.References(descriptor);

    public bool TryTakeRedirectionDescriptor(FileDescriptor descriptor, EvaluationMemory? memory) => _redirections.TryTakeHandle(descriptor, memory);

    internal void RetireDescriptorHandles() => _redirections.RetireHandles();

    public FileDescriptor? GetStdInOverride() => CurrentRedirectionTable?.StdIn;

    public FileDescriptor? GetStdOutOverride() => CurrentRedirectionTable?.StdOut;

    public FileDescriptor? GetStdErrOverride() => CurrentRedirectionTable?.StdErr;

    /// <summary>Consumes a pending expansion error, without clearing a fatal exit.</summary>
    public bool TryConsumeExpansionError(out int exitCode) => TryConsumeExpansionError(out exitCode, out _);

    internal bool TryConsumeExpansionError(out int exitCode, out ExpansionErrorKind kind)
    {
        if (_pendingExpansionError is { } error)
        {
            (exitCode, kind) = error;
            _pendingExpansionError = null;
            return true;
        }

        exitCode = 0;
        kind = default;
        return false;
    }

    public void SetExpansionError(int exitCode) => _pendingExpansionError = (exitCode, ExpansionErrorKind.General);

    internal void SetHereDocumentSyntaxError() => _pendingExpansionError = (1, ExpansionErrorKind.HereDocumentSyntax);

    internal void SetHereDocumentParameterError() => _pendingExpansionError = (1, ExpansionErrorKind.HereDocumentParameter);

    /// <summary>Redefinition retains attributes and fails without changing a readonly function.</summary>
    public bool TryRegisterFunction(FunctionDefinition functionDefinition, Evaluation.EvaluationContext context, EvaluationMemory memory)
    {
        var attributes = _functions.TryGetValue(functionDefinition.Name.Value, out var previous)
            ? previous.Attributes : FunctionAttributes.None;
        if (attributes.HasFlag(FunctionAttributes.Readonly))
            return false;
        memory.Charge(256);
        var coordinates = context.Coordinates;
        var bodyLine = coordinates.Locations?.TryGet(functionDefinition.Body, out var body) == true
            ? coordinates.Offset + body.StartLine : coordinates.Line;
        SetFunctionBinding(new FunctionBinding(functionDefinition, attributes, context.SourceMemory, memory)
        {
            Coordinates = coordinates with { FunctionBodyLine = bodyLine, CompoundOpenLine = coordinates.RedirectionOpenLine }
        });
        return true;
    }

    public void SetFunctionBinding(FunctionBinding function)
    {
        _functions.TryGetValue(function.Definition.Name.Value, out var previous);
        // Replacing a dictionary value preserves its original key. Keep source-backed
        // name storage with the new definition's owner, rather than the retired source.
        if (previous != null && !previous.Definition.Name.Value.Bytes.Equals(function.Definition.Name.Value.Bytes))
            _functions.Remove(function.Definition.Name.Value);
        _functions[function.Definition.Name.Value] = function;
        _functionCapacity = Math.Max(_functionCapacity, _functions.Count);
        var wasExported = previous?.Attributes.HasFlag(FunctionAttributes.Exported) == true;
        var isExported = function.Attributes.HasFlag(FunctionAttributes.Exported);
        if (wasExported || isExported)
        {
            ReleaseExportedFunctionSelection();
            _exportedFunctionCount += (isExported ? 1 : 0) - (wasExported ? 1 : 0);
        }
        // Acquire before retiring the old binding: attribute copies can share this owner.
        function.Retain();
        previous?.Release();
    }

    /// <summary>Removes the current binding, not a definition already being invoked.</summary>
    public bool TryUnsetFunction(ShellValue name)
    {
        if (!_functions.TryGetValue(name, out var function))
            return true;
        if (function.Attributes.HasFlag(FunctionAttributes.Readonly))
            return false;

        _functions.Remove(name);
        if (function.Attributes.HasFlag(FunctionAttributes.Exported))
        {
            ReleaseExportedFunctionSelection();
            _exportedFunctionCount--;
        }
        function.Release();
        return true;
    }

    /// <summary>Retires a temporary namespace only after its execution and cleanup have settled.</summary>
    public void ReleaseFunctions()
    {
        ReleaseExportedFunctionSelection();
        _exportedFunctionCount = 0;
        foreach (var function in _functions.Values)
            function.Release();
        _functions.Clear();
    }

    public void ResetDirectoryStack()
    {
        _directoryStack.RemoveRange(0, _directoryStack.Count - 1);
        _ownedDirectoryPathBytes = _directoryStack[0].OwnedBytes;
    }

    public void PushDeferredDirectory(ShellValue operand, EvaluationMemory memory)
    {
        var bytes = checked(48L + operand.Bytes.Length);
        memory.Charge(bytes);
        PushStackEntry(DirectoryStackEntry.Deferred(operand.Bytes.ToArray(), bytes), memory);
    }

    public void RemoveSavedDirectory(int index)
    {
        var slot = DirectoryStackSlot(index);
        _ownedDirectoryPathBytes -= _directoryStack[slot].OwnedBytes;
        _directoryStack.RemoveAt(slot);
    }

    /// <summary>Rotates saved entries while the active directory remains in place until lookup succeeds.</summary>
    public DirectoryStackEntry RotateSavedDirectories(int index, EvaluationMemory memory)
    {
        var current = _directoryStack[^1];
        if (index == 0)
            return current;

        var selected = GetDirectoryStackEntry(index);
        memory.Charge(Math.Max(0, current.OwnedBytes - selected.OwnedBytes));
        // Logical left rotation is right rotation in the backing list.
        var split = _directoryStack.Count - index;
        _directoryStack.Reverse(0, split);
        _directoryStack.Reverse(split, index);
        _directoryStack.Reverse();
        _directoryStack[^1] = current;
        _ownedDirectoryPathBytes = checked(_ownedDirectoryPathBytes + current.OwnedBytes - selected.OwnedBytes);
        return selected;
    }

    /// <summary>Applies a settled lookup, including Bash's partial stack change after a rejected swap.</summary>
    public DirectoryStackEntry ApplyDirectoryLookup(DirectoryChangeKind kind, ShellPath target, bool succeeded, EvaluationMemory memory)
    {
        var previous = _directoryStack[^1];
        var fromStack = kind is DirectoryChangeKind.PopDirectory or DirectoryChangeKind.SwapDirectories;
        var stored = fromStack ? _directoryStack[^2] : previous;
        var destination = ReferenceEquals(stored.Path, target) ? stored : DirectoryStackEntry.Resolved(target);
        if (kind == DirectoryChangeKind.SwapDirectories)
        {
            if (!succeeded)
                memory.Charge(Math.Max(0, previous.OwnedBytes - stored.OwnedBytes));
            Replace(1, previous);
        }
        // A successful lookup publishes the head before variable assignments. A pop can
        // leave two occurrences of the same path if those assignments reject the change.
        if (succeeded)
        {
            if (kind == DirectoryChangeKind.PopDirectory)
                memory.Charge(Math.Max(0, destination.OwnedBytes - previous.OwnedBytes));
            Replace(0, destination);
        }
        return previous;

        void Replace(int index, DirectoryStackEntry entry)
        {
            var slot = DirectoryStackSlot(index);
            _ownedDirectoryPathBytes = checked(_ownedDirectoryPathBytes + entry.OwnedBytes - _directoryStack[slot].OwnedBytes);
            _directoryStack[slot] = entry;
        }
    }

    /// <summary>Completes stack publication only after directory-variable bookkeeping succeeds.</summary>
    public void CompleteDirectoryChange(DirectoryChangeKind kind, DirectoryStackEntry previous, EvaluationMemory memory)
    {
        switch (kind)
        {
            case DirectoryChangeKind.PushDirectory:
                PushStackEntry(previous, memory);
                break;
            case DirectoryChangeKind.ChangeDirectory:
            case DirectoryChangeKind.SwapDirectories:
            case DirectoryChangeKind.RotateDirectories:
                break;
            case DirectoryChangeKind.PopDirectory:
                RemoveSavedDirectory(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private void PushStackEntry(DirectoryStackEntry entry, EvaluationMemory memory)
    {
        if (_directoryStack.Count == _directoryStack.Capacity)
        {
            var capacity = (int)Math.Min(Array.MaxLength, Math.Max(4L, 2L * _directoryStack.Capacity));
            if (capacity <= _directoryStack.Count)
                throw new ByteMemoryBuilder.LimitExceededException();
            // Replacement and the old backing overlap during List's copy.
            memory.Charge(EstimateDirectoryStackBytes(capacity));
            _directoryStack.Capacity = capacity;
        }
        _directoryStack.Insert(_directoryStack.Count - 1, entry);
        _ownedDirectoryPathBytes = checked(_ownedDirectoryPathBytes + entry.OwnedBytes);
    }

    public ReadOnlyMemory<byte> FormatDirectoryStack(int? selected, DirectoryStackLayout layout, ShellValue home,
        EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var start = selected ?? 0;
        var end = selected.HasValue ? start + 1 : _directoryStack.Count;
        var homeBytes = home.BeforeFirstNul().Bytes.Span;
        if (homeBytes.Length <= 1)
            homeBytes = default;
        var encoding = Utf8Text.StrictUtf8;
        const int MaxStackHomeBytes = 256;
        // Resolved paths are absolute. A deferred selection compares the original bytes directly.
        var decodeHome = homeBytes is [(byte)'/', ..] && (!selected.HasValue || GetDirectoryStackEntry(start).Path != null);
        // Valid UTF-8 needs at most one UTF-16 code unit per input byte.
        scoped Span<char> homeCharacters = decodeHome && homeBytes.Length <= MaxStackHomeBytes
            ? stackalloc char[homeBytes.Length] : default;
        scoped ReadOnlySpan<char> homeText = default;
        using var homeMemory = decodeHome && homeCharacters.IsEmpty && Utf8.IsValid(homeBytes)
            ? new MemoryCapacity(memory.Budget, 0) : null;
        if (!homeCharacters.IsEmpty)
        {
            if (Utf8.ToUtf16(homeBytes, homeCharacters, out _, out var written, replaceInvalidSequences: false)
                == OperationStatus.Done)
                homeText = homeCharacters[..written];
        }
        else if (homeMemory != null)
        {
            memory.Charge(homeMemory, 32L + 2L * encoding.GetCharCount(homeBytes));
            homeText = encoding.GetString(homeBytes).AsSpan();
        }

        long bytes = end - start;
        for (var index = start; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += FormatEntry(index, homeBytes, homeText, default);
        }
        if (bytes >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        memory.Charge(checked(32 + bytes));
        var output = new byte[(int)bytes];
        var offset = 0;
        for (var index = start; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offset += FormatEntry(index, homeBytes, homeText, output.AsSpan(offset));
            output[offset++] = layout == DirectoryStackLayout.Compact && index + 1 < end ? (byte)' ' : (byte)'\n';
        }
        return output;

        int FormatEntry(int index, ReadOnlySpan<byte> homePrefix, ReadOnlySpan<char> homeText, Span<byte> destination)
        {
            var writing = !destination.IsEmpty;
            var position = 0;
            if (layout == DirectoryStackLayout.Numbered)
            {
                Span<byte> digits = stackalloc byte[10];
                if (!Utf8Formatter.TryFormat(index, digits, out var written))
                    throw new InvalidOperationException("The directory stack index exceeds the decimal buffer.");
                var padding = written == 1 ? 1 : 0;
                position = padding + written + 2;
                if (writing)
                {
                    destination[..position].Fill((byte)' ');
                    digits[..written].CopyTo(destination[padding..]);
                }
            }

            var entry = GetDirectoryStackEntry(index);
            var path = entry.Path?.Path;
            var text = path.AsSpan();
            var raw = path == null ? entry.DeferredPath.Bytes.Span : default;
            var abbreviate = path == null
                ? MatchesHome(raw, homePrefix, (byte)'/')
                : MatchesHome(text, homeText, '/');
            if (abbreviate)
            {
                if (writing)
                    destination[position] = (byte)'~';
                position++;
                if (path == null)
                    raw = raw[homePrefix.Length..];
                else
                    text = text[homeText.Length..];
            }
            if (path != null)
                return checked(position + (writing ? encoding.GetBytes(text, destination[position..]) : encoding.GetByteCount(text)));
            if (writing)
                raw.CopyTo(destination[position..]);
            return checked(position + raw.Length);
        }

        static bool MatchesHome<T>(ReadOnlySpan<T> path, ReadOnlySpan<T> prefix, T separator) where T : IEquatable<T> =>
            !prefix.IsEmpty && path.StartsWith(prefix) && (path.Length == prefix.Length || path[prefix.Length].Equals(separator));
    }
}

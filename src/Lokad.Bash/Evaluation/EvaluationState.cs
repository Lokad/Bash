using System;
using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Distinguishes failures recoverable at eval/source boundaries from shell-fatal expansions.</summary>
internal enum ExpansionFailureKind
{
    Arithmetic,
    /// <summary>A generated word fails substitution parsing and discards its command unit.</summary>
    WordSyntax,
    Parameter,
    /// <summary>An errexit-enabled file substitution could not open its input.</summary>
    InputSubstitution,
    Assignment,
    /// <summary>A parameter-default assignment rejected an unset nameref target (status 2).</summary>
    NamerefDefault,
    /// <summary>A direct write failed; eval/source can return control to their caller.</summary>
    AssignmentTarget
}

internal readonly record struct ExpansionFailure(ExpansionFailureKind Kind, int ExitCode);

[Flags]
internal enum FunctionAttributes { None = 0, Readonly = 1, Exported = 2 }

internal enum FunctionDisplayStyle { Name, Attributes, Definition, DefinitionWithAttributes }

/// <summary>Immutable runtime binding; child evaluations may share it until an attribute or definition changes.</summary>
internal sealed record FunctionBinding
{
    public FunctionDefinition Definition { get; }
    public SourceMemory? SourceMemory { get; }
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
    /// Stores a pending expansion error exit code; set by expansion failures and consumed on next check.
    /// </summary>
    private int? _pendingExpansionErrorExitCode;

    /// <summary>Stops preparation before later operands or command effects after an expansion failure.</summary>
    public bool HasPendingExpansionError => _pendingExpansionErrorExitCode.HasValue;

    /// <summary>Fatal noninteractive expansion failures cannot be consumed by an OR-list or negation.</summary>
    public ExpansionFailure? FatalExpansion { get; set; }

    /// <summary>An exec invocation ended the shell; cleanup has run or been discarded, and enclosing timers cannot print.</summary>
    public bool ExitedThroughExec { get; set; }

    // Embedded and kept private so mutable scope state cannot escape as a value copy.
    private RedirectionState _redirections;

    /// <summary>
    /// Stores the directory stack with the current directory at index 0; updated by pushd/popd style builtins.
    /// </summary>
    private readonly List<DirectoryStackEntry> _directoryStack;
    private long _ownedDirectoryPathBytes;

    internal readonly record struct DirectoryStackEntry(ShellPath Path, long OwnedBytes);

    private static DirectoryStackEntry OwnPath(ShellPath path) => new(path, 48L + 2L * path.Path.Length);

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
        : this(script, new RedirectionState(inheritedDescriptorTable), new List<DirectoryStackEntry>(1) { OwnPath(currentDirectory) })
    {
        _ownedDirectoryPathBytes = _directoryStack[0].OwnedBytes;
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

    /// <summary>The shell's errexit option; tested execution contexts suppress its effect without changing it.</summary>
    public bool ErrexitEnabled { get; set; }

    /// <summary>
    /// Last substitution in the current simple command; null means none ran.
    /// Reset at command entry and do not copy into nested evaluation state.
    /// </summary>
    public int? LastCommandSubstitutionExitCode { get; set; }

    /// <summary>
    /// Stores the current working directory; initialized to "/" and updated by directory-changing builtins.
    /// </summary>
    public ShellPath CurrentDirectory => GetDirectoryStackPath(0);

    internal int DirectoryStackCount => _directoryStack.Count;
    internal ShellPath GetDirectoryStackPath(int index) => _directoryStack[index].Path;
    internal long RetainedDirectoryStackBytes => checked(EstimateDirectoryStackBytes(_directoryStack.Capacity)
        + _ownedDirectoryPathBytes);

    private static long EstimateDirectoryStackBytes(int capacity) => 32L + 16L * capacity;

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
            foreach (var binding in _functions.Values)
                if (binding.Attributes.HasFlag(FunctionAttributes.Exported))
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

    internal void ReleaseExportedFunctionSelection()
    {
        _exportedFunctions = null;
        _exportedFunctionsMemory?.Dispose();
        _exportedFunctionsMemory = null;
    }

    public ShellTraps? Traps { get; private set; }

    public ShellTraps GetOrCreateTraps() => Traps ??= new();

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
        clone._pendingExpansionErrorExitCode = _pendingExpansionErrorExitCode;
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
            childStack.Add(new DirectoryStackEntry(entry.Path, OwnedBytes: 0));
        var clone = new EvaluationState(script, redirections, childStack)
        {
            LastExitCode = LastExitCode,
            ErrexitEnabled = kind != ShellForkKind.CommandSubstitution && ErrexitEnabled,
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
    public void SetPersistentDescriptor(LogicalDescriptor descriptor, FileDescriptor handle, EvaluationMemory? memory, bool acquired) =>
        _redirections.SetPersistentDescriptor(descriptor, handle, memory, acquired);

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
    public bool TryConsumeExpansionError(out int exitCode)
    {
        if (_pendingExpansionErrorExitCode.HasValue)
        {
            exitCode = _pendingExpansionErrorExitCode.Value;
            _pendingExpansionErrorExitCode = null;
            return true;
        }

        exitCode = 0;
        return false;
    }

    public void SetExpansionError(int exitCode)
    {
        _pendingExpansionErrorExitCode = exitCode;
    }

    /// <summary>Redefinition retains attributes and fails without changing a readonly function.</summary>
    public bool TryRegisterFunction(FunctionDefinition functionDefinition, SourceMemory? sourceMemory, EvaluationMemory memory)
    {
        var attributes = _functions.TryGetValue(functionDefinition.Name.Value, out var previous)
            ? previous.Attributes : FunctionAttributes.None;
        if (attributes.HasFlag(FunctionAttributes.Readonly))
            return false;
        memory.Charge(256);
        SetFunctionBinding(new FunctionBinding(functionDefinition, attributes, sourceMemory, memory));
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
        if (_functions.TryGetValue(name, out var function) && function.Attributes.HasFlag(FunctionAttributes.Readonly))
            return false;
        if (_functions.Remove(name, out var removed))
        {
            if (removed.Attributes.HasFlag(FunctionAttributes.Exported))
            {
                ReleaseExportedFunctionSelection();
                _exportedFunctionCount--;
            }
            removed.Release();
        }
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
        _directoryStack.RemoveRange(1, _directoryStack.Count - 1);
        _ownedDirectoryPathBytes = _directoryStack[0].OwnedBytes;
    }

    /// <summary>Applies a settled lookup, including Bash's partial stack change after a rejected swap.</summary>
    public DirectoryStackEntry ApplyDirectoryLookup(DirectoryChangeKind kind, ShellPath target, bool succeeded, EvaluationMemory memory)
    {
        var previous = _directoryStack[0];
        var fromStack = kind is DirectoryChangeKind.PopDirectory or DirectoryChangeKind.SwapDirectories;
        var destination = fromStack ? _directoryStack[1] : ReferenceEquals(previous.Path, target) ? previous : OwnPath(target);
        if (kind == DirectoryChangeKind.SwapDirectories)
        {
            if (!succeeded)
                memory.Charge(Math.Max(0, previous.OwnedBytes - destination.OwnedBytes));
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
            _ownedDirectoryPathBytes = checked(_ownedDirectoryPathBytes + entry.OwnedBytes - _directoryStack[index].OwnedBytes);
            _directoryStack[index] = entry;
        }
    }

    /// <summary>Completes stack publication only after directory-variable bookkeeping succeeds.</summary>
    public void CompleteDirectoryChange(DirectoryChangeKind kind, DirectoryStackEntry previous, EvaluationMemory memory)
    {
        switch (kind)
        {
            case DirectoryChangeKind.PushDirectory:
                if (_directoryStack.Count == _directoryStack.Capacity)
                {
                    var capacity = (int)Math.Min(Array.MaxLength, Math.Max(4L, 2L * _directoryStack.Capacity));
                    if (capacity <= _directoryStack.Count)
                        throw new ByteMemoryBuilder.LimitExceededException();
                    // Replacement and the old backing overlap during List's copy.
                    memory.Charge(EstimateDirectoryStackBytes(capacity));
                    _directoryStack.Capacity = capacity;
                }
                _directoryStack.Insert(1, previous);
                _ownedDirectoryPathBytes = checked(_ownedDirectoryPathBytes + previous.OwnedBytes);
                break;
            case DirectoryChangeKind.ChangeDirectory:
            case DirectoryChangeKind.SwapDirectories:
                break;
            case DirectoryChangeKind.PopDirectory:
                _ownedDirectoryPathBytes -= _directoryStack[1].OwnedBytes;
                _directoryStack.RemoveAt(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public ReadOnlyMemory<byte> FormatDirectoryStack(bool currentOnly, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var count = currentOnly ? 1 : _directoryStack.Count;
        long bytes = count; // A space after each path except the last, which ends with LF.
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += Utf8Text.StrictUtf8.GetByteCount(GetDirectoryStackPath(index).Path);
        }
        if (bytes >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        memory.Charge(checked(32 + bytes));
        var output = new byte[(int)bytes];
        var offset = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offset += Utf8Text.StrictUtf8.GetBytes(GetDirectoryStackPath(index).Path.AsSpan(), output.AsSpan(offset));
            output[offset++] = index + 1 == count ? (byte)'\n' : (byte)' ';
        }
        return output;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
    Parameter,
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

    /// <summary>
    /// Stores a sticky exit code that should be returned even if later commands succeed.
    /// </summary>
    private int? _stickyExitCode;

    // Embedded and kept private so mutable scope state cannot escape as a value copy.
    private RedirectionState _redirections;

    /// <summary>
    /// Stores the directory stack with the current directory at index 0; updated by pushd/popd style builtins.
    /// </summary>
    private readonly List<ShellPath> _directoryStack = [];

    /// <summary>One function namespace for invocation, attributes and inspection; independent of variables.</summary>
    private readonly Dictionary<ShellValue, FunctionBinding> _functions = [];
    private int _functionCapacity;

    // Capacity covers dictionary growth/overlap; live entries cover binding/cache-cell metadata.
    internal long EstimatedFunctionMemoryBytes => 128L * _functionCapacity + 128L * _functions.Count;

    public EvaluationState(
        ParsedScript script,
        ShellPath currentDirectory,
        DescriptorTable? inheritedDescriptorTable)
        : this(script, currentDirectory, new RedirectionState(inheritedDescriptorTable))
    {
    }

    private EvaluationState(ParsedScript script, ShellPath currentDirectory, RedirectionState redirections)
    {
        Script = script;
        CurrentDirectory = currentDirectory;
        _redirections = redirections;
        _directoryStack.Add(CurrentDirectory);
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
    public ShellPath CurrentDirectory { get; private set; }

    public IReadOnlyList<ShellPath> DirectoryStack => _directoryStack;

    public IReadOnlyDictionary<ShellValue, FunctionBinding> Functions => _functions;

    public ShellTraps? Traps { get; private set; }

    public ShellTraps GetOrCreateTraps() => Traps ??= new();

    public DescriptorTable? CurrentRedirectionTable => _redirections.CurrentTable;

    public IReadOnlyList<FileDescriptor> CurrentRedirectionOpenedDescriptors => _redirections.OpenedDescriptors;

    /// <summary>
    /// Creates an isolated shell-state copy for subshell evaluation.
    /// </summary>
    public EvaluationState CloneForSubshell(EvaluationMemory memory)
    {
        memory.Charge(EstimatedFunctionMemoryBytes);
        var clone = CloneShellState(Script, _redirections.ForkForSubshell(), ShellForkKind.Subshell);
        clone._pendingExpansionErrorExitCode = _pendingExpansionErrorExitCode;
        clone.FatalExpansion = FatalExpansion;
        clone._stickyExitCode = _stickyExitCode;
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
        memory.Charge(EstimatedFunctionMemoryBytes);
        return CloneShellState(script, _redirections.ForkForNestedEvaluation(descriptors.Bindings), kind);
    }

    private EvaluationState CloneShellState(ParsedScript script, RedirectionState redirections, ShellForkKind kind)
    {
        var clone = new EvaluationState(script, CurrentDirectory, redirections)
        {
            LastExitCode = LastExitCode,
            ErrexitEnabled = kind != ShellForkKind.CommandSubstitution && ErrexitEnabled,
            Traps = Traps?.Fork(kind)
        };

        clone._directoryStack.Clear();
        clone._directoryStack.AddRange(_directoryStack);

        foreach (var function in _functions)
            clone.SetFunctionBinding(function.Value);

        return clone;
    }

    /// <summary>Appends active redirection handles, excluding handles supplied by the caller.</summary>
    public void AddRedirectionHandlesTo(
        ref DescriptorHandleCollector handles,
        IReadOnlyList<FileDescriptor> excludedHandles) =>
        _redirections.AddHandlesTo(ref handles, excludedHandles);

    public void PushRedirectionTable(
        DescriptorTable table,
        IReadOnlyList<FileDescriptor> openedDescriptors,
        IReadOnlySet<LogicalDescriptor> changedDescriptors) =>
        _redirections.Push(table, openedDescriptors, changedDescriptors);

    public IReadOnlyList<FileDescriptor> PopRedirectionTable() => _redirections.Pop();

    /// <summary>Commits the current scope's changed bindings through their temporary restoration boundaries.</summary>
    public IReadOnlyList<FileDescriptor> PreserveRedirectionTable() => _redirections.Preserve();

    /// <summary>Persists a binding through enclosing scopes up to its temporary restoration boundary.</summary>
    public void SetPersistentDescriptor(LogicalDescriptor descriptor, FileDescriptor handle) =>
        _redirections.SetPersistentDescriptor(descriptor, handle);

    /// <summary>Transfers handles to their longest-lived referencing scope; returns unreferenced handles in reverse order.</summary>
    public IReadOnlyList<FileDescriptor> TransferRedirectionDescriptors(IReadOnlyList<FileDescriptor> descriptors) =>
        _redirections.TransferHandles(descriptors);

    public bool ReferencesDescriptor(FileDescriptor descriptor) => _redirections.References(descriptor);

    public bool TryTakeRedirectionDescriptor(FileDescriptor descriptor) => _redirections.TryTakeHandle(descriptor);

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

    public bool TryGetStickyExitCode(out int exitCode)
    {
        if (_stickyExitCode.HasValue)
        {
            exitCode = _stickyExitCode.Value;
            return true;
        }

        exitCode = 0;
        return false;
    }

    public void SetStickyExitCode(int exitCode)
    {
        if (exitCode == 0)
            return;

        _stickyExitCode ??= exitCode;
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
            removed.Release();
        return true;
    }

    /// <summary>Retires a temporary namespace only after its execution and cleanup have settled.</summary>
    public void ReleaseFunctions()
    {
        foreach (var function in _functions.Values)
            function.Release();
        _functions.Clear();
    }

    public void ResetDirectoryStack()
    {
        EnsureDirectoryStack();
        _directoryStack.Clear();
        _directoryStack.Add(CurrentDirectory);
    }

    /// <summary>Applies a settled lookup, including Bash's partial stack change after a rejected swap.</summary>
    public void ApplyDirectoryLookup(DirectoryChangeKind kind, ShellPath target, bool succeeded)
    {
        if (kind == DirectoryChangeKind.SwapDirectories)
            _directoryStack[1] = CurrentDirectory;
        if (!succeeded)
            return;

        CurrentDirectory = target;
        EnsureDirectoryStack();
        _directoryStack[0] = target;
    }

    /// <summary>Completes stack publication only after directory-variable bookkeeping succeeds.</summary>
    public void CompleteDirectoryChange(DirectoryChangeKind kind, ShellPath previous)
    {
        switch (kind)
        {
            case DirectoryChangeKind.PushDirectory:
                _directoryStack.Insert(1, previous);
                break;
            case DirectoryChangeKind.ChangeDirectory:
            case DirectoryChangeKind.SwapDirectories:
                break;
            case DirectoryChangeKind.PopDirectory:
                _directoryStack.RemoveAt(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private void EnsureDirectoryStack()
    {
        if (_directoryStack.Count == 0)
        {
            _directoryStack.Add(CurrentDirectory);
        }
    }

    public ReadOnlyMemory<byte> FormatDirectoryStack()
    {
        EnsureDirectoryStack();
        var output = string.Join(' ', _directoryStack.Select(p => p.Path)) + "\n";
        return Utf8Text.Encode(output);
    }
}

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
    public FunctionAttributes Attributes { get; init; }

    // Attribute copies and child evaluations share this cell, including before its first publication.
    private readonly ExportCache _exportCache = new();

    public FunctionBinding(FunctionDefinition definition, FunctionAttributes attributes)
    {
        Definition = definition;
        Attributes = attributes;
    }

    /// <summary>Retains one bounded serialization until redefinition creates a new binding.</summary>
    public bool TryGetExport(int maximumBytes, IMemoryBudget memoryBudget, out ExportedFunction export)
    {
        export = default;
        var cached = Volatile.Read(ref _exportCache.Value);
        if (cached == null)
        {
            using var output = new BoundedByteMemoryBuilder(0, Math.Min(maximumBytes, Parser.MaximumScriptBytes), memoryBudget);
            if (!FunctionDefinitionFormatter.TryAppendExport(Definition, output)) return false;
            var rendered = new CachedExport(new ExportedFunction(output.ToMemory()));
            // Concurrent first readers may render independently; no evaluator waits for another one.
            cached = Interlocked.CompareExchange(ref _exportCache.Value, rendered, null);
            if (cached == null)
            {
                // Only the published buffer outlives this builder; a losing candidate is refunded.
                _ = output.DetachSnapshot();
                cached = rendered;
            }
        }
        if (cached.Export.Source.Length > maximumBytes) return false;
        export = cached.Export;
        return true;
    }

    private sealed class ExportCache
    {
        public CachedExport? Value;
    }

    private sealed record CachedExport(ExportedFunction Export);

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
    public EvaluationState CloneForSubshell()
    {
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
        ShellForkKind kind) =>
        CloneShellState(script, _redirections.ForkForNestedEvaluation(descriptors.Bindings), kind);

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
            clone._functions[function.Key] = function.Value;

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

    internal long FunctionDefinitionVersion { get; private set; }

    /// <summary>Redefinition retains attributes and fails without changing a readonly function.</summary>
    public bool TryRegisterFunction(FunctionDefinition functionDefinition)
    {
        var attributes = _functions.TryGetValue(functionDefinition.Name.Value, out var previous)
            ? previous.Attributes : FunctionAttributes.None;
        if (attributes.HasFlag(FunctionAttributes.Readonly))
            return false;
        _functions[functionDefinition.Name.Value] = new FunctionBinding(functionDefinition, attributes);
        FunctionDefinitionVersion++;
        return true;
    }

    public void SetFunctionBinding(FunctionBinding function) =>
        _functions[function.Definition.Name.Value] = function;

    /// <summary>Removes the current binding, not a definition already being invoked.</summary>
    public bool TryUnsetFunction(ShellValue name)
    {
        if (_functions.TryGetValue(name, out var function) && function.Attributes.HasFlag(FunctionAttributes.Readonly))
            return false;
        _functions.Remove(name);
        return true;
    }

    public void ResetDirectoryStack()
    {
        EnsureDirectoryStack();
        _directoryStack.Clear();
        _directoryStack.Add(CurrentDirectory);
    }

    public bool TryPopDirectory()
    {
        if (_directoryStack.Count < 2)
        {
            return false;
        }

        _directoryStack.RemoveAt(0);
        CurrentDirectory = _directoryStack[0];
        return true;
    }

    public void ApplyDirectoryChange(DirectoryChangeKind kind, ShellPath target)
    {
        switch (kind)
        {
            case DirectoryChangeKind.ChangeDirectory:
                CurrentDirectory = target;
                EnsureDirectoryStack();
                _directoryStack[0] = target;
                break;
            case DirectoryChangeKind.PushDirectory:
                EnsureDirectoryStack();
                _directoryStack.Insert(0, target);
                CurrentDirectory = target;
                break;
        }
    }

    private void EnsureDirectoryStack()
    {
        if (_directoryStack.Count == 0)
        {
            _directoryStack.Add(CurrentDirectory);
        }
    }

    public void SwapDirectoryStack()
    {
        (_directoryStack[0], _directoryStack[1]) = (_directoryStack[1], _directoryStack[0]);
        CurrentDirectory = _directoryStack[0];
    }

    public ReadOnlyMemory<byte> FormatDirectoryStack()
    {
        EnsureDirectoryStack();
        var output = string.Join(' ', _directoryStack.Select(p => p.Path)) + "\n";
        return Utf8Text.Encode(output);
    }
}

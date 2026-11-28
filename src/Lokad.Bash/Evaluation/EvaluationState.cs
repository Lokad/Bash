using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
    /// <summary>A reached substitution ends in a bare pipeline prefix with errexit disabled.</summary>
    BarePipelinePrefixSyntax,
    /// <summary>Rereading rendered capture or array syntax terminates the current reader.</summary>
    ReaderSyntax,
    Parameter,
    /// <summary>An invalid parameter target discards the current command unit.</summary>
    ParameterTarget,
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
    /// <summary>A read rejected an array subscript while the reader had errexit active.</summary>
    ArrayRead,
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
    internal ExpansionFailure(ExpansionFailureKind kind)
        : this(kind, kind == ExpansionFailureKind.NamerefDefault ? 2 : 1) { }

    internal bool IsTerminalParameterFailure =>
        Kind is ExpansionFailureKind.RequiredParameter or ExpansionFailureKind.InvalidParameterTransform;

    // A local option scope may be restored before the loaded script reader handles the failure.
    internal bool CanResumeUntestedUnit { get; init; }

    // Argument expansion in a direct pipeline stage precedes the child boundary that resets EXIT.
    internal bool UseInheritedPipelineExitAction { get; init; }

    // A terminal parameter failure with errexit off exits through the active loaded readers.
    internal bool UnwindsLoadedReader { get; init; }

    // Carries the command-string exit status through loaded-reader cleanup.
    internal bool ExitsCommandString { get; init; }
}

internal enum ExpansionErrorKind
{
    General,
    HereDocumentSyntax,
    HereDocumentReaderSyntax,
    HereDocumentArithmeticHeader,
    HereDocumentNestedSyntax,
    HereDocumentWordSyntax,
    HereDocumentMissingBrace,
    HereDocumentParameter
}

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
    public bool TryGetExport(int maximumBytes, out ExportedFunction export) =>
        TryGetExport(maximumBytes, out export, out _);

    internal bool TryGetExport(int maximumBytes, out ExportedFunction export, out bool hasDecodedNulCapture)
    {
        export = default;
        hasDecodedNulCapture = false;
        var cached = Volatile.Read(ref _exportCache.Value);
        if (cached == null)
        {
            var memory = _exportCache.Memory.Budget.CreateScope();
            var published = false;
            try
            {
                _ = memory.Reserve(192); // Cache/scope/result metadata and backing-array headers.
                using var output = new BoundedByteMemoryBuilder(0, Math.Min(maximumBytes, Parser.MaximumScriptBytes), memory);
                if (!FunctionDefinitionFormatter.TryAppendExport(Definition, output, out var damagedCapture)) return false;
                var rendered = new CachedExport(new ExportedFunction(output.ToMemory()), damagedCapture, memory);
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
        hasDecodedNulCapture = cached.HasDecodedNulCapture;
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

    private sealed record CachedExport(ExportedFunction Export, bool HasDecodedNulCapture, IMemoryScope Memory);

    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public bool TryAppend(ByteMemoryBuilder output, FunctionDisplayStyle style)
    {
        if (style is FunctionDisplayStyle.Definition or FunctionDisplayStyle.DefinitionWithAttributes)
        {
            if (!FunctionDefinitionFormatter.TryAppend(Definition, output)) return false;
            if (style == FunctionDisplayStyle.Definition || Attributes == FunctionAttributes.None) return true;
        }
        var declaration = "declare -f"u8;
        Span<byte> prefix = stackalloc byte[declaration.Length + 6]; // Five flags and a space.
        var prefixLength = 0;
        if (style != FunctionDisplayStyle.Name)
        {
            declaration.CopyTo(prefix);
            prefixLength = declaration.Length;
            if (Attributes.HasFlag(FunctionAttributes.Readonly)) prefix[prefixLength++] = (byte)'r';
            if (Attributes.HasFlag(FunctionAttributes.Exported)) prefix[prefixLength++] = (byte)'x';
            if (Attributes.HasFlag(FunctionAttributes.Capitalize)) prefix[prefixLength++] = (byte)'c';
            if (Attributes.HasFlag(FunctionAttributes.Lowercase)) prefix[prefixLength++] = (byte)'l';
            if (Attributes.HasFlag(FunctionAttributes.Uppercase)) prefix[prefixLength++] = (byte)'u';
            prefix[prefixLength++] = (byte)' ';
        }

        var name = Definition.Name.Value.Bytes.Span;
        var length = (long)prefixLength + name.Length + 1;
        if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes || !output.CanAppend((int)length))
            return false;

        var row = output.AppendSpan((int)length, exactCapacity: true);
        prefix[..prefixLength].CopyTo(row);
        name.CopyTo(row[prefixLength..]);
        row[^1] = (byte)'\n';
        return true;
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

    /// <summary>A rejected here-document capture left temporary command prefixes out of ordinary builtin lookup.</summary>
    internal bool SuppressTemporaryLookup { get; set; }

    /// <summary>Fatal noninteractive expansion failures cannot be consumed by an OR-list or negation.</summary>
    public ExpansionFailure? FatalExpansion { get; set; }

    /// <summary>An exec invocation ended the shell; cleanup has run or been discarded, and enclosing timers cannot print.</summary>
    public bool ExitedThroughExec { get; set; }

    /// <summary>The active command label before a simple command publishes its own name.</summary>
    internal ShellValue PreparationDiagnosticName { get; set; }

    /// <summary>Borrows an invocation's name through execution and clears the label when it completes.</summary>
    internal readonly struct CommandDiagnosticScope : IDisposable
    {
        private readonly EvaluationState _state;

        internal CommandDiagnosticScope(EvaluationState state, ShellValue name)
        {
            _state = state;
            state.PreparationDiagnosticName = name;
        }

        public void Dispose() => _state.PreparationDiagnosticName = default;
    }

    // Embedded and kept private so mutable scope state cannot escape as a value copy.
    private RedirectionState _redirections;

    /// <summary>
    /// Stores entries in reverse logical order, with the current directory at the end.
    /// </summary>
    private readonly List<DirectoryStackEntry> _directoryStack;
    private long _ownedDirectoryPathBytes;
    private const int ResolvedPathValueOverheadBytes = 64;

    private sealed record ResolvedDirectoryPath(ShellPath Path, ShellValue Value);

    internal readonly struct DirectoryStackEntry
    {
        // Both forms use a path reference and an ownership estimate.
        private readonly object _path;

        private DirectoryStackEntry(object path, long ownedBytes)
        {
            _path = path;
            OwnedBytes = ownedBytes;
        }

        internal ShellPath? Path => (_path as ResolvedDirectoryPath)?.Path;
        internal ShellValue Value => _path is ResolvedDirectoryPath resolved ? resolved.Value : DeferredPath;
        internal ShellValue DeferredPath => ShellValue.FromBytes((byte[])_path);
        internal long OwnedBytes { get; }

        internal static DirectoryStackEntry Resolved(ShellPath path)
        {
            var value = ShellValue.FromText(path.Path);
            return new(new ResolvedDirectoryPath(path, value),
                48L + 2L * path.Path.Length + ResolvedPathValueOverheadBytes + value.Bytes.Length);
        }

        internal object PathIdentity => _path;
        internal static DirectoryStackEntry Deferred(byte[] path, long ownedBytes) => new(path, ownedBytes);
        internal DirectoryStackEntry Borrow() => new(_path, 0);
    }

    /// <summary>One function namespace for invocation, attributes and inspection; independent of variables.</summary>
    private readonly Dictionary<ShellValue, int> _functions = [];
    private readonly List<FunctionSlot> _functionSlots = [];
    private const int FunctionSlotsPerBlock = 64;
    private const uint InitialFunctionBucketCount = 512;
    private uint _functionBucketCount = InitialFunctionBucketCount;
    private long _functionInsertionSequence;
    private bool _oddFunctionRehash;
    private readonly List<FunctionSlotMasks> _functionSlotMasks = [];
    // A single block is cheap to scan. Index larger namespaces so forks skip empty blocks.
    private SortedSet<int>? _occupiedFunctionBlocks;
    private readonly SortedSet<int> _exportedFunctionBlocks = [];
    private int _freeFunctionSlot = -1;
    private int _functionCapacity;
    private int _exportedFunctionCount;
    private FunctionBinding[]? _exportedFunctions;
    private IMemoryReservation? _exportedFunctionsMemory;

    // Occupied slots keep their cached export index. Empty slots link to the next free slot.
    private readonly record struct FunctionSlot(FunctionBinding? Binding, int Index, long InsertionRank);
    private readonly record struct FunctionSlotMasks(ulong Occupied, ulong Exported);

    // The capacity estimate covers the name map, slots, and membership indexes.
    // The live-binding estimate covers binding and cache-cell metadata.
    private const long FunctionCapacityBytesPerSlot = 136;
    private const long FunctionBindingBytes = 128;
    internal const long FunctionPublicationBytes = FunctionCapacityBytesPerSlot + FunctionBindingBytes;

    internal long EstimatedFunctionMemoryBytes => EstimateFunctionMemoryBytes(_functionCapacity, _functions.Count);

    private static long EstimateFunctionMemoryBytes(int capacity, int count) =>
        FunctionCapacityBytesPerSlot * capacity + FunctionBindingBytes * count;

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
    internal bool UsesCommandStringExpansionExit { get; set; }
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

    internal int FunctionCount => _functions.Count;
    internal bool HasExportedFunctions => _exportedFunctionCount != 0;

    internal bool TryGetFunction(ShellValue name, [NotNullWhen(true)] out FunctionBinding? binding)
    {
        var found = _functions.TryGetValue(name, out var slot);
        binding = found ? GetFunctionBinding(slot) : null;
        return found;
    }

    internal IEnumerable<KeyValuePair<ShellValue, FunctionBinding>> EnumerateFunctions()
    {
        foreach (var (name, slot) in _functions)
            yield return new KeyValuePair<ShellValue, FunctionBinding>(name, GetFunctionBinding(slot));
    }

    private FunctionBinding GetFunctionBinding(int slot) => _functionSlots[slot].Binding
        ?? throw new InvalidOperationException("The function slot is empty.");

    internal void SortFunctionImports(BashCommandInheritance.ImportEntry[] entries, EvaluationMemory memory)
    {
        using var scratch = memory.Budget.Reserve(MemoryCapacity.ArrayBackingBytes<(uint, long)>(entries.Length));
        var order = new (uint Bucket, long Chain)[entries.Length];
        var index = 0;
        // The export payload was built in this same slot order.
        if (_exportedFunctionBlocks.Count == 1)
            FillOrder(_exportedFunctionBlocks.Min);
        else
            foreach (var block in _exportedFunctionBlocks)
                FillOrder(block);
        Array.Sort(order, entries);

        void FillOrder(int block)
        {
            var first = block * FunctionSlotsPerBlock;
            var mask = _functionSlotMasks[block].Exported;
            while (TryTakeFunctionSlot(first, ref mask, out var slot))
            {
                // Bash's function table uses FNV-1 with signed bytes on the pinned builds.
                var hash = 2166136261u;
                foreach (var value in GetFunctionBinding(slot).Definition.Name.Value.Bytes.Span)
                    hash = unchecked((hash * 16777619u) ^ (uint)(sbyte)value);

                var rank = _functionSlots[slot].InsertionRank;
                order[index++] = (hash & (_functionBucketCount - 1), _oddFunctionRehash ? rank : -rank);
            }
        }
    }

    /// <summary>Borrows the current export selection in function namespace order.</summary>
    internal ReadOnlySpan<FunctionBinding> GetExportedFunctions(EvaluationMemory memory)
    {
        var count = _exportedFunctionCount;
        if (count == 0)
            return [];
        if (_exportedFunctions is { } cached)
            return cached;

        var reservation = memory.Budget.Reserve(MemoryCapacity.ArrayBackingBytes<FunctionBinding>(count));
        try
        {
            var selected = new FunctionBinding[count];
            var index = 0;
            foreach (var slot in EnumerateExportedFunctionSlots())
            {
                var binding = GetFunctionBinding(slot);
                _functionSlots[slot] = _functionSlots[slot] with { Index = index };
                selected[index++] = binding;
            }
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

    private IEnumerable<int> EnumerateExportedFunctionSlots() =>
        EnumerateFunctionSlots(_exportedFunctionBlocks, exported: true);

    private IEnumerable<int> EnumerateFunctionSlots(SortedSet<int> blocks, bool exported)
    {
        foreach (var block in blocks)
        {
            var first = block * FunctionSlotsPerBlock;
            var membership = _functionSlotMasks[block];
            var mask = exported ? membership.Exported : membership.Occupied;
            while (TryTakeFunctionSlot(first, ref mask, out var slot))
                yield return slot;
        }
    }

    private static bool TryTakeFunctionSlot(int first, ref ulong mask, out int slot)
    {
        if (mask == 0)
        {
            slot = 0;
            return false;
        }
        slot = first + BitOperations.TrailingZeroCount(mask);
        mask &= mask - 1;
        return true;
    }

    /// <summary>Clears the exported subset while an in-process replacement is in flight.</summary>
    internal FunctionExportClear? BeginClearFunctionExports(EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var count = _exportedFunctionCount;
        if (count == 0)
            return null;

        // Both versions can still be held after a nested edit.
        const int bindingPairBytes = 96;
        var staging = memory.Budget.Reserve(checked(64L
            + MemoryCapacity.ArrayBackingBytes<(FunctionBinding, FunctionBinding)>(count)
            + (long)bindingPairBytes * count));
        FunctionExportClear? clear = null;
        try
        {
            var changes = new (FunctionBinding Original, FunctionBinding Cleared)[count];
            var index = 0;
            if (_exportedFunctions is { } cached)
            {
                foreach (var binding in cached)
                    AddClearedBinding(binding);
            }
            else
            {
                foreach (var slot in EnumerateExportedFunctionSlots())
                    AddClearedBinding(GetFunctionBinding(slot));
            }

            cancellationToken.ThrowIfCancellationRequested();
            clear = new FunctionExportClear(this, changes, staging);
            clear.Apply();
            return clear;

            void AddClearedBinding(FunctionBinding binding)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changes[index++] = (binding, binding with { Attributes = binding.Attributes & ~FunctionAttributes.Exported });
            }
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
                        if (!_committed && owner.TryGetFunction(name, out var current) && ReferenceEquals(current, cleared))
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

    internal int RedirectionDepth => _redirections.Depth;

    internal RedirectionState.PipelineExitRedirections? CapturePipelineExitRedirections(int depth, bool useInherited,
        EvaluationMemory memory) =>
        _redirections.CaptureForPipelineExit(depth, useInherited, memory);

    internal RedirectionState.InheritedExitBindings? CaptureInheritedExitBindings(int depth, EvaluationMemory memory) =>
        _redirections.CaptureInheritedExitBindings(depth, memory);

    internal void RestorePipelineExitRedirections(RedirectionState.PipelineExitRedirections redirections, EvaluationMemory memory) =>
        _redirections.RestoreForPipelineExit(redirections, memory);

    internal DescriptorTable? CaptureLoadedExitTable(LoadedExitUnwind unwind, EvaluationMemory memory) =>
        _redirections.CaptureLoadedExitTable(unwind, memory);

    internal long RetainedDescriptorMemoryBytes => _redirections.OwnedMemoryBytes;

    public IReadOnlyCollection<FileDescriptor> CurrentRedirectionOpenedDescriptors => _redirections.OpenedDescriptors;

    /// <summary>
    /// Creates an isolated shell-state copy for subshell evaluation.
    /// </summary>
    public EvaluationState CloneForSubshell(EvaluationMemory memory, RedirectionState.InheritedExitBindings? inheritedExitBindings)
    {
        // Keep the parent's saved handlers admitted through restoration if the child clears them.
        memory.Charge(checked(EstimateForkBytes() + (Traps?.EstimatedMemoryBytes ?? 0)));
        var clone = CloneShellState(Script, _redirections.ForkForSubshell(inheritedExitBindings), ShellForkKind.Subshell);
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
        EvaluationMemory memory,
        RedirectionState.InheritedExitBindings? inheritedExitBindings)
    {
        memory.Charge(EstimateForkBytes());
        return CloneShellState(script, _redirections.ForkForNestedEvaluation(descriptors.Bindings, inheritedExitBindings), kind);
    }

    private long EstimateForkBytes() => checked(EstimateFunctionMemoryBytes(_functions.Count, _functions.Count)
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
            // The parent command remains alive through the child's execution and cleanup.
            PreparationDiagnosticName = PreparationDiagnosticName,
            FunctionBraceLine = FunctionBraceLine,
            ErrexitEnabled = kind != ShellForkKind.CommandSubstitution && ErrexitEnabled,
            UsesCommandStringExpansionExit = kind == ShellForkKind.PipelineSimpleCommand && UsesCommandStringExpansionExit,
            SuppressTemporaryLookup = kind != ShellForkKind.CommandSubstitution && SuppressTemporaryLookup,
            ForkKind = kind,
            Traps = Traps?.Fork(kind),
            _functionBucketCount = _functionBucketCount,
            _functionInsertionSequence = _functionInsertionSequence,
            _oddFunctionRehash = _oddFunctionRehash
        };

        var functionCount = _functions.Count;
        if (functionCount == 0)
            return clone;

        clone._functions.EnsureCapacity(functionCount);
        clone._functionSlots.EnsureCapacity(functionCount);
        clone._functionSlotMasks.EnsureCapacity((functionCount - 1) / FunctionSlotsPerBlock + 1);
        if (_occupiedFunctionBlocks is { } occupiedBlocks)
        {
            foreach (var position in EnumerateFunctionSlots(occupiedBlocks, exported: false))
                clone.SetFunctionBinding(GetFunctionBinding(position), _functionSlots[position].InsertionRank);
        }
        else
        {
            foreach (var slot in _functionSlots)
                if (slot.Binding is { } binding)
                    clone.SetFunctionBinding(binding, slot.InsertionRank);
        }

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
        EvaluationMemory? memory, OrderedDescriptorSet? pending, IReadOnlySet<FileDescriptor>? borrowed) =>
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

    public void SetExpansionError(int exitCode, ExpansionErrorKind kind = ExpansionErrorKind.General) =>
        _pendingExpansionError = (exitCode, kind);

    internal void SetHereDocumentParameterError() => _pendingExpansionError = (1, ExpansionErrorKind.HereDocumentParameter);

    /// <summary>Redefinition retains attributes and fails without changing a readonly function.</summary>
    public bool TryRegisterFunction(FunctionDefinition functionDefinition, Evaluation.EvaluationContext context, EvaluationMemory memory)
    {
        var attributes = TryGetFunction(functionDefinition.Name.Value, out var previous)
            ? previous.Attributes : FunctionAttributes.None;
        if (attributes.HasFlag(FunctionAttributes.Readonly))
            return false;
        memory.Charge(FunctionPublicationBytes);
        var coordinates = context.Coordinates;
        var bodyLine = coordinates.Locations?.TryGet(functionDefinition.Body, out var body) == true
            ? coordinates.Offset + body.StartLine : coordinates.Line;
        SetFunctionBinding(new FunctionBinding(functionDefinition, attributes, context.SourceMemory, memory)
        {
            Coordinates = coordinates with
            {
                FunctionBodyLine = bodyLine,
                CompoundOpenLine = context.SourceMemory?.Kind == SourceMemory.ReaderKind.ImportedFunction
                    ? bodyLine : coordinates.RedirectionOpenLine,
                EnclosingExecutionLine = 0,
                UnitReadLine = 0
            }
        });
        return true;
    }

    internal readonly record struct FunctionImport(FunctionBinding Binding, int ExportIndex, long InsertionRank);

    internal void PublishFunctionImports(List<FunctionImport> imports, bool inBucketOrder)
    {
        if (inBucketOrder)
        {
            // Preserve the native insertion history for future exports while retaining
            // the original order of slots observed by the host.
            var count = _functions.Count;
            for (var i = 0; i < imports.Count; i++)
                imports[i] = imports[i] with { InsertionRank = NextFunctionInsertionRank(++count) };
            imports.Sort(static (left, right) => left.ExportIndex.CompareTo(right.ExportIndex));
        }

        foreach (var import in imports)
            SetFunctionBinding(import.Binding, import.InsertionRank);
    }

    public void SetFunctionBinding(FunctionBinding function) => SetFunctionBinding(function, inheritedRank: 0);

    private void SetFunctionBinding(FunctionBinding function, long inheritedRank)
    {
        var name = function.Definition.Name.Value;
        var previous = _functions.TryGetValue(name, out var position) ? GetFunctionBinding(position) : null;
        var wasExported = previous?.Attributes.HasFlag(FunctionAttributes.Exported) == true;
        var isExported = function.Attributes.HasFlag(FunctionAttributes.Exported);
        if (previous == null)
        {
            position = AllocateSlot();
            SetOccupiedMembership(position, occupied: true);
        }
        // Replacing a dictionary value preserves its original key. Keep source-backed
        // name storage with the new definition's owner, rather than the retired source.
        else if (!previous.Definition.Name.Value.Bytes.Equals(name.Bytes))
            _functions.Remove(name);
        _functions[name] = position;
        _functionCapacity = Math.Max(_functionCapacity, _functions.Count);
        // Acquire before retiring the old binding: attribute copies can share this owner.
        function.Retain();
        var slot = _functionSlots[position];
        var rank = slot.InsertionRank;
        if (previous == null)
            rank = inheritedRank != 0 ? inheritedRank : NextFunctionInsertionRank(_functions.Count);
        _functionSlots[position] = new FunctionSlot(function, slot.Index, rank);
        if (wasExported != isExported)
            SetExportMembership(position, isExported);
        else if (isExported && _exportedFunctions is { } selected)
            selected[slot.Index] = function;
        previous?.Release();

        int AllocateSlot()
        {
            // Match namespace enumeration: reuse the most recently vacated slot first.
            if (_freeFunctionSlot >= 0)
            {
                var free = _freeFunctionSlot;
                _freeFunctionSlot = _functionSlots[free].Index;
                return free;
            }
            var next = _functionSlots.Count;
            if (next == FunctionSlotsPerBlock && _occupiedFunctionBlocks is null)
                _occupiedFunctionBlocks = [0];
            if (next % FunctionSlotsPerBlock == 0)
                _functionSlotMasks.Add(default);
            _functionSlots.Add(default);
            return next;
        }
    }

    private long NextFunctionInsertionRank(int count)
    {
        // A new name starts at the head of its bucket. Each later growth reverses
        // the chain for names still sharing that bucket. Rank sign records insertion parity.
        if (count > 2L * _functionBucketCount)
        {
            _functionBucketCount *= 4;
            _oddFunctionRehash = !_oddFunctionRehash;
        }
        var sequence = ++_functionInsertionSequence;
        return _oddFunctionRehash ? -sequence : sequence;
    }

    private void SetOccupiedMembership(int position, bool occupied)
    {
        var block = position / FunctionSlotsPerBlock;
        var bit = 1UL << (position % FunctionSlotsPerBlock);
        var membership = _functionSlotMasks[block];
        var next = occupied ? membership.Occupied | bit : membership.Occupied & ~bit;
        if (_occupiedFunctionBlocks is { } blocks)
            UpdateFunctionBlock(blocks, block, membership.Occupied, next);
        _functionSlotMasks[block] = membership with { Occupied = next };
    }

    /// <summary>Updates the export index for a slot whose membership changed.</summary>
    private void SetExportMembership(int position, bool exported)
    {
        var block = position / FunctionSlotsPerBlock;
        var bit = 1UL << (position % FunctionSlotsPerBlock);
        var membership = _functionSlotMasks[block];
        var next = exported ? membership.Exported | bit : membership.Exported & ~bit;
        UpdateFunctionBlock(_exportedFunctionBlocks, block, membership.Exported, next);
        _functionSlotMasks[block] = membership with { Exported = next };
        _exportedFunctionCount += exported ? 1 : -1;
        ReleaseExportedFunctionSelection();
    }

    private static void UpdateFunctionBlock(SortedSet<int> blocks, int block, ulong previous, ulong next)
    {
        if (previous == 0)
            blocks.Add(block);
        else if (next == 0)
            blocks.Remove(block);
    }

    /// <summary>Removes the current binding, not a definition already being invoked.</summary>
    public bool TryUnsetFunction(ShellValue name)
    {
        if (!_functions.TryGetValue(name, out var position))
            return true;
        var function = GetFunctionBinding(position);
        if (function.Attributes.HasFlag(FunctionAttributes.Readonly))
            return false;

        _functions.Remove(name);
        _functionSlots[position] = new FunctionSlot(null, _freeFunctionSlot, 0);
        _freeFunctionSlot = position;
        SetOccupiedMembership(position, occupied: false);
        if (function.Attributes.HasFlag(FunctionAttributes.Exported))
            SetExportMembership(position, exported: false);
        function.Release();
        return true;
    }

    /// <summary>Retires a temporary namespace only after its execution and cleanup have settled.</summary>
    public void ReleaseFunctions()
    {
        ReleaseExportedFunctionSelection();
        if (_occupiedFunctionBlocks is { } occupiedBlocks)
        {
            if (occupiedBlocks.Count != 0)
                foreach (var slot in EnumerateFunctionSlots(occupiedBlocks, exported: false))
                    GetFunctionBinding(slot).Release();
            occupiedBlocks.Clear();
        }
        else
        {
            foreach (var slot in _functionSlots)
                slot.Binding?.Release();
        }
        _exportedFunctionBlocks.Clear();
        _functionSlotMasks.Clear();
        _exportedFunctionCount = 0;
        _freeFunctionSlot = -1;
        _functionBucketCount = InitialFunctionBucketCount;
        _functionInsertionSequence = 0;
        _oddFunctionRehash = false;
        _functionSlots.Clear();
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

    /// <summary>Replaces one saved path without resolving it or changing the working directory.</summary>
    internal void SetSavedDirectory(IndexedArrayIndex index, ShellValue value, EvaluationMemory? memory)
    {
        if (!ContainsSavedDirectory(index.Value))
            return;
        var bytes = checked(48L + value.Bytes.Length);
        memory?.Charge(bytes);
        memory?.CancellationToken.ThrowIfCancellationRequested();
        ReplaceDeferredDirectory((int)index.Value, value);
    }

    internal bool ContainsSavedDirectory(long index) => index > 0 && index < DirectoryStackCount;

    /// <summary>Admits all replacement paths before publishing an already-read batch.</summary>
    internal void SetSavedDirectories(uint origin, IReadOnlyList<ShellValue> values,
        EvaluationMemory? memory, CancellationToken cancellationToken)
    {
        long bytes = 0;
        for (var offset = 0; offset < values.Count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsSavedDirectory(unchecked(origin + (uint)offset)))
                bytes = checked(bytes + 48L + values[offset].Bytes.Length);
        }
        memory?.Charge(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        for (var offset = 0; offset < values.Count; offset++)
        {
            var position = unchecked(origin + (uint)offset);
            if (ContainsSavedDirectory(position))
                ReplaceDeferredDirectory((int)position, values[offset]);
        }
    }

    /// <summary>Restores unpublished input writes while preserving stack edits made by callbacks.</summary>
    internal void RestoreSavedDirectoryWrites(Dictionary<object, DirectoryStackEntry> writes)
    {
        for (var index = 1; index < DirectoryStackCount; index++)
        {
            var entry = GetDirectoryStackEntry(index);
            if (!writes.TryGetValue(entry.PathIdentity, out var previous))
                continue;
            while (writes.TryGetValue(previous.PathIdentity, out var earlier))
                previous = earlier;
            ReplaceDirectoryStackEntry(index, previous);
        }
    }

    private void ReplaceDeferredDirectory(int index, ShellValue value)
    {
        var path = new byte[value.Bytes.Length];
        value.Bytes.Span.CopyTo(path);
        ReplaceDirectoryStackEntry(index, DirectoryStackEntry.Deferred(path, 48L + path.Length));
    }

    private void ReplaceDirectoryStackEntry(int index, DirectoryStackEntry entry)
    {
        var slot = DirectoryStackSlot(index);
        _ownedDirectoryPathBytes = checked(_ownedDirectoryPathBytes + entry.OwnedBytes - _directoryStack[slot].OwnedBytes);
        _directoryStack[slot] = entry;
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
        var destination = stored;
        if (succeeded && !ReferenceEquals(stored.Path, target))
        {
            memory.Charge(ResolvedPathValueOverheadBytes + 3L * target.Path.Length);
            destination = DirectoryStackEntry.Resolved(target);
        }
        if (kind == DirectoryChangeKind.SwapDirectories)
        {
            if (!succeeded)
                memory.Charge(Math.Max(0, previous.OwnedBytes - stored.OwnedBytes));
            ReplaceDirectoryStackEntry(1, previous);
        }
        // A successful lookup publishes the head before variable assignments. A pop can
        // leave two occurrences of the same path if those assignments reject the change.
        if (succeeded)
        {
            if (kind == DirectoryChangeKind.PopDirectory)
                memory.Charge(Math.Max(0, stored.OwnedBytes - previous.OwnedBytes));
            ReplaceDirectoryStackEntry(0, destination);
        }
        return previous;
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
        long bytes = end - start;
        for (var index = start; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += FormatEntry(index, homeBytes, default);
        }
        if (bytes >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        memory.Charge(checked(32 + bytes));
        var output = new byte[(int)bytes];
        var offset = 0;
        for (var index = start; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offset += FormatEntry(index, homeBytes, output.AsSpan(offset));
            output[offset++] = layout == DirectoryStackLayout.Compact && index + 1 < end ? (byte)' ' : (byte)'\n';
        }
        return output;

        int FormatEntry(int index, ReadOnlySpan<byte> homePrefix, Span<byte> destination)
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

            var path = GetDirectoryStackEntry(index).Value.Bytes.Span;
            if (!homePrefix.IsEmpty && path.StartsWith(homePrefix)
                && (path.Length == homePrefix.Length || path[homePrefix.Length] == (byte)'/'))
            {
                if (writing)
                    destination[position] = (byte)'~';
                position++;
                path = path[homePrefix.Length..];
            }
            if (writing)
                path.CopyTo(destination[position..]);
            return checked(position + path.Length);
        }
    }
}

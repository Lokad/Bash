using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal enum RedirectionPersistence { None, Allocations, AllocationsAndMoves }
internal enum RedirectionOrigin { Command, IsolatedCommandless, IsolatedHostCommand, InputSubstitution }

/// <summary>
/// Evaluates redirections and resolves descriptor mappings in order.
/// </summary>
internal sealed class RedirectionEvaluation
{
    /// <summary> Stores the redirections to evaluate in order. </summary>
    private readonly IReadOnlyList<Redirection> _redirections;
    /// <summary> Stores the descriptor mappings produced while evaluating redirections. </summary>
    private readonly DescriptorTable _descriptorTable;
    private readonly RedirectionOrigin _origin;
    private readonly bool _reportMoveRestorationFailure;
    /// <summary> Stores descriptors opened during redirection evaluation, in open order. </summary>
    private readonly DescriptorHandleOwner _openedDescriptors = new();
    /// <summary>Stores replaced handles in encounter order when the scope may be preserved.</summary>
    private readonly OrderedDescriptorSet? _replacedHandles;
    /// <summary> Stores logical descriptors explicitly targeted by the redirections. </summary>
    private readonly HashSet<LogicalDescriptor> _touchedDescriptors = new();
    private OrderedDescriptorSet? _consumedSources;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedirectionEvaluation"/> class.
    /// </summary>
    public RedirectionEvaluation(
        IReadOnlyList<Redirection> redirections,
        DescriptorTable descriptorTable,
        RedirectionOrigin origin,
        bool trackReplacedHandles,
        bool reportMoveRestorationFailure)
    {
        _redirections = redirections;
        _descriptorTable = descriptorTable;
        _origin = origin;
        _reportMoveRestorationFailure = reportMoveRestorationFailure;
        _replacedHandles = trackReplacedHandles ? new OrderedDescriptorSet() : null;
    }

    /// <summary>
    /// Gets the resolved descriptor table.
    /// </summary>
    public DescriptorTable DescriptorTable => _descriptorTable;
    internal bool IsIsolated => _origin is RedirectionOrigin.IsolatedCommandless or RedirectionOrigin.IsolatedHostCommand;
    /// <summary>
    /// Gets the descriptors opened during evaluation, in open order.
    /// </summary>
    public DescriptorHandleOwner OpenedDescriptors => _openedDescriptors;
    /// <summary>Whether opening an input path failed after its target expanded successfully.</summary>
    public bool InputOpenFailed { get; private set; }
    /// <summary>Borrows replacement candidates after setup until preservation has settled them.</summary>
    public IReadOnlyCollection<FileDescriptor> ReplacedHandles =>
        _replacedHandles is { } replaced ? replaced : Array.Empty<FileDescriptor>();
    /// <summary>
    /// Gets logical descriptors explicitly targeted while evaluating the redirections.
    /// </summary>
    public IReadOnlySet<LogicalDescriptor> TouchedDescriptors => _touchedDescriptors;

    /// <summary>Gets moved handles whose caller binding was closed during setup.</summary>
    public IReadOnlyCollection<FileDescriptor> ConsumedSources =>
        _consumedSources is { } consumed ? consumed : Array.Empty<FileDescriptor>();

    /// <summary>
    /// Evaluates all redirections sequentially, issuing operations as needed.
    /// Allocation uses the active expansion bindings and the command's descriptor lifetime.
    /// </summary>
    public async Task EvaluateAsync(ScopedEvaluation scope, RedirectionPersistence persistence)
    {
        var persistAllocations = persistence != RedirectionPersistence.None;
        foreach (var redirection in _redirections)
        {
            if (scope.Effects.HasPendingExpansionError)
                break;

            var targetDescriptor = new LogicalDescriptor(redirection.FileDescriptor ?? redirection.DefaultFileDescriptor);
            var allocate = redirection.VariableName.Length != 0;
            if (redirection.Kind is RedirectionKind.DuplicateInput or RedirectionKind.DuplicateOutput)
            {
                var outcome = await DuplicateDescriptorAsync(scope, redirection, targetDescriptor,
                    persistence).ConfigureAwait(false);
                if (outcome == RedirectionOutcome.Failure) return;
                continue;
            }
            if (allocate && redirection.Kind is RedirectionKind.CloseInput or RedirectionKind.CloseOutput)
            {
                if (await CloseVariableAsync(scope, redirection.VariableName).ConfigureAwait(false)
                    == RedirectionOutcome.Failure) return;
                continue;
            }
            if (allocate && !_descriptorTable.TryFindAvailableDescriptor(out targetDescriptor))
            {
                await ReportFailureAsync(scope, new OperationError("Too many open files\n")).ConfigureAwait(false);
                return;
            }
            var previouslyTouched = _touchedDescriptors.Contains(targetDescriptor);
            switch (redirection.Kind)
            {
                case RedirectionKind.Input:
                case RedirectionKind.AllocateInput:
                    await OpenAndAssignAsync(scope, redirection, targetDescriptor, FileOpenMode.Read, null)
                        .ConfigureAwait(false);
                    break;
                case RedirectionKind.Output:
                case RedirectionKind.AllocateOutput:
                case RedirectionKind.ForceOutput:
                    await OpenAndAssignAsync(scope, redirection, targetDescriptor, FileOpenMode.WriteTruncate, null)
                        .ConfigureAwait(false);
                    break;
                case RedirectionKind.OutputAppend:
                    await OpenAndAssignAsync(scope, redirection, targetDescriptor, FileOpenMode.WriteAppend, null)
                        .ConfigureAwait(false);
                    break;
                case RedirectionKind.InputOutput:
                    await OpenAndAssignAsync(scope, redirection, targetDescriptor, FileOpenMode.ReadWrite, null)
                        .ConfigureAwait(false);
                    break;
                case RedirectionKind.OutputAndError:
                    await OpenAndAssignAsync(scope, redirection, LogicalDescriptor.StdOut, FileOpenMode.WriteTruncate, LogicalDescriptor.StdErr)
                        .ConfigureAwait(false);
                    break;
                case RedirectionKind.OutputAndErrorAppend:
                    await OpenAndAssignAsync(scope, redirection, LogicalDescriptor.StdOut, FileOpenMode.WriteAppend, LogicalDescriptor.StdErr)
                        .ConfigureAwait(false);
                    break;
                case RedirectionKind.HereDocument:
                case RedirectionKind.HereDocumentStripTabs:
                case RedirectionKind.HereString:
                    await HereInputAsync(scope, redirection, targetDescriptor).ConfigureAwait(false);
                    break;
                case RedirectionKind.CloseOutput:
                case RedirectionKind.CloseInput:
                    SetDescriptor(scope, targetDescriptor, DescriptorBinding.Closed);
                    break;
                default:
                    throw new NotSupportedException($"Redirection kind '{redirection.Kind}' is not supported in this evaluation stage.");
            }
            if (allocate && !scope.Effects.HasPendingExpansionError)
                await BindAllocatedDescriptorAsync(scope, redirection.VariableName,
                    targetDescriptor, _descriptorTable.ResolveBinding(targetDescriptor), previouslyTouched, persistAllocations).ConfigureAwait(false);
        }
    }

    /// <summary> Opens the redirection target and assigns the resulting descriptor to the mapping. </summary>
    private async Task OpenAndAssignAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor,
        FileOpenMode mode,
        LogicalDescriptor? secondaryTargetDescriptor)
    {
        ResumeTemporaryLookup(scope);
        var path = await ExpandRedirectionTargetAsync(scope, redirection.Target).ConfigureAwait(false);
        if (path == null)
        {
            return;
        }
        await OpenResolvedPathAsync(scope, path, targetDescriptor, mode, secondaryTargetDescriptor).ConfigureAwait(false);
    }

    /// <summary>Opens an already expanded path without repeating expansion effects.</summary>
    private async Task OpenResolvedPathAsync(
        ScopedEvaluation scope, string path, LogicalDescriptor targetDescriptor, FileOpenMode mode,
        LogicalDescriptor? secondaryTargetDescriptor)
    {
        if (path.Length == 0)
        {
            await ReportOpenFailureAsync(scope, path, mode,
                new OperationError("No such file or directory\n") { Kind = OperationErrorKind.NotFound }).ConfigureAwait(false);
            return;
        }
        if (string.Equals(path, FileDescriptor.DevNullPath.Path, StringComparison.Ordinal))
        {
            SetOpenedBinding(new DescriptorBinding(FileDescriptor.ForDevNull(mode)));
            return;
        }

        PrepareOpenedDescriptor(scope);
        scope.Effects.PrepareOwnedDescriptors(1);
        var open = await scope.Effects.ExecuteOpenFileAsync(path, mode).ConfigureAwait(false);
        var descriptor = open.FileDescriptor;
        if (!descriptor.HasValue)
        {
            await ReportOpenFailureAsync(scope, path, mode, open.Error).ConfigureAwait(false);
            return;
        }

        scope.Effects.TrackOwnedDescriptor(descriptor.Value);
        _openedDescriptors.AddPrepared(descriptor.Value);
        SetOpenedBinding(new DescriptorBinding(descriptor.Value, mode is FileOpenMode.WriteTruncate or FileOpenMode.WriteAppend));

        void SetOpenedBinding(DescriptorBinding binding)
        {
            SetDescriptor(scope, targetDescriptor, binding);
            if (secondaryTargetDescriptor is { } secondary)
                SetDescriptor(scope, secondary, binding);
        }
    }

    /// <summary> Resolves and duplicates a descriptor redirection. </summary>
    private async Task<RedirectionOutcome> DuplicateDescriptorAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor,
        RedirectionPersistence persistence)
    {
        var allocate = redirection.VariableName.Length != 0;
        var move = redirection.MovesDescriptor;
        var target = redirection.DescriptorOperand;
        if (move && target.Parts.Count == 0)
            return await RejectBadDescriptorAsync(scope, redirection, targetDescriptor, sourceDescriptor: null)
                .ConfigureAwait(false);
        if ((scope.Effects.SuppressesTemporaryLookup || scope.Variables.DeferredFieldSeparators != null)
            && !redirection.TryGetLiteralDescriptor(out _))
            ResumeTemporaryLookup(scope);
        var (fieldCount, sourceWord) = await scope.Effects.ExpandRedirectionFieldsAsync(target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError) return RedirectionOutcome.Failure;
        if (fieldCount != 1 && !move)
        {
            await scope.Effects.ReportAmbiguousRedirectAsync(_descriptorTable, target, fieldCount, scope)
                .ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }
        if (sourceWord == "-" || move && fieldCount != 1)
        {
            if (allocate)
                return await CloseVariableAsync(scope, redirection.VariableName).ConfigureAwait(false);
            SetDescriptor(scope, targetDescriptor, DescriptorBinding.Closed);
            return RedirectionOutcome.Success;
        }

        if ((move || allocate) && sourceWord.Length == 0)
            return await RejectBadDescriptorAsync(scope, redirection, targetDescriptor, sourceDescriptor: null)
                .ConfigureAwait(false);

        if (allocate && !_descriptorTable.TryFindAvailableDescriptor(out targetDescriptor))
        {
            await ReportFailureAsync(scope, new OperationError("Too many open files\n")).ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }

        if (!LogicalDescriptor.TryParse(sourceWord.AsSpan(), out var sourceDescriptor))
        {
            if (sourceWord.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0)
                return await RejectBadDescriptorAsync(scope, redirection, targetDescriptor, sourceDescriptor: null)
                    .ConfigureAwait(false);
            if (move || redirection.Kind != RedirectionKind.DuplicateOutput || targetDescriptor != LogicalDescriptor.StdOut)
            {
                await scope.Effects.ReportAmbiguousRedirectAsync(_descriptorTable, target, fieldCount, scope)
                    .ConfigureAwait(false);
                return RedirectionOutcome.Failure;
            }

            await OpenResolvedPathAsync(
                    scope,
                    sourceWord,
                    LogicalDescriptor.StdOut,
                    FileOpenMode.WriteTruncate,
                    LogicalDescriptor.StdErr)
                .ConfigureAwait(false);

            return RedirectionOutcome.Success;
        }

        if (!allocate && targetDescriptor == sourceDescriptor)
            return RedirectionOutcome.Success;

        var sourceBinding = _descriptorTable.ResolveBinding(sourceDescriptor);
        var resolvedSource = sourceBinding.Handle;
        if (resolvedSource.IsClosed)
            return await RejectBadDescriptorAsync(scope, redirection, targetDescriptor, sourceDescriptor)
                .ConfigureAwait(false);

        var previouslyTouched = _touchedDescriptors.Contains(targetDescriptor);
        var consumeSource = move && !allocate && persistence == RedirectionPersistence.AllocationsAndMoves
            && _descriptorTable.Resolve(targetDescriptor).IsClosed && !_touchedDescriptors.Contains(sourceDescriptor);
        SetDescriptor(scope, targetDescriptor, sourceBinding);
        if (consumeSource)
        {
            // An unopened destination contributes no saved source binding. Retire the source
            // through enclosing scopes while keeping the moved handle live through its aliases.
            var consumed = _consumedSources ??= new OrderedDescriptorSet();
            if (!consumed.Contains(resolvedSource))
            {
                consumed.PrepareAdditional(1, scope.Effects.Memory.Workspace);
                consumed.AddPrepared(resolvedSource);
            }
            scope.Effects.BindPersistentDescriptor(sourceDescriptor, DescriptorBinding.Closed);
        }
        else if (move)
            SetDescriptor(scope, sourceDescriptor, DescriptorBinding.Closed);
        if (allocate)
            await BindAllocatedDescriptorAsync(scope, redirection.VariableName,
                targetDescriptor, sourceBinding, previouslyTouched, persistence != RedirectionPersistence.None).ConfigureAwait(false);
        return RedirectionOutcome.Success;
    }

    private async Task<RedirectionOutcome> RejectBadDescriptorAsync(ScopedEvaluation scope, Redirection redirection,
        LogicalDescriptor targetDescriptor, LogicalDescriptor? sourceDescriptor)
    {
        var standardError = _descriptorTable.Resolve(LogicalDescriptor.StdErr);
        if (!standardError.IsClosed)
        {
            var allocate = redirection.VariableName.Length != 0;
            var move = redirection.MovesDescriptor;
            LogicalDescriptor? label = allocate ? null : targetDescriptor;
            Word? spelling = null;
            if (sourceDescriptor.HasValue && (allocate || redirection.TryGetLiteralDescriptor(out _)))
                label = sourceDescriptor;
            else if (!allocate && !move && targetDescriptor.Number == redirection.DefaultFileDescriptor)
                spelling = redirection.DescriptorOperand;

            // A restorable move saves the source when the destination is open.
            var reportDuplication = allocate || move && _reportMoveRestorationFailure
                && !_descriptorTable.Resolve(targetDescriptor).IsClosed;
            await scope.Effects.WriteBadDescriptorDiagnosticAsync(standardError, label, spelling, reportDuplication,
                scope.Context).ConfigureAwait(false);
        }
        scope.Effects.SetExpansionError(1);
        return RedirectionOutcome.Failure;
    }

    /// <summary>Publishes the reader while its independently owned producer can wait for pipe capacity.</summary>
    private async Task HereInputAsync(ScopedEvaluation scope, Redirection redirection, LogicalDescriptor targetDescriptor)
    {
        var effects = scope.Effects;
        if (redirection.Kind == RedirectionKind.HereString
            || redirection.Target.Parts is [SourceWordPart { Kind: WordPartKind.HereDocument, Source.Length: > 0 }])
            ResumeTemporaryLookup(scope);
        FileDescriptor? reader = null;
        OperationError? error;
        using (var storage = new MemoryCapacity(effects.Memory.Budget, 0))
        {
            var (fields, length) = await effects.ExpandHereInputAsync(redirection.Target, scope, storage).ConfigureAwait(false);
            if (effects.HasPendingExpansionError)
                return;

            storage.ReleaseUnusedCapacity();
            PrepareOpenedDescriptor(scope);
            effects.PrepareOwnedDescriptors(1);
            effects.PreparePipeInput();
            var creation = await effects.ExecuteCreatePipeAsync().ConfigureAwait(false);
            error = creation.Error;
            if (creation.Descriptors is { } pipe)
            {
                reader = pipe.Read;
                _openedDescriptors.AddPrepared(pipe.Read);
                effects.TrackOwnedDescriptor(pipe.Read);
                error = await effects.StartPipeInputAsync(pipe, fields, length,
                    appendNewline: redirection.Kind == RedirectionKind.HereString).ConfigureAwait(false);
            }
        }

        if (!reader.HasValue || error.HasValue)
        {
            await ReportFailureAsync(scope, error).ConfigureAwait(false);
            return;
        }
        SetDescriptor(scope, targetDescriptor, new DescriptorBinding(reader.Value));
    }

    private void ResumeTemporaryLookup(ScopedEvaluation scope)
    {
        scope.Effects.ResumeTemporaryLookup();
        if (!IsIsolated || scope.Variables.BorrowsBindings)
            scope.Variables.UseVisibleFieldSeparators();
    }

    /// <summary>Publishes one selected variable output and persists the acquired alias through restoration boundaries.</summary>
    private async Task BindAllocatedDescriptorAsync(
        ScopedEvaluation scope,
        string variableName,
        LogicalDescriptor number,
        DescriptorBinding binding,
        bool previouslyTouched,
        bool persistAllocations)
    {
        var value = ShellValue.FromInteger(number.Number);
        var name = ShellValue.FromText(variableName);
        var assignment = await scope.Effects.AssignBuiltinAsync(VariableReference.ForName(variableName), value,
            scope, scope.SelectorBindings,
            Evaluation.BuiltinAssignmentKind.Descriptor, name,
            scope.ExpansionWriteTarget).ConfigureAwait(false);
        if (assignment == Evaluation.BuiltinAssignmentResult.NoWrite)
            return; // An unbound allocation remains temporary and is closed by the existing restore path.
        if (assignment != Evaluation.BuiltinAssignmentResult.Assigned)
        {
            if (!scope.Effects.HasPendingExpansionError)
            {
                var standardError = _descriptorTable.Resolve(LogicalDescriptor.StdErr);
                if (!standardError.IsClosed)
                    await scope.Effects.WriteDescriptorAssignmentErrorAsync(standardError, name, scope.Context).ConfigureAwait(false);
                scope.Effects.SetExpansionError(1);
            }
            return;
        }
        if (!persistAllocations) return;

        // Applying the operator touched this new target. Only an earlier redirection
        // establishes a restoration boundary; the allocation itself must persist.
        if (!previouslyTouched) _touchedDescriptors.Remove(number);
        scope.Effects.BindPersistentDescriptor(number, binding);
    }

    /// <summary>Closes the current value of a descriptor variable without assigning to it.</summary>
    private async Task<RedirectionOutcome> CloseVariableAsync(
        ScopedEvaluation scope, string variableName)
    {
        var reference = await WordExpansion.ResolveReadReferenceAsync(VariableReference.ForName(variableName), scope)
            .ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError) return RedirectionOutcome.Failure;
        if (reference == null || !reference.TryRead(scope.Variables, out var value) || value.IsEmpty)
            return await RejectVariableAsync().ConfigureAwait(false);
        // Bash converts a stored, nonempty value to signed decimal and then Int32.
        // A failed numeric conversion supplies zero; a negative Int32 is rejected.
        _ = Integers.TryParseNumber(value.Bytes.Span.Trim(" \t\r\n\v\f"u8), out var parsed);
        var number = unchecked((int)parsed);
        if (number < 0)
            return await RejectVariableAsync().ConfigureAwait(false);
        SetDescriptor(scope, new LogicalDescriptor(number), DescriptorBinding.Closed);
        return RedirectionOutcome.Success;

        async Task<RedirectionOutcome> RejectVariableAsync()
        {
            await scope.Effects.ReportAmbiguousRedirectAsync(_descriptorTable, ShellValue.FromText(variableName), scope)
                .ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }
    }

    private Task ReportOpenFailureAsync(ScopedEvaluation scope, string path, FileOpenMode mode, OperationError? error)
    {
        InputOpenFailed = _origin == RedirectionOrigin.InputSubstitution;
        if (error is { Kind: not OperationErrorKind.Unknown, Message.Length: > 0 })
            return ReportFailureAsync(scope, path, error);
        var detail = error is { Message: not null } failure ? failure : new OperationError("No such file or directory\n");
        return mode == FileOpenMode.Read && _origin is (RedirectionOrigin.Command or RedirectionOrigin.IsolatedHostCommand)
            ? ReportFailureAsync(scope, error)
            : ReportFailureAsync(scope, path, detail);
    }

    private Task ReportFailureAsync(ScopedEvaluation scope, OperationError? error) =>
        ReportFailureAsync(scope, path: null, error);

    private async Task ReportFailureAsync(ScopedEvaluation scope, string? path, OperationError? error)
    {
        var standardError = _descriptorTable.Resolve(LogicalDescriptor.StdErr);
        if (!standardError.IsClosed && error is { Message.Length: > 0 } failure)
            await scope.Effects.WriteRedirectionOperationDiagnosticAsync(standardError, path, failure,
                scope.Context).ConfigureAwait(false);
        scope.Effects.SetExpansionError(1);
    }

    private void SetDescriptor(ScopedEvaluation scope, LogicalDescriptor logicalDescriptor, DescriptorBinding binding)
    {
        AddDescriptorToSet(_touchedDescriptors, logicalDescriptor, scope.Effects.Memory);
        var replaced = _replacedHandles;
        var previousDescriptor = replaced != null ? _descriptorTable.Resolve(logicalDescriptor) : FileDescriptor.Closed;
        _descriptorTable.Set(logicalDescriptor, binding, scope.Effects.Memory);
        if (replaced == null)
            return;
        var resolvedDescriptor = binding.Handle;
        replaced.Remove(resolvedDescriptor);
        if (previousDescriptor != resolvedDescriptor
            && !previousDescriptor.IsClosed
            && !_descriptorTable.References(previousDescriptor)
            && !replaced.Contains(previousDescriptor))
        {
            replaced.PrepareAdditional(1, scope.Effects.Memory.Workspace);
            replaced.AddPrepared(previousDescriptor);
        }
    }

    private void PrepareOpenedDescriptor(ScopedEvaluation scope)
    {
        _openedDescriptors.PrepareAdditional(1, scope.Effects.Memory.Workspace);
    }

    /// <summary>Grows command-owned descriptor membership before publishing a newly touched number.</summary>
    internal static void AddDescriptorToSet(HashSet<LogicalDescriptor> descriptors, LogicalDescriptor descriptor, EvaluationMemory memory)
    {
        if (descriptors.Contains(descriptor))
            return;
        var capacity = descriptors.EnsureCapacity(0);
        if (descriptors.Count == capacity)
        {
            var requested = Math.Min(int.MaxValue, Math.Max(4L, 2L * capacity));
            memory.Charge(checked(128L + 32L * (2L * requested + 1)));
            descriptors.EnsureCapacity((int)requested);
        }
        descriptors.Add(descriptor);
    }

    /// <summary> Expands the redirection target word into a single path or returns null on failure. </summary>
    private async Task<string?> ExpandRedirectionTargetAsync(
        ScopedEvaluation scope,
        Word target)
    {
        var (fieldCount, path) = await scope.Effects.ExpandRedirectionFieldsAsync(target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError)
            return null;

        if (fieldCount != 1)
        {
            await scope.Effects.ReportAmbiguousRedirectAsync(_descriptorTable, target, fieldCount, scope)
                .ConfigureAwait(false);
            return null;
        }

        return path;
    }

    private enum RedirectionOutcome
    {
        Success,
        Failure
    }
}

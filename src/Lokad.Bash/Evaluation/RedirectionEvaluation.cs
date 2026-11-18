using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tools;

namespace Lokad.Bash.Evaluation;

internal enum RedirectionPersistence { None, Allocations, AllocationsAndMoves }

/// <summary>
/// Evaluates redirections and resolves descriptor mappings in order.
/// </summary>
internal sealed class RedirectionEvaluation
{
    /// <summary> Stores the redirections to evaluate in order. </summary>
    private readonly IReadOnlyList<Redirection> _redirections;
    /// <summary> Stores the descriptor mappings produced while evaluating redirections. </summary>
    private readonly DescriptorTable _descriptorTable;
    /// <summary> Stores descriptors opened during redirection evaluation, in open order. </summary>
    private readonly DescriptorHandleOwner _openedDescriptors = new();
    /// <summary> Stores resolved descriptors hidden by close or replacement redirections. </summary>
    private readonly OrderedDescriptorSet _releasedDescriptors = new();
    /// <summary> Stores logical descriptors explicitly targeted by the redirections. </summary>
    private readonly HashSet<LogicalDescriptor> _touchedDescriptors = new();
    private OrderedDescriptorSet? _consumedSources;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedirectionEvaluation"/> class.
    /// </summary>
    public RedirectionEvaluation(
        IReadOnlyList<Redirection> redirections,
        DescriptorTable descriptorTable)
    {
        _redirections = redirections;
        _descriptorTable = descriptorTable;
    }

    /// <summary>
    /// Gets the resolved descriptor table.
    /// </summary>
    public DescriptorTable DescriptorTable => _descriptorTable;
    /// <summary>
    /// Gets the descriptors opened during evaluation, in open order.
    /// </summary>
    public DescriptorHandleOwner OpenedDescriptors => _openedDescriptors;
    /// <summary>
    /// Gets released descriptors that no logical mapping retains after all redirections have been applied.
    /// </summary>
    public IReadOnlyList<FileDescriptor> GetDescriptorsToReleaseWhenPreserved(EvaluationMemory memory)
    {
        if (_releasedDescriptors.Count == 0)
            return [];
        memory.Charge(checked(64L + 4L * _releasedDescriptors.Count));
        var result = new List<FileDescriptor>(_releasedDescriptors.Count);
        foreach (var descriptor in _releasedDescriptors)
            if (!_descriptorTable.References(descriptor))
                result.Add(descriptor);
        return result;
    }
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

            var targetDescriptor = GetTargetDescriptor(redirection);
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
                    await HereDocumentAsync(scope, redirection, targetDescriptor).ConfigureAwait(false);
                    break;
                case RedirectionKind.HereString:
                    await HereStringAsync(scope, redirection, targetDescriptor).ConfigureAwait(false);
                    break;
                case RedirectionKind.CloseOutput:
                case RedirectionKind.CloseInput:
                    SetDescriptor(scope, targetDescriptor, FileDescriptor.Closed);
                    break;
                default:
                    throw new NotSupportedException($"Redirection kind '{redirection.Kind}' is not supported in this evaluation stage.");
            }
            if (allocate && !scope.Effects.HasPendingExpansionError)
                await BindAllocatedDescriptorAsync(scope, redirection.VariableName,
                    targetDescriptor, _descriptorTable.Resolve(targetDescriptor), previouslyTouched, persistAllocations).ConfigureAwait(false);
        }

        static LogicalDescriptor GetTargetDescriptor(Redirection redirection) => new(redirection.Kind switch
        {
            RedirectionKind.Input => redirection.FileDescriptor ?? 0,
            RedirectionKind.InputOutput => redirection.FileDescriptor ?? 0,
            RedirectionKind.DuplicateInput => redirection.FileDescriptor ?? 0,
            RedirectionKind.CloseInput => redirection.FileDescriptor ?? 0,
            RedirectionKind.HereString => redirection.FileDescriptor ?? 0,
            RedirectionKind.HereDocument => redirection.FileDescriptor ?? 0,
            RedirectionKind.HereDocumentStripTabs => redirection.FileDescriptor ?? 0,
            _ => redirection.FileDescriptor ?? 1
        });
    }

    /// <summary> Opens the redirection target and assigns the resulting descriptor to the mapping. </summary>
    private async Task OpenAndAssignAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor,
        FileOpenMode mode,
        LogicalDescriptor? secondaryTargetDescriptor)
    {
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
        if (string.Equals(path, FileDescriptor.DevNullPath.Path, StringComparison.Ordinal))
        {
            var nullDescriptor = FileDescriptor.ForDevNull(mode);
            SetDescriptor(scope, targetDescriptor, nullDescriptor);
            if (secondaryTargetDescriptor.HasValue)
                SetDescriptor(scope, secondaryTargetDescriptor.Value, nullDescriptor);

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
        SetDescriptor(scope, targetDescriptor, descriptor.Value);
        if (secondaryTargetDescriptor.HasValue)
            SetDescriptor(scope, secondaryTargetDescriptor.Value, descriptor.Value);
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
        {
            await ReportFailureAsync(scope, new OperationError($"{targetDescriptor}: Bad file descriptor\n")).ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }
        var sourceWords = await scope.Effects.ExpandTextWordsAsync(target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError) return RedirectionOutcome.Failure;
        if (sourceWords.Count > 1 || sourceWords.Count == 0 && !move)
        {
            await ReportFailureAsync(scope, new OperationError("ambiguous redirect\n")).ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }
        var sourceWord = sourceWords.Count == 0 ? string.Empty : sourceWords[0];
        if (sourceWord == "-" || move && sourceWord.Length == 0)
        {
            if (allocate)
                return await CloseVariableAsync(scope, redirection.VariableName).ConfigureAwait(false);
            SetDescriptor(scope, targetDescriptor, FileDescriptor.Closed);
            return RedirectionOutcome.Success;
        }

        if (allocate && !_descriptorTable.TryFindAvailableDescriptor(out targetDescriptor))
        {
            await ReportFailureAsync(scope, new OperationError("Too many open files\n")).ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }

        if (!LogicalDescriptor.TryParse(sourceWord.AsSpan(), out var sourceDescriptor))
        {
            var numericOverflow = sourceWord.Length > 0 && sourceWord.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;
            if (numericOverflow || sourceWord.Length == 0)
            {
                await ReportFailureAsync(scope, new OperationError($"{sourceWord}: Bad file descriptor\n")).ConfigureAwait(false);
                return RedirectionOutcome.Failure;
            }
            if (move || redirection.Kind != RedirectionKind.DuplicateOutput || targetDescriptor != LogicalDescriptor.StdOut)
            {
                // GNU expands an invalid word again while forming this diagnostic.
                // Valid filename fallback deliberately keeps the first expansion only.
                var diagnostic = await ExpandRedirectionTargetAsync(scope, target).ConfigureAwait(false);
                if (diagnostic != null)
                    await ReportFailureAsync(scope, new OperationError($"{diagnostic}: ambiguous redirect\n")).ConfigureAwait(false);
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

        var resolvedSource = _descriptorTable.Resolve(sourceDescriptor);
        if (resolvedSource.IsClosed)
        {
            await ReportFailureAsync(scope, new OperationError($"{sourceDescriptor}: Bad file descriptor\n"))
                .ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }

        var previouslyTouched = _touchedDescriptors.Contains(targetDescriptor);
        var consumeSource = move && !allocate && persistence == RedirectionPersistence.AllocationsAndMoves
            && _descriptorTable.Resolve(targetDescriptor).IsClosed && !_touchedDescriptors.Contains(sourceDescriptor);
        SetDescriptor(scope, targetDescriptor, resolvedSource);
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
            scope.Effects.BindPersistentDescriptor(sourceDescriptor, FileDescriptor.Closed);
        }
        else if (move)
            SetDescriptor(scope, sourceDescriptor, FileDescriptor.Closed);
        if (allocate)
            await BindAllocatedDescriptorAsync(scope, redirection.VariableName,
                targetDescriptor, resolvedSource, previouslyTouched, persistence != RedirectionPersistence.None).ConfigureAwait(false);
        return RedirectionOutcome.Success;

    }

    /// <summary> Writes a here-string into a pipe and assigns it to the target descriptor. </summary>
    private async Task HereStringAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor)
    {
        var content = await scope.Effects.ExpandHereInputValueAsync(redirection.Target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError)
            return;

        await WritePipeInputAsync(scope, targetDescriptor, content.Bytes, appendNewline: true).ConfigureAwait(false);
    }

    /// <summary> Writes a here-document into a pipe and assigns it to the target descriptor. </summary>
    private async Task HereDocumentAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor)
    {
        var content = await scope.Effects.ExpandHereInputValueAsync(redirection.Target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError)
            return;

        await WritePipeInputAsync(scope, targetDescriptor, content.Bytes, appendNewline: false).ConfigureAwait(false);
    }

    /// <summary>Publishes the reader while its independently owned producer can wait for pipe capacity.</summary>
    private async Task WritePipeInputAsync(
        ScopedEvaluation scope, LogicalDescriptor targetDescriptor, ReadOnlyMemory<byte> content, bool appendNewline)
    {
        PrepareOpenedDescriptor(scope);
        scope.Effects.PrepareOwnedDescriptors(1);
        scope.Effects.PreparePipeInput();
        var creation = await scope.Effects.ExecuteCreatePipeAsync().ConfigureAwait(false);
        if (creation.Descriptors is not { } pipe)
        {
            await ReportFailureAsync(scope, creation.Error).ConfigureAwait(false);
            return;
        }
        _openedDescriptors.AddPrepared(pipe.Read);
        scope.Effects.TrackOwnedDescriptor(pipe.Read);
        var error = await scope.Effects.StartPipeInputAsync(pipe, content, appendNewline).ConfigureAwait(false);
        if (error is { } failure)
        {
            await ReportFailureAsync(scope, failure).ConfigureAwait(false);
            return;
        }
        SetDescriptor(scope, targetDescriptor, pipe.Read);
    }

    /// <summary>Publishes one selected variable output and persists the acquired alias through restoration boundaries.</summary>
    private async Task BindAllocatedDescriptorAsync(
        ScopedEvaluation scope,
        string variableName,
        LogicalDescriptor number,
        FileDescriptor descriptor,
        bool previouslyTouched,
        bool persistAllocations)
    {
        var value = ShellValue.FromBytes(NumberFormatting.FormatNumber(number.Number));
        var assignment = await scope.Effects.AssignBuiltinAsync(VariableReference.ForName(variableName), value,
            scope, scope.SelectorBindings,
            Evaluation.BuiltinAssignmentKind.Descriptor, ShellValue.FromText(variableName),
            scope.ExpansionWriteTarget).ConfigureAwait(false);
        if (assignment == Evaluation.BuiltinAssignmentResult.NoWrite)
            return; // An unbound allocation remains temporary and is closed by the existing restore path.
        if (assignment != Evaluation.BuiltinAssignmentResult.Assigned)
        {
            if (!scope.Effects.HasPendingExpansionError)
                await ReportFailureAsync(scope, new OperationError($"{variableName}: cannot assign fd to variable\n")).ConfigureAwait(false);
            return;
        }
        if (!persistAllocations) return;

        // Applying the operator touched this new target. Only an earlier redirection
        // establishes a restoration boundary; the allocation itself must persist.
        if (!previouslyTouched) _touchedDescriptors.Remove(number);
        scope.Effects.BindPersistentDescriptor(number, descriptor);
    }

    /// <summary>Closes the current value of a descriptor variable without assigning to it.</summary>
    private async Task<RedirectionOutcome> CloseVariableAsync(
        ScopedEvaluation scope, string variableName)
    {
        var reference = await WordExpansion.ResolveReadReferenceAsync(VariableReference.ForName(variableName), scope)
            .ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError) return RedirectionOutcome.Failure;
        if (reference == null || !reference.TryRead(scope.Variables, out var value) || value.IsEmpty)
        {
            await ReportFailureAsync(scope, new OperationError($"{variableName}: ambiguous redirect\n")).ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }
        // Bash converts a stored, nonempty value to signed decimal and then Int32.
        // A failed numeric conversion supplies zero; a negative Int32 is rejected.
        _ = Integers.TryParseNumber(value.Bytes.Span.Trim(" \t\r\n\v\f"u8), out var parsed);
        var number = unchecked((int)parsed);
        if (number < 0)
        {
            await ReportFailureAsync(scope, new OperationError($"{variableName}: ambiguous redirect\n")).ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }
        SetDescriptor(scope, new LogicalDescriptor(number), FileDescriptor.Closed);
        return RedirectionOutcome.Success;
    }

    private Task ReportOpenFailureAsync(ScopedEvaluation scope, string path, FileOpenMode mode, OperationError? error) =>
        mode == FileOpenMode.Read
            ? ReportFailureAsync(scope, error)
            : ReportFailureAsync(scope, ShellToolErrors.FormatPathError(Utf8Text.Encode(path), error));

    private Task ReportFailureAsync(ScopedEvaluation scope, OperationError? error) =>
        ReportFailureAsync(scope, error is { } failure ? Utf8Text.Encode(failure.Message) : ReadOnlyMemory<byte>.Empty);

    /// <summary>Reports a failure through the stderr mapping reached so far, before scope restoration.</summary>
    private async Task ReportFailureAsync(ScopedEvaluation scope, ReadOnlyMemory<byte> diagnostic)
    {
        var standardError = _descriptorTable.Resolve(LogicalDescriptor.StdErr);
        if (!diagnostic.IsEmpty && !standardError.IsClosed)
            await scope.Effects.ExecuteWriteAsync(standardError, diagnostic).ConfigureAwait(false);
        scope.Effects.SetExpansionError(1);
    }

    private void SetDescriptor(ScopedEvaluation scope, LogicalDescriptor logicalDescriptor, FileDescriptor resolvedDescriptor)
    {
        AddDescriptorToSet(_touchedDescriptors, logicalDescriptor, scope.Effects.Memory);
        var previousDescriptor = _descriptorTable.Resolve(logicalDescriptor);
        _descriptorTable.Set(logicalDescriptor, resolvedDescriptor, scope.Effects.Memory);
        _releasedDescriptors.Remove(resolvedDescriptor);
        if (previousDescriptor != resolvedDescriptor
            && !previousDescriptor.IsClosed
            && !_descriptorTable.References(previousDescriptor)
            && !_releasedDescriptors.Contains(previousDescriptor))
        {
            _releasedDescriptors.PrepareAdditional(1, scope.Effects.Memory.Workspace);
            _releasedDescriptors.AddPrepared(previousDescriptor);
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
    private static async Task<string?> ExpandRedirectionTargetAsync(
        ScopedEvaluation scope,
        Word target)
    {
        var words = await scope.Effects.ExpandTextWordsAsync(target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError)
            return null;

        if (words.Count != 1)
        {
            scope.Effects.SetExpansionError(1);
            return null;
        }

        return words[0];
    }

    private enum RedirectionOutcome
    {
        Success,
        Failure
    }
}

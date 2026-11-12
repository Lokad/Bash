using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tools;

namespace Lokad.Bash.Evaluation;

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
    private readonly List<FileDescriptor> _openedDescriptors = new();
    /// <summary> Stores resolved descriptors hidden by close or replacement redirections. </summary>
    private readonly List<FileDescriptor> _releasedDescriptors = new();
    /// <summary> Stores logical descriptors explicitly targeted by the redirections. </summary>
    private readonly HashSet<LogicalDescriptor> _touchedDescriptors = new();

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
    public IReadOnlyList<FileDescriptor> OpenedDescriptors => _openedDescriptors;
    /// <summary>
    /// Gets released descriptors that no logical mapping retains after all redirections have been applied.
    /// </summary>
    public IReadOnlyList<FileDescriptor> DescriptorsToReleaseWhenPreserved =>
        _releasedDescriptors.Where(descriptor => !_descriptorTable.References(descriptor)).Distinct().ToArray();
    /// <summary>
    /// Gets logical descriptors explicitly targeted while evaluating the redirections.
    /// </summary>
    public IReadOnlySet<LogicalDescriptor> TouchedDescriptors => _touchedDescriptors;

    /// <summary>
    /// Evaluates all redirections sequentially, issuing operations as needed.
    /// Expansion bindings may be isolated; allocated descriptor variables keep their caller-owned assignment scope.
    /// </summary>
    public async Task EvaluateAsync(ScopedEvaluation scope, ScopedEvaluation descriptorAssignmentScope)
    {
        foreach (var redirection in _redirections)
        {
            if (scope.Effects.HasPendingExpansionError)
                break;

            var targetDescriptor = GetTargetDescriptor(redirection);
            switch (redirection.Kind)
            {
                case RedirectionKind.Input:
                    await OpenAndAssignAsync(scope, redirection, targetDescriptor, FileOpenMode.Read, null)
                        .ConfigureAwait(false);
                    break;
                case RedirectionKind.Output:
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
                case RedirectionKind.DuplicateOutput:
                case RedirectionKind.DuplicateInput:
                    var outcome = await DuplicateDescriptorAsync(scope, redirection, targetDescriptor)
                        .ConfigureAwait(false);
                    if (outcome == RedirectionOutcome.Failure)
                        return;
                    break;
                case RedirectionKind.HereDocument:
                case RedirectionKind.HereDocumentStripTabs:
                    await HereDocumentAsync(scope, redirection, targetDescriptor).ConfigureAwait(false);
                    break;
                case RedirectionKind.HereString:
                    await HereStringAsync(scope, redirection, targetDescriptor).ConfigureAwait(false);
                    break;
                case RedirectionKind.AllocateOutput:
                    await AllocateRedirectionAsync(scope, descriptorAssignmentScope, redirection, FileOpenMode.WriteTruncate).ConfigureAwait(false);
                    break;
                case RedirectionKind.AllocateInput:
                    await AllocateRedirectionAsync(scope, descriptorAssignmentScope, redirection, FileOpenMode.Read).ConfigureAwait(false);
                    break;
                case RedirectionKind.CloseOutput:
                case RedirectionKind.CloseInput:
                    SetDescriptor(targetDescriptor, FileDescriptor.Closed);
                    break;
                default:
                    throw new NotSupportedException($"Redirection kind '{redirection.Kind}' is not supported in this evaluation stage.");
            }
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

        if (string.Equals(path, FileDescriptor.DevNullPath.Path, StringComparison.Ordinal))
        {
            var nullDescriptor = FileDescriptor.ForDevNull(mode);
            SetDescriptor(targetDescriptor, nullDescriptor);
            if (secondaryTargetDescriptor.HasValue)
                SetDescriptor(secondaryTargetDescriptor.Value, nullDescriptor);

            return;
        }

        var open = await scope.Effects.ExecuteOpenFileAsync(path, mode).ConfigureAwait(false);
        var descriptor = open.FileDescriptor;
        if (!descriptor.HasValue)
        {
            await ReportOpenFailureAsync(scope, path, mode, open.Error).ConfigureAwait(false);
            return;
        }

        scope.Effects.TrackOwnedDescriptor(descriptor.Value);
        _openedDescriptors.Add(descriptor.Value);
        SetDescriptor(targetDescriptor, descriptor.Value);
        if (secondaryTargetDescriptor.HasValue)
            SetDescriptor(secondaryTargetDescriptor.Value, descriptor.Value);
    }

    /// <summary> Resolves and duplicates a descriptor redirection. </summary>
    private async Task<RedirectionOutcome> DuplicateDescriptorAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor)
    {
        var sourceWord = await ExpandRedirectionTargetAsync(scope, redirection.Target).ConfigureAwait(false);
        if (sourceWord == null)
            return RedirectionOutcome.Failure;

        if (!LogicalDescriptor.TryParse(sourceWord.AsSpan(), out var sourceDescriptor))
        {
            if (targetDescriptor != LogicalDescriptor.StdOut)
                throw new NotSupportedException(
                    "Only numeric descriptor duplication is supported in this evaluation stage.");

            await OpenAndAssignAsync(
                    scope,
                    redirection,
                    LogicalDescriptor.StdOut,
                    FileOpenMode.WriteTruncate,
                    LogicalDescriptor.StdErr)
                .ConfigureAwait(false);

            return RedirectionOutcome.Success;
        }

        var resolvedSource = _descriptorTable.Resolve(sourceDescriptor);
        if (resolvedSource.IsClosed)
        {
            await ReportFailureAsync(scope, new OperationError($"{sourceDescriptor}: Bad file descriptor\n"))
                .ConfigureAwait(false);
            return RedirectionOutcome.Failure;
        }

        SetDescriptor(targetDescriptor, resolvedSource);
        return RedirectionOutcome.Success;
    }

    /// <summary> Writes a here-string into a pipe and assigns it to the target descriptor. </summary>
    private async Task HereStringAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor)
    {
        var content = await scope.Effects.ExpandWordValueAsync(redirection.Target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError)
            return;

        var withNewline = new byte[content.Bytes.Length + 1];
        content.Bytes.CopyTo(withNewline);
        withNewline[^1] = (byte)'\n';
        await WritePipeInputAsync(scope, targetDescriptor, withNewline).ConfigureAwait(false);
    }

    /// <summary> Writes a here-document into a pipe and assigns it to the target descriptor. </summary>
    private async Task HereDocumentAsync(
        ScopedEvaluation scope,
        Redirection redirection,
        LogicalDescriptor targetDescriptor)
    {
        var content = await scope.Effects.ExpandWordValueAsync(redirection.Target, scope).ConfigureAwait(false);
        if (scope.Effects.HasPendingExpansionError)
            return;

        await WritePipeInputAsync(scope, targetDescriptor, content.Bytes).ConfigureAwait(false);
    }

    /// <summary>Closes a populated pipe before exposing its input, preserving setup failures.</summary>
    private async Task WritePipeInputAsync(ScopedEvaluation scope, LogicalDescriptor targetDescriptor, ReadOnlyMemory<byte> content)
    {
        var creation = await scope.Effects.ExecuteCreatePipeAsync().ConfigureAwait(false);
        if (creation.Descriptors is not { } pipe)
        {
            await ReportFailureAsync(scope, creation.Error).ConfigureAwait(false);
            return;
        }
        _openedDescriptors.Add(pipe.Read);
        var failed = true;
        OperationError? closeError = null;
        try
        {
            await scope.Effects.ExecuteWriteAsync(pipe.Write, content).ConfigureAwait(false);
            failed = false;
        }
        finally
        {
            closeError = await scope.Effects.CloseDescriptorsAsync([pipe.Write], operationFailed: failed).ConfigureAwait(false);
        }

        if (closeError is { } error)
        {
            await ReportFailureAsync(scope, error).ConfigureAwait(false);
            return;
        }
        SetDescriptor(targetDescriptor, pipe.Read);
    }

    /// <summary> Allocates a descriptor by opening the target and assigning it to the logical descriptor. </summary>
    private async Task AllocateRedirectionAsync(
        ScopedEvaluation scope,
        ScopedEvaluation descriptorAssignmentScope,
        Redirection redirection,
        FileOpenMode mode)
    {
        var path = await ExpandRedirectionTargetAsync(scope, redirection.Target).ConfigureAwait(false);
        if (path == null)
            return;

        if (!_descriptorTable.TryFindAvailableDescriptor(out var number))
        {
            await scope.Effects.ExecuteWriteAsync(_descriptorTable.StdErr, "Too many open files\n"u8.ToArray())
                .ConfigureAwait(false);
            scope.Effects.SetExpansionError(1);
            return;
        }

        var open = string.Equals(path, FileDescriptor.DevNullPath.Path, StringComparison.Ordinal)
            ? FileOpenResult.Opened(FileDescriptor.ForDevNull(mode), readSize: null)
            : await scope.Effects.ExecuteOpenFileAsync(path, mode).ConfigureAwait(false);
        var descriptor = open.FileDescriptor;
        if (!descriptor.HasValue)
        {
            await ReportOpenFailureAsync(scope, path, mode, open.Error).ConfigureAwait(false);
            return;
        }

        if (!descriptor.Value.IsDevNull)
        {
            scope.Effects.TrackOwnedDescriptor(descriptor.Value);
            _openedDescriptors.Add(descriptor.Value);
        }
        var variables = descriptorAssignmentScope.Variables;
        if (variables.IsReadonly(redirection.VariableName))
        {
            // Opening precedes binding validation; lexical cleanup retains ownership of a rejected handle.
            BindingName readonlyName = redirection.VariableName;
            if (!variables.IsReadonlyBinding(readonlyName)
                && variables.TryResolveBinding(readonlyName, selectOwner: null, out var binding))
                readonlyName = binding.Name;
            using var diagnostic = new ByteMemoryBuilder(0, scope.Effects.Memory.Budget);
            diagnostic.Append(readonlyName.ToValue().Bytes).Append(": readonly variable\n"u8)
                .Append(Utf8Text.Encode(redirection.VariableName)).Append(": cannot assign fd to variable\n"u8);
            await ReportFailureAsync(scope, diagnostic.ToMemory())
                .ConfigureAwait(false);
            return;
        }

        scope.Effects.BindPersistentDescriptor(number, descriptor.Value);
        var value = ShellValue.FromBytes(NumberFormatting.FormatNumber(number.Number));
        descriptorAssignmentScope.SetExpansionValueIgnoringReadonly(redirection.VariableName, value);
        // Later redirections also need the allocated number in their isolated expansion bindings.
        if (scope.Variables != descriptorAssignmentScope.Variables)
            scope.SetExpansionValueIgnoringReadonly(redirection.VariableName, value);
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

    private void SetDescriptor(LogicalDescriptor logicalDescriptor, FileDescriptor resolvedDescriptor)
    {
        var previousDescriptor = _descriptorTable.Resolve(logicalDescriptor);
        _descriptorTable.Set(logicalDescriptor, resolvedDescriptor);
        _releasedDescriptors.Remove(resolvedDescriptor);
        if (previousDescriptor != resolvedDescriptor
            && !previousDescriptor.IsClosed
            && !_descriptorTable.References(previousDescriptor)
            && !_releasedDescriptors.Contains(previousDescriptor))
        {
            _releasedDescriptors.Add(previousDescriptor);
        }

        _touchedDescriptors.Add(logicalDescriptor);
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

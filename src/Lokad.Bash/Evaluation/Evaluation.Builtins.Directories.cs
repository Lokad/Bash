using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Handles the <c>pwd</c> builtin, emitting the current directory to stdout.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinPwdAsync(bool negated)
    {
        var exitCode = (await WriteDirectoryStackAsync("pwd", currentOnly: true).ConfigureAwait(false)).IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    /// <summary>
    /// Handles the <c>cd</c> builtin, updating the working directory and related variables.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinCdAsync(
        IReadOnlyList<ShellValue> words,
        Variables variables,
        Variables.CommandBindings? bindings,
        EvaluationContext context,
        bool negated)
    {
        var first = 1;
        if (first < words.Count)
        {
            var operand = words[first];
            if (operand.Bytes.Span.SequenceEqual("--"u8))
                first++;
            else if (operand.Bytes.Span is [(byte)'-', _, ..])
            {
                var scope = new ScopedEvaluation(this, variables, context);
                return await FailAsync(WordExpansion.AppendAssignmentValue(
                    operand, ShellValue.FromText(": invalid option"), scope), 2).ConfigureAwait(false);
            }
        }

        if (words.Count - first > 1)
            return await FailAsync(ShellValue.FromText("too many arguments"), 1).ConfigureAwait(false);

        var destination = first < words.Count ? words[first] : ShellValue.Empty;
        var previous = destination.Bytes.Span.SequenceEqual("-"u8);
        if (previous && !variables.TryGetExpansionValue("OLDPWD", out destination))
            return await FailAsync(ShellValue.FromText("OLDPWD not set"), 1).ConfigureAwait(false);
        if (first == words.Count && !variables.TryGetExpansionValue("HOME", out destination))
            return await FailAsync(ShellValue.FromText("HOME not set"), 1).ConfigureAwait(false);

        var target = await DecodeExpandedValueAsync(destination).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

        using var announcement = previous ? new ByteMemoryBuilder(checked(destination.Bytes.Length + 1), Memory.Budget) : null;
        var output = announcement == null ? ReadOnlyMemory<byte>.Empty
            : announcement.Append(destination.Bytes).Append((byte)'\n').ToMemory();
        var result = await ExecuteDirectoryChangeAsync(
                DirectoryChangeKind.ChangeDirectory, target, variables, bindings, context, output)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(result, negated).ConfigureAwait(false);

        async Task<EvaluationResult> FailAsync(ShellValue message, int status)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("cd"), message).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles the <c>dirs</c> builtin by printing or clearing the directory stack.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinDirsAsync(
        IReadOnlyList<string> words,
        bool negated)
    {
        if (words.Count > 1)
        {
            var option = words[1];

            if (option.AsSpan().Length != 2 || option.AsSpan()[0] != '-' || option.AsSpan()[1] != 'c')
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

            _state.ResetDirectoryStack();
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
        }

        var appendExitCode = (await WriteDirectoryStackAsync("dirs", currentOnly: false).ConfigureAwait(false)).IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(appendExitCode), negated);
    }

    /// <summary>
    /// Handles the <c>pushd</c> builtin by pushing a directory or swapping stack entries.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinPushdAsync(
        IReadOnlyList<ShellValue> words,
        Variables variables,
        Variables.CommandBindings? bindings,
        EvaluationContext context,
        bool negated)
    {
        var operands = ReadDirectoryStackOperands(words);
        if (operands.Index is { } index)
        {
            if (index < 0)
                return await ReportDirectoryStackIndexAsync("pushd", operands.IndexOperand, negated).ConfigureAwait(false);
            CheckCancellation();
            var target = _state.RotateSavedDirectories(index, Memory);
            if (operands.KeepDirectory)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);

            var rotated = await ChangeToSavedDirectoryAsync(DirectoryChangeKind.RotateDirectories, target, variables, bindings, context)
                .ConfigureAwait(false);
            return await FinalizeResultAsync(rotated, negated).ConfigureAwait(false);
        }

        if (operands.NextOperand == words.Count)
        {
            if (operands.KeepDirectory)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
            if (_state.DirectoryStackCount < 2)
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText("pushd"), ShellValue.FromText("no other directory")).ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
            }

            var result = await ChangeToSavedDirectoryAsync(DirectoryChangeKind.SwapDirectories,
                _state.GetDirectoryStackEntry(1), variables, bindings, context).ConfigureAwait(false);
            return await FinalizeResultAsync(result, negated).ConfigureAwait(false);
        }

        var operand = words[operands.NextOperand];
        if (operands.KeepDirectory)
        {
            CheckCancellation();
            _state.PushDeferredDirectory(operand, Memory);
            return await FinishStackEditAsync("pushd", negated).ConfigureAwait(false);
        }

        var path = await DecodeExpandedValueAsync(operand).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        var pushResult = await ExecuteDirectoryChangeAsync(DirectoryChangeKind.PushDirectory, path, variables, bindings, context, default)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(pushResult, negated).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the <c>popd</c> builtin by popping the directory stack and updating the current directory.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinPopdAsync(
        IReadOnlyList<ShellValue> words, Variables variables, Variables.CommandBindings? bindings, EvaluationContext context, bool negated)
    {
        if (_state.DirectoryStackCount < 2)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("popd"), ShellValue.FromText("directory stack empty")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
        }

        var operands = ReadDirectoryStackOperands(words);
        var index = operands.Index ?? 0;
        if (index < 0)
            return await ReportDirectoryStackIndexAsync("popd", operands.IndexOperand, negated).ConfigureAwait(false);
        if (index > 0 || operands.KeepDirectory)
        {
            CheckCancellation();
            _state.RemoveSavedDirectory(Math.Max(1, index));
            return await FinishStackEditAsync("popd", negated).ConfigureAwait(false);
        }

        var result = await ChangeToSavedDirectoryAsync(DirectoryChangeKind.PopDirectory,
            _state.GetDirectoryStackEntry(1), variables, bindings, context).ConfigureAwait(false);
        return await FinalizeResultAsync(result, negated).ConfigureAwait(false);
    }

    private readonly record struct DirectoryStackOperands(int NextOperand, bool KeepDirectory, int? Index, ShellValue IndexOperand);

    private DirectoryStackOperands ReadDirectoryStackOperands(IReadOnlyList<ShellValue> words)
    {
        var keep = false;
        int? index = null;
        var selected = ShellValue.Empty;
        var next = 1;
        for (; next < words.Count; next++)
        {
            var operand = words[next];
            if (operand.Bytes.Span.SequenceEqual("-n"u8))
            {
                keep = true;
                continue;
            }
            if (operand.Bytes.Span.SequenceEqual("--"u8))
            {
                next++;
                break;
            }
            if (operand.Bytes.Span is not [(byte)'+' or (byte)'-', _, ..]
                || !TryParseControlNumber(ShellValue.FromBytes(operand.Bytes[1..]), out var count))
                break;

            var size = _state.DirectoryStackCount;
            index = count < 0 || count >= size ? -1
                : operand.Bytes.Span[0] == (byte)'-' ? size - (int)count - 1 : (int)count;
            selected = operand;
        }
        return new DirectoryStackOperands(next, keep, index, selected);
    }

    private async Task<EvaluationResult> ReportDirectoryStackIndexAsync(string name, ShellValue operand, bool negated)
    {
        using var diagnostic = new ByteMemoryBuilder(0, Memory.Budget);
        if (_state.DirectoryStackCount == 1)
            diagnostic.Append("directory stack empty"u8);
        else
            diagnostic.Append(operand.Bytes).Append(": directory stack index out of range"u8);
        await ReportExpansionDiagnosticAsync(ShellValue.FromText(name), ShellValue.FromBytes(diagnostic.ToMemory())).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> FinishStackEditAsync(string name, bool negated)
    {
        await WriteDirectoryStackAsync(name, currentOnly: false).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ChangeToSavedDirectoryAsync(
        DirectoryChangeKind kind, EvaluationState.DirectoryStackEntry entry,
        Variables variables, Variables.CommandBindings? bindings, EvaluationContext context)
    {
        if (entry.Path is { } path)
            return await ExecuteDirectoryChangeAsync(kind, path, variables, bindings, context, default).ConfigureAwait(false);

        var target = await DecodeExpandedValueAsync(entry.DeferredPath).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return EvaluationResult.FromExitCode(1);
        return await ExecuteDirectoryChangeAsync(kind, target, variables, bindings, context, default).ConfigureAwait(false);
    }

    private Task<EvaluationResult> ExecuteDirectoryChangeAsync(
        DirectoryChangeKind kind, string target, Variables variables, Variables.CommandBindings? bindings,
        EvaluationContext context, ReadOnlyMemory<byte> announcement)
    {
        var resolved = kind == DirectoryChangeKind.ChangeDirectory && target.Length == 0
            ? _state.CurrentDirectory : ResolvePath(target);
        return resolved != null
            ? ExecuteDirectoryChangeAsync(kind, resolved, variables, bindings, context, announcement)
            : InvalidPathAsync();

        async Task<EvaluationResult> InvalidPathAsync()
        {
            await ReportBuiltinOperationErrorAsync(ShellValue.FromText(DirectoryCommandName(kind)), string.Empty,
                target, new OperationError("invalid path\n")).ConfigureAwait(false);
            return EvaluationResult.FromExitCode(1);
        }
    }

    /// <summary>Every navigation route validates through the host before publishing its destination.</summary>
    private async Task<EvaluationResult> ExecuteDirectoryChangeAsync(
        DirectoryChangeKind kind, ShellPath target, Variables variables, Variables.CommandBindings? bindings,
        EvaluationContext context, ReadOnlyMemory<byte> announcement)
    {
        CheckCancellation();
        // The host borrows the admitted canonical path until the lookup settles.
        var result = await PathInfosOperation.AsTask(PublishOperation,
            [new FilePathLookup(target, FileLinkFollowing.Follow)], FilePathInfoRequest.KindOnly).ConfigureAwait(false);
        CheckCancellation();
        var selected = result[0];
        var succeeded = selected.Error == null && selected.Info.Kind == FilePathKind.Directory;
        var previous = _state.ApplyDirectoryLookup(kind, target, succeeded, Memory);
        if (!succeeded)
        {
            var error = selected.Error ?? new OperationError(
                selected.Info.Kind == FilePathKind.Missing ? "No such file or directory\n" : "Not a directory\n");
            await ReportBuiltinOperationErrorAsync(ShellValue.FromText(DirectoryCommandName(kind)), string.Empty,
                target.Path, error).ConfigureAwait(false);
            return EvaluationResult.FromExitCode(1);
        }

        // cd - prints the selected operand before PWD and OLDPWD bookkeeping.
        var announced = DescriptorWriteResult.Open;
        if (!announcement.IsEmpty)
            announced = await ExecuteWriteAsync(GetStdOutDescriptor(), announcement).ConfigureAwait(false);
        var scope = new ScopedEvaluation(this, variables, context);
        ShellValue? oldPwd = variables.TryGetExpansionValue("PWD", out var pwd) ? pwd : null;
        var oldResult = await AssignBuiltinAsync(VariableReference.ForName("OLDPWD"), oldPwd, scope,
            bindings, BuiltinAssignmentKind.Directory, ShellValue.FromText("cd"), expansionWriteTarget: null).ConfigureAwait(false);
        if (ExitsOnReadonly(oldResult))
            return await RunExitTrapAsync(scope, EvaluationResult.Exit(1)).ConfigureAwait(false);
        var currentPath = _state.CurrentDirectory.Path;
        Memory.Charge(checked(32L + 3L * currentPath.Length));
        var pwdResult = await AssignBuiltinAsync(VariableReference.ForName("PWD"), ShellValue.FromText(currentPath), scope,
            bindings, BuiltinAssignmentKind.Directory, ShellValue.FromText("cd"), expansionWriteTarget: null).ConfigureAwait(false);
        if (ExitsOnReadonly(pwdResult))
            return await RunExitTrapAsync(scope, EvaluationResult.Exit(1)).ConfigureAwait(false);
        if (announced.IsError)
            await ReportBuiltinWriteFailureAsync(ShellValue.FromText("cd"), announced).ConfigureAwait(false);
        if (oldResult is BuiltinAssignmentResult.Failed or BuiltinAssignmentResult.Readonly
            || pwdResult is BuiltinAssignmentResult.Failed or BuiltinAssignmentResult.Readonly)
            return EvaluationResult.FromExitCode(1);
        _state.CompleteDirectoryChange(kind, previous, Memory);
        if (kind != DirectoryChangeKind.ChangeDirectory)
            await WriteDirectoryStackAsync(DirectoryCommandName(kind), currentOnly: false).ConfigureAwait(false);
        var exitCode = announced.IsError ? 1 : 0;
        return EvaluationResult.FromExitCode(exitCode);

        // Bash's readonly assignment exit precedes builtin/redirection restoration.
        // It follows the enabled option even when the command's status is tested.
        bool ExitsOnReadonly(BuiltinAssignmentResult assigned) =>
            assigned == BuiltinAssignmentResult.Readonly && _state.ErrexitEnabled;
    }

    private Task<DescriptorWriteResult> WriteDirectoryStackAsync(string commandName, bool currentOnly)
    {
        var content = _state.FormatDirectoryStack(currentOnly, Memory, _cancellationToken);
        return WriteBuiltinOutputAsync(ShellValue.FromText(commandName), content);
    }

    private static string DirectoryCommandName(DirectoryChangeKind kind) => kind switch
    {
        DirectoryChangeKind.ChangeDirectory => "cd",
        DirectoryChangeKind.PopDirectory => "popd",
        DirectoryChangeKind.PushDirectory or DirectoryChangeKind.SwapDirectories or DirectoryChangeKind.RotateDirectories => "pushd",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Scans pwd flags as argument fields are expanded, without retaining ignored operands.</summary>
    private struct PwdOptions
    {
        public bool Finished { get; private set; }
        public bool RequestedHelp { get; private set; }
        public ReadOnlyMemory<byte> InvalidFlag { get; private set; }

        public void Read(ShellValue field)
        {
            var bytes = field.Bytes;
            var kind = ClassifyShortOptionField(bytes.Span);
            if (kind != ShortOptionField.Flags)
            {
                Finished = true;
                RequestedHelp = kind == ShortOptionField.Help;
                return;
            }

            var invalid = bytes.Span[1..].IndexOfAnyExcept((byte)'L', (byte)'P');
            if (invalid >= 0)
            {
                InvalidFlag = bytes.Slice(invalid + 1, 1);
                Finished = true;
            }
        }
    }

    /// <summary>
    /// Handles the <c>pwd</c> builtin, emitting the current directory to stdout.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinPwdAsync(ShellValue name, PwdOptions options, bool negated)
    {
        if (!options.InvalidFlag.IsEmpty)
        {
            await WriteInvalidShortOptionUnlessStderrClosedAsync(name, options.InvalidFlag, BuiltinKind.Pwd, "pwd")
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(name, BuiltinKind.Pwd, "pwd", negated)
                .ConfigureAwait(false);

        var output = await WriteDirectoryStackAsync("pwd", 0, DirectoryStackLayout.Compact, ShellValue.Empty)
            .ConfigureAwait(false);
        var exitCode = output.IsError ? 1 : 0;
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
            switch (ClassifyShortOptionField(operand.Bytes.Span))
            {
                case ShortOptionField.Terminator:
                    first++;
                    break;
                case ShortOptionField.Help:
                    return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Cd, "cd", negated)
                        .ConfigureAwait(false);
                case ShortOptionField.Flags:
                    await ReportExpansionDiagnosticAsync([words[0]], [operand, ShellValue.FromText(": invalid option")])
                        .ConfigureAwait(false);
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
            }
        }

        if (words.Count - first > 1)
            return await FailAsync("too many arguments").ConfigureAwait(false);

        var destination = first < words.Count ? words[first] : ShellValue.Empty;
        var previous = destination.Bytes.Span.SequenceEqual("-"u8);
        if (first == words.Count && !variables.TryGetExpansionValue("HOME", out destination))
            return await FailAsync("HOME not set").ConfigureAwait(false);

        var result = await ChangeToDirectoryOperandAsync(
                DirectoryChangeKind.ChangeDirectory, destination, previous, variables, bindings, context)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(result, negated).ConfigureAwait(false);

        async Task<EvaluationResult> FailAsync(string message)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("cd"), ShellValue.FromText(message)).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles the <c>dirs</c> builtin by printing or clearing the directory stack.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinDirsAsync(
        IReadOnlyList<ShellValue> words,
        Variables variables,
        bool negated)
    {
        var command = ShellValue.FromText("dirs");
        var documentation = BuiltinDocumentation.For(BuiltinKind.Dirs);
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
            return await WriteBuiltinHelpAsync(command, documentation.Synopsis("dirs"), documentation.Description, negated)
                .ConfigureAwait(false);

        var clear = false;
        var fullPaths = false;
        var layout = DirectoryStackLayout.Compact;
        int? index = null;
        var selected = ShellValue.Empty;
        for (var position = 1; position < words.Count; position++)
        {
            var operand = words[position];
            var option = operand.Bytes.Span;
            if (option.SequenceEqual("--"u8))
                break;
            if (option.SequenceEqual("-c"u8))
                clear = true;
            else if (option.SequenceEqual("-l"u8))
                fullPaths = true;
            else if (option.SequenceEqual("-v"u8))
                layout = DirectoryStackLayout.Numbered;
            else if (option.SequenceEqual("-p"u8))
            {
                if (layout == DirectoryStackLayout.Compact)
                    layout = DirectoryStackLayout.Lines;
            }
            else
            {
                if (option is not [(byte)'+' or (byte)'-', ..])
                    return await ReportDirectoryStackUsageAsync("dirs", documentation, operand, ": invalid option", negated)
                        .ConfigureAwait(false);
                if (!TryReadDirectoryStackIndex(option, _state.DirectoryStackCount, out var logicalIndex))
                    return await ReportDirectoryStackUsageAsync("dirs", documentation, operand, ": invalid number", negated)
                        .ConfigureAwait(false);
                index = logicalIndex;
                selected = operand;
            }
        }
        if (clear)
        {
            CheckCancellation();
            _state.ResetDirectoryStack();
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
        }
        if (index is < 0)
            return await ReportDirectoryStackIndexAsync("dirs", ShellValue.FromBytes(selected.Bytes[1..]), negated)
                .ConfigureAwait(false);
        var home = !fullPaths && variables.TryGetExpansionValue("HOME", out var value) ? value : ShellValue.Empty;
        var output = await WriteDirectoryStackAsync("dirs", index, layout, home).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(output.IsError ? 1 : 0), negated)
            .ConfigureAwait(false);
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
        var operands = ReadDirectoryStackOperands(words, BuiltinKind.Pushd);
        if (operands.Outcome != DirectoryStackOperandOutcome.Run)
            return await FinishDirectoryStackOperandsAsync(BuiltinKind.Pushd, operands, words, negated).ConfigureAwait(false);
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
            return await FinishStackEditAsync("pushd", variables, negated).ConfigureAwait(false);
        }

        var pushResult = await ChangeToDirectoryOperandAsync(DirectoryChangeKind.PushDirectory, operand,
                operand.Bytes.Span.SequenceEqual("-"u8), variables, bindings, context)
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
        var operands = ReadDirectoryStackOperands(words, BuiltinKind.Popd);
        if (operands.Outcome != DirectoryStackOperandOutcome.Run)
            return await FinishDirectoryStackOperandsAsync(BuiltinKind.Popd, operands, words, negated).ConfigureAwait(false);
        if (_state.DirectoryStackCount < 2)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("popd"), ShellValue.FromText("directory stack empty")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
        }

        var index = operands.Index ?? 0;
        if (index < 0)
            return await ReportDirectoryStackIndexAsync("popd", operands.IndexOperand, negated).ConfigureAwait(false);
        if (index > 0 || operands.KeepDirectory)
        {
            CheckCancellation();
            _state.RemoveSavedDirectory(Math.Max(1, index));
            return await FinishStackEditAsync("popd", variables, negated).ConfigureAwait(false);
        }

        var result = await ChangeToSavedDirectoryAsync(DirectoryChangeKind.PopDirectory,
            _state.GetDirectoryStackEntry(1), variables, bindings, context).ConfigureAwait(false);
        return await FinalizeResultAsync(result, negated).ConfigureAwait(false);
    }

    private enum DirectoryStackOperandOutcome { Run, Help, InvalidArgument, InvalidNumber, TooManyArguments }

    private readonly record struct DirectoryStackOperands(
        int NextOperand, bool KeepDirectory, int? Index, ShellValue IndexOperand, DirectoryStackOperandOutcome Outcome);

    private DirectoryStackOperands ReadDirectoryStackOperands(IReadOnlyList<ShellValue> words, BuiltinKind kind)
    {
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
            return new DirectoryStackOperands(1, false, null, ShellValue.Empty, DirectoryStackOperandOutcome.Help);

        var push = kind == BuiltinKind.Pushd;
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
            if (operand.Bytes.Span is not [(byte)'+' or (byte)'-', ..]
                || push && operand.Bytes.Span.SequenceEqual("-"u8))
            {
                if (!push)
                    return new DirectoryStackOperands(next, keep, index, selected, DirectoryStackOperandOutcome.InvalidArgument);
                break;
            }
            if (!TryReadDirectoryStackIndex(operand.Bytes.Span, _state.DirectoryStackCount, out var logicalIndex))
                return new DirectoryStackOperands(next, keep, index, selected, DirectoryStackOperandOutcome.InvalidNumber);

            index = logicalIndex;
            selected = operand;
        }
        var outcome = push && !keep && !index.HasValue && words.Count - next > 1
            ? DirectoryStackOperandOutcome.TooManyArguments : DirectoryStackOperandOutcome.Run;
        return new DirectoryStackOperands(next, keep, index, selected, outcome);
    }

    private static bool TryReadDirectoryStackIndex(ReadOnlySpan<byte> operand, int size, out int index)
    {
        if (!Integers.TryParseBuiltinNumber(operand[1..], out var count))
        {
            index = -1;
            return false;
        }
        index = count < 0 || count >= size ? -1 : operand[0] == (byte)'-' ? size - (int)count - 1 : (int)count;
        return true;
    }

    private async Task<EvaluationResult> FinishDirectoryStackOperandsAsync(
        BuiltinKind kind, DirectoryStackOperands operands, IReadOnlyList<ShellValue> words, bool negated)
    {
        var name = kind == BuiltinKind.Pushd ? "pushd" : "popd";
        var command = ShellValue.FromText(name);
        var documentation = BuiltinDocumentation.For(kind);
        if (operands.Outcome == DirectoryStackOperandOutcome.Help)
            return await WriteBuiltinHelpAsync(command, documentation.Synopsis(name), documentation.Description, negated)
                .ConfigureAwait(false);
        if (operands.Outcome == DirectoryStackOperandOutcome.TooManyArguments)
        {
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("too many arguments")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }

        var reason = operands.Outcome == DirectoryStackOperandOutcome.InvalidNumber ? ": invalid number" : ": invalid argument";
        return await ReportDirectoryStackUsageAsync(name, documentation, words[operands.NextOperand], reason, negated)
            .ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ReportDirectoryStackUsageAsync(string name, BuiltinDocumentation documentation,
        ShellValue operand, string reason, bool negated)
    {
        var command = ShellValue.FromText(name);
        await ReportExpansionDiagnosticAsync([command], [operand, ShellValue.FromText(reason)])
            .ConfigureAwait(false);
        await ReportExpansionDiagnosticAsync([command],
                [ShellValue.FromText("usage: "), ShellValue.FromText(documentation.Synopsis(name))])
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ReportDirectoryStackIndexAsync(string name, ShellValue operand, bool negated)
    {
        var command = ShellValue.FromText(name);
        if (_state.DirectoryStackCount == 1)
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("directory stack empty")).ConfigureAwait(false);
        else
            await ReportExpansionDiagnosticAsync([command], [operand, ShellValue.FromText(": directory stack index out of range")])
                .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> FinishStackEditAsync(string name, Variables variables, bool negated)
    {
        await WriteDirectoryStackAsync(name, variables).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ChangeToDirectoryOperandAsync(
        DirectoryChangeKind kind, ShellValue destination, bool previous, Variables variables,
        Variables.CommandBindings? bindings, EvaluationContext context)
    {
        if (previous && !variables.TryGetExpansionValue("OLDPWD", out destination))
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText(DirectoryCommandName(kind)), ShellValue.FromText("OLDPWD not set"))
                .ConfigureAwait(false);
            return EvaluationResult.FromExitCode(1);
        }

        var target = await DecodeExpandedValueAsync(destination).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return EvaluationResult.FromExitCode(1);
        using var announcement = previous ? new ByteMemoryBuilder(checked(destination.Bytes.Length + 1), Memory.Budget) : null;
        var output = announcement == null ? ReadOnlyMemory<byte>.Empty
            : announcement.Append(destination.Bytes).Append((byte)'\n').ToMemory();
        return await ExecuteDirectoryChangeAsync(kind, target, variables, bindings, context, output).ConfigureAwait(false);
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
        var resolved = target.Length == 0 && kind is DirectoryChangeKind.ChangeDirectory or DirectoryChangeKind.PushDirectory
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

        // A dash operand prints the selected spelling before PWD and OLDPWD bookkeeping.
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
            await ReportBuiltinWriteFailureAsync(ShellValue.FromText(DirectoryCommandName(kind)), announced).ConfigureAwait(false);
        if (oldResult is BuiltinAssignmentResult.Failed or BuiltinAssignmentResult.Readonly
            || pwdResult is BuiltinAssignmentResult.Failed or BuiltinAssignmentResult.Readonly
            || kind == DirectoryChangeKind.PushDirectory && announced.IsError)
            return EvaluationResult.FromExitCode(1);
        _state.CompleteDirectoryChange(kind, previous, Memory);
        if (kind != DirectoryChangeKind.ChangeDirectory)
            await WriteDirectoryStackAsync(DirectoryCommandName(kind), variables).ConfigureAwait(false);
        var exitCode = announced.IsError ? 1 : 0;
        return EvaluationResult.FromExitCode(exitCode);

        // Bash's readonly assignment exit precedes builtin/redirection restoration.
        // It follows the enabled option even when the command's status is tested.
        bool ExitsOnReadonly(BuiltinAssignmentResult assigned) =>
            assigned == BuiltinAssignmentResult.Readonly && _state.ErrexitEnabled;
    }

    private Task<DescriptorWriteResult> WriteDirectoryStackAsync(string commandName, Variables variables)
    {
        variables.TryGetExpansionValue("HOME", out var home);
        return WriteDirectoryStackAsync(commandName, null, DirectoryStackLayout.Compact, home);
    }

    private Task<DescriptorWriteResult> WriteDirectoryStackAsync(
        string commandName, int? index, DirectoryStackLayout layout, ShellValue home)
    {
        var content = _state.FormatDirectoryStack(index, layout, home, Memory, _cancellationToken);
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

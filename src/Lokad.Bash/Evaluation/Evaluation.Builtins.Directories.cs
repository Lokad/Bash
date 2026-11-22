using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
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
        var selection = first == words.Count ? DirectoryOperandKind.Home : ClassifyDirectoryOperand(destination);
        var result = await ChangeToDirectoryOperandAsync(
                DirectoryChangeKind.ChangeDirectory, destination, selection, variables, bindings, context)
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
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], BuiltinKind.Dirs, "dirs", negated)
                .ConfigureAwait(false);
        var documentation = BuiltinDocumentation.For(BuiltinKind.Dirs);

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
                ClassifyDirectoryOperand(operand), variables, bindings, context)
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
        var size = _state.DirectoryStackCount;
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
            if (!push && operand.IsEmpty)
            {
                // An empty popd operand ends scanning and selects a saved entry from the bottom.
                if (index == null)
                    index = size - 1;
                else if (index >= 0 && selected.Bytes.Span[0] == (byte)'+')
                    index = size - index.Value - 1;
                if (index == 0)
                    index = -1;
                break;
            }
            if (operand.Bytes.Span is not [(byte)'+' or (byte)'-', ..]
                || push && operand.Bytes.Span.SequenceEqual("-"u8))
            {
                if (!push)
                    return new DirectoryStackOperands(next, keep, index, selected, DirectoryStackOperandOutcome.InvalidArgument);
                break;
            }
            if (!TryReadDirectoryStackIndex(operand.Bytes.Span, size, out var logicalIndex))
                return new DirectoryStackOperands(next, keep, index, selected, DirectoryStackOperandOutcome.InvalidNumber);

            index = logicalIndex;
            selected = operand;
            if (push && logicalIndex < 0)
                break;
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
        if (operands.Outcome == DirectoryStackOperandOutcome.Help)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(words[0], kind, name, negated)
                .ConfigureAwait(false);
        if (operands.Outcome == DirectoryStackOperandOutcome.TooManyArguments)
        {
            await ReportExpansionDiagnosticAsync(words[0], ShellValue.FromText("too many arguments")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }

        var reason = operands.Outcome == DirectoryStackOperandOutcome.InvalidNumber ? ": invalid number" : ": invalid argument";
        return await ReportDirectoryStackUsageAsync(name, BuiltinDocumentation.For(kind),
            words[operands.NextOperand], reason, negated).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ReportDirectoryStackUsageAsync(string name, BuiltinDocumentation documentation,
        ShellValue operand, string reason, bool negated)
    {
        var command = ShellValue.FromText(name);
        await WriteDiagnosticUnlessStderrClosedAsync([command], [operand, ShellValue.FromText(reason)], _builtinDiagnostics)
            .ConfigureAwait(false);
        await WriteDiagnosticUnlessStderrClosedAsync([command],
                [ShellValue.FromText("usage: "), ShellValue.FromText(documentation.Synopsis(name))], _builtinDiagnostics)
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

    private enum DirectoryOperandKind { Explicit, Home, Previous }

    private static DirectoryOperandKind ClassifyDirectoryOperand(ShellValue operand) =>
        operand.Bytes.Span.SequenceEqual("-"u8) ? DirectoryOperandKind.Previous : DirectoryOperandKind.Explicit;

    private async Task<EvaluationResult> ChangeToDirectoryOperandAsync(
        DirectoryChangeKind kind, ShellValue destination, DirectoryOperandKind selection, Variables variables,
        Variables.CommandBindings? bindings, EvaluationContext context)
    {
        if (selection != DirectoryOperandKind.Explicit)
        {
            var variable = selection == DirectoryOperandKind.Home ? "HOME" : "OLDPWD";
            if (!variables.TryGetExpansionValue(variable, out destination))
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(DirectoryCommandName(kind)),
                    ShellValue.FromText(variable + " not set")).ConfigureAwait(false);
                return EvaluationResult.FromExitCode(1);
            }
        }

        var target = await DecodeExpandedValueAsync(destination).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return EvaluationResult.FromExitCode(1);
        using var announcement = selection == DirectoryOperandKind.Previous
            ? new ByteMemoryBuilder(checked(destination.Bytes.Length + 1), Memory.Budget) : null;
        var output = announcement == null ? ReadOnlyMemory<byte>.Empty
            : announcement.Append(destination.Bytes).Append((byte)'\n').ToMemory();
        return await ExecuteDirectoryChangeAsync(kind, target, variables, bindings, context, output,
            searchCdPath: selection == DirectoryOperandKind.Explicit).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ChangeToSavedDirectoryAsync(
        DirectoryChangeKind kind, EvaluationState.DirectoryStackEntry entry,
        Variables variables, Variables.CommandBindings? bindings, EvaluationContext context)
    {
        if (entry.Path is { } path)
            return await ExecuteDirectoryChangeAsync(kind, path, path.Path, variables, bindings, context, default)
                .ConfigureAwait(false);

        var target = await DecodeExpandedValueAsync(entry.DeferredPath).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return EvaluationResult.FromExitCode(1);
        return await ExecuteDirectoryChangeAsync(kind, target, variables, bindings, context, default,
            searchCdPath: target != "-").ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ExecuteDirectoryChangeAsync(
        DirectoryChangeKind kind, string target, Variables variables, Variables.CommandBindings? bindings,
        EvaluationContext context, ReadOnlyMemory<byte> announcement, bool searchCdPath)
    {
        if (searchCdPath && target is not ("." or "..")
            && !target.StartsWith('/') && !target.StartsWith("./", StringComparison.Ordinal)
            && !target.StartsWith("../", StringComparison.Ordinal)
            && variables.TryGetExpansionValue("CDPATH", out var cdPath) && !cdPath.IsEmpty
            && await TryChangeThroughCdPathAsync(cdPath.Bytes).ConfigureAwait(false) is { } searched)
            return searched;

        var resolved = target.Length == 0 && kind is DirectoryChangeKind.ChangeDirectory or DirectoryChangeKind.PushDirectory
            ? _state.CurrentDirectory : ResolvePath(target, Memory.Workspace);
        if (resolved != null)
            return await ExecuteDirectoryChangeAsync(kind, resolved, target, variables, bindings, context, announcement)
                .ConfigureAwait(false);
        await ReportBuiltinOperationErrorAsync(ShellValue.FromText(DirectoryCommandName(kind)), string.Empty,
            target, new OperationError("invalid path\n")).ConfigureAwait(false);
        return EvaluationResult.FromExitCode(1);

        async Task<EvaluationResult?> TryChangeThroughCdPathAsync(ReadOnlyMemory<byte> remaining)
        {
            using var lookupMemory = new MemoryCapacity(Memory.Budget, 0);
            var scope = new ScopedEvaluation(this, variables, context);
            while (true)
            {
                CheckCancellation();
                var separator = remaining.Span.IndexOf((byte)':');
                var component = separator < 0 ? remaining : remaining[..separator];
                var (prefix, suffix) = WordExpansion.ExpandPathTilde(component, scope, lookupMemory);
                var joined = JoinSearchPathComponent(prefix, suffix, target, lookupMemory);
                var candidate = joined is "" ? _state.CurrentDirectory
                    : joined != null ? ResolvePath(joined, lookupMemory) : null;
                if (candidate != null)
                {
                    var lookup = await LookupDirectoryAsync(candidate).ConfigureAwait(false);
                    if (IsDirectory(lookup))
                    {
                        ReadOnlyMemory<byte> output = default;
                        if (!component.IsEmpty)
                        {
                            Memory.Charge(lookupMemory, checked(33L + Utf8Text.StrictUtf8.GetByteCount(candidate.Path)));
                            output = Utf8Text.EncodeLine(candidate.Path);
                        }
                        // The command holds the new retained head after the candidate workspace is retired.
                        Memory.PrepareRetainedStorage(checked(48L + 2L * candidate.Path.Length));
                        return await FinishDirectoryChangeAsync(kind, candidate, target, lookup,
                            variables, bindings, context, output).ConfigureAwait(false);
                    }
                }
                lookupMemory.Resize(0);
                if (separator < 0)
                    return null;
                remaining = remaining[(separator + 1)..];
            }
        }
    }

    private static bool IsDirectory(FilePathResolutionResult result) =>
        result.Error == null && result.Info.Kind == FilePathKind.Directory;

    private ShellPath? ResolvePath(string path, MemoryCapacity owner)
    {
        if (!RawShellPath.TryParse(path, out var raw))
            return null;
        var current = _state.CurrentDirectory;
        var length = (long)path.Length + (path[0] == '/' ? 0 : (long)current.Path.Length + 1);
        // Cover the combined and normalized text plus overlapping segment buffers.
        Memory.Charge(owner, checked(256 + 16 * length));
        return raw.TryToAbsolute(current, jail: false, out var absolute) ? absolute : null;
    }

    /// <summary>Looks up the admitted candidate before any directory state is published.</summary>
    private async Task<FilePathResolutionResult> LookupDirectoryAsync(ShellPath target)
    {
        CheckCancellation();
        var result = await PathInfosOperation.AsTask(PublishOperation,
            [new FilePathLookup(target, FileLinkFollowing.Follow)], FilePathInfoRequest.KindOnly).ConfigureAwait(false);
        CheckCancellation();
        return result[0];
    }

    private async Task<EvaluationResult> ExecuteDirectoryChangeAsync(
        DirectoryChangeKind kind, ShellPath target, string diagnosticPath, Variables variables, Variables.CommandBindings? bindings,
        EvaluationContext context, ReadOnlyMemory<byte> announcement)
    {
        var lookup = await LookupDirectoryAsync(target).ConfigureAwait(false);
        return await FinishDirectoryChangeAsync(kind, target, diagnosticPath, lookup, variables, bindings, context, announcement)
            .ConfigureAwait(false);
    }

    /// <summary>Publishes the final settled lookup and applies its announcement and directory-variable effects.</summary>
    private async Task<EvaluationResult> FinishDirectoryChangeAsync(
        DirectoryChangeKind kind, ShellPath target, string diagnosticPath, FilePathResolutionResult selected,
        Variables variables, Variables.CommandBindings? bindings, EvaluationContext context, ReadOnlyMemory<byte> announcement)
    {
        var succeeded = IsDirectory(selected);
        var previous = _state.ApplyDirectoryLookup(kind, target, succeeded, Memory);
        if (!succeeded)
        {
            var error = selected.Error ?? new OperationError(
                selected.Info.Kind == FilePathKind.Missing ? "No such file or directory\n" : "Not a directory\n");
            await ReportBuiltinOperationErrorAsync(ShellValue.FromText(DirectoryCommandName(kind)), string.Empty,
                diagnosticPath, error).ConfigureAwait(false);
            return EvaluationResult.FromExitCode(1);
        }

        // Announce the selected path before PWD and OLDPWD bookkeeping.
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

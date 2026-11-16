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
        var exitCode = (await ExecuteWriteAsync(
                GetStdOutDescriptor(),
                Utf8Text.Encode(_state.CurrentDirectory.Path + "\n"))
            .ConfigureAwait(false)).IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    /// <summary>
    /// Handles the <c>cd</c> builtin, updating the working directory and related variables.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinCdAsync(
        IReadOnlyList<string> words,
        Variables variables,
        Variables.CommandBindings? bindings,
        bool negated)
    {
        if (words.Count < 2)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        var target = words[1];

        var exitCode = await ExecuteDirectoryChangeAsync(DirectoryChangeKind.ChangeDirectory, target, variables, bindings)
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
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
        }

        var content = _state.FormatDirectoryStack();
        var appendExitCode = (await ExecuteWriteAsync(GetStdOutDescriptor(), content).ConfigureAwait(false)).IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(appendExitCode), negated);
    }

    /// <summary>
    /// Handles the <c>pushd</c> builtin by pushing a directory or swapping stack entries.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinPushdAsync(
        IReadOnlyList<string> words,
        Variables variables,
        Variables.CommandBindings? bindings,
        bool negated)
    {
        if (words.Count == 1)
        {
            if (_state.DirectoryStack.Count < 2)
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText("pushd"), ShellValue.FromText("no other directory")).ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
            }

            var exitCode = await ExecuteDirectoryChangeAsync(DirectoryChangeKind.SwapDirectories,
                _state.DirectoryStack[1], variables, bindings).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
        }

        var target = words[1];

        var pushExitCode = await ExecuteDirectoryChangeAsync(DirectoryChangeKind.PushDirectory, target, variables, bindings).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(pushExitCode), negated);
    }

    /// <summary>
    /// Handles the <c>popd</c> builtin by popping the directory stack and updating the current directory.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinPopdAsync(
        Variables variables, Variables.CommandBindings? bindings, bool negated)
    {
        if (_state.DirectoryStack.Count < 2)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("popd"), ShellValue.FromText("directory stack empty")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);
        }

        var exitCode = await ExecuteDirectoryChangeAsync(DirectoryChangeKind.PopDirectory,
            _state.DirectoryStack[1], variables, bindings).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    private Task<int> ExecuteDirectoryChangeAsync(
        DirectoryChangeKind kind, string target, Variables variables, Variables.CommandBindings? bindings)
    {
        return ResolvePath(target) is { } resolved
            ? ExecuteDirectoryChangeAsync(kind, resolved, variables, bindings)
            : InvalidPathAsync();

        async Task<int> InvalidPathAsync()
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText(DirectoryCommandName(kind)),
                ShellValue.FromText(target + ": invalid path")).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Every navigation route validates through the host before publishing its destination.</summary>
    private async Task<int> ExecuteDirectoryChangeAsync(
        DirectoryChangeKind kind, ShellPath target, Variables variables, Variables.CommandBindings? bindings)
    {
        var result = await AwaitOperationAsync(PathInfosOperation.AsTask(PublishOperation,
            [new FilePathLookup(target, FileLinkFollowing.Follow)], FilePathInfoRequest.KindOnly)).ConfigureAwait(false);
        var selected = result[0];
        var succeeded = selected.Error == null && selected.Info.Kind == FilePathKind.Directory;
        var previous = _state.CurrentDirectory;
        _state.ApplyDirectoryChange(kind, target, succeeded);
        if (!succeeded)
        {
            var detail = selected.Error is { } error ? error.MessageWithoutTerminator
                : selected.Info.Kind == FilePathKind.Missing ? "No such file or directory" : "Not a directory";
            await ReportExpansionDiagnosticAsync(ShellValue.FromText(DirectoryCommandName(kind)),
                ShellValue.FromText(target.Path + ": " + detail)).ConfigureAwait(false);
            return 1;
        }

        UpdateDirectoryVariables();
        return kind == DirectoryChangeKind.ChangeDirectory ? 0
            : (await ExecuteWriteAsync(GetStdOutDescriptor(), _state.FormatDirectoryStack()).ConfigureAwait(false)).IsError ? 1 : 0;

        void UpdateDirectoryVariables()
        {
            var oldPwd = variables.TryGetScalar("PWD", out var pwd) ? pwd : ShellValue.FromText(previous.Path);
            if (!variables.IsReadonly("OLDPWD"))
            {
                variables.SetScalarIgnoringReadonly("OLDPWD", oldPwd);
                bindings?.Retain("OLDPWD");
            }
            if (!variables.IsReadonly("PWD"))
            {
                variables.SetScalarIgnoringReadonly("PWD", ShellValue.FromText(_state.CurrentDirectory.Path));
                bindings?.Retain("PWD");
            }
        }
    }

    private static string DirectoryCommandName(DirectoryChangeKind kind) => kind switch
    {
        DirectoryChangeKind.ChangeDirectory => "cd",
        DirectoryChangeKind.PopDirectory => "popd",
        DirectoryChangeKind.PushDirectory or DirectoryChangeKind.SwapDirectories => "pushd",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

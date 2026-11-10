using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

            var previous = _state.CurrentDirectory;
            _state.SwapDirectoryStack();
            UpdateDirectoryVariables(previous, _state.CurrentDirectory, variables, bindings);
            var appendExitCode = (await ExecuteWriteAsync(GetStdOutDescriptor(), _state.FormatDirectoryStack())
                .ConfigureAwait(false)).IsError ? 1 : 0;
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(appendExitCode), negated);
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
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        var previous = _state.CurrentDirectory;
        if (!_state.TryPopDirectory())
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        UpdateDirectoryVariables(previous, _state.CurrentDirectory, variables, bindings);
        var appendExitCode = (await ExecuteWriteAsync(GetStdOutDescriptor(), _state.FormatDirectoryStack())
            .ConfigureAwait(false)).IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(appendExitCode), negated);
    }
}

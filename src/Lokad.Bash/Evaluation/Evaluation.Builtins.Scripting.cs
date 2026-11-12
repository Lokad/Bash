using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Handles the <c>caller</c> builtin by emitting current function context information.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinCallerAsync(
        EvaluationContext context,
        bool negated)
    {
        if (context.FunctionContext == null)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        var functionName = context.FunctionContext.FunctionName;
        using var output = new ByteMemoryBuilder(0, Memory.Budget);
        output.Append("0 "u8).Append(functionName.Bytes).Append((byte)'\n');
        var exitCode = (await ExecuteWriteAsync(
            GetStdOutDescriptor(),
            output.ToMemory()).ConfigureAwait(false)).IsError ? 1 : 0;
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
    }

    /// <summary>
    /// Handles the <c>source</c>/<c>.</c> builtin by evaluating a script file in the current context.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinSourceAsync(
        string? path,
        IReadOnlyList<ShellValue> arguments,
        ScopedEvaluation scope,
        Variables commandVars,
        bool negated)
    {
        if (path is null)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        if (arguments.Count == 0)
        {
            var resultWithoutArguments = await ExecuteSourceAsync(path, scope with { Variables = commandVars }).ConfigureAwait(false);
            return ApplyNegation(resultWithoutArguments, negated);
        }

        Memory.Charge(checked(commandVars.PositionalMemoryBytes + 32L * arguments.Count));
        using var positionalParametersScope = commandVars.EnterPositionalParametersScope(
            arguments,
            PositionalParametersRestorePolicy.UnlessReplaced);
        var result = await ExecuteSourceAsync(path, scope with { Variables = commandVars }).ConfigureAwait(false);

        return ApplyNegation(result, negated);
    }

    /// <summary>
    /// Handles the <c>eval</c> builtin by parsing and executing the concatenated arguments.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinEvalAsync(
        IReadOnlyList<ShellValue> words,
        ScopedEvaluation scope,
        Variables commandVars,
        bool negated)
    {
        if (words.Count == 1)
            return ApplyNegation(EvaluationResult.FromExitCode(0), negated);

        // Source copies and separators were reserved before prefix assignments and redirections.
        var builder = ByteMemoryBuilder.CreateUnaccounted(0);
        for (var index = 1; index < words.Count; index++)
        {
            var word = words[index];

            if (index > 1)
                builder.Append((byte)' ');

            builder.Append(word.Bytes);
        }

        return ApplyNegation(await EvaluateCommandUnitsAsync(ShellSource.FromBytes(builder.ToMemory()),
            scope with { Variables = commandVars }, ScriptExecutionKind.Eval).ConfigureAwait(false), negated);
    }

    private enum ScriptExecutionKind { Root, Source, Eval }

    /// <summary>Shares complete-command parsing and execution across the public entry point, source and eval.</summary>
    private async Task<EvaluationResult> EvaluateCommandUnitsAsync(
        ShellSource source, ScopedEvaluation scope, ScriptExecutionKind kind)
    {
        var result = EvaluationResult.FromExitCode(0);
        using var commands = Parser.TryParseCommandUnits(source.Bytes).GetEnumerator();
        while (true)
        {
            CheckCancellation();
            if (!commands.MoveNext())
                break;
            if (commands.Current is CommandUnitParseResult.Failed failedUnit)
            {
                await ReportSyntaxErrorAsync(failedUnit.Error).ConfigureAwait(false);
                return result.WithExitCode(2);
            }

            // Loop flow skips execution but still checks the remaining source/eval syntax.
            if (result.Flow is FlowControlKind.Break or FlowControlKind.Continue)
                continue;

            result = await EvaluateCommandAsync(((CommandUnitParseResult.Parsed)commands.Current).Command, scope, negated: false)
                .ConfigureAwait(false);
            if (result.Flow == FlowControlKind.Return)
                return kind == ScriptExecutionKind.Source ? EvaluationResult.FromExitCode(result.ExitCode) : result;
            if (result.Flow == FlowControlKind.Exit)
            {
                // Recover only the expansion failures that end the current command unit.
                // Explicit exit, errexit and unrecoverable expansion failures still terminate.
                result = await FinalizeResultAsync(RecoverScriptExpansionFailure(result, kind), negated: false).ConfigureAwait(false);
                if (result.Flow == FlowControlKind.Exit)
                    return result;
            }

            if (result.Flow == FlowControlKind.None && _state.TryGetStickyExitCode(out var sticky))
                result = result.WithExitCode(sticky);
        }
        return RecoverScriptExpansionFailure(result, kind);
    }

    /// <summary>Recovers unit-scoped expansion failures while preserving root versus nested status rules.</summary>
    private EvaluationResult RecoverScriptExpansionFailure(EvaluationResult result, ScriptExecutionKind kind)
    {
        if (_state.FatalExpansion is not { Kind: ExpansionFailureKind.Arithmetic or ExpansionFailureKind.AssignmentTarget or ExpansionFailureKind.NamerefDefault } failure)
            return result;

        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        // Nameref defaults fail with 1 at script boundaries, but with 2 at the root.
        return EvaluationResult.FromExitCode(failure.Kind == ExpansionFailureKind.NamerefDefault && kind != ScriptExecutionKind.Root ? 1 : failure.ExitCode);
    }
}

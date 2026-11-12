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

        Memory.Charge(commandVars.PositionalMemoryBytes);
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

        if (!Parser.TryParse(builder.ToMemory(), out var parsed, out var error))
        {
            await ReportSyntaxErrorAsync(error).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated);
        }

        if (parsed.Commands.Count == 0)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated);

        var evalResult = await EvaluateParsedScriptAsync(parsed, scope with { Variables = commandVars }, RootFlowHandling.Preserve)
            .ConfigureAwait(false);
        return ApplyNegation(RecoverScriptExpansionFailure(evalResult), negated);
    }

    /// <summary>Returns recoverable expansion failures to an eval/source caller.</summary>
    private EvaluationResult RecoverScriptExpansionFailure(EvaluationResult result)
    {
        if (_state.FatalExpansion is not { Kind: ExpansionFailureKind.Arithmetic or ExpansionFailureKind.AssignmentTarget or ExpansionFailureKind.NamerefDefault } failure)
            return result;

        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        // Nameref defaults fail with 1 at script boundaries, but with 2 at the root.
        return EvaluationResult.FromExitCode(failure.Kind == ExpansionFailureKind.NamerefDefault ? 1 : failure.ExitCode);
    }
}

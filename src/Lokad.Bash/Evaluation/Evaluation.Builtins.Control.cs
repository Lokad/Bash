using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Validates return operands and unwinds the enclosing function or sourced script.
    /// </summary>
    /// <param name="words">The completed command name and operands.</param>
    /// <param name="scope">The active bindings and lexical evaluation context.</param>
    /// <param name="negated">Whether to negate the final exit status.</param>
    /// <returns>
    /// The finalized evaluation result.
    /// </returns>
    private async Task<EvaluationResult> EvaluateBuiltinReturnAsync(
        IReadOnlyList<ShellValue> words,
        ScopedEvaluation scope,
        bool negated)
    {
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
        {
            var documentation = BuiltinDocumentation.For(BuiltinKind.Return);
            await ExecuteWriteAsync(GetStdOutDescriptor(),
                Utf8Text.Encode("return: " + documentation.Synopsis("return") + "\n" + documentation.Description + "\n"))
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }

        var exitCode = _state.Traps?.ExitTrapStatus ?? _state.LastExitCode ?? 0;
        var firstOperand = words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--"u8) ? 2 : 1;
        if (firstOperand < words.Count)
        {
            var operand = words[firstOperand];
            // Bash accepts leading C whitespace, but only space/tab after the number.
            var number = operand.Bytes.Span.TrimStart(" \t\r\n\v\f"u8).TrimEnd(" \t"u8);
            if (!Integers.TryParseNumber(number, out var status))
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText("return"),
                    WordExpansion.AppendAssignmentValue(operand, ShellValue.FromText(": numeric argument required"), scope))
                    .ConfigureAwait(false);
                exitCode = 2;
            }
            else if (words.Count - firstOperand > 1)
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText("return"),
                    ShellValue.FromText("too many arguments")).ConfigureAwait(false);
                return EvaluationResult.Exit(1);
            }
            else
            {
                exitCode = (int)(status & 255);
            }
        }

        if (scope.Context.ReturnTarget == ReturnTargetKind.None)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("return"),
                ShellValue.FromText("can only `return' from a function or sourced script")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }

        return await FinalizeResultAsync(EvaluationResult.Return(exitCode), negated).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the <c>break</c> builtin by producing break flow control with a loop level.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinBreakAsync(
        IReadOnlyList<string> words,
        ScopedEvaluation scope,
        bool negated)
    {
        var loopLevel = GetLoopLevel(words);
        if (!loopLevel.Success)
        {
            if (scope.Context.LoopDepth == 0)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(loopLevel.ErrorExitCode), negated);

            var breakLevel = Math.Min(1, scope.Context.LoopDepth);
            return await FinalizeResultAsync(EvaluationResult.Break(breakLevel, loopLevel.ErrorExitCode), negated);
        }

        if (scope.Context.LoopDepth == 0)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        var breakLevelAdjusted = Math.Min(loopLevel.Level, scope.Context.LoopDepth);
        return await FinalizeResultAsync(EvaluationResult.Break(breakLevelAdjusted, 0), negated);
    }

    /// <summary>
    /// Handles the <c>continue</c> builtin by producing continue flow control with a loop level.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinContinueAsync(
        IReadOnlyList<string> words,
        ScopedEvaluation scope,
        bool negated)
    {
        var loopLevel = GetLoopLevel(words);
        if (!loopLevel.Success)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(loopLevel.ErrorExitCode), negated);

        if (scope.Context.LoopDepth == 0)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        var continueLevelAdjusted = Math.Min(loopLevel.Level, scope.Context.LoopDepth);
        return await FinalizeResultAsync(EvaluationResult.Continue(continueLevelAdjusted, 0), negated);
    }

    /// <summary>
    /// Handles the <c>exit</c> builtin by producing an exit flow control result.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinExitAsync(
        IReadOnlyList<string> words)
    {
        var exitCode = _state.Traps?.ExitTrapStatus ?? _state.LastExitCode ?? 0;
        if (words.Count > 1)
        {
            var statusWord = words[1];

            if (!Integers.TryParseExitStatus(statusWord.AsSpan(), out exitCode))
                exitCode = 2;
        }

        return EvaluationResult.Exit(exitCode);
    }
}

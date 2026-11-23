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
            return await WriteControlHelpAsync(words[0], BuiltinKind.Return, "return", negated).ConfigureAwait(false);

        var exitCode = _state.Traps?.ExitTrapStatus ?? _state.LastExitCode ?? 0;
        var firstOperand = words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--"u8) ? 2 : 1;
        if (firstOperand < words.Count)
        {
            var operand = words[firstOperand];
            // Bash accepts leading C whitespace, but only space/tab after the number.
            if (!TryParseControlNumber(operand, out var status))
            {
                await ReportExpansionDiagnosticAsync([ShellValue.FromText("return")],
                    [operand, ShellValue.FromText(": numeric argument required")])
                    .ConfigureAwait(false);
                exitCode = 2;
            }
            else if (words.Count - firstOperand > 1)
                return await DiscardForExtraControlArgumentsAsync(ShellValue.FromText("return")).ConfigureAwait(false);
            else
            {
                exitCode = (int)(status & 255);
            }
        }

        if (scope.Context.ReturnTarget == ReturnTargetKind.None)
        {
            var name = ShellValue.FromText("return");
            var message = ShellValue.FromText("can only `return' from a function or sourced script");
            if (_state.Traps?.ExitTrapStatus.HasValue != true)
                await ReportExpansionDiagnosticAsync(name, message).ConfigureAwait(false);
            else if (_builtinDiagnostics is not { Result.CanAcceptMore: false })
            {
                var prefix = new SyntaxDiagnosticPrefix(scope.Context.CallerSource?.Path, SyntaxDiagnosticOrigin.ScriptLine,
                    Math.Max(1, scope.Context.Coordinates.Line));
                await WriteDiagnosticAsync(GetStdErrDescriptor(), [name], [message], _builtinDiagnostics, prefix).ConfigureAwait(false);
            }
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }

        return await FinalizeResultAsync(EvaluationResult.Return(exitCode), negated).ConfigureAwait(false);
    }

    /// <summary>Validates a reached loop-control builtin and preserves its distinct failure flows.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinLoopControlAsync(
        BuiltinKind builtin, IReadOnlyList<ShellValue> words, ScopedEvaluation scope, bool negated)
    {
        var commandName = builtin == BuiltinKind.Break ? "break" : "continue";
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
            return await WriteControlHelpAsync(words[0], builtin, commandName, negated).ConfigureAwait(false);
        var name = ShellValue.FromText(commandName);
        if (scope.Context.LoopDepth == 0)
        {
            await ReportExpansionDiagnosticAsync(name,
                ShellValue.FromText("only meaningful in a `for', `while', or `until' loop")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);
        }
        var first = words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--"u8) ? 2 : 1;
        var count = 1L;
        if (first < words.Count)
        {
            if (!TryParseControlNumber(words[first], out count))
            {
                var status = (_state.LastExitCode ?? 0) | 128;
                await ReportExpansionDiagnosticAsync([name], [words[first], ShellValue.FromText(": numeric argument required")])
                    .ConfigureAwait(false);
                // Use the reached binding and the simple command's admitted status storage.
                scope.Variables.SetPipelineStatuses([status], scope.Context, _cancellationToken);
                return EvaluationResult.InvalidLoopCount(status);
            }
            if (words.Count - first > 1)
                return await DiscardForExtraControlArgumentsAsync(name).ConfigureAwait(false);
            if (count <= 0)
            {
                // Bash reports the first operand, including a leading option terminator.
                await ReportExpansionDiagnosticAsync([name], [words[1], ShellValue.FromText(": loop count out of range")])
                    .ConfigureAwait(false);
                // Both builtins abandon every active loop after a nonpositive count.
                return await FinalizeResultAsync(EvaluationResult.Break(scope.Context.LoopDepth, 1), negated).ConfigureAwait(false);
            }
        }
        var level = (int)Math.Min(count, scope.Context.LoopDepth);
        return await FinalizeResultAsync(builtin == BuiltinKind.Break
            ? EvaluationResult.Break(level, 0) : EvaluationResult.Continue(level, 0), negated).ConfigureAwait(false);
    }

    /// <summary>Validates exit arguments before unwinding through the existing EXIT/descriptor path.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinExitAsync(
        IReadOnlyList<ShellValue> words, ScopedEvaluation scope, CommandCompletion completion)
    {
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
            return await WriteControlHelpAsync(words[0], BuiltinKind.Exit, "exit", false).ConfigureAwait(false);
        var exitCode = (words.Count == 1 ? _state.Traps?.ExitTrapStatus : null) ?? _state.LastExitCode ?? 0;
        var first = words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--"u8) ? 2 : 1;
        if (first < words.Count)
        {
            if (!TryParseControlNumber(words[first], out var status))
            {
                await ReportExpansionDiagnosticAsync([ShellValue.FromText("exit")],
                    [words[first], ShellValue.FromText(": numeric argument required")])
                    .ConfigureAwait(false);
                return await CompleteExitAsync(2).ConfigureAwait(false);
            }
            if (words.Count - first > 1)
                return await DiscardForExtraControlArgumentsAsync(ShellValue.FromText("exit")).ConfigureAwait(false);
            exitCode = (int)(status & 255);
        }
        return await CompleteExitAsync(exitCode).ConfigureAwait(false);

        async Task<EvaluationResult> CompleteExitAsync(int status)
        {
            var result = EvaluationResult.Exit(status);
            // Explicit exit, including an invalid numeric operand, runs EXIT before active
            // bindings, loop targets and redirections unwind. Arity failure unwinds first.
            return completion != CommandCompletion.ExitEvaluation
                ? await RunExitTrapAsync(scope, result).ConfigureAwait(false) : result;
        }
    }

    private async Task<EvaluationResult> DiscardForExtraControlArgumentsAsync(ShellValue command)
    {
        await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("too many arguments")).ConfigureAwait(false);
        return EvaluationResult.DiscardCommandUnit();
    }

    private static bool TryParseControlNumber(ShellValue operand, out long status) =>
        Integers.TryParseBuiltinNumber(operand.Bytes.Span, out status);
}

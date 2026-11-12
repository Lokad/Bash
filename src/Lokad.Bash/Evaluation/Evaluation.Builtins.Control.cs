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
            if (!TryParseControlNumber(operand, out var status))
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

    /// <summary>Validates a reached loop-control builtin and preserves its distinct failure flows.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinLoopControlAsync(
        BuiltinKind builtin, IReadOnlyList<ShellValue> words, ScopedEvaluation scope, bool negated)
    {
        var name = ShellValue.FromText(builtin == BuiltinKind.Break ? "break" : "continue");
        if (words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--help"u8))
            return await WriteControlHelpAsync(builtin, negated).ConfigureAwait(false);
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
                await ReportExpansionDiagnosticAsync(name,
                    WordExpansion.AppendAssignmentValue(words[first], ShellValue.FromText(": numeric argument required"), scope))
                    .ConfigureAwait(false);
                return EvaluationResult.Exit(128);
            }
            if (words.Count - first > 1)
            {
                await ReportExpansionDiagnosticAsync(name, ShellValue.FromText("too many arguments")).ConfigureAwait(false);
                return EvaluationResult.Exit(1);
            }
            if (count <= 0)
            {
                await ReportExpansionDiagnosticAsync(name,
                    WordExpansion.AppendAssignmentValue(words[first], ShellValue.FromText(": loop count out of range"), scope))
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
            return await WriteControlHelpAsync(BuiltinKind.Exit, false).ConfigureAwait(false);
        var exitCode = _state.Traps?.ExitTrapStatus ?? _state.LastExitCode ?? 0;
        var first = words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--"u8) ? 2 : 1;
        if (first < words.Count)
        {
            if (!TryParseControlNumber(words[first], out var status))
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText("exit"),
                    WordExpansion.AppendAssignmentValue(words[first], ShellValue.FromText(": numeric argument required"), scope))
                    .ConfigureAwait(false);
                return await CompleteExitAsync(2).ConfigureAwait(false);
            }
            if (words.Count - first > 1)
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText("exit"), ShellValue.FromText("too many arguments"))
                    .ConfigureAwait(false);
                return EvaluationResult.Exit(1);
            }
            exitCode = (int)(status & 255);
        }
        return await CompleteExitAsync(exitCode).ConfigureAwait(false);

        async Task<EvaluationResult> CompleteExitAsync(int status)
        {
            var result = EvaluationResult.Exit(status);
            // An explicit exit, including an invalid numeric operand, runs EXIT while
            // function locals are still visible. An arity failure unwinds those locals first.
            return scope.Context.FunctionContext != null && completion != CommandCompletion.ExitEvaluation
                ? await RunExitTrapAsync(scope, result).ConfigureAwait(false) : result;
        }
    }

    private static bool TryParseControlNumber(ShellValue operand, out long status) =>
        Integers.TryParseNumber(operand.Bytes.Span.TrimStart(" \t\r\n\v\f"u8).TrimEnd(" \t"u8), out status);

    private async Task<EvaluationResult> WriteControlHelpAsync(BuiltinKind builtin, bool negated)
    {
        var name = builtin switch
        {
            BuiltinKind.Break => "break",
            BuiltinKind.Continue => "continue",
            BuiltinKind.Exit => "exit",
            _ => throw new ArgumentOutOfRangeException(nameof(builtin))
        };
        var documentation = BuiltinDocumentation.For(builtin);
        await ExecuteWriteAsync(GetStdOutDescriptor(),
            Utf8Text.Encode(name + ": " + documentation.Synopsis(name) + "\n" + documentation.Description + "\n"))
            .ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
    }
}

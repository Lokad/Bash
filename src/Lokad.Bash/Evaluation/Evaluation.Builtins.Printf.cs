using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Formatting;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Formats printf output, applying count writes before selecting the final -v destination.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinPrintfAsync(
        IReadOnlyList<ShellValue> operands,
        Variables targetVars,
        Variables.CommandBindings? bindings,
        EvaluationContext context,
        bool negated)
    {
        static string Usage() => "printf: usage: " + BuiltinDocumentation.For(BuiltinKind.Printf).Synopsis("printf") + "\n";

        VariableReference? outputReference = null;
        var operandIndex = 0;
        while (operandIndex < operands.Count)
        {
            var optionBytes = operands[operandIndex].Bytes;
            if (optionBytes.Length < 2 || optionBytes.Span[0] != (byte)'-')
                break;

            if (optionBytes.Span.SequenceEqual("--"u8))
            {
                operandIndex++;
                break;
            }

            if (optionBytes.Span.SequenceEqual("--help"u8))
            {
                var documentation = BuiltinDocumentation.For(BuiltinKind.Printf);
                var helpWrite = await ExecuteWriteAsync(GetStdOutDescriptor(),
                        Utf8Text.Encode("printf: " + documentation.Synopsis("printf") + "\n    " + documentation.Description + "\n"))
                    .ConfigureAwait(false);
                if (helpWrite.IsError)
                    await ReportBuiltinWriteFailureAsync(ShellValue.FromText("printf"), helpWrite).ConfigureAwait(false);
                var helpExitCode = helpWrite.IsError ? 1 : 0;
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(helpExitCode), negated);
            }

            if (optionBytes.Span[1] != (byte)'v')
                return await FailAsync(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                    .Append("printf: -"u8).Append(optionBytes.Slice(1, 1))
                    .Append(Utf8Text.Encode(": invalid option\n" + Usage())).ToMemory()), 2).ConfigureAwait(false);

            ShellValue outputVariable;
            if (optionBytes.Length > 2)
                outputVariable = ShellValue.FromBytes(optionBytes[2..]);
            else
            {
                operandIndex++;
                if (operandIndex >= operands.Count)
                    return await FailAsync(ShellValue.FromText(Usage()), 2).ConfigureAwait(false);
                outputVariable = operands[operandIndex];
            }

            if (!VariableReference.TryParse(ShellSource.FromBytes(outputVariable.Bytes), Memory, out var reference)
                || reference.Kind != VariableReferenceKind.Name && reference.Subscript.IsEmpty)
                return await FailAsync(ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                    .Append("printf: "u8).Append(ShellArgumentEscaping.QuoteDiagnosticOperand(outputVariable).Bytes)
                    .Append(": not a valid identifier\n"u8).ToMemory()), 2).ConfigureAwait(false);

            outputReference = reference;
            operandIndex++;
        }

        if (operandIndex >= operands.Count)
            return await FailAsync(ShellValue.FromText(Usage()), 2).ConfigureAwait(false);

        var format = operands[operandIndex++];
        var arguments = new List<ShellValue>(operands.Count - operandIndex);
        while (operandIndex < operands.Count)
            arguments.Add(operands[operandIndex++]);

        var scope = new ScopedEvaluation(this, targetVars, context) { SelectorBindings = bindings };
        using var diagnostics = new BuiltinDiagnosticScope(this);
        var outputMemory = Memory.Budget.CreateScope();
        IMemoryReservation? truncatedMemory = null;
        try
        {
            var result = await PrintfFormatting.EvaluateAsync(format, arguments, Options.Timestamp,
                outputMemory, AssignCountAsync, ReportDiagnosticAsync).ConfigureAwait(false);
            var exitCode = result.ExitCode;

            if (outputReference is { } destination && !HasPendingExpansionError && diagnostics.Result.CanAcceptMore)
            {
                var value = ShellValue.FromBytes(result.Output).BeforeFirstNul();
                Memory.Charge(global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(value));
                if (value.Bytes.Length < result.Output.Length)
                {
                    // Admit the compact copy before releasing the discarded formatted buffer.
                    value = ShellValue.FromBytes(ScopedMemory.Copy(value.Bytes, Memory.Budget, out truncatedMemory));
                    outputMemory.Dispose();
                }
                var assignment = await AssignBuiltinAsync(destination, value,
                    scope, bindings, BuiltinOutputVisibility.Caller, ShellValue.FromText("printf")).ConfigureAwait(false);
                if (assignment == BuiltinAssignmentResult.Failed)
                    exitCode = 1;
            }

            if (outputReference == null && diagnostics.Result.CanAcceptMore && !result.Output.IsEmpty)
            {
                var write = await ExecuteWriteAsync(GetStdOutDescriptor(), result.Output).ConfigureAwait(false);
                if (write.IsError)
                {
                    await ReportBuiltinWriteFailureAsync(ShellValue.FromText("printf"), write).ConfigureAwait(false);
                    exitCode = 1;
                }
            }
            if (diagnostics.Result.IsError)
                exitCode = 1;

            return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated);
        }
        finally
        {
            // Accepted -v output is covered by the variable owner before releasing formatting scratch.
            outputMemory.Dispose();
            truncatedMemory?.Dispose();
        }

        async ValueTask<bool> AssignCountAsync(PrintfVariableAssignment assignment)
        {
            // A rejected binding is recoverable for %n; an expansion failure stops later conversions.
            await AssignBuiltinAsync(VariableReference.ForName(assignment.Name), ShellValue.FromText(Integers.FormatNumber(assignment.Count)),
                scope, bindings, BuiltinOutputVisibility.CommandAndCaller, ShellValue.FromText("printf")).ConfigureAwait(false);
            return !HasPendingExpansionError && diagnostics.Result.CanAcceptMore;
        }

        async ValueTask<bool> ReportDiagnosticAsync(ShellValue diagnostic)
        {
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("printf"), diagnostic).ConfigureAwait(false);
            return diagnostics.Result.CanAcceptMore;
        }

        async Task<EvaluationResult> FailAsync(ShellValue message, int failureExitCode)
        {
            var appendExitCode = (await ExecuteWriteAsync(GetStdErrDescriptor(), message.Bytes)
                .ConfigureAwait(false)).IsError ? 1 : 0;
            return await FinalizeResultAsync(
                    EvaluationResult.FromExitCode(appendExitCode == 0 ? failureExitCode : appendExitCode),
                    negated)
                .ConfigureAwait(false);
        }

    }
}

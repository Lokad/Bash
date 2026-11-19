using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Files;
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
        static string Usage() => "usage: " + BuiltinDocumentation.For(BuiltinKind.Printf).Synopsis("printf");

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
                return await FinishInvalidInvocationAsync(WriteDiagnosticUnlessStderrClosedAsync([ShellValue.FromText("printf")],
                    [ShellValue.FromText("-"), ShellValue.FromBytes(optionBytes.Slice(1, 1)),
                        ShellValue.FromText(": invalid option\nprintf: " + Usage())], scope: null)).ConfigureAwait(false);

            ShellValue outputVariable;
            if (optionBytes.Length > 2)
                outputVariable = ShellValue.FromBytes(optionBytes[2..]);
            else
            {
                operandIndex++;
                if (operandIndex >= operands.Count)
                    return await ReportUsageAsync().ConfigureAwait(false);
                outputVariable = operands[operandIndex];
            }

            if (!VariableReference.TryParse(ShellSource.FromBytes(outputVariable.Bytes), Memory, out var reference)
                || reference.Kind != VariableReferenceKind.Name && reference.Subscript.IsEmpty)
                return await FinishInvalidInvocationAsync(
                    WriteInvalidIdentifierDiagnosticAsync(ShellValue.FromText("printf"), outputVariable,
                        scope: null, ClosedStderrHandling.Discard)).ConfigureAwait(false);

            outputReference = reference;
            operandIndex++;
        }

        if (operandIndex >= operands.Count)
            return await ReportUsageAsync().ConfigureAwait(false);

        var format = operands[operandIndex++];
        var scope = new ScopedEvaluation(this, targetVars, context) { SelectorBindings = bindings };
        using var diagnostics = new BuiltinDiagnosticScope(this);
        var outputMemory = Memory.Budget.CreateScope();
        IMemoryReservation? truncatedMemory = null;
        try
        {
            var result = await PrintfFormatting.EvaluateAsync(format, operands, operandIndex, Options.Timestamp,
                outputReference.HasValue, outputMemory, AssignCountAsync,
                GetStdErrDescriptor().IsClosed ? null : ReportDiagnosticAsync, _cancellationToken).ConfigureAwait(false);
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
                    scope, bindings, BuiltinAssignmentKind.Value, ShellValue.FromText("printf"), expansionWriteTarget: null).ConfigureAwait(false);
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
                scope, bindings, BuiltinAssignmentKind.PrintfCount, ShellValue.FromText("printf"), expansionWriteTarget: null).ConfigureAwait(false);
            return !HasPendingExpansionError && diagnostics.Result.CanAcceptMore;
        }

        async ValueTask<bool> ReportDiagnosticAsync(PrintfFormatting.Diagnostic diagnostic)
        {
            await ReportExpansionDiagnosticAsync([ShellValue.FromText("printf")],
                [ShellValue.FromText(diagnostic.Prefix), diagnostic.Operand, ShellValue.FromText(diagnostic.Suffix)]).ConfigureAwait(false);
            return diagnostics.Result.CanAcceptMore;
        }

        Task<EvaluationResult> ReportUsageAsync() => FinishInvalidInvocationAsync(
            WriteDiagnosticUnlessStderrClosedAsync([ShellValue.FromText("printf")], [ShellValue.FromText(Usage())], scope: null));

        async Task<EvaluationResult> FinishInvalidInvocationAsync(Task<DescriptorWriteResult> diagnostic)
        {
            var result = await diagnostic.ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(result.IsError ? 1 : 2), negated).ConfigureAwait(false);
        }

    }
}

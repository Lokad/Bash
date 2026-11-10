using System;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Captures a nested evaluation through an ordinary streaming pipe.</summary>
internal sealed class CommandSubstitutionEvaluation(CommandSubstitutionSyntax script)
{
    public async Task<CommandSubstitutionResult> EvaluateAsync(ScopedEvaluation scope)
    {
        var outer = scope.Effects;
        ParsedScript parsed;
        try
        {
            parsed = script is CommandSubstitutionSyntax.Parsed retained
                ? retained.Script : Parser.Parse(script.Source.Bytes);
        }
        catch (SyntaxException exception)
        {
            await outer.ReportSyntaxErrorAsync(exception.Error).ConfigureAwait(false);
            return new CommandSubstitutionResult(ShellValue.Empty, 2);
        }

        var creation = await outer.ExecuteCreatePipeAsync().ConfigureAwait(false);
        if (creation.Descriptors is not { } pipe)
        {
            await outer.ReportOperationErrorAsync(creation.Error.GetValueOrDefault()).ConfigureAwait(false);
            return new CommandSubstitutionResult(ShellValue.Empty, 1);
        }
        outer.TrackOwnedDescriptor(pipe.Read);
        outer.TrackOwnedDescriptor(pipe.Write);
        var nested = Evaluation.CreateCommandSubstitution(scope, parsed, pipe.Write);
        using var outputMemory = outer.ExpansionBudget.MemoryBudget.CreateScope();
        CapturedEvaluationResult result;
        try
        {
            // Hosting drives the nested evaluator and drains its output concurrently. Await both
            // through cancellation so unfinished cleanup transfers only after they have settled.
            var capture = RunCapturedEvaluation.AsTask(outer.PublishOperation,
                new PipelineEvaluationStage(nested, [pipe.Write]), pipe.Read, ByteMemoryBuilder.MaxMemoryBuilderBytes - 1,
                outputMemory);
            // The stage owns the writer once published; setup refusal leaves it with the caller.
            outer.MarkDescriptorCleanupCompleted(pipe.Write);
            result = await capture.ConfigureAwait(false);
        }
        finally
        {
            nested.TransferDescriptorCleanupTo(outer);
        }

        switch (result.Output)
        {
            case BoundedReadResult.Complete complete:
                outer.ExpansionBudget.Memory.Charge(Variables.EstimateValueBytes(ShellValue.FromBytes(complete.Content)));
                var error = await outer.CloseDescriptorsAsync([pipe.Read], operationFailed: result.ExitCode != 0).ConfigureAwait(false);
                if (error is { } failure)
                {
                    await outer.ReportOperationErrorAsync(failure).ConfigureAwait(false);
                    return new CommandSubstitutionResult(ShellValue.FromBytes(complete.Content), 1);
                }
                return new CommandSubstitutionResult(ShellValue.FromBytes(complete.Content), result.ExitCode);
            case BoundedReadResult.TooLarge:
                throw new ExpansionBudget.LimitExceededException("shell: command substitution exceeds the CLR array length limit\n");
            case BoundedReadResult.Failed failed:
                failed.Failure.ThrowIfError();
                break;
        }
        throw new InvalidOperationException("Unexpected command substitution capture outcome.");
    }
}

/// <summary>Captured bytes and the nested shell's status, independent of the caller's command result.</summary>
internal readonly record struct CommandSubstitutionResult(ShellValue Output, int ExitCode);

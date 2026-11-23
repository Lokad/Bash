using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Evaluates command substitution syntax using descriptor input or nested capture.</summary>
internal sealed class CommandSubstitutionEvaluation(CommandSubstitutionSyntax script)
{
    public async Task<CommandSubstitutionResult> EvaluateAsync(ScopedEvaluation scope)
    {
        var outer = scope.Effects;
        SourceMemory? parsedMemory = null;
        try
        {
            ParsedScript parsed;
            try
            {
                if (script is CommandSubstitutionSyntax.Parsed retained)
                    parsed = retained.Script;
                else if (script is CommandSubstitutionSyntax.AdjacentSubshell adjacent)
                    parsed = new ParsedScript([new ScriptCommand(adjacent.Source) { SourceReader = ScriptCommand.Reader.AdjacentCapture }]);
                else if (script is CommandSubstitutionSyntax.Legacy legacy)
                {
                    var temporary = legacy.Origin == LegacySubstitutionOrigin.Temporary;
                    var owner = temporary ? (parsedMemory = CreateParseOwner())
                        : scope.Context.SourceMemory ?? outer.GetPreparedSubstitutionMemory();
                    ParsedScript? prepared;
                    ParserFailure? failure;
                    if (temporary)
                        Parser.TryParseScript(legacy.Source.Bytes, new ShellScanContext { SyntaxMemory = owner.SyntaxMemory },
                            out prepared, out failure);
                    else
                        owner.TryPrepareLegacy(legacy, outer.CancellationToken, out prepared, out failure);

                    if (prepared != null)
                    {
                        parsed = prepared;
                        scope = scope with { Context = scope.Context.WithSourceMemory(owner) };
                    }
                    else
                    {
                        parsedMemory?.Release();
                        parsedMemory = null;
                        if (failure is { Kind: ParserFailureKind.Limit })
                        {
                            await outer.ReportSyntaxErrorAsync(failure.Error).ConfigureAwait(false);
                            return new CommandSubstitutionResult(ShellValue.Empty, 2, IgnoredNuls: false);
                        }

                        // A body with a grammar error can still have earlier complete units to execute.
                        parsed = new ParsedScript([new ScriptCommand(legacy.Source) { SourceReader = ScriptCommand.Reader.LegacyCapture }]);
                    }
                }
                else
                {
                    parsedMemory = CreateParseOwner();
                    parsed = Parser.Parse(script.Source.Bytes, new ShellScanContext { SyntaxMemory = parsedMemory.SyntaxMemory });
                    scope = scope with { Context = scope.Context.WithSourceMemory(parsedMemory) };
                }
            }
            catch (SyntaxException exception)
            {
                await outer.ReportSyntaxErrorAsync(exception.Error).ConfigureAwait(false);
                return new CommandSubstitutionResult(ShellValue.Empty, 2, IgnoredNuls: false);
            }

            if (parsed.Commands is [var command])
            {
                // Bash recognizes the one-redirection form even with a leading negation.
                while (command is NegatedCommand negated)
                    command = negated.Inner;

                if (command is SimpleCommand
                    {
                        Assignments.Count: 0,
                        Words.Count: 0,
                        Redirections: [{ Kind: RedirectionKind.Input, FileDescriptor: null or 0, VariableName.Length: 0 }]
                    } input)
                    return await outer.ReadInputSubstitutionAsync(input.Redirections, scope).ConfigureAwait(false);
            }

            outer.PrepareOwnedDescriptors(2);
            var creation = await outer.ExecuteCreatePipeAsync().ConfigureAwait(false);
            if (creation.Descriptors is not { } pipe)
            {
                await outer.ReportOperationErrorAsync(creation.Error.GetValueOrDefault()).ConfigureAwait(false);
                return new CommandSubstitutionResult(ShellValue.Empty, 1, IgnoredNuls: false);
            }
            outer.TrackOwnedDescriptor(pipe.Read);
            outer.TrackOwnedDescriptor(pipe.Write);
            outer.CancellationToken.ThrowIfCancellationRequested();
            using var outputMemory = outer.Memory.Budget.CreateScope();
            var nested = Evaluation.CreateCommandSubstitution(scope, parsed, pipe.Write);
            CapturedEvaluationResult result;
            Exception? captureFailure = null;
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
            catch (Exception error)
            {
                captureFailure = error;
                throw;
            }
            finally
            {
                var refusal = await nested.TransferDescriptorCleanupToAsync(outer).ConfigureAwait(false);
                nested.ReleaseFunctions();
                if (captureFailure == null && refusal != null)
                    ExceptionDispatchInfo.Capture(refusal).Throw();
            }

            switch (result.Output)
            {
                case BoundedReadResult.Complete complete:
                    var output = CommandSubstitutionResult.FromCapture(complete.Content, result.ExitCode, outer.Memory);
                    var error = await outer.CloseDescriptorsAsync([pipe.Read], operationFailed: result.ExitCode != 0).ConfigureAwait(false);
                    if (error is { } failure)
                    {
                        await outer.ReportOperationErrorAsync(failure).ConfigureAwait(false);
                        return output with { ExitCode = 1 };
                    }
                    return output;
                case BoundedReadResult.TooLarge:
                    throw new ByteMemoryBuilder.LimitExceededException();
                case BoundedReadResult.Failed failed:
                    failed.Failure.ThrowIfError();
                    break;
            }
            throw new InvalidOperationException("Unexpected command substitution capture outcome.");
        }
        finally { parsedMemory?.Release(); }

        SourceMemory CreateParseOwner()
        {
            var parent = scope.Context.SourceMemory;
            return new SourceMemory((parent?.Budget ?? outer.Memory.Budget).CreateScope(), parent);
        }
    }
}

/// <summary>Normalized capture bytes, the nested shell's status and whether to warn about NULs.</summary>
internal readonly record struct CommandSubstitutionResult(ShellValue Output, int ExitCode, bool IgnoredNuls)
{
    /// <summary>Normalizes while the capture owner is live and admits the result to the caller.</summary>
    internal static CommandSubstitutionResult FromCapture(ReadOnlyMemory<byte> capture, int exitCode, EvaluationMemory memory)
    {
        var bytes = capture.Span;
        var firstNul = bytes.IndexOf((byte)0);
        // LFs separated from the end only by NULs are trailing after filtering.
        var length = bytes.LastIndexOfAnyExcept((byte)0, (byte)'\n') + 1;
        if (length == 0)
            return new(ShellValue.Empty, exitCode, firstNul >= 0);

        var trimmed = capture[..length];
        if (firstNul >= 0 && firstNul < length)
        {
            var tail = trimmed[(firstNul + 1)..];
            var outputLength = length - 1 - tail.Span.Count((byte)0);
            memory.Charge(Variables.EstimateValueBytes(outputLength));
            using var filtered = ByteMemoryBuilder.CreateUnaccounted(outputLength);
            filtered.Append(trimmed[..firstNul]);
            ShellValue.FromBytes(tail).AppendWithoutNuls(filtered);
            return new(ShellValue.FromBytes(filtered.ToMemory()), exitCode, IgnoredNuls: true);
        }

        memory.Charge(Variables.EstimateValueBytes(length));
        var value = ShellValue.FromBytes(trimmed);
        if (!value.TryGetCompactArrayCapacity(out _))
            value = ShellValue.FromBytes(trimmed.ToArray());
        return new(value, exitCode, firstNul >= 0);
    }
}

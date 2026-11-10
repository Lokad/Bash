using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    internal long RetainedTrapMemoryBytes => _state.Traps?.EstimatedMemoryBytes ?? 0;

    private async Task<EvaluationResult> EvaluateBuiltinTrapAsync(IReadOnlyList<ShellValue> words, ScopedEvaluation scope)
    {
        var index = 1;
        var list = false;
        while (index < words.Count)
        {
            var option = words[index].Bytes.Span;
            if (option.SequenceEqual("--"u8))
            {
                index++;
                break;
            }
            if (option.SequenceEqual("-p"u8))
            {
                list = true;
                index++;
                continue;
            }
            if (option.Length < 2 || option[0] != (byte)'-')
                break;

            return await UsageAsync(option.SequenceEqual("--help"u8)).ConfigureAwait(false);
        }

        var traps = _state.GetOrCreateTraps();
        var status = 0;
        if (list || index == words.Count)
        {
            if (index == words.Count)
            {
                foreach (var condition in ShellTraps.Conditions)
                    if (!await PrintAsync(condition).ConfigureAwait(false))
                        return EvaluationResult.FromExitCode(1);
            }
            else
            {
                for (; index < words.Count; index++)
                {
                    if (!ShellTraps.TryParse(words[index], out var condition))
                    {
                        await InvalidConditionAsync(words[index]).ConfigureAwait(false);
                        status = 1;
                    }
                    else if (!await PrintAsync(condition).ConfigureAwait(false))
                        return EvaluationResult.FromExitCode(1);
                }
            }
            return EvaluationResult.FromExitCode(status);
        }

        var action = words[index++];
        var numericReset = !action.Bytes.IsEmpty && action.Bytes.Span.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0;
        var reset = numericReset || action.Bytes.Span.SequenceEqual("-"u8);
        if (index == words.Count)
        {
            if (!ShellTraps.TryParse(action, out _))
                return await UsageAsync(standardOutput: false).ConfigureAwait(false);
            reset = true;
            index--;
        }
        else if (numericReset)
            index--;

        traps.DiscardInheritedListing();
        for (; index < words.Count; index++)
        {
            if (!ShellTraps.TryParse(words[index], out var condition))
            {
                await InvalidConditionAsync(words[index]).ConfigureAwait(false);
                status = 1;
            }
            else if (reset)
                traps.Reset(condition);
            else
                traps.Set(condition, action, ExpansionBudget.Memory);
        }
        return EvaluationResult.FromExitCode(status);

        async Task<EvaluationResult> UsageAsync(bool standardOutput)
        {
            var documentation = BuiltinDocumentation.For(BuiltinKind.Trap);
            var output = Utf8Text.Encode("trap: " + documentation.Synopsis("trap") + "\n    " + documentation.Description + "\n");
            var written = await ExecuteWriteAsync(standardOutput ? GetStdOutDescriptor() : GetStdErrDescriptor(), output).ConfigureAwait(false);
            return EvaluationResult.FromExitCode(written.IsError ? 1 : 2);
        }

        async Task InvalidConditionAsync(ShellValue operand) =>
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("trap"),
                WordExpansion.AppendAssignmentValue(operand, ShellValue.FromText(": invalid signal specification"), scope)).ConfigureAwait(false);

        async Task<bool> PrintAsync(TrapCondition condition)
        {
            if (!traps.ListedHandlers.TryGetValue(condition, out var handler))
                return true;
            var name = ShellTraps.GetName(condition);
            var length = 8L + handler.Bytes.Length + 3L * handler.Bytes.Span.Count((byte)'\'') + 2 + 1 + name.Length + 1;
            ExpansionBudget.Words.Charge(0, length);
            if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ExpansionBudget.LimitExceededException(ExpansionBudget.Words.LimitMessage);
            using var output = new BoundedByteMemoryBuilder(0, (int)length, ExpansionBudget.MemoryBudget);
            if (!output.TryAppend("trap -- "u8)
                || !ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, handler.Bytes.Span)
                || !output.TryAppend((byte)' ')
                || !output.TryAppend(Utf8Text.Encode(name).Span)
                || !output.TryAppend((byte)'\n'))
                throw new InvalidOperationException("Trap listing exceeded its reserved size.");
            return !(await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false)).IsError;
        }
    }

    /// <summary>Runs EXIT once, before its shell loses bindings or descriptors. Other registered signals have no host delivery.</summary>
    private async ValueTask<EvaluationResult> RunExitTrapAsync(ScopedEvaluation scope, EvaluationResult result)
    {
        if (_state.Traps is not { } traps || _state.ExitedThroughExec || !traps.TryBeginExit(result.ExitCode, out var action))
            return result;

        var failure = _state.FatalExpansion;
        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        _state.LastExitCode = result.ExitCode;
        // Function exits run before their bindings and redirections unwind; shell completion runs afterward.
        try
        {
            if (!Parser.TryParse(action.Bytes, out var script, out var error))
                await ReportSyntaxErrorAsync(error).ConfigureAwait(false);
            else
            {
                var handler = await EvaluateParsedScriptAsync(script, scope, RootFlowHandling.Preserve).ConfigureAwait(false);
                if ((handler.Flow is FlowControlKind.Exit or FlowControlKind.Return) && _state.FatalExpansion == null)
                    result = result.WithExitCode(handler.ExitCode);
            }
            return result;
        }
        finally
        {
            traps.EndExit();
            _state.FatalExpansion = failure;
            _state.LastExitCode = result.ExitCode;
        }
    }
}

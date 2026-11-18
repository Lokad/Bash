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
    /// Selects the script operands of the resolved builtin before parsing or opening any source.
    /// </summary>
    /// <returns>The finalized evaluation result.</returns>
    private async Task<EvaluationResult> EvaluateBuiltinScriptingAsync(
        BuiltinKind kind,
        string commandName,
        IReadOnlyList<ShellValue> words,
        ScopedEvaluation scope,
        bool negated)
    {
        var first = 1;
        var command = words[0];
        if (first < words.Count)
        {
            var option = words[first].Bytes;
            if (option.Span.SequenceEqual("--"u8))
                first++;
            else if (option.Span.SequenceEqual("--help"u8))
            {
                var documentation = BuiltinDocumentation.For(kind);
                var output = Utf8Text.Encode(commandName + ": " + documentation.Synopsis(commandName)
                    + "\n    " + documentation.Description + "\n");
                var write = await ExecuteWriteAsync(GetStdOutDescriptor(), output).ConfigureAwait(false);
                await ReportBuiltinWriteFailureAsync(command, write).ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(write.IsError ? 1 : 2), negated)
                    .ConfigureAwait(false);
            }
            else if (option.Span is [(byte)'-', _, ..])
            {
                using var diagnostic = new ByteMemoryBuilder(0, Memory.Budget);
                diagnostic.Append(option[..2]).Append(": invalid option"u8);
                return await UsageErrorAsync(ShellValue.FromBytes(diagnostic.ToMemory())).ConfigureAwait(false);
            }
        }

        if (kind == BuiltinKind.Eval)
        {
            if (first == words.Count)
                return ApplyNegation(EvaluationResult.FromExitCode(0), negated);

            var result = await EvaluateCopiedCommandUnitsAsync(words, first, scope, ScriptExecutionKind.Eval)
                .ConfigureAwait(false);
            return ApplyNegation(result, negated);
        }

        if (first == words.Count)
            return await UsageErrorAsync(ShellValue.FromText("filename argument required")).ConfigureAwait(false);
        if (words[first].IsEmpty)
        {
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("empty filename")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }

        var path = await DecodeExpandedValueAsync(words[first]).ConfigureAwait(false);
        if (HasPendingExpansionError)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

        var argumentStart = first + 1;
        var argumentCount = words.Count - argumentStart;
        if (argumentCount > 0)
            Memory.Charge(checked(scope.Variables.PositionalMemoryBytes + 32L * argumentCount));
        using var positionalScope = argumentCount == 0 ? null : scope.Variables.EnterPositionalParametersScope(
            words, argumentStart, PositionalParametersRestorePolicy.UnlessReplaced);
        return ApplyNegation(await ExecuteSourceAsync(path, scope).ConfigureAwait(false), negated);

        async Task<EvaluationResult> UsageErrorAsync(ShellValue message)
        {
            await ReportExpansionDiagnosticAsync(command, message).ConfigureAwait(false);
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("usage: "
                + BuiltinDocumentation.For(kind).Synopsis(commandName))).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }
    }

    private enum ScriptExecutionKind { Root, Source, Eval, Trap }

    /// <summary>Copies eval/trap input into an owner retained by any syntax that survives execution.</summary>
    private async Task<EvaluationResult> EvaluateCopiedCommandUnitsAsync(
        IReadOnlyList<ShellValue> words, int start, ScopedEvaluation scope, ScriptExecutionKind kind)
    {
        var sourceMemory = new SourceMemory(Memory.Budget.CreateScope());
        try
        {
            using var builder = new ByteMemoryBuilder(0, sourceMemory.Budget);
            for (var index = start; index < words.Count; index++)
            {
                CheckCancellation();
                if (index > start) builder.Append((byte)' ');
                builder.Append(words[index].Bytes);
            }
            var source = ShellSource.FromBytes(builder.DetachSnapshot());
            return await EvaluateCommandUnitsAsync(source,
                scope with { Context = scope.Context.WithSourceMemory(sourceMemory) }, kind).ConfigureAwait(false);
        }
        finally { sourceMemory.Release(); }
    }

    /// <summary>Shares complete-command parsing and execution across the public entry point, source, eval and EXIT actions.</summary>
    private async Task<EvaluationResult> EvaluateCommandUnitsAsync(
        ShellSource source, ScopedEvaluation scope, ScriptExecutionKind kind)
    {
        // A reached nested parser needs its own stack headroom for syntax guards,
        // independently of the caller's function/eval execution depth.
        if (kind != ScriptExecutionKind.Root)
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        var result = EvaluationResult.FromExitCode(0);
        var scanContext = new ShellScanContext();
        using var commands = Parser.TryParseCommandUnits(source.Bytes, scanContext).GetEnumerator();
        while (true)
        {
            CheckCancellation();
            var parent = scope.Context.SourceMemory;
            var unitMemory = new SourceMemory((parent?.Budget ?? Memory.Budget).CreateScope(), parent);
            scanContext.SyntaxMemory = unitMemory.SyntaxMemory;
            try
            {
                if (!commands.MoveNext())
                    break;
                if (commands.Current is CommandUnitParseResult.Failed failedUnit)
                {
                    await ReportSyntaxErrorAsync(failedUnit.Error).ConfigureAwait(false);
                    if (kind == ScriptExecutionKind.Trap && _state.ErrexitEnabled && !scope.Context.ErrexitIgnored)
                        return EvaluationResult.Exit(2);
                    return result.WithExitCode(2);
                }

                // Loop flow skips execution but still checks the remaining source/eval syntax.
                if (result.Flow is FlowControlKind.Break or FlowControlKind.Continue)
                    continue;

                result = await EvaluateCommandAsync(((CommandUnitParseResult.Parsed)commands.Current).Command,
                    scope with { Context = scope.Context.WithSourceMemory(unitMemory) }, negated: false)
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
            finally
            {
                scanContext.SyntaxMemory = null;
                unitMemory.Release();
            }
        }
        return RecoverScriptExpansionFailure(result, kind);
    }

    /// <summary>Recovers unit-scoped expansion failures while preserving root versus nested status rules.</summary>
    private EvaluationResult RecoverScriptExpansionFailure(EvaluationResult result, ScriptExecutionKind kind)
    {
        if (_state.FatalExpansion is not
            { Kind: ExpansionFailureKind.Arithmetic or ExpansionFailureKind.WordSyntax
                or ExpansionFailureKind.AssignmentTarget or ExpansionFailureKind.NamerefDefault } failure)
            return result;

        // Recoverable expansion failures end their current unit even under errexit.
        // A reached assignment-target failure terminates an errexit-enabled EXIT action.
        if (kind == ScriptExecutionKind.Trap && _state.ErrexitEnabled && failure.Kind == ExpansionFailureKind.AssignmentTarget)
            return result;

        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        // Nameref defaults fail with 1 at script boundaries, but with 2 at the root.
        return EvaluationResult.FromExitCode(failure.Kind == ExpansionFailureKind.NamerefDefault && kind != ScriptExecutionKind.Root ? 1 : failure.ExitCode);
    }
}

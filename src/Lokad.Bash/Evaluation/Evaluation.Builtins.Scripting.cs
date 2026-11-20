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
        IReadOnlyList<ShellValue> words,
        EvaluationContext context,
        bool negated)
    {
        var frame = SkipScriptPrefixes(context.FunctionContext);
        var first = words.Count > 1 && words[1].Bytes.Span.SequenceEqual("--"u8) ? 2 : 1;
        if (first < words.Count && TryParseControlNumber(words[first], out var index))
        {
            if (index < 0)
                frame = null;
            while (frame != null && index-- >= 0)
            {
                CheckCancellation();
                frame = SkipScriptPrefixes(frame.Enclosing);
            }
        }
        if (frame == null)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated);

        using var output = new ByteMemoryBuilder(0, Memory.Budget);
        output.Append("0 "u8).Append(frame.FunctionName.Bytes).Append((byte)'\n');
        await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);

        static FunctionContext? SkipScriptPrefixes(FunctionContext? frame)
        {
            while (frame?.Locals.IsTemporaryPrefixScope == true)
                frame = frame.Enclosing;
            return frame;
        }
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
                await ReportExpansionDiagnosticAsync([command],
                    [ShellValue.FromBytes(option[..2]), ShellValue.FromText(": invalid option")]).ConfigureAwait(false);
                return await FinishUsageErrorAsync().ConfigureAwait(false);
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
        {
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("filename argument required"))
                .ConfigureAwait(false);
            return await FinishUsageErrorAsync().ConfigureAwait(false);
        }
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

        async Task<EvaluationResult> FinishUsageErrorAsync()
        {
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("usage: "
                + BuiltinDocumentation.For(kind).Synopsis(commandName))).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }
    }

    private enum ScriptExecutionKind { Root, LegacyCapture, Source, Eval, Trap }
    private const int BuiltinSyntaxErrorStatus = 257;

    /// <summary>Copies eval/trap input into an owner retained by any syntax that survives execution.</summary>
    private async Task<EvaluationResult> EvaluateCopiedCommandUnitsAsync(
        IReadOnlyList<ShellValue> words, int start, ScopedEvaluation scope, ScriptExecutionKind kind)
    {
        // Expansion is complete. Include a space between every pair of operands,
        // including empty ones, and admit the final backing before copying.
        long length = Math.Max(0, words.Count - start - 1);
        for (var index = start; index < words.Count; index++)
        {
            CheckCancellation();
            length += words[index].Bytes.Length;
        }
        if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        var sourceMemory = new SourceMemory(Memory.Budget.CreateScope());
        try
        {
            using var builder = new ByteMemoryBuilder((int)length, sourceMemory.Budget);
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
        if (kind is not (ScriptExecutionKind.Root or ScriptExecutionKind.LegacyCapture))
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (kind is ScriptExecutionKind.Source or ScriptExecutionKind.Eval)
            scope = scope with { InNestedCommandUnits = true };
        var lineOffset = kind switch
        {
            ScriptExecutionKind.Eval => Math.Max(1, scope.Context.Coordinates.EndLine) - 1,
            ScriptExecutionKind.Source or ScriptExecutionKind.Trap => 0,
            _ => scope.Context.Coordinates.Offset
        };

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
                var failedUnit = commands.Current as CommandUnitParseResult.Failed;
                var skipExecution = result.Flow is FlowControlKind.Break or FlowControlKind.Continue;
                ICommand command;
                if (failedUnit != null)
                {
                    await ReportSyntaxErrorAsync(failedUnit.Error).ConfigureAwait(false);
                    if (failedUnit.Failure.IsDirectQuotedSubstitutionFailure)
                    {
                        PreparePipelineStatusStorage(1);
                        scope.Variables.SetPipelineStatuses([2], scope.Context, _cancellationToken);
                        _state.LastExitCode = 2;
                    }
                    if (failedUnit.CompletedPrefix is { } prefix && !skipExecution)
                        command = prefix;
                    else
                    {
                        if (kind == ScriptExecutionKind.LegacyCapture && failedUnit.Failure.ExitsSubstitution)
                            return EvaluationResult.Exit(1);
                        if (kind is (ScriptExecutionKind.Source or ScriptExecutionKind.Eval) && failedUnit.Failure.ExitsSubstitution)
                        {
                            // These child boundaries unwind the builtin before running their EXIT action.
                            if (_state.ForkKind is ShellForkKind.CommandSubstitution or ShellForkKind.PipelineSimpleCommand)
                                return EvaluationResult.Exit(1);

                            // In the invoking shell, EXIT sees the full parser status and the active
                            // builtin scope. The result crossing the shell boundary uses its low byte.
                            var status = failedUnit.Failure.Kind == ParserFailureKind.SubstitutionGrammar
                                ? BuiltinSyntaxErrorStatus : 2;
                            var exit = await RunExitTrapAsync(scope, EvaluationResult.Exit(status)).ConfigureAwait(false);
                            return exit.WithExitCode(exit.ExitCode & byte.MaxValue);
                        }
                        if (failedUnit.Failure.Kind == ParserFailureKind.ArrayQuotedSubstitutionForHeader)
                            return result.WithExitCode(1);
                        if (kind == ScriptExecutionKind.Trap && _state.ErrexitEnabled && !scope.Context.ErrexitIgnored)
                            return EvaluationResult.Exit(2);
                        return result.WithExitCode(2);
                    }
                }
                else
                    command = ((CommandUnitParseResult.Parsed)commands.Current).Command;

                // Loop flow skips execution but still checks the remaining source/eval syntax.
                if (skipExecution)
                    continue;

                result = await EvaluateCommandAsync(command,
                    scope with
                    {
                        Context = scope.Context.WithSourceMemory(unitMemory).WithCoordinates(
                            new CommandCoordinates(commands.Current.Locations, lineOffset, lineOffset + 1, lineOffset + 1))
                    }, negated: false)
                    .ConfigureAwait(false);
                if (result.Flow == FlowControlKind.Return)
                    return kind == ScriptExecutionKind.Source ? EvaluationResult.FromExitCode(result.ExitCode) : result;
                if (result.Flow == FlowControlKind.Exit)
                {
                    // Recover only the expansion failures that end the current command unit.
                    // Explicit exit, errexit and unrecoverable expansion failures still terminate.
                    result = await FinalizeResultAsync(RecoverScriptExpansionFailure(result, kind, scope.Context), negated: false)
                        .ConfigureAwait(false);
                    if (result.Flow == FlowControlKind.Exit)
                        return result;
                }
                if (failedUnit != null)
                    break;
            }
            finally
            {
                scanContext.SyntaxMemory = null;
                unitMemory.Release();
            }
        }
        return RecoverScriptExpansionFailure(result, kind, scope.Context);
    }

    /// <summary>Recovers unit-scoped expansion failures while preserving root versus nested status rules.</summary>
    private EvaluationResult RecoverScriptExpansionFailure(
        EvaluationResult result, ScriptExecutionKind kind, EvaluationContext context) =>
        GetRecoverableScriptExpansionStatus(kind, context) is { } status ? RecoverExpansionFailure(status) : result;

    private int? GetRecoverableScriptExpansionStatus(ScriptExecutionKind kind, EvaluationContext context)
    {
        if (CanRecoverCommandSubstitutionSyntax)
            return 1;
        if (_state.FatalExpansion is not
            { Kind: ExpansionFailureKind.Arithmetic or ExpansionFailureKind.WordSyntax
                or ExpansionFailureKind.AssignmentTarget or ExpansionFailureKind.NamerefDefault } failure)
            return null;

        // Recovery uses the caller's errexit state, including for tested commands within the unit.
        if (_state.ErrexitEnabled && !context.ErrexitIgnored
            && failure.Kind is ExpansionFailureKind.AssignmentTarget or ExpansionFailureKind.WordSyntax)
            return null;

        // Nameref defaults fail with 1 at script boundaries, but with 2 at the root.
        return failure.Kind == ExpansionFailureKind.NamerefDefault
            && kind is not (ScriptExecutionKind.Root or ScriptExecutionKind.LegacyCapture) ? 1 : failure.ExitCode;
    }

    private bool CanRecoverCommandSubstitutionSyntax =>
        _state.FatalExpansion is { Kind: ExpansionFailureKind.CommandSubstitutionSyntax, ExitCode: 1 };

    private EvaluationResult RecoverExpansionFailure(int exitCode)
    {
        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);
        return EvaluationResult.FromExitCode(exitCode);
    }
}

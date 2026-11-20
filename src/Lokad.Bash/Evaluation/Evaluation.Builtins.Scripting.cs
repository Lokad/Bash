using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
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
        var command = words[0];
        var first = 1;
        if (first < words.Count)
        {
            var option = words[first].Bytes;
            if (option.Span.SequenceEqual("--"u8))
                first++;
            else if (option.Span.SequenceEqual("--help"u8))
                return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Caller, "caller", negated)
                    .ConfigureAwait(false);
            else if (option.Span is [(byte)'-', _, ..])
                return await ReportUsageAsync(ShellValue.FromBytes(option[..2]), ": invalid option").ConfigureAwait(false);
        }

        var index = -1L;
        if (first < words.Count)
        {
            var operand = words[first];
            if (!TryParseControlNumber(operand, out index))
                return await ReportUsageAsync(operand, operand.Bytes.Span.StartsWith("0x"u8)
                    ? ": invalid hex number" : ": invalid number").ConfigureAwait(false);
            if (index < 0)
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
        var frame = SkipScriptPrefixes(context.FunctionContext);
        while (frame != null && index-- >= 0)
        {
            CheckCancellation();
            frame = SkipScriptPrefixes(frame.Enclosing);
        }
        if (frame == null)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);

        using var output = new ByteMemoryBuilder(0, Memory.Budget);
        output.Append("0 "u8).Append(frame.FunctionName.Bytes).Append((byte)'\n');
        await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(0), negated).ConfigureAwait(false);

        async Task<EvaluationResult> ReportUsageAsync(ShellValue operand, string reason)
        {
            await ReportExpansionDiagnosticAsync([command], [operand, ShellValue.FromText(reason)])
                .ConfigureAwait(false);
            var synopsis = BuiltinDocumentation.For(BuiltinKind.Caller).Synopsis("caller");
            await ReportExpansionDiagnosticAsync([command], [ShellValue.FromText("usage: "), ShellValue.FromText(synopsis)])
                .ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
        }

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
                return await WriteBuiltinHelpAsync(command, documentation.Synopsis(commandName), documentation.Description, negated)
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
                    result = await RecoverScriptExpansionFailureAsync(result, kind, scope).ConfigureAwait(false);
                    result = await FinalizeResultAsync(result, negated: false).ConfigureAwait(false);
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
        return await RecoverScriptExpansionFailureAsync(result, kind, scope).ConfigureAwait(false);
    }

    /// <summary>Recovers unit-scoped expansion failures while preserving root versus nested status rules.</summary>
    private ValueTask<EvaluationResult> RecoverScriptExpansionFailureAsync(
        EvaluationResult result, ScriptExecutionKind kind, ScopedEvaluation scope) =>
        GetRecoverableScriptExpansionStatus(kind, scope) is { } status
            ? RecoverExpansionFailureAsync(status, scope, IsLoadedReader(kind))
            : ValueTask.FromResult(result);

    private int? GetRecoverableScriptExpansionStatus(ScriptExecutionKind kind, ScopedEvaluation scope)
    {
        if (CanRecoverCommandSubstitutionSyntax)
            return 1;
        if (kind == ScriptExecutionKind.Root)
        {
            // A list inside a loaded unit must unwind to that reader before it resumes.
            if (_state.FatalExpansion is { Kind: ExpansionFailureKind.AssignmentTarget }
                && (scope.InNestedCommandUnits || _state.Traps?.ExitTrapStatus != null))
                return null;
            if (RecoverableRootExpansionStatus is { } status)
                return status;
        }
        if (_state.FatalExpansion is
            { Kind: ExpansionFailureKind.ParameterDefaultAssignment or ExpansionFailureKind.AssignmentTarget } assignmentFailure)
        {
            // Loaded units recover at the invoking reader, including when it was tested.
            return _state.ForkKind == null && IsLoadedReader(kind)
                && (assignmentFailure.CanResumeUntestedUnit || scope.Context.ErrexitIgnored) ? 1 : null;
        }
        if (_state.FatalExpansion is not
            { Kind: ExpansionFailureKind.Arithmetic or ExpansionFailureKind.WordSyntax
                or ExpansionFailureKind.NamerefDefault } failure)
            return null;

        // Recovery uses the caller's errexit state, including for tested commands within the unit.
        if (_state.ErrexitEnabled && !scope.Context.ErrexitIgnored && failure.Kind == ExpansionFailureKind.WordSyntax)
            return null;

        // Nameref defaults fail with 1 at script boundaries, but with 2 at the root.
        return failure.Kind == ExpansionFailureKind.NamerefDefault
            && kind is not (ScriptExecutionKind.Root or ScriptExecutionKind.LegacyCapture) ? 1 : failure.ExitCode;
    }

    private static bool IsLoadedReader(ScriptExecutionKind kind) =>
        kind is ScriptExecutionKind.Eval or ScriptExecutionKind.Source or ScriptExecutionKind.Trap;

    private bool CanRecoverCommandSubstitutionSyntax =>
        _state.FatalExpansion is { Kind: ExpansionFailureKind.CommandSubstitutionSyntax, ExitCode: 1 };

    private int? RecoverableRootExpansionStatus => _state.ForkKind != null ? null : _state.FatalExpansion switch
    {
        { Kind: ExpansionFailureKind.IntegerAssignment or ExpansionFailureKind.ArrayIndex, ExitCode: var status } => status,
        { Kind: ExpansionFailureKind.ParameterDefaultAssignment, ExitCode: 2 } => 2,
        { Kind: ExpansionFailureKind.AssignmentTarget, CanResumeUntestedUnit: true } => 1,
        _ => null
    };

    private async ValueTask<EvaluationResult> RecoverExpansionFailureAsync(int exitCode, ScopedEvaluation scope, bool loadedReader)
    {
        var kind = _state.FatalExpansion?.Kind;
        var publishLoadedAssignment = loadedReader
            && kind is ExpansionFailureKind.ParameterDefaultAssignment or ExpansionFailureKind.AssignmentTarget;
        _state.FatalExpansion = null;
        _state.TryConsumeExpansionError(out _);

        if (kind == ExpansionFailureKind.IntegerAssignment || publishLoadedAssignment)
        {
            // The caller's bindings and descriptors are visible again. Integer assignments
            // replace PIPESTATUS only after a previous success.
            if (publishLoadedAssignment || _state.LastExitCode.GetValueOrDefault() == 0)
            {
                try
                {
                    using var memory = Memory.BeginCommand(scope);
                    PublishExpansionPipelineStatus(exitCode, scope);
                }
                catch (MemoryBudgetExceededException)
                {
                    Memory.ReleaseUnusedCapacity();
                    await ReportMemoryRefusalAsync("shell").ConfigureAwait(false);
                }
            }
            _state.LastExitCode = exitCode;
        }
        return EvaluationResult.FromExitCode(exitCode);
    }
}

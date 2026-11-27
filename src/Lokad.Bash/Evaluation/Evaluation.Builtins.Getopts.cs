using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private async Task<EvaluationResult> EvaluateBuiltinGetoptsAsync(
        IReadOnlyList<ShellValue> words, ScopedEvaluation scope, Variables.CommandBindings? bindings, bool negated)
    {
        using var diagnostics = new BuiltinDiagnosticScope(this);
        var command = ShellValue.FromText("getopts");
        var operand = 1;
        if (words.Count > operand)
        {
            var first = words[operand].Bytes;
            var field = ClassifyShortOptionField(first.Span);
            if (field == ShortOptionField.Help)
                return await WriteControlHelpAsync(words[0], BuiltinKind.Getopts, "getopts", negated).ConfigureAwait(false);
            if (field == ShortOptionField.Terminator)
                operand++;
            else if (field == ShortOptionField.Flags)
            {
                await WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromBytes(first[..2]), ShellValue.FromText(": invalid option")],
                    diagnostics, GetBuiltinDiagnosticPrefix(scope.Context)).ConfigureAwait(false);
                return await UsageAsync().ConfigureAwait(false);
            }
        }
        if (words.Count < operand + 2)
            return await UsageAsync().ConfigureAwait(false);

        var variables = scope.Variables;
        var optionSpec = words[operand].Bytes;
        var name = words[operand + 1];
        var start = operand + 2;
        var arguments = words.Count > start ? words : variables.PositionalParameters;
        if (words.Count == start)
            start = 0;
        var silent = !optionSpec.IsEmpty && optionSpec.Span[0] == (byte)':';
        if (silent)
            optionSpec = optionSpec[1..];

        var cursor = variables.GetoptsPosition;
        var option = Scan(optionSpec.Span, arguments, start, ref cursor);
        if (option.Diagnostic && !silent && variables.GetoptsDiagnosticsEnabled)
        {
            var text = option.Outcome == GetoptsOutcome.Missing ? "option requires an argument -- " : "illegal option -- ";
            await WriteDiagnosticUnlessStderrClosedAsync([variables.ArgumentZero.IsEmpty ? command : variables.ArgumentZero],
                [ShellValue.FromText(text), ShellValue.FromByte(option.Flag)], diagnostics).ConfigureAwait(false);
        }

        int status;
        try
        {
            _ = await AssignAsync("OPTIND", ShellValue.FromInteger(cursor.ArgumentIndex)).ConfigureAwait(false);
            switch (option.Outcome)
            {
                case GetoptsOutcome.End:
                    ClearArgument();
                    _ = await SetNameAsync((byte)'?').ConfigureAwait(false);
                    status = 1;
                    break;
                case GetoptsOutcome.Unknown:
                case GetoptsOutcome.Missing:
                    status = await SetNameAsync(silent && option.Outcome == GetoptsOutcome.Missing ? (byte)':' : (byte)'?')
                        .ConfigureAwait(false);
                    if (silent)
                        _ = await AssignAsync("OPTARG", ShellValue.FromByte(option.Flag)).ConfigureAwait(false);
                    else
                        ClearArgument();
                    break;
                default:
                    _ = await AssignAsync("OPTARG", option.Argument).ConfigureAwait(false);
                    status = await SetNameAsync(option.Flag).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            // Even a readonly OPTIND leaves the scanner at the next option. Explicit
            // writes between calls invalidate this cursor through variable storage.
            variables.GetoptsPosition = cursor;
        }
        return await FinishAsync(status).ConfigureAwait(false);

        async Task<EvaluationResult> UsageAsync()
        {
            var usage = BuiltinDocumentation.For(BuiltinKind.Getopts).Synopsis("getopts");
            await WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("usage: " + usage)], diagnostics).ConfigureAwait(false);
            return await FinishAsync(2).ConfigureAwait(false);
        }

        Task<EvaluationResult> FinishAsync(int code) => FinalizeResultAsync(
            EvaluationResult.FromExitCode(diagnostics.Result.CanAcceptMore ? code : 1), negated);

        Task<BuiltinAssignmentResult> AssignAsync(BindingName name, ShellValue? value) =>
            AssignBuiltinAsync(VariableReference.ForName(name), value, scope,
                bindings, BuiltinAssignmentKind.Getopts, command, expansionWriteTarget: null);

        async Task<int> SetNameAsync(byte optionByte)
        {
            if (!VariableNames.IsValidVariableName(name.Bytes.Span))
            {
                await WriteInvalidIdentifierDiagnosticAsync(command, name, diagnostics, ClosedStderrHandling.AttemptWrite,
                        GetBuiltinDiagnosticPrefix(scope.Context))
                    .ConfigureAwait(false);
                return 1;
            }
            if (!scope.CanExpand)
                return 1;

            var target = BindingNameExpansion.FromValue(name, Memory);
            var assigned = await AssignAsync(target, ShellValue.FromByte(optionByte)).ConfigureAwait(false);
            return assigned switch
            {
                BuiltinAssignmentResult.Readonly => 2,
                BuiltinAssignmentResult.Failed => 1,
                _ => 0
            };
        }

        void ClearArgument()
        {
            if (!BuiltinDiagnosticsOpen)
                return;
            var destination = bindings?.SelectOutputVariables("OPTARG", Memory, _cancellationToken) ?? variables;
            destination.ClearGetoptsArgument();
            bindings?.CompleteOutput("OPTARG", destination);
        }

        static GetoptsOption Scan(ReadOnlySpan<byte> spec, IReadOnlyList<ShellValue> args, int start,
            ref GetoptsCursor cursor)
        {
            var count = args.Count - start;
            var index = cursor.ArgumentIndex;
            if (index > count)
            {
                cursor = GetoptsCursor.AtArgument(count + 1);
                return default;
            }

            var bundle = cursor.BundleIndex;
            var offset = cursor.ByteOffset;
            var current = bundle > 0 && bundle <= count ? args[start + bundle - 1].Bytes : default;
            // The new argument vector may not have a byte at the saved bundle offset.
            if (offset >= current.Length)
            {
                bundle = index;
                offset = 1;
                current = args[start + index - 1].Bytes;
                if (current.Span.SequenceEqual("--"u8))
                {
                    cursor = GetoptsCursor.AtArgument(index + 1);
                    return default;
                }
                if (current.Length < 2 || current.Span[0] != (byte)'-')
                {
                    cursor = GetoptsCursor.AtArgument(index);
                    return default;
                }
            }

            var flag = current.Span[offset++];
            var specIndex = spec.IndexOf(flag);
            cursor = offset < current.Length ? new(index, bundle, offset) : GetoptsCursor.AtArgument(index + 1);
            if (flag == ':' || specIndex < 0)
                return new(GetoptsOutcome.Unknown, flag, null, Diagnostic: true);

            ShellValue? argument = null;
            if (specIndex + 1 < spec.Length && spec[specIndex + 1] == ':')
            {
                cursor = GetoptsCursor.AtArgument(index + 1);
                if (offset < current.Length)
                    argument = ShellValue.FromBytes(current[offset..]);
                else if (index < count)
                {
                    argument = args[start + index];
                    cursor = GetoptsCursor.AtArgument(index + 2);
                }
                else
                    return new(GetoptsOutcome.Missing, flag, null, Diagnostic: true);
            }

            // Bash reports these results after consuming any required argument.
            if (flag == '?' && (argument == null || argument.Value.IsEmpty))
                return new(argument == null ? GetoptsOutcome.Unknown : GetoptsOutcome.Missing, flag, null, Diagnostic: false);
            return flag switch
            {
                0xfd => new(GetoptsOutcome.Missing, flag, null, Diagnostic: false),
                0xfe => new(GetoptsOutcome.Unknown, flag, null, Diagnostic: false),
                0xff => default,
                _ => new(GetoptsOutcome.Option, flag, argument, Diagnostic: false)
            };
        }
    }

    private enum GetoptsOutcome { End, Option, Unknown, Missing }
    private readonly record struct GetoptsOption(
        GetoptsOutcome Outcome, byte Flag, ShellValue? Argument, bool Diagnostic);
}

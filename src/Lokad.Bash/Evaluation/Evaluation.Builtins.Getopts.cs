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
            if (first.Span.SequenceEqual("--help"u8))
                return await WriteControlHelpAsync(BuiltinKind.Getopts, negated).ConfigureAwait(false);
            if (first.Span.SequenceEqual("--"u8))
                operand++;
            else if (first.Length > 1 && first.Span[0] == (byte)'-')
            {
                var message = ByteMemoryBuilder.CreateUnaccounted(0).Append(first[..2]).Append(": invalid option"u8);
                await ReportExpansionDiagnosticAsync(command, ShellValue.FromBytes(message.ToMemory())).ConfigureAwait(false);
                return await UsageAsync().ConfigureAwait(false);
            }
        }
        if (words.Count < operand + 2)
            return await UsageAsync().ConfigureAwait(false);

        var variables = scope.Variables;
        var optionSpec = words[operand].Bytes;
        var name = words[operand + 1];
        var target = BindingName.FromValue(name);
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
            var diagnostic = ByteMemoryBuilder.CreateUnaccounted(0)
                .Append(option.Outcome == GetoptsOutcome.Missing ? "option requires an argument -- "u8 : "illegal option -- "u8)
                .Append(option.Flag);
            await ReportExpansionDiagnosticAsync(variables.ArgumentZero.IsEmpty ? command : variables.ArgumentZero,
                ShellValue.FromBytes(diagnostic.ToMemory())).ConfigureAwait(false);
        }

        int status;
        try
        {
            _ = await AssignAsync("OPTIND", ShellValue.FromText(FormatExitCode(cursor.ArgumentIndex))).ConfigureAwait(false);
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
                        _ = await AssignAsync("OPTARG", ByteValue(option.Flag)).ConfigureAwait(false);
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
            await ReportExpansionDiagnosticAsync(command, ShellValue.FromText("usage: " + usage)).ConfigureAwait(false);
            return await FinishAsync(2).ConfigureAwait(false);
        }

        Task<EvaluationResult> FinishAsync(int code) => FinalizeResultAsync(
            EvaluationResult.FromExitCode(diagnostics.Result.CanAcceptMore ? code : 1), negated);

        Task<BuiltinAssignmentResult> AssignAsync(BindingName name, ShellValue? value) =>
            AssignBuiltinAsync(VariableReference.ForName(name), value, scope,
                bindings, BuiltinAssignmentKind.Getopts, command, expansionWriteTarget: null);

        async Task<int> SetNameAsync(byte optionByte)
        {
            if (!target.IsIdentifier)
            {
                var diagnostic = ByteMemoryBuilder.CreateUnaccounted(0)
                    .Append(ShellArgumentEscaping.QuoteDiagnosticOperand(name).Bytes).Append(": not a valid identifier"u8);
                await ReportExpansionDiagnosticAsync(command, ShellValue.FromBytes(diagnostic.ToMemory())).ConfigureAwait(false);
                return 1;
            }
            var assigned = await AssignAsync(target, ByteValue(optionByte)).ConfigureAwait(false);
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
            var destination = bindings?.SelectOutputVariables("OPTARG") ?? variables;
            destination.ClearGetoptsArgument();
            bindings?.CompleteOutput("OPTARG", destination);
        }

        static ShellValue ByteValue(byte value) => ShellValue.FromBytes(new[] { value });

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

            // The pinned scanners use '?' for an error result and -1 for EOF.
            if (flag == '?' && (argument == null || argument.Value.IsEmpty))
                return new(argument == null ? GetoptsOutcome.Unknown : GetoptsOutcome.Missing, flag, null, Diagnostic: false);
            return flag == byte.MaxValue ? default : new(GetoptsOutcome.Option, flag, argument, Diagnostic: false);
        }
    }

    private enum GetoptsOutcome { End, Option, Unknown, Missing }
    private readonly record struct GetoptsOption(
        GetoptsOutcome Outcome, byte Flag, ShellValue? Argument, bool Diagnostic);
}

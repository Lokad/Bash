using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private static readonly ShellValue[] SupportedShoptOptions =
        [ShellValue.FromText("inherit_errexit"), ShellValue.FromText("localvar_inherit")];

    private static readonly ShellValue[] SupportedSetOptions =
        [ShellValue.FromText("braceexpand"), ShellValue.FromText("errexit"), ShellValue.FromText("pipefail")];

    [Flags]
    private enum ShoptFlags { None = 0, Print = 1, Quiet = 2, SetOptions = 4, Enable = 8, Disable = 16 }

    private async Task<EvaluationResult> EvaluateBuiltinShoptAsync(
        IReadOnlyList<ShellValue> words, EvaluationContext context, bool negated)
    {
        var command = words[0];
        var prefix = GetBuiltinDiagnosticPrefix(context);
        var cursor = new ShortOptionCursor(words);
        var flags = ShoptFlags.None;
        while (cursor.TryReadFlag(out var flag))
        {
            CheckCancellation();
            var selected = flag.Span[0] switch
            {
                (byte)'p' => ShoptFlags.Print,
                (byte)'q' => ShoptFlags.Quiet,
                (byte)'o' => ShoptFlags.SetOptions,
                (byte)'s' => ShoptFlags.Enable,
                (byte)'u' => ShoptFlags.Disable,
                _ => ShoptFlags.None
            };
            if (selected == ShoptFlags.None)
            {
                await WriteInvalidShortOptionUnlessStderrClosedAsync(command, ShellValue.FromByte((byte)'-'),
                    flag, BuiltinKind.Shopt, "shopt", prefix).ConfigureAwait(false);
                return await FinishAsync(2).ConfigureAwait(false);
            }
            flags |= selected;
        }
        if (cursor.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Shopt, "shopt", negated).ConfigureAwait(false);

        var mode = flags & (ShoptFlags.Enable | ShoptFlags.Disable);
        if (mode == (ShoptFlags.Enable | ShoptFlags.Disable))
        {
            await ReportBuiltinDiagnosticAsync(command, "cannot set and unset shell options simultaneously", context)
                .ConfigureAwait(false);
            return await FinishAsync(1).ConfigureAwait(false);
        }

        var setOptions = (flags & ShoptFlags.SetOptions) != 0;
        var first = cursor.ArgumentIndex;
        var listing = first == words.Count;
        var status = 0;
        var outputClosed = (flags & ShoptFlags.Quiet) != 0;
        using var output = new ByteMemoryBuilder(0, Memory.Budget);
        if (listing && !outputClosed)
        {
            foreach (var name in setOptions ? SupportedSetOptions : SupportedShoptOptions)
            {
                var state = ReadState(name).GetValueOrDefault();
                if (mode == ShoptFlags.None || state == (mode == ShoptFlags.Enable))
                    await WriteRowAsync(name, state).ConfigureAwait(false);
            }
        }
        for (var index = first; index < words.Count; index++)
        {
            CheckCancellation();
            var name = words[index];
            var state = ReadState(name);
            var rejectsChange = state is { } current && mode != ShoptFlags.None
                && !TrySetState(name, current, enabled: mode == ShoptFlags.Enable);
            if (state == null || rejectsChange)
            {
                await ReportBuiltinDiagnosticAsync([command], [name, ShellValue.FromText(setOptions
                    ? ": invalid option name" : ": invalid shell option name")], context).ConfigureAwait(false);
                // The named -o write path reports invalid options without accumulating the query status.
                if (!setOptions || mode == ShoptFlags.None || rejectsChange)
                    status = 1;
                continue;
            }
            if (mode == ShoptFlags.None)
            {
                if (!state.Value)
                    status = 1;
                await WriteRowAsync(name, state.Value).ConfigureAwait(false);
            }
        }
        return await FinishAsync(status).ConfigureAwait(false);

        bool? ReadState(ShellValue name)
        {
            var spelling = name.Bytes.Span;
            if (setOptions)
                return ShellPredicates.GetOptionState(spelling, _state.ErrexitEnabled);
            if (spelling.SequenceEqual("inherit_errexit"u8))
                return _state.InheritErrexit;
            return spelling.SequenceEqual("localvar_inherit"u8) ? _state.LocalVariableInheritance : null;
        }

        bool TrySetState(ShellValue name, bool current, bool enabled)
        {
            var spelling = name.Bytes.Span;
            if (setOptions)
            {
                if (!spelling.SequenceEqual("errexit"u8))
                    return current == enabled;
                _state.ErrexitEnabled = enabled;
            }
            else if (spelling.SequenceEqual("inherit_errexit"u8))
                _state.InheritErrexit = enabled;
            else
                _state.LocalVariableInheritance = enabled;
            return true;
        }

        async Task WriteRowAsync(ShellValue name, bool enabled)
        {
            if (outputClosed)
                return;
            output.Truncate(0);
            if ((flags & ShoptFlags.Print) != 0)
            {
                output.Append(setOptions
                    ? enabled ? "set -o "u8 : "set +o "u8
                    : enabled ? "shopt -s "u8 : "shopt -u "u8);
                output.Append(name.Bytes).Append((byte)'\n');
            }
            else
            {
                output.Append(name.Bytes);
                output.Append("               "u8[..Math.Max(0, 15 - name.Bytes.Length)]);
                output.Append(enabled ? "\ton\n"u8 : "\toff\n"u8);
            }
            var result = await WriteBuiltinOutputAsync(command, output.ToMemory(), prefix).ConfigureAwait(false);
            outputClosed = !result.CanAcceptMore;
            if (result.IsError)
                status = 1;
        }

        Task<EvaluationResult> FinishAsync(int status) =>
            FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated);
    }
}

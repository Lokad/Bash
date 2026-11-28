using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
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
            foreach (var entry in ShellOptions.Names(setOptions))
            {
                var state = _state.HasOption(entry.Option);
                if (mode == ShoptFlags.None || state == (mode == ShoptFlags.Enable))
                    await WriteRowAsync(entry.Name, state).ConfigureAwait(false);
            }
        }
        for (var index = first; index < words.Count; index++)
        {
            CheckCancellation();
            var name = words[index];
            var option = ShellOptions.Find(name.Bytes.Span, setOptions);
            var state = _state.HasOption(option);
            var enabled = mode == ShoptFlags.Enable;
            var fixedOption = (option & ShellOptions.Fixed) != 0;
            var rejectsChange = fixedOption && mode != ShoptFlags.None && state != enabled;
            if (option == ShellOption.None || rejectsChange)
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
                if (!state)
                    status = 1;
                await WriteRowAsync(name, state).ConfigureAwait(false);
            }
            else if (!fixedOption)
                _state.SetOption(option, enabled);
        }
        return await FinishAsync(status).ConfigureAwait(false);

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

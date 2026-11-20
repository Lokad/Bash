using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private enum BuiltinHelpFormat { Detailed, Description, Synopsis, Manual }

    /// <summary>Prints builtin documentation through the current command's stdout and redirections.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinHelpAsync(IReadOnlyList<ShellValue> words, bool negated)
    {
        var command = words[0];
        var descriptions = false;
        var synopses = false;
        var manual = false;
        var options = new ShortOptionCursor(words);
        while (options.TryReadFlag(out var option))
        {
            switch (option.Span[0])
            {
                case (byte)'d': descriptions = true; break;
                case (byte)'s': synopses = true; break;
                case (byte)'m': manual = true; break;
                default:
                    await WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("-"),
                        ShellValue.FromBytes(option), ShellValue.FromText(": invalid option\nhelp: usage: "
                            + BuiltinDocumentation.For(BuiltinKind.Help).Synopsis("help"))], scope: null).ConfigureAwait(false);
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
            }
        }
        if (options.RequestedHelp)
            return await WriteBuiltinHelpUnlessStdoutClosedAsync(command, BuiltinKind.Help, "help", negated)
                .ConfigureAwait(false);

        var firstPattern = options.ArgumentIndex;
        var format = descriptions ? BuiltinHelpFormat.Description : manual ? BuiltinHelpFormat.Manual
            : synopses ? BuiltinHelpFormat.Synopsis : BuiltinHelpFormat.Detailed;
        using var output = new ByteMemoryBuilder(0, Memory.Budget);
        var matched = false;
        if (firstPattern == words.Count)
        {
            Append("Supported shell builtins (use help name for details):\n");
            foreach (var (name, kind) in BuiltinClassification.Entries)
                Append(BuiltinDocumentation.For(kind).Synopsis(name) + "\n");
            matched = true;
        }
        else
        {
            var allActive = new GlobMask(ReadOnlyMemory<byte>.Empty);
            for (var index = firstPattern; index < words.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!allActive.TryUnescape(words[index], Memory, _cancellationToken,
                        out var pattern, out var mask))
                    continue;
                var patternMatched = SelectTopics(prefix: false);
                if (!patternMatched)
                    patternMatched = SelectTopics(prefix: true);
                matched |= patternMatched;

                bool SelectTopics(bool prefix)
                {
                    var found = false;
                    var activity = mask.AsSpan();
                    foreach (var registration in BuiltinClassification.Entries)
                    {
                        if (!(prefix ? registration.NameBytes.Span.StartsWith(words[index].Bytes.Span)
                            : TextGlobMatcher.MatchUtf8(pattern.Bytes.Span, in activity, registration.NameBytes.Span,
                                requireEnd: true, Memory.Workspace, _cancellationToken, out _) == GlobMatchResult.Match))
                            continue;
                        found = true;
                        var (name, kind) = registration;
                        var documentation = BuiltinDocumentation.For(kind);
                        var synopsis = format == BuiltinHelpFormat.Description ? name : documentation.Synopsis(name);
                        Append(format switch
                        {
                            BuiltinHelpFormat.Description => name + " - " + documentation.Summary + "\n",
                            BuiltinHelpFormat.Synopsis => name + ": " + synopsis + "\n",
                            BuiltinHelpFormat.Manual => "NAME\n    " + name + " - " + documentation.Summary
                                + "\n\nSYNOPSIS\n    " + synopsis + "\n\nDESCRIPTION\n    " + documentation.Description + "\n\n",
                            _ => name + ": " + synopsis + "\n    " + documentation.Description + "\n"
                        });
                    }
                    return found;
                }
            }
        }

        if (!matched)
        {
            var topic = words[^1];
            await WriteDiagnosticUnlessStderrClosedAsync([command], [ShellValue.FromText("no help topics match `"),
                topic, ShellValue.FromText("'.  Try `help help' or `man -k "),
                topic, ShellValue.FromText("' or `info "), topic, ShellValue.FromText("'.")], scope: null).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
        var result = await WriteBuiltinOutputAsync(command, output.ToMemory()).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(result.IsError ? 1 : 0), negated).ConfigureAwait(false);

        void Append(string text)
        {
            var encoding = Utf8Text.StrictUtf8;
            var byteCount = encoding.GetByteCount(text);
            encoding.GetBytes(text.AsSpan(), output.AppendSpan(byteCount));
        }
    }
}

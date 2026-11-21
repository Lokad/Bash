using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private enum BuiltinHelpFormat { Catalog, Detailed, Description, Synopsis, Manual }

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
                    await WriteInvalidShortOptionUnlessStderrClosedAsync(command, option, BuiltinKind.Help, "help")
                        .ConfigureAwait(false);
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
            output.Append("Supported shell builtins (use help name for details):\n"u8);
            foreach (var (name, kind) in BuiltinClassification.Entries)
                AppendTopic(name, kind, BuiltinHelpFormat.Catalog);
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
                        AppendTopic(registration.Name, registration.Kind, format);
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

        void AppendTopic(string name, BuiltinKind kind, BuiltinHelpFormat topicFormat)
        {
            var documentation = BuiltinDocumentation.For(kind);
            var topic = name.AsMemory();
            var newline = "\n".AsMemory();
            if (topicFormat == BuiltinHelpFormat.Description)
            {
                AppendRecord([topic, " - ".AsMemory(), documentation.Summary, newline]);
                return;
            }

            var arguments = documentation.SynopsisArguments(name).AsMemory();
            var separator = arguments.IsEmpty ? default : " ".AsMemory();
            switch (topicFormat)
            {
                case BuiltinHelpFormat.Catalog:
                    AppendRecord([topic, separator, arguments, newline]);
                    break;
                case BuiltinHelpFormat.Synopsis:
                    AppendRecord([topic, ": ".AsMemory(), topic, separator, arguments, newline]);
                    break;
                case BuiltinHelpFormat.Manual:
                    AppendRecord(["NAME\n    ".AsMemory(), topic, " - ".AsMemory(), documentation.Summary,
                        "\n\nSYNOPSIS\n    ".AsMemory(), topic, separator, arguments,
                        "\n\nDESCRIPTION\n    ".AsMemory(), documentation.Description.AsMemory(), "\n\n".AsMemory()]);
                    break;
                default:
                    AppendRecord([topic, ": ".AsMemory(), topic, separator, arguments, "\n    ".AsMemory(),
                        documentation.Description.AsMemory(), newline]);
                    break;
            }
        }

        void AppendRecord(ReadOnlySpan<ReadOnlyMemory<char>> fragments)
        {
            var encoding = Utf8Text.StrictUtf8;
            // Admit each complete record as one append before encoding its borrowed fragments.
            var byteCount = 0;
            foreach (var fragment in fragments)
                byteCount += encoding.GetByteCount(fragment.Span);
            var destination = output.AppendSpan(byteCount);
            foreach (var fragment in fragments)
                destination = destination[encoding.GetBytes(fragment.Span, destination)..];
        }
    }
}

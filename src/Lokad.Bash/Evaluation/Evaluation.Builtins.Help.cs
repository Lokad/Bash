using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private enum BuiltinHelpFormat { Detailed, Description, Synopsis, Manual }

    /// <summary>Prints builtin documentation through the current command's stdout and redirections.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinHelpAsync(IReadOnlyList<ShellValue> words, bool negated)
    {
        var descriptions = false;
        var synopses = false;
        var manual = false;
        var firstPattern = 1;
        while (firstPattern < words.Count)
        {
            var argument = words[firstPattern];
            if (argument.Bytes.Span.SequenceEqual("--"u8))
            {
                firstPattern++;
                break;
            }
            if (argument.Bytes.Span.SequenceEqual("--help"u8))
            {
                var help = BuiltinDocumentation.For(BuiltinKind.Help);
                return await WriteOutputAsync(
                    Utf8Text.Encode("help: " + help.Synopsis("help") + "\n    " + help.Description + "\n"), 2).ConfigureAwait(false);
            }
            if (argument.Bytes.Length < 2 || argument.Bytes.Span[0] != (byte)'-')
                break;

            var options = new CommandLineOptionParser([argument], allowSingleDashLongOptions: false);
            while (options.TryRead(out var option))
            {
                var flag = option.Kind == CommandLineOptionKind.ShortOption ? option.ShortName : (byte)'-';
                switch (flag)
                {
                    case (byte)'d': descriptions = true; break;
                    case (byte)'s': synopses = true; break;
                    case (byte)'m': manual = true; break;
                    default:
                        var message = ShellValue.FromText("-" + (char)flag + ": invalid option\nhelp: usage: "
                            + BuiltinDocumentation.For(BuiltinKind.Help).Synopsis("help"));
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText("help"), message).ConfigureAwait(false);
                        return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
                }
            }
            firstPattern++;
        }

        var format = descriptions ? BuiltinHelpFormat.Description : manual ? BuiltinHelpFormat.Manual
            : synopses ? BuiltinHelpFormat.Synopsis : BuiltinHelpFormat.Detailed;
        using var output = new ByteMemoryBuilder(0, ExpansionBudget.MemoryBudget);
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
                if (!allActive.TryUnescape(words[index], ExpansionBudget.Words, _cancellationToken,
                        out var pattern, out var mask))
                    continue;
                var remainingSteps = long.MaxValue;
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
                                requireEnd: true, ref remainingSteps, _cancellationToken, out _) == GlobMatchResult.Match))
                            continue;
                        found = true;
                        var (name, kind) = registration;
                        var documentation = BuiltinDocumentation.For(kind);
                        var synopsis = documentation.Synopsis(name);
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
            await ReportExpansionDiagnosticAsync(ShellValue.FromText("help"),
                ShellValue.FromText("no help topics match the supplied patterns")).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
        return await WriteOutputAsync(output.ToMemory(), 0).ConfigureAwait(false);

        async Task<EvaluationResult> WriteOutputAsync(ReadOnlyMemory<byte> content, int exitCode)
        {
            var result = await ExecuteWriteAsync(GetStdOutDescriptor(), content).ConfigureAwait(false);
            await ReportBuiltinWriteFailureAsync(ShellValue.FromText("help"), result).ConfigureAwait(false);
            return await FinalizeResultAsync(
                EvaluationResult.FromExitCode(result.IsError ? 1 : exitCode), negated).ConfigureAwait(false);
        }

        void Append(string text)
        {
            ExpansionBudget.Words.Charge(0, Encoding.UTF8.GetByteCount(text));
            output.Append(Utf8Text.Encode(text));
        }
    }
}

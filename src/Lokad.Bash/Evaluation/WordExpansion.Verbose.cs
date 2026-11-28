using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    private static async Task EchoCaptureDocumentsAsync(IReadOnlyList<WordPart> parts, int start, int end, ScopedEvaluation scope)
    {
        for (var index = start; index < end; index++)
        {
            if (parts[index] is CommandSubstitutionWordPart command)
            {
                await scope.Effects.EchoCaptureDocumentsAsync(command.Substitution).ConfigureAwait(false);
            }
            else if (parts[index] is SourceWordPart { SpellingCaptures: { } captures } source)
            {
                foreach (var capture in captures)
                {
                    var offset = capture.Offset - source.SourceOffset;
                    if (offset >= 0 && capture.Length <= source.Source.Length - offset)
                        await scope.Effects.EchoCaptureDocumentsAsync(capture.Syntax).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task EchoScannedCaptureDocumentsAsync(ShellSource source, ShellScanContext scan, ScopedEvaluation scope)
    {
        var next = 0;
        while (true)
        {
            var position = FunctionDefinitionFormatter.FindNextCapture(source, scan, next, readAheadOnly: false,
                scope.Effects.CancellationToken, out var capture);
            if (position < 0 || capture.Length > source.Length - position)
                return;
            await scope.Effects.EchoCaptureDocumentsAsync(capture.Substitution).ConfigureAwait(false);
            next = position + capture.Length;
        }
    }
}

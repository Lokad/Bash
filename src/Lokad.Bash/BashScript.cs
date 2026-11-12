using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash;

/// <summary>Parses and executes scripts through application-supplied effects.</summary>
public static class BashScript
{
    /// <summary>Runs a new script with the supplied host's descriptors and live-memory owner.</summary>
    public static async Task<RunResult> RunAsync(
        IBashHost host, string source, EvaluationOptions options, CancellationToken cancel)
    {
        if (!Parser.TryParse(source, out var parsed, out var error))
        {
            await host.WriteLineAsync(FileDescriptor.StdErr, error.ToDiagnosticMessage(), cancel).ConfigureAwait(false);
            return new RunResult(2, options.CurrentDirectory);
        }

        var evaluation = ShellEvaluationStartup.CreateForNewHost(parsed, options);
        using var memory = host.MemoryBudget.CreateScope();
        return await new Engine().RunAsync(evaluation, host, memory, cancel).ConfigureAwait(false);
    }
}

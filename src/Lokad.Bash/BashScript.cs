using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash;

/// <summary>Parses and executes scripts through application-supplied effects.</summary>
public static class BashScript
{
    /// <summary>Runs a new script with independent expansion allowances.</summary>
    public static Task<RunResult> RunAsync(
        IBashHost host, string source, EvaluationOptions options, CancellationToken cancel) =>
        RunAsync(host, source, options, parentInvocation: null, cancel);

    /// <summary>Runs on a new host, sharing a parent's expansion allowance when supplied.</summary>
    /// <remarks>The new host supplies descriptors and memory; parent environment and descriptor mappings do not transfer.</remarks>
    public static async Task<RunResult> RunAsync(
        IBashHost host, string source, EvaluationOptions options, CommandInvocation? parentInvocation, CancellationToken cancel)
    {
        if (!Parser.TryParse(source, out var parsed, out var error))
        {
            await host.WriteLineAsync(FileDescriptor.StdErr, error.ToDiagnosticMessage(), cancel).ConfigureAwait(false);
            return new RunResult(2, options.CurrentDirectory);
        }

        var evaluation = ShellEvaluationStartup.CreateForNewHost(parentInvocation, parsed, options);
        using var memory = host.MemoryBudget.CreateScope();
        return await new Engine().RunAsync(evaluation, host, memory, cancel).ConfigureAwait(false);
    }
}

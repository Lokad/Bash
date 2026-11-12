using System.Text;
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
        if (source.Length > Parser.MaximumScriptBytes || Encoding.UTF8.GetByteCount(source) > Parser.MaximumScriptBytes)
        {
            var error = new SyntaxError(Parser.ScriptSizeLimitMessage, 0);
            await host.WriteLineAsync(FileDescriptor.StdErr, error.ToDiagnosticMessage(), cancel).ConfigureAwait(false);
            return new RunResult(2, options.CurrentDirectory);
        }

        var script = new ParsedScript([new ScriptCommand(ShellSource.FromText(source))]);
        var evaluation = ShellEvaluationStartup.CreateForNewHost(script, options);
        using var memory = host.MemoryBudget.CreateScope();
        return await new Engine().RunAsync(evaluation, host, memory, cancel).ConfigureAwait(false);
    }
}

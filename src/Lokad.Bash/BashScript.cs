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
        // An already oversized UTF-16 count proves the byte bound without scanning it.
        var byteCount = source.Length > Parser.MaximumScriptBytes ? source.Length : Encoding.UTF8.GetByteCount(source);
        if (byteCount > Parser.MaximumScriptBytes)
        {
            var error = new SyntaxError(Parser.ScriptSizeLimitMessage, 0);
            await host.WriteLineAsync(FileDescriptor.StdErr, error.ToDiagnosticMessage(), cancel).ConfigureAwait(false);
            return new RunResult(2, options.CurrentDirectory);
        }

        cancel.ThrowIfCancellationRequested();
        using var memory = host.MemoryBudget.CreateScope();
        var sourceMemory = new SourceMemory(memory.CreateScope());
        try
        {
            if (!sourceMemory.Budget.TryReserve(byteCount, out _))
            {
                await host.WriteLineAsync(FileDescriptor.StdErr, "shell: shared memory budget exhausted", cancel).ConfigureAwait(false);
                return new RunResult(1, options.CurrentDirectory);
            }
            var script = new ParsedScript([new ScriptCommand(ShellSource.FromText(source))]);
            var evaluation = ShellEvaluationStartup.CreateForNewHost(script, options, sourceMemory);
            try { return await new Engine().RunAsync(evaluation, host, memory, cancel).ConfigureAwait(false); }
            finally { evaluation.ReleaseFunctions(); }
        }
        finally { sourceMemory.Release(); }
    }
}

using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

// Fatal-process regressions run this same test assembly in a disposable child.
internal static class TestProcess
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments is not ["--run-shell", var entry, var source, var allowance, var milliseconds, var cancellation])
            throw new ArgumentException("Expected a shell containment request.");

        var memory = new MemoryGovernor(long.Parse(allowance));
        var counted = new CountingMemoryBudget(memory);
        await using var host = new TestHost { MemoryBudget = counted };
        host.AddFile("/depth.sh", "if ((n-- > 0)); then source /depth.sh; fi");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(int.Parse(milliseconds)));
        if (Enum.Parse<TestCancellation>(cancellation) == TestCancellation.Admission)
            counted.AdmissionSucceeded += () =>
            {
                if (memory.ReservedBytes >= ExecutionDepthTests.CancellationAdmissionBytes)
                    deadline.Cancel();
            };
        int? status = null;
        var canceled = false;
        using (var owner = counted.CreateScope())
        {
            try
            {
                var result = entry == "prepared"
                    ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(source)), host, owner, deadline.Token)
                    : await host.RunAsync(source, deadline.Token);
                status = result.ExitCode;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                canceled = true;
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new TestProcessResult(
            Environment.Version.Major, status, canceled,
            Convert.ToHexString(host.StandardOutput.Content.Span), Convert.ToHexString(host.StandardError.Content.Span),
            host.OpenFileCount, memory.ReservedBytes, memory.PeakBytes)));
        return 0;
    }
}

internal sealed record TestProcessResult(
    int RuntimeMajor, int? Status, bool Canceled, string OutputHex, string ErrorHex,
    int OpenFileCount, long ReservedBytes, long PeakBytes);

internal enum TestCancellation { Deadline, Admission }

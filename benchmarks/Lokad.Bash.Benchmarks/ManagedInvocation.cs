using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Benchmarks;

internal sealed record ObservedResult(int ExitCode, ContentDigest Stdout, ContentDigest Stderr,
    IReadOnlyDictionary<string, ContentDigest> Files, HostCounts Host, long EstimatedPeakBytes);

/// <summary>Setup owns fixtures and fresh state. RunAsync covers awaited execution and evaluation cleanup.</summary>
internal sealed class ManagedInvocation : IAsyncDisposable
{
    private readonly MemoryGovernor _memory = new(256 * 1024 * 1024);
    private readonly IMemoryScope _evaluationMemory;
    private readonly IMemoryScope _inputMemory;
    private readonly BenchmarkHost _host;
    private readonly Evaluation.Evaluation? _evaluation;
    private readonly string _source;
    public ManagedInvocation(Workload workload, ParsedScript? prepared)
    {
        _source = new UTF8Encoding(false, true).GetString(workload.Script.Bytes);
        _evaluationMemory = _memory.CreateScope();
        _inputMemory = _memory.CreateScope();
        _inputMemory.Reserve(checked(workload.Input.Length + workload.FixtureBytes));
        _host = new BenchmarkHost(_memory, workload.Input.Bytes,
            workload.Files.ToDictionary(pair => pair.Key, pair => (ReadOnlyMemory<byte>)pair.Value.Bytes, StringComparer.Ordinal), null, null);
        if (prepared != null) _evaluation = new Evaluation.Evaluation(prepared);
    }
    public async Task<ObservedResult> RunAsync(CancellationToken token)
    {
        RunResult run;
        try
        {
            run = _evaluation == null
                ? await BashScript.RunAsync(_host, _source, EvaluationOptions.Default, token).ConfigureAwait(false)
                : await new Engine().RunAsync(_evaluation, _host, _evaluationMemory, token).ConfigureAwait(false);
        }
        finally { _evaluationMemory.Dispose(); }
        return new ObservedResult(run.ExitCode, _host.Stdout, _host.Stderr, _host.FileDigests, _host.Counts, _memory.PeakBytes);
    }
    public async ValueTask DisposeAsync()
    {
        _evaluationMemory.Dispose();
        await _host.DisposeAsync().ConfigureAwait(false);
        _inputMemory.Dispose();
        if (_memory.ReservedBytes != 0) throw new InvalidOperationException("Benchmark invocation retained admitted storage after cleanup.");
    }
    public static void Verify(Workload workload, ObservedResult observed)
        => Verify(workload, observed.ExitCode, observed.Stdout, observed.Stderr, observed.Files);

    public static void Verify(Workload workload, int exitCode, ContentDigest stdout, ContentDigest stderr,
        IReadOnlyDictionary<string, ContentDigest> files)
    {
        var expected = workload.RequiredExpected;
        if (exitCode != expected.ExitCode || stdout != ContentDigest.Of(expected.Stdout)
            || stderr != ContentDigest.Of(expected.Stderr))
            throw new InvalidDataException($"{workload.Id}: output/status mismatch. Expected {expected.ExitCode}/{ContentDigest.Of(expected.Stdout)}/{ContentDigest.Of(expected.Stderr)}, got {exitCode}/{stdout}/{stderr}.");
        var expectedFiles = workload.Files.ToDictionary(pair => pair.Key, pair => ContentDigest.Of(pair.Value), StringComparer.Ordinal);
        foreach (var file in expected.Files) expectedFiles[file.Key] = ContentDigest.Of(file.Value);
        if (files.Count != expectedFiles.Count || expectedFiles.Any(pair => !files.TryGetValue(pair.Key, out var actual) || actual != pair.Value))
            throw new InvalidDataException(workload.Id + ": file effects mismatch.");
    }
}

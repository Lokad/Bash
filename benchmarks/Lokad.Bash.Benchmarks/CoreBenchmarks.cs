using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Lokad.Bash.Benchmarks;

internal sealed record MicroContext(string Directory, IReadOnlyList<string> CaseIds, VmDeclaration? Vm)
{
    public const string EnvironmentKey = "LOKAD_BASH_MICRO_CONTEXT";
    public static MicroContext Load(bool timing)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentKey) ?? throw new InvalidOperationException("Missing micro context.");
        var context = JsonSerializer.Deserialize<MicroContext>(File.ReadAllBytes(path), WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Missing micro context.");
        context.Validate(timing);
        return context;
    }
    public void Validate(bool timing)
    {
        if (Environment.Version.Major is not (10 or 11)) throw new InvalidDataException("Unsupported micro runtime.");
        if (timing) VmDeclaration.RequireQuiet(Vm);
        MicroGate.Read(Directory);
        var selected = MicroCatalog.Read(Path.Combine(Directory, "micro-catalog.json")).Cases.Where(row => CaseIds.Contains(row.Id)).ToArray();
        if (selected.Length == 0 || selected.Length != CaseIds.Count || CaseIds.Distinct().Count() != CaseIds.Count)
            throw new InvalidDataException("Micro selection is empty, duplicated or unknown.");
    }
}

/// <summary>Only synchronous kernels with completed callbacks; no asynchronous allocation claim.</summary>
[MemoryDiagnoser]
public class CoreBenchmarks
{
    private MicroInvocation? _invocation;
    [ParamsSource(nameof(Cases))] public string CaseId { get; set; } = "";
    public IEnumerable<string> Cases => MicroContext.Load(false).CaseIds;
    [GlobalSetup] public void Setup() => Prepare(MicroContext.Load(true));
    internal void Prepare(MicroContext context)
    {
        var row = MicroCatalog.Read(Path.Combine(context.Directory, "micro-catalog.json")).Cases.Single(row => row.Id == CaseId);
        _invocation = new MicroInvocation(row);
        MicroInvocation.Verify(row, _invocation.Observe());
    }
    [Benchmark] public long Run() => (_invocation ?? throw new InvalidOperationException("Micro setup did not run.")).Run();
    [GlobalCleanup] public void Cleanup() { _invocation?.Dispose(); _invocation = null; }
}

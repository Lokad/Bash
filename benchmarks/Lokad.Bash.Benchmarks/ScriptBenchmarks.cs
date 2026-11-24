using BenchmarkDotNet.Attributes;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Benchmarks;

/// <summary>Synchronous parsing alone; the returned AST is consumed by BenchmarkDotNet.</summary>
[MemoryDiagnoser]
public class ParseBenchmarks
{
    private string _source = "";
    [ParamsSource(nameof(Cases))] public string CaseId { get; set; } = "";
    public IEnumerable<string> Cases => MeasurementContext.LoadMetadata().CaseIds;
    [GlobalSetup] public void Setup()
    {
        Prepare(MeasurementContext.LoadCase(CaseId).Workload);
    }
    internal void Prepare(Workload workload) => _source = workload.DecodeScript();
    [Benchmark] public ParsedScript Parse() => Parser.Parse(_source);
}

/// <summary>Each iteration owns fresh bindings/host state. Async allocation is measured separately.</summary>
public abstract class ScriptBenchmarks
{
    private Workload? _workload;
    private string _source = "";
    private ParsedScript? _prepared;
    private ManagedInvocation? _invocation;
    private ObservedResult? _observed;
    private CancellationTokenSource? _deadline;
    protected abstract bool Prepared { get; }
    [ParamsSource(nameof(Cases))] public string CaseId { get; set; } = "";
    public IEnumerable<string> Cases => MeasurementContext.LoadMetadata().CaseIds;
    [GlobalSetup] public Task Setup() => PrepareAsync(MeasurementContext.LoadCase(CaseId).Workload);
    internal async Task PrepareAsync(Workload workload)
    {
        var source = workload.DecodeScript();
        _workload = workload;
        _prepared = Prepared ? Parser.Parse(source) : null;
        _source = _prepared == null ? source : "";
        // Populate source-owned metadata outside measurement, and validate this exact setup.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var warm = new ManagedInvocation(workload, _source, _prepared);
        ManagedInvocation.Verify(_workload, await warm.RunAsync(deadline.Token));
    }
    [IterationSetup] public void BeforeInvocation()
    {
        _invocation = new ManagedInvocation(_workload ?? throw new InvalidOperationException("Setup did not run."), _source, _prepared);
        _observed = null;
        _deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    }
    [Benchmark] public async Task<int> Execute()
    {
        var invocation = _invocation ?? throw new InvalidOperationException("Invocation setup did not run.");
        var deadline = _deadline ?? throw new InvalidOperationException("Missing deadline.");
        _observed = await invocation.RunAsync(deadline.Token);
        return _observed.ExitCode;
    }
    // BenchmarkDotNet iteration cleanup is an Action; unlike global setup it does
    // not adapt Task-returning methods. Drain asynchronous disposal explicitly.
    [IterationCleanup] public void AfterInvocation()
    {
        CleanupAsync().GetAwaiter().GetResult();

        async Task CleanupAsync()
        {
            try
            {
                if (_observed != null && _workload != null) ManagedInvocation.Verify(_workload, _observed);
            }
            finally
            {
                if (_invocation != null) await _invocation.DisposeAsync().ConfigureAwait(false);
                _deadline?.Dispose();
                _invocation = null; _deadline = null;
            }
        }
    }
}
public class PreparedBenchmarks : ScriptBenchmarks { protected override bool Prepared => true; }
public class PublicBenchmarks : ScriptBenchmarks { protected override bool Prepared => false; }

/// <summary>Fresh managed apphost and direct GNU processes share the same parent byte collector.</summary>
public class ProcessBenchmarks
{
    private MeasurementContext? _context;
    private Workload? _workload;
    private ProbeInvocation? _probe;
    private NativeInvocation? _native;
    private ProcessOutput? _output;
    private CancellationTokenSource? _deadline;
    [ParamsSource(nameof(Cases))] public string CaseId { get; set; } = "";
    public IEnumerable<string> Cases => MeasurementContext.LoadMetadata().CaseIds;
    [GlobalSetup] public void Setup()
    {
        var (context, workload) = MeasurementContext.LoadCase(CaseId);
        Prepare(context, workload);
    }
    internal void Prepare(MeasurementContext context, Workload workload)
    {
        _context = context;
        _workload = workload;
    }
    [IterationSetup] public void BeforeInvocation()
    {
        var context = _context ?? throw new InvalidOperationException("Setup did not run.");
        var workload = _workload ?? throw new InvalidOperationException("Missing workload.");
        var root = Path.Combine(context.OutputDirectory, "invocations", CaseId, Guid.NewGuid().ToString("N"));
        if (context.Lane == MeasurementLane.ManagedProcess)
            _probe = new ProbeInvocation(workload, ProbeMode.Public, false,
                context.ProbeExecutable ?? throw new InvalidOperationException("Missing frozen managed process probe."), root,
                captureStderr: false);
        else _native = new NativeInvocation(workload, context.NativeExecutable ?? throw new InvalidOperationException("Missing native executable."), null, root, null);
        _output = null;
        _deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    }
    [Benchmark] public async Task<int> InvokeProcess()
    {
        var token = (_deadline ?? throw new InvalidOperationException("Missing deadline.")).Token;
        _output = _probe != null ? await _probe.RunAsync(token)
            : await (_native ?? throw new InvalidOperationException("Missing native invocation.")).RunAsync(token);
        return _output.ExitCode;
    }
    [IterationCleanup] public void AfterInvocation()
    {
        try
        {
            // BenchmarkDotNet also calls iteration cleanup for its empty overhead action.
            if (_output == null) return;
            var output = _output;
            var files = _probe != null ? _probe.ObserveResult(output).Files
                : (_native ?? throw new InvalidOperationException("Missing native invocation.")).ObserveFiles();
            ManagedInvocation.Verify(_workload ?? throw new InvalidOperationException("Missing workload."), output.ExitCode, output.Stdout, output.Stderr, files);
        }
        finally { _deadline?.Dispose(); _deadline = null; _probe = null; _native = null; }
    }
}

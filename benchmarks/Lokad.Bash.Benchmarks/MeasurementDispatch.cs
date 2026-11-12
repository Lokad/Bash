using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace Lokad.Bash.Benchmarks;

internal static class MeasurementDispatch
{
    internal sealed record SourceIdentity(string Commit, bool Dirty);
    public static async Task RunAsync(string[] args)
    {
        var timing = args[0] == "measure";
        if (args.Length < (timing ? 4 : 3))
            throw new ArgumentException("plan DIRECTORY LANE [CASE_PREFIX] [NATIVE_PATH]; measure DIRECTORY LANE VM_JSON [CASE_PREFIX] [NATIVE_PATH]");
#if DEBUG
        if (timing) throw new InvalidOperationException("Measurement requires Release.");
#endif
        if (!Enum.TryParse<MeasurementLane>(args[2].Replace("-", "", StringComparison.Ordinal), true, out var lane))
            throw new ArgumentException("Unknown measurement lane.");
        var directory = Path.GetFullPath(args[1]);
        var catalogPath = Path.Combine(directory, "catalog.json");
        var catalog = WorkloadCatalog.Read(catalogPath);
        var prefixIndex = timing ? 4 : 3;
        var prefix = args.Length > prefixIndex ? args[prefixIndex] : "";
        var native = args.Length > prefixIndex + 1 ? Path.GetFullPath(args[prefixIndex + 1]) : null;
        var selected = catalog.Cases.Where(workload => workload.SkipReason.Length == 0
            && workload.Id.StartsWith(prefix, StringComparison.Ordinal)
            && (lane is not (MeasurementLane.ManagedProcess or MeasurementLane.NativeProcess)
                || workload.ProcessComparisonExclusion.Length == 0 && workload.NativeExclusion(OperatingSystem.IsWindows() ? "gnu-msys" : "gnu-linux").Length == 0)
            && (lane != MeasurementLane.Parse || workload.Variant == WorkloadVariant.WholeInvocation)).ToArray();
        VmDeclaration? vm = timing ? JsonSerializer.Deserialize<VmDeclaration>(File.ReadAllBytes(args[3]), WorkloadCatalog.Json) : null;
        var root = Path.Combine(directory, "measurements", lane + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new MeasurementContext(directory, root, lane, selected.Select(workload => workload.Id).ToArray(), native, vm);
        context.Validate(timing);
        var contextPath = Path.Combine(root, "context.json");
        context.Write(contextPath);
        File.Copy(catalogPath, Path.Combine(root, "catalog.json"));
        foreach (var evidence in Directory.EnumerateFiles(directory, "*verification.json"))
            File.Copy(evidence, Path.Combine(root, Path.GetFileName(evidence)));
        var environment = HostEnvironmentInfo.GetInformation();
        var source = IdentifySource();
        if (timing && source.Dirty) throw new InvalidDataException("Dedicated-VM measurements require a clean source checkout.");
        var manifestPath = Path.Combine(root, "manifest.json");
        var completed = false;
        var benchmarkCount = 0;
        var lifecycleChecked = false;
        var oldContext = Environment.GetEnvironmentVariable(MeasurementContext.EnvironmentKey);
        Environment.SetEnvironmentVariable(MeasurementContext.EnvironmentKey, contextPath);
        try
        {
            var config = Configure(root, MeasurementContext.EnvironmentKey, contextPath);
            var type = lane switch
            {
                MeasurementLane.Parse => typeof(ParseBenchmarks),
                MeasurementLane.Prepared => typeof(PreparedBenchmarks),
                MeasurementLane.Public => typeof(PublicBenchmarks),
                _ => typeof(ProcessBenchmarks)
            };
            using var run = BenchmarkConverter.TypeToBenchmarks(type, config);
            benchmarkCount = ValidateDiscovery(run, selected.Length, root);
            WriteManifest();
            if (timing)
            {
                var summary = BenchmarkRunner.Run(run);
                if (summary.Reports.Length != benchmarkCount || summary.Reports.Any(report => !report.Success))
                    throw new InvalidDataException("A benchmark failed or produced partial results; do not score this run.");
                completed = true;
            }
            else
            {
                await CheckLifecycleAsync();
                lifecycleChecked = true;
                Console.WriteLine($"Planned {benchmarkCount} verified {lane} benchmark cases and checked one iteration lifecycle. No timing job started. Manifest: {manifestPath}");
            }
        }
        finally
        {
            WriteManifest();
            Environment.SetEnvironmentVariable(MeasurementContext.EnvironmentKey, oldContext);
        }

        void WriteManifest()
        {
            var document = new
            {
                schemaVersion = 1, timingsRequested = timing, timingsComplete = completed, lifecycleChecked,
                recordedAt = DateTimeOffset.UtcNow, source, environment, vm, lane, benchmarkCount,
                runtimeRole = Environment.Version.Major == 11 ? "primary" : "compatibility-control",
                catalogSha256 = Blob.Hash(File.ReadAllBytes(catalogPath)),
                harnessSha256 = Blob.Hash(File.ReadAllBytes(typeof(MeasurementDispatch).Assembly.Location)),
                interpreterSha256 = Blob.Hash(File.ReadAllBytes(typeof(BashScript).Assembly.Location)),
                independentLaunches = 3, warmup = "BenchmarkDotNet automatic", iterations = "BenchmarkDotNet automatic",
                allocationInstrumentation = lane == MeasurementLane.Parse ? "Synchronous caller-thread MemoryDiagnoser" : "Separate isolated probe pass; asynchronous methods have no caller-thread memory diagnoser",
                selected = selected.Select(workload => new { workload.Id, workload.Family, workload.Domain, workload.Variant, workload.Scale,
                    workload.Rounds, workload.WorkUnits, workload.WorkUnit, scriptSha256 = workload.Script.Sha256, workload.Locale }),
                exclusions = catalog.Cases.Where(workload => workload.SkipReason.Length > 0
                    || lane is MeasurementLane.ManagedProcess or MeasurementLane.NativeProcess
                        && (workload.ProcessComparisonExclusion.Length > 0 || workload.NativeExclusion(OperatingSystem.IsWindows() ? "gnu-msys" : "gnu-linux").Length > 0))
                    .Select(workload => new { workload.Id, reason = workload.SkipReason.Length > 0 ? workload.SkipReason
                        : workload.ProcessComparisonExclusion.Length > 0 ? workload.ProcessComparisonExclusion
                        : workload.NativeExclusion(OperatingSystem.IsWindows() ? "gnu-msys" : "gnu-linux") }),
                boundaries = new
                {
                    parse = "Parser.Parse only; source and environment setup excluded; AST consumed",
                    prepared = "Fresh evaluator over warmed immutable parsed syntax; awaited execution, digest/result observation and evaluation cleanup included; host setup/final disposal excluded",
                    @public = "Complete BashScript.RunAsync source invocation, awaited effects, digest/result observation and evaluation cleanup; host setup/final disposal excluded",
                    managedProcess = "Fresh minimal apphost through exit/output drain, including runtime startup, source/request/input loading, host setup/teardown and result metadata transport; parent uses the same digest collector as GNU",
                    nativeProcess = "Direct GNU process through exit/output drain, including startup and script-file parsing; parent file verification follows outside timing",
                    amortized = "One process/evaluation executes the declared function wrapper and rounds; startup remains included and no empty-process subtraction is used"
                }
            };
            File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(document, WorkloadCatalog.Json));
        }

        async Task CheckLifecycleAsync()
        {
            var id = context.CaseIds[0];
            if (lane == MeasurementLane.Parse)
            {
                var benchmark = new ParseBenchmarks { CaseId = id };
                benchmark.Prepare(context);
                if (benchmark.Parse().Commands.Count == 0) throw new InvalidDataException("The parse result was empty.");
            }
            else if (lane is MeasurementLane.Public or MeasurementLane.Prepared)
            {
                ScriptBenchmarks benchmark = lane == MeasurementLane.Public ? new PublicBenchmarks() : new PreparedBenchmarks();
                benchmark.CaseId = id;
                await benchmark.PrepareAsync(context);
                benchmark.BeforeInvocation();
                benchmark.AfterInvocation(); // The empty overhead iteration also owns setup/cleanup.
                benchmark.BeforeInvocation();
                try { await benchmark.Execute(); }
                finally { benchmark.AfterInvocation(); }
            }
            else
            {
                var benchmark = new ProcessBenchmarks { CaseId = id };
                benchmark.Prepare(context);
                benchmark.BeforeInvocation();
                benchmark.AfterInvocation();
                benchmark.BeforeInvocation();
                try { await benchmark.InvokeProcess(); }
                finally { benchmark.AfterInvocation(); }
            }
        }
    }

    internal static ManualConfig Configure(string root, string contextKey, string contextPath)
    {
        var role = Environment.Version.Major == 11 ? "primary" : "compatibility-control";
        var job = Job.Default.WithId($"net{Environment.Version.Major}-{role}")
            .WithRuntime(CoreRuntime.CreateForNewVersion($"net{Environment.Version.Major}.0", $".NET {Environment.Version.Major}"))
            .WithLaunchCount(3).WithGcServer(System.Runtime.GCSettings.IsServerGC)
            .WithEnvironmentVariable(contextKey, contextPath);
        return ManualConfig.Create(DefaultConfig.Instance).AddJob(job).AddValidator(CompilationValidator.FailOnError)
            .AddExporter(JsonExporter.Full).WithArtifactsPath(Path.Combine(root, "bdn"));
    }

    internal static int ValidateDiscovery(BenchmarkRunInfo run, int expectedCount, string root)
    {
        var count = run.BenchmarksCases.Length;
        if (count == 0 || count != expectedCount)
            throw new InvalidDataException("Benchmark discovery did not cover the selected cases exactly once.");
        var validation = run.Config.GetCompositeValidator().Validate(new ValidationParameters(run.BenchmarksCases, run.Config)).ToArray();
        File.WriteAllBytes(Path.Combine(root, "validation.json"), JsonSerializer.SerializeToUtf8Bytes(
            validation.Select(error => new { error.IsCritical, error.Message }), WorkloadCatalog.Json));
        if (validation.Any(error => error.IsCritical))
            throw new InvalidDataException("BenchmarkDotNet configuration validation failed: " + string.Join("; ", validation.Select(error => error.Message)));
        return count;
    }

    internal static SourceIdentity IdentifySource()
    {
        var directory = Path.GetDirectoryName(VerificationGate.FindReferenceCatalog()) ?? throw new InvalidOperationException("Missing checkout.");
        return new(Git("rev-parse", "HEAD").Trim(), Git("status", "--porcelain").Length > 0);

        string Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(error);
            return output;
        }
    }
}

using System.Text.Json;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Running;

namespace Lokad.Bash.Benchmarks;

internal static class MicroDispatch
{
    public static void Run(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("micro-prepare" or "micro-verify" or "micro-smoke" or "micro-plan" or "micro-measure"))
            throw new ArgumentException("micro-prepare|micro-verify DIRECTORY; micro-smoke DIRECTORY CASE_ID; micro-plan DIRECTORY [PREFIX]; micro-measure DIRECTORY VM_JSON [PREFIX]");
        var directory = Path.GetFullPath(args[1]);
        var catalogPath = Path.Combine(directory, "micro-catalog.json");
        if (args[0] == "micro-prepare")
        {
            EvidenceFiles.CreateDirectory(directory);
            MicroCatalog.Create().Write(catalogPath);
            Console.WriteLine("Prepared 36 core micro workloads. No timings collected.");
            return;
        }
        if (args[0] == "micro-verify")
        {
            var path = Path.Combine(directory, $"micro-net{Environment.Version.Major}-verification.json");
            var catalogBytes = File.ReadAllBytes(catalogPath);
            var report = VerificationReport.Begin(catalogBytes, VerificationTransport.MicroCore);
            using var outputFile = EvidenceFiles.Create(path);
            var results = new List<VerificationRow>();
            var complete = false;
            try
            {
                foreach (var row in MicroCatalog.Read(catalogBytes).Cases)
                {
                    try
                    {
                        using var invocation = new MicroInvocation(row);
                        var first = invocation.Observe(); MicroInvocation.Verify(row, first);
                        var repeated = invocation.Observe(); MicroInvocation.Verify(row, repeated);
                        results.Add(new(row.Id, VerificationLane.MicroCore, VerificationStatus.Passed, "", new MicroRepeat(first, repeated)));
                    }
                    catch (Exception error) { results.Add(new(row.Id, VerificationLane.MicroCore, VerificationStatus.Failed, error.Message, null)); throw; }
                }
                var result = report with { Complete = true, Results = results };
                MicroGate.Validate(result, catalogPath);
                MicroGate.CheckRejections(result, catalogPath);
                complete = true;
            }
            finally
            {
                EvidenceFiles.WriteJson(outputFile, report with
                {
                    RecordedAt = DateTimeOffset.UtcNow, Complete = complete, Results = results
                });
            }
            Console.WriteLine("Verified all 36 core cases twice, exact values/restored state, admission refund and evidence rejection controls. No timings collected.");
            return;
        }
        if (args[0] == "micro-smoke")
        {
            if (args.Length != 3) throw new ArgumentException("micro-smoke DIRECTORY CASE_ID");
            MicroGate.Read(directory);
            var row = MicroCatalog.Read(catalogPath).Cases.Single(row => row.Id == args[2]);
            var path = Path.Combine(directory, $"micro-smoke-net{Environment.Version.Major}-{row.Id}.json");
            using var outputFile = EvidenceFiles.Create(path);
            using var invocation = new MicroInvocation(row);
            var observed = invocation.Observe(); MicroInvocation.Verify(row, observed);
            EvidenceFiles.WriteJson(outputFile, new { row.Id, observed, timingsCollected = false });
            Console.WriteLine("Core single invocation verified. No timings collected.");
            return;
        }
        var timing = args[0] == "micro-measure";
#if DEBUG
        if (timing) throw new InvalidOperationException("Measurement requires Release.");
#endif
        if (timing && args.Length < 3) throw new ArgumentException("micro-measure DIRECTORY VM_JSON [PREFIX]");
        var prefixIndex = timing ? 3 : 2;
        var prefix = args.Length > prefixIndex ? args[prefixIndex] : "";
        VmDeclaration? vm = timing ? JsonSerializer.Deserialize<VmDeclaration>(File.ReadAllBytes(args[2]), WorkloadCatalog.Json) : null;
        var source = MeasurementDispatch.IdentifySource();
        if (timing && source.Dirty) throw new InvalidDataException("Dedicated-VM measurements require a clean source checkout.");
        var root = Path.Combine(directory, "measurements", "Micro-" + Guid.NewGuid().ToString("N"));
        EvidenceFiles.CreateDirectory(root);
        var catalogCopy = Path.Combine(root, "micro-catalog.json");
        File.Copy(catalogPath, catalogCopy);
        var evidence = $"micro-net{Environment.Version.Major}-verification.json";
        File.Copy(Path.Combine(directory, evidence), Path.Combine(root, evidence));
        var catalogBytesForRun = File.ReadAllBytes(catalogCopy);
        var selected = MicroCatalog.Read(catalogBytesForRun).Cases
            .Where(row => row.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var context = new MicroContext(root, selected.Select(row => row.Id).ToArray(), vm);
        context.Validate(timing);
        var contextPath = Path.Combine(root, "context.json");
        EvidenceFiles.WriteJson(contextPath, context);
        var environment = HostEnvironmentInfo.GetInformation();
        var completeTiming = false;
        var lifecycleChecked = false;
        var count = 0;
        var old = Environment.GetEnvironmentVariable(MicroContext.EnvironmentKey);
        Environment.SetEnvironmentVariable(MicroContext.EnvironmentKey, contextPath);
        try
        {
            var config = MeasurementDispatch.Configure(root, MicroContext.EnvironmentKey, contextPath);
            using var run = BenchmarkConverter.TypeToBenchmarks(typeof(CoreBenchmarks), config);
            count = MeasurementDispatch.ValidateDiscovery(run, selected.Length, root);
            if (timing)
            {
                var summary = BenchmarkRunner.Run(run);
                if (summary.Reports.Length != count || summary.Reports.Any(report => !report.Success))
                    throw new InvalidDataException("Micro measurements are partial or failed; do not score this run.");
                completeTiming = true;
            }
            else
            {
                var benchmark = new CoreBenchmarks { CaseId = selected[0].Id };
                try { benchmark.Prepare(context); benchmark.Run(); }
                finally { benchmark.Cleanup(); }
                lifecycleChecked = true;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MicroContext.EnvironmentKey, old);
            EvidenceFiles.WriteJson(Path.Combine(root, "manifest.json"), new
            {
                schemaVersion = 1, timingsRequested = timing, timingsComplete = completeTiming, lifecycleChecked,
                recordedAt = DateTimeOffset.UtcNow, source, environment, vm, benchmarkCount = count,
                runtimeRole = Environment.Version.Major == 11 ? "primary" : "compatibility-control",
                catalogSha256 = Blob.Hash(catalogBytesForRun),
                harnessSha256 = Blob.Hash(File.ReadAllBytes(typeof(MicroDispatch).Assembly.Location)),
                interpreterSha256 = Blob.Hash(File.ReadAllBytes(typeof(BashScript).Assembly.Location)),
                independentLaunches = 3, warmup = "BenchmarkDotNet automatic", iterations = "BenchmarkDotNet automatic",
                allocationInstrumentation = "Caller-thread MemoryDiagnoser; kernels assert completed callbacks and cannot suspend. Fixture construction, exact verification and serialization excluded.",
                selected, wrapper = "One kind dispatch, checksum consumption and declared loop/adapter work are included. Immutable fixtures and reusable state are prepared outside. No host effects or GNU parity ratio."
            });
        }
        if (!timing)
            Console.WriteLine($"Planned {count} verified micro cases with lifecycle/configuration checks. No timing job started. Manifest: {Path.Combine(root, "manifest.json")}");
    }
}

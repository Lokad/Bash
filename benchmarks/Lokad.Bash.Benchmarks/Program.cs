using System.Text.Json;
using Lokad.Bash.Benchmarks;
using Lokad.Bash.Syntax;

if (args.Length > 0 && args[0] == "build-check")
{
    BuildCheck.Run(args);
    return;
}
if (args.Length > 0 && args[0].StartsWith("micro-", StringComparison.Ordinal))
{
    MicroDispatch.Run(args);
    return;
}
if (args.Length > 0 && args[0] is "plan" or "measure")
{
    await MeasurementDispatch.RunAsync(args);
    return;
}
if (args.Length > 0 && args[0] == "memory-managed")
{
    await ManagedMemory.RunAsync(args);
    return;
}
if (args.Length > 0 && args[0] == "memory-native")
{
    await NativeMemory.RunAsync(args);
    return;
}
if (args.Length < 2 || args[0] is not ("prepare" or "verify-managed" or "verify-native" or "verify-process" or "verify-gates" or "smoke"))
    throw new ArgumentException("Usage: prepare|verify-managed|verify-process OUTPUT_DIRECTORY; verify-native OUTPUT_DIRECTORY BASH_PATH [WSL_DISTRIBUTION]; smoke OUTPUT_DIRECTORY CASE_ID parse|prepared|public");
var directory = Path.GetFullPath(args[1]);
var catalogPath = Path.Combine(directory, "catalog.json");
if (args[0] == "verify-gates")
{
    GateChecks.Run(directory);
    return;
}
if (args[0] == "verify-process")
{
    var path = Path.Combine(directory, $"process-net{Environment.Version.Major}-verification.json");
    var catalogBytes = File.ReadAllBytes(catalogPath);
    var report = VerificationReport.Begin(catalogBytes, VerificationTransport.ManagedProcess);
    using var outputFile = EvidenceFiles.Create(path);
    ProbeIdentity? identity = null;
    var rows = new List<VerificationRow>();
    var complete = false;
    var root = Path.Combine(directory, "process", Guid.NewGuid().ToString("N"));
    try
    {
        var processCatalog = WorkloadCatalog.Read(catalogBytes);
        EvidenceFiles.CreateDirectory(root);
        var (executable, frozenIdentity) = ProbeInvocation.Freeze(Path.Combine(root, "probe"));
        identity = frozenIdentity;
        foreach (var workload in processCatalog.Cases)
        {
            if (workload.SkipReason.Length != 0)
            {
                rows.Add(new(workload.Id, VerificationLane.ManagedProcess, VerificationStatus.Skipped, workload.SkipReason, null));
                continue;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(190));
            var invocation = new ProbeInvocation(workload, ProbeMode.Public, false, executable, Path.Combine(root, workload.Id),
                captureStderr: true);
            ProcessOutput? output = null;
            ProbeResult? probe = null;
            try
            {
                output = await invocation.RunAsync(deadline.Token);
                probe = invocation.ObserveResult(output);
                ManagedInvocation.Verify(workload, output.ExitCode, output.Stdout, output.Stderr, probe.Files);
            }
            catch (Exception error) { rows.Add(new(workload.Id, VerificationLane.ManagedProcess, VerificationStatus.Failed, error.Message, new { output, probe })); throw; }
            rows.Add(new(workload.Id, VerificationLane.ManagedProcess, VerificationStatus.Passed, "", new { output, probe }));
        }
        complete = true;
    }
    finally
    {
        EvidenceFiles.WriteJson(outputFile, report with
        {
            RecordedAt = DateTimeOffset.UtcNow, Probe = identity, Complete = complete, Results = rows
        });
    }
    Console.WriteLine($"Verified {rows.Count(row => row.Status == VerificationStatus.Passed)} fresh managed processes with {rows.Count(row => row.Status == VerificationStatus.Skipped)} labelled exclusions. No timings collected.");
    return;
}
if (args[0] == "smoke")
{
    if (args.Length != 4 || args[3] is not ("parse" or "prepared" or "public"))
        throw new ArgumentException("A smoke invocation requires one case and one managed lane: parse, prepared or public.");
    var catalogBytes = File.ReadAllBytes(catalogPath);
    var workload = WorkloadCatalog.Read(catalogBytes).Cases.Single(row => row.Id == args[2]);
    if (workload.SkipReason.Length != 0)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { workload.Id, skipped = workload.SkipReason }, WorkloadCatalog.Json));
        return;
    }
    object result;
    if (args[3] == "parse")
    {
        var script = Parser.Parse(System.Text.Encoding.UTF8.GetString(workload.Script.Bytes));
        result = new { parsed = true, commands = script.Commands.Count };
    }
    else
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var script = args[3] == "prepared" ? Parser.Parse(System.Text.Encoding.UTF8.GetString(workload.Script.Bytes)) : null;
        await using var invocation = new ManagedInvocation(workload, script);
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var observed = await invocation.RunAsync(deadline.Token);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        var collections = new[] { GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2 };
        ManagedInvocation.Verify(workload, observed);
        result = new { observed, managedAllocatedBytes = allocated,
            gcCollections = collections,
            allocationScope = "All managed threads in this otherwise idle probe process during awaited execution and evaluation cleanup; includes harness result/digest work. Setup and final host disposal are excluded. Cold smoke only; no throughput or steady-state allocation claim." };
    }
    var report = new { workload.Id, lane = args[3], singleInvocation = true, timingsCollected = false,
        catalogSha256 = Blob.Hash(catalogBytes), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        result };
    var path = Path.Combine(directory, "smoke-" + workload.Id + "-" + Guid.NewGuid().ToString("N") + ".json");
    EvidenceFiles.WriteJson(path, report);
    Console.WriteLine("Single invocation verified. No timings collected. Evidence: " + path);
    return;
}
if (args[0] == "verify-native")
{
    if (args.Length is < 3 or > 4) throw new ArgumentException("Native verification requires a Bash path and optional WSL distribution.");
    var distribution = args.Length == 4 ? args[3] : null;
    var native = distribution == null ? Path.GetFullPath(args[2]) : args[2];
    var referenceId = distribution != null || !OperatingSystem.IsWindows() ? "gnu-linux" : "gnu-msys";
    var reportPath = VerificationReport.NativePath(directory, referenceId);
    var transport = distribution == null ? VerificationTransport.NativeDirect : VerificationTransport.WslCorrectness;
    var catalogBytes = File.ReadAllBytes(catalogPath);
    var report = VerificationReport.Begin(catalogBytes, transport);
    using var outputFile = EvidenceFiles.Create(reportPath);
    ReferencePin? pin = null;
    var results = new List<VerificationRow>();
    var root = Path.Combine(directory, "native-" + referenceId, Guid.NewGuid().ToString("N"));
    var complete = false;
    try
    {
        var prepared = WorkloadCatalog.Read(catalogBytes);
        using var identification = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pin = await NativeInvocation.VerifyReferenceAsync(native, distribution,
            VerificationGate.FindReferenceCatalog(), identification.Token);
        EvidenceFiles.CreateDirectory(root);
        foreach (var workload in prepared.Cases)
        {
            if (workload.NativeExclusion(pin.Id) is { Length: > 0 } exclusion)
            {
                results.Add(new(workload.Id, VerificationLane.GnuCorrectness, VerificationStatus.Skipped, exclusion, null));
                continue;
            }
            using var deadline = new CancellationTokenSource(workload.Variant == WorkloadVariant.StartupAmortized
                ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(1));
            var invocation = new NativeInvocation(workload, native, distribution, Path.Combine(root, workload.Id), null);
            ProcessOutput? output = null;
            IReadOnlyDictionary<string, ContentDigest>? files = null;
            try
            {
                output = await invocation.RunAsync(deadline.Token);
                files = invocation.ObserveFiles();
                ManagedInvocation.Verify(workload, output.ExitCode, output.Stdout, output.Stderr, files);
            }
            catch (Exception error)
            {
                results.Add(new(workload.Id, VerificationLane.GnuCorrectness, VerificationStatus.Failed, error.Message, new { output, files }));
                throw;
            }
            results.Add(new(workload.Id, VerificationLane.GnuCorrectness, VerificationStatus.Passed, "", new { output, files }));
        }
        complete = true;
    }
    finally
    {
        EvidenceFiles.WriteJson(outputFile, report with
        {
            RecordedAt = DateTimeOffset.UtcNow, Reference = pin, Complete = complete, Results = results
        });
    }
    Console.WriteLine($"Verified {results.Count(row => row.Status == VerificationStatus.Passed)} GNU workload expectations with {results.Count(row => row.Status == VerificationStatus.Skipped)} labelled exclusions ({referenceId}). No timings collected.");
    return;
}
if (args[0] == "verify-managed")
{
    var reportPath = Path.Combine(directory, $"managed-net{Environment.Version.Major}-verification.json");
    var catalogBytes = File.ReadAllBytes(catalogPath);
    var report = VerificationReport.Begin(catalogBytes, VerificationTransport.InProcess);
    using var outputFile = EvidenceFiles.Create(reportPath);
    var results = new List<VerificationRow>();
    var complete = false;
    var hostChecks = false;
    try
    {
        await HostChecks.RunAsync();
        hostChecks = true;
        var prepared = WorkloadCatalog.Read(catalogBytes);
        foreach (var workload in prepared.Cases)
        {
            if (workload.SkipReason.Length != 0)
            {
                results.Add(new(workload.Id, VerificationLane.ManagedExclusion, VerificationStatus.Skipped, workload.SkipReason, null));
                continue;
            }
            var script = Parser.Parse(System.Text.Encoding.UTF8.GetString(workload.Script.Bytes));
            foreach (var syntax in new[] { null, script })
            {
                using var deadline = new CancellationTokenSource(workload.Variant == WorkloadVariant.StartupAmortized
                    ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(1));
                var lane = syntax == null ? VerificationLane.ManagedPublic : VerificationLane.ManagedPrepared;
                await using var invocation = new ManagedInvocation(workload, syntax);
                ObservedResult? observed = null;
                try
                {
                    observed = await invocation.RunAsync(deadline.Token);
                    ManagedInvocation.Verify(workload, observed);
                }
                catch (Exception error) { results.Add(new(workload.Id, lane, VerificationStatus.Failed, error.Message, observed)); throw; }
                results.Add(new(workload.Id, lane, VerificationStatus.Passed, "", observed));
            }
        }
        complete = true;
    }
    finally
    {
        EvidenceFiles.WriteJson(outputFile, report with
        {
            RecordedAt = DateTimeOffset.UtcNow, HostChecksPassed = hostChecks, Complete = complete, Results = results
        });
    }
    Console.WriteLine($"Verified {results.Count} managed observations, including labelled exclusions. Host ownership/backpressure controls passed. No timings collected.");
    return;
}
EvidenceFiles.CreateDirectory(directory);
var catalog = CatalogBuilder.Create();
foreach (var workload in catalog.Cases)
{
    var root = Path.Combine(directory, workload.Id);
    Directory.CreateDirectory(root);
    EvidenceFiles.WriteBytes(Path.Combine(root, "script.bash"), workload.Script.Bytes);
    EvidenceFiles.WriteBytes(Path.Combine(root, "stdin.bin"), workload.Input.Bytes);
    if (workload.Expected is { } expected)
    {
        EvidenceFiles.WriteBytes(Path.Combine(root, "stdout.expected.bin"), expected.Stdout.Bytes);
        EvidenceFiles.WriteBytes(Path.Combine(root, "stderr.expected.bin"), expected.Stderr.Bytes);
    }
    foreach (var fixture in workload.Files)
    {
        var path = Path.Combine(root, "fixtures", fixture.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
        EvidenceFiles.WriteBytes(path, fixture.Value.Bytes);
    }
}
MicroCatalog.Create().Write(Path.Combine(directory, "micro-catalog.json"));
EvidenceFiles.WriteJson(catalogPath, catalog);
Console.WriteLine($"Prepared {catalog.Cases.Count} versioned workload rows in {directory}. Timings pending.");

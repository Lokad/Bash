using System.Text.Json;
using Lokad.Bash.Benchmarks;
using Lokad.Bash.Syntax;

if (args.Length < 2 || args[0] is not ("prepare" or "verify-managed" or "verify-native" or "smoke"))
    throw new ArgumentException("Usage: prepare|verify-managed OUTPUT_DIRECTORY; verify-native OUTPUT_DIRECTORY BASH_PATH [WSL_DISTRIBUTION]; smoke OUTPUT_DIRECTORY CASE_ID parse|prepared|public");
var directory = Path.GetFullPath(args[1]);
var catalogPath = Path.Combine(directory, "catalog.json");
if (args[0] == "smoke")
{
    if (args.Length != 4 || args[3] is not ("parse" or "prepared" or "public"))
        throw new ArgumentException("A smoke invocation requires one case and one managed lane: parse, prepared or public.");
    var workload = WorkloadCatalog.Read(catalogPath).Cases.Single(row => row.Id == args[2]);
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
        await using var invocation = new ManagedInvocation(workload, script, null, null);
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
        catalogSha256 = Blob.Hash(File.ReadAllBytes(catalogPath)), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        result };
    var path = Path.Combine(directory, "smoke-" + workload.Id + "-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(report, WorkloadCatalog.Json));
    Console.WriteLine("Single invocation verified. No timings collected. Evidence: " + path);
    return;
}
if (args[0] == "verify-native")
{
    if (args.Length is < 3 or > 4) throw new ArgumentException("Native verification requires a Bash path and optional WSL distribution.");
    var distribution = args.Length == 4 ? args[3] : null;
    var native = distribution == null ? Path.GetFullPath(args[2]) : args[2];
    var referenceId = distribution != null || !OperatingSystem.IsWindows() ? "gnu-linux" : "gnu-msys";
    var reportPath = Path.Combine(directory, referenceId + "-verification.json");
    VerificationReport.Create(catalogPath, null, false, false, []).Write(reportPath);
    DirectoryInfo? repository = new DirectoryInfo(AppContext.BaseDirectory);
    while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Lokad.Bash.slnx"))) repository = repository.Parent;
    if (repository == null) throw new InvalidOperationException("Cannot locate the pinned reference catalog.");
    using var identification = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var pin = await NativeInvocation.VerifyReferenceAsync(native, distribution,
        Path.Combine(repository.FullName, "tests/Lokad.Bash.Tests/Conformance/cases.json"), identification.Token);
    var prepared = WorkloadCatalog.Read(catalogPath);
    var results = new List<VerificationRow>();
    var root = Path.Combine(directory, "native-" + pin.Id, Guid.NewGuid().ToString("N"));
    var complete = false;
    try
    {
        foreach (var workload in prepared.Cases)
        {
            if (workload.SkipReason.Length != 0)
            {
                results.Add(new(workload.Id, VerificationLane.GnuCorrectness, VerificationStatus.Skipped, workload.SkipReason, null));
                continue;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var invocation = new NativeInvocation(workload, native, distribution, Path.Combine(root, workload.Id));
            var output = await invocation.RunAsync(deadline.Token);
            var files = invocation.ObserveFiles();
            try { ManagedInvocation.Verify(workload, output.ExitCode, output.Stdout, output.Stderr, files); }
            catch (Exception error)
            {
                results.Add(new(workload.Id, VerificationLane.GnuCorrectness, VerificationStatus.Failed, error.Message, new { output, files }));
                throw;
            }
            results.Add(new(workload.Id, VerificationLane.GnuCorrectness, VerificationStatus.Passed, "", new { output, files }));
        }
        complete = true;
    }
    finally { VerificationReport.Create(catalogPath, pin, false, complete, results).Write(reportPath); }
    Console.WriteLine($"Verified {results.Count(row => row.Status == VerificationStatus.Passed)} GNU workload expectations with {results.Count(row => row.Status == VerificationStatus.Skipped)} labelled exclusions ({pin.Id}). No timings collected.");
    return;
}
if (args[0] == "verify-managed")
{
    var reportPath = Path.Combine(directory, $"managed-net{Environment.Version.Major}-verification.json");
    VerificationReport.Create(catalogPath, null, false, false, []).Write(reportPath);
    var results = new List<VerificationRow>();
    var complete = false;
    var hostChecks = false;
    try
    {
        await HostChecks.RunAsync();
        hostChecks = true;
        var prepared = WorkloadCatalog.Read(catalogPath);
        foreach (var workload in prepared.Cases)
        {
            if (workload.SkipReason.Length != 0)
            {
                results.Add(new(workload.Id, VerificationLane.ManagedExclusion, VerificationStatus.Skipped, workload.SkipReason, null));
                continue;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var script = Parser.Parse(System.Text.Encoding.UTF8.GetString(workload.Script.Bytes));
            foreach (var syntax in new[] { null, script })
            {
                var lane = syntax == null ? VerificationLane.ManagedPublic : VerificationLane.ManagedPrepared;
                await using var invocation = new ManagedInvocation(workload, syntax, null, null);
                var observed = await invocation.RunAsync(deadline.Token);
                try { ManagedInvocation.Verify(workload, observed); }
                catch (Exception error) { results.Add(new(workload.Id, lane, VerificationStatus.Failed, error.Message, observed)); throw; }
                results.Add(new(workload.Id, lane, VerificationStatus.Passed, "", observed));
            }
        }
        complete = true;
    }
    finally { VerificationReport.Create(catalogPath, null, hostChecks, complete, results).Write(reportPath); }
    Console.WriteLine($"Verified {results.Count} managed observations, including labelled exclusions. Host ownership/backpressure controls passed. No timings collected.");
    return;
}
Directory.CreateDirectory(directory);
var catalog = CatalogBuilder.Create();
File.WriteAllBytes(catalogPath, JsonSerializer.SerializeToUtf8Bytes(catalog, WorkloadCatalog.Json));
foreach (var workload in catalog.Cases)
{
    var root = Path.Combine(directory, workload.Id);
    Directory.CreateDirectory(root);
    File.WriteAllBytes(Path.Combine(root, "script.bash"), workload.Script.Bytes);
    File.WriteAllBytes(Path.Combine(root, "stdin.bin"), workload.Input.Bytes);
    if (workload.Expected is { } expected)
    {
        File.WriteAllBytes(Path.Combine(root, "stdout.expected.bin"), expected.Stdout.Bytes);
        File.WriteAllBytes(Path.Combine(root, "stderr.expected.bin"), expected.Stderr.Bytes);
    }
    foreach (var fixture in workload.Files)
    {
        var path = Path.Combine(root, "fixtures", fixture.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
        File.WriteAllBytes(path, fixture.Value.Bytes);
    }
}
Console.WriteLine($"Prepared {catalog.Cases.Count} versioned workload rows in {directory}. Timings pending.");

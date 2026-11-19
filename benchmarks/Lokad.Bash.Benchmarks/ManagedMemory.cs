using System.Runtime.InteropServices;

namespace Lokad.Bash.Benchmarks;

/// <summary>One fresh child measures all managed threads, independently of timing jobs.</summary>
internal static class ManagedMemory
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 4 || !Enum.TryParse<ProbeMode>(args[3], true, out var mode) || !Enum.IsDefined(mode))
            throw new ArgumentException("memory-managed DIRECTORY CASE_ID public|prepared");
        var directory = Path.GetFullPath(args[1]);
        var catalogPath = Path.Combine(directory, "catalog.json");
        var catalog = File.ReadAllBytes(catalogPath);
        var workload = WorkloadCatalog.Read(catalog).Cases.Single(row => row.Id == args[2]);
        if (workload.SkipReason.Length > 0) throw new InvalidDataException(workload.SkipReason);
        var root = Path.Combine(directory, "memory", $"net{Environment.Version.Major}-" + Guid.NewGuid().ToString("N"));
        EvidenceFiles.CreateDirectory(root);
        EvidenceFiles.WriteBytes(Path.Combine(root, "catalog.json"), catalog);
        var managedReport = $"managed-net{Environment.Version.Major}-verification.json";
        var processReport = $"process-net{Environment.Version.Major}-verification.json";
        File.Copy(Path.Combine(directory, managedReport), Path.Combine(root, managedReport));
        File.Copy(Path.Combine(directory, processReport), Path.Combine(root, processReport));
        VerificationGate.Read(Path.Combine(root, managedReport), catalog, VerificationTransport.InProcess, false, probeExecutable: null);
        var (executable, identity) = ProbeInvocation.Freeze(Path.Combine(root, "probe"));
        VerificationGate.Read(Path.Combine(root, processReport), catalog, VerificationTransport.ManagedProcess, false, executable);
        var path = Path.Combine(root, "result.json");
        var source = MeasurementDispatch.IdentifySource();
        ProcessOutput? output = null;
        ProbeResult? probe = null;
        var complete = false;
        try
        {
            var invocation = new ProbeInvocation(workload, mode, true, executable, Path.Combine(root, "child"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(190));
            output = await invocation.RunAsync(deadline.Token);
            probe = invocation.ObserveResult();
            if (probe.Mode != mode || probe.Allocations is not { ManagedBytes: >= 0, Gen0: >= 0, Gen1: >= 0, Gen2: >= 0 })
                throw new InvalidDataException("Probe omitted the requested allocation instrumentation.");
            ManagedInvocation.Verify(workload, output.ExitCode, output.Stdout, output.Stderr, probe.Files);
            complete = true;
        }
        finally { Write(); }
        Console.WriteLine("Isolated allocation/GC invocation verified. No timings collected. Evidence: " + path);

        void Write() => EvidenceFiles.WriteJson(path, new
        {
            schemaVersion = 1, complete, timingsCollected = false, singleColdInvocation = true,
            recordedAt = DateTimeOffset.UtcNow, workload.Id, mode, source, probeIdentity = identity,
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            catalogSha256 = Blob.Hash(catalog),
            harnessSha256 = Blob.Hash(File.ReadAllBytes(typeof(ManagedMemory).Assembly.Location)),
            output, probe,
            allocationScope = "GC.GetTotalAllocatedBytes(precise: true) across all child managed threads during awaited execution and evaluation cleanup. Source/input/host setup, prepared parsing/evaluator construction, result serialization and final host disposal are excluded. Public invocation includes its own parsing/evaluator construction. Cold single invocation; no steady-state claim.",
            memoryScope = "Estimated peak cooperative live admission includes child source/input/host/evaluation owners. Separate from allocation bytes and process RSS. Parent wrapper allocations are not observed."
        });
    }
}

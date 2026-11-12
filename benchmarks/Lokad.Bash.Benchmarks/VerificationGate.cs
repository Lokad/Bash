using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

/// <summary>Requires complete, current, exactly covered evidence and rechecks the recorded outcomes.</summary>
internal static class VerificationGate
{
    public static VerificationReport Read(string path, string catalogPath, VerificationTransport transport, bool forMeasurement)
    {
        var report = JsonSerializer.Deserialize<VerificationReport>(File.ReadAllBytes(path), WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Missing verification report.");
        Validate(report, catalogPath, transport, forMeasurement);
        return report;
    }

    public static void Validate(VerificationReport report, string catalogPath, VerificationTransport transport, bool forMeasurement)
    {
        Require(report.SchemaVersion == 2 && report.Complete, "Verification is partial or uses an unsupported schema.");
        Require(report.CatalogSha256 == Blob.Hash(File.ReadAllBytes(catalogPath)), "Catalog changed after verification.");
        Require(report.HarnessSha256 == Blob.Hash(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))
            && report.InterpreterSha256 == Blob.Hash(File.ReadAllBytes(typeof(BashScript).Assembly.Location)), "Verified binaries changed.");
        Require(report.RuntimeMajor == Environment.Version.Major && report.Runtime == RuntimeInformation.FrameworkDescription
            && report.OS == RuntimeInformation.OSDescription && report.Architecture == RuntimeInformation.ProcessArchitecture.ToString()
            && report.ServerGC == System.Runtime.GCSettings.IsServerGC && report.ProcessorCount == Environment.ProcessorCount,
            "Verification belongs to another runtime/environment.");
        Require(report.Transport == transport && Enum.IsDefined(transport), "Verification covers another transport.");
        Require(!forMeasurement || transport != VerificationTransport.WslCorrectness, "WSL is a correctness transport, excluded from measurement.");
        if (transport == VerificationTransport.InProcess)
            Require(report.HostChecksPassed && report.Reference == null && report.Probe == null, "Managed host ownership checks are missing.");
        else if (transport == VerificationTransport.ManagedProcess)
            Require(report.Reference == null && report.Probe == ProbeInvocation.Identify(ProbeInvocation.FindExecutable()), "Verified process probe changed.");
        else
        {
            var reference = report.Reference ?? throw new InvalidDataException("Native reference identity is missing.");
            Require(report.Probe == null, "Native evidence contains a managed probe identity.");
            using var pins = JsonDocument.Parse(File.ReadAllBytes(FindReferenceCatalog()));
            Require(pins.RootElement.GetProperty("references").EnumerateArray().Any(pin =>
                pin.GetProperty("id").GetString() == reference.Id
                && pin.GetProperty("version").GetString() == reference.Version
                && pin.GetProperty("sha256").GetString() == reference.Sha256), "Native reference is not pinned.");
        }
        var catalog = WorkloadCatalog.Read(catalogPath);
        var expected = new Dictionary<(string Id, VerificationLane Lane), Workload>();
        foreach (var workload in catalog.Cases)
        {
            var lanes = transport switch
            {
                VerificationTransport.InProcess => workload.SkipReason.Length == 0
                    ? new[] { VerificationLane.ManagedPublic, VerificationLane.ManagedPrepared } : [VerificationLane.ManagedExclusion],
                VerificationTransport.ManagedProcess => [VerificationLane.ManagedProcess],
                _ => new[] { VerificationLane.GnuCorrectness }
            };
            foreach (var lane in lanes) expected.Add((workload.Id, lane), workload);
        }
        Require(report.Results.Count == expected.Count, "Verification coverage has missing or extra rows.");
        foreach (var row in report.Results)
        {
            Require(expected.Remove((row.Id, row.Lane), out var workload), "Verification contains a duplicate or unknown row.");
            if (workload == null) throw new InvalidDataException("Missing workload.");
            if (workload.SkipReason.Length > 0)
            {
                Require(row.Status == VerificationStatus.Skipped && row.Reason == workload.SkipReason && row.Observation == null,
                    "Excluded rows need their declared reason and no invented observation.");
                continue;
            }
            Require(row.Status == VerificationStatus.Passed && row.Reason.Length == 0 && row.Observation != null,
                "Runnable rows cannot fail, skip or omit their observed result.");
            var observation = row.Observation is JsonElement element ? element : JsonSerializer.SerializeToElement(row.Observation, WorkloadCatalog.Json);
            if (transport == VerificationTransport.InProcess)
            {
                var observed = observation.Deserialize<ObservedResult>(WorkloadCatalog.Json) ?? throw new InvalidDataException("Missing managed observation.");
                ManagedInvocation.Verify(workload, observed);
            }
            else
            {
                var output = observation.GetProperty("output").Deserialize<ProcessOutput>(WorkloadCatalog.Json)
                    ?? throw new InvalidDataException("Missing process output.");
                IReadOnlyDictionary<string, ContentDigest> files;
                if (transport == VerificationTransport.ManagedProcess)
                {
                    var probe = observation.GetProperty("probe").Deserialize<ProbeResult>(WorkloadCatalog.Json)
                        ?? throw new InvalidDataException("Missing probe observation.");
                    Require(probe.RuntimeMajor == Environment.Version.Major && probe.Mode == ProbeMode.Public && probe.Allocations == null,
                        "Process correctness used another runtime, execution or instrumentation lane.");
                    files = probe.Files;
                }
                else files = observation.GetProperty("files").Deserialize<Dictionary<string, ContentDigest>>(WorkloadCatalog.Json)
                    ?? throw new InvalidDataException("Missing native file effects.");
                ManagedInvocation.Verify(workload, output.ExitCode, output.Stdout, output.Stderr, files);
            }
        }
        Require(expected.Count == 0, "Verification is incomplete.");
    }

    internal static string FindReferenceCatalog()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Lokad.Bash.slnx"))) directory = directory.Parent;
        return directory == null ? throw new InvalidOperationException("Cannot locate reference catalog.")
            : Path.Combine(directory.FullName, "tests/Lokad.Bash.Tests/Conformance/cases.json");
    }
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }
}

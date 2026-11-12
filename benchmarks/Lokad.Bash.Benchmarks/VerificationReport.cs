using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal enum VerificationStatus { Passed, Skipped, Failed }
internal enum VerificationLane { ManagedPublic, ManagedPrepared, ManagedExclusion, GnuCorrectness, ManagedProcess }
internal enum VerificationTransport { InProcess, NativeDirect, WslCorrectness, ManagedProcess }
internal sealed record VerificationRow(string Id, VerificationLane Lane, VerificationStatus Status, string Reason, object? Observation);

internal sealed record VerificationReport(int SchemaVersion, string CatalogSha256, string HarnessSha256,
    string InterpreterSha256, string Runtime, int RuntimeMajor, string OS, string Architecture,
    int ProcessorCount, bool ServerGC, DateTimeOffset RecordedAt, ReferencePin? Reference, ProbeIdentity? Probe,
    VerificationTransport Transport,
    bool HostChecksPassed, bool Complete, IReadOnlyList<VerificationRow> Results)
{
    public static string NativePath(string directory, string referenceId) =>
        Path.Combine(directory, $"{referenceId}-net{Environment.Version.Major}-verification.json");

    public static VerificationReport Create(string catalog, ReferencePin? reference, ProbeIdentity? probe,
        VerificationTransport transport, bool hostChecks, bool complete,
        IReadOnlyList<VerificationRow> results) => new(2, Blob.Hash(File.ReadAllBytes(catalog)),
            Blob.Hash(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location)),
            Blob.Hash(File.ReadAllBytes(typeof(BashScript).Assembly.Location)),
            RuntimeInformation.FrameworkDescription, Environment.Version.Major, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC, DateTimeOffset.UtcNow, reference, probe, transport, hostChecks, complete, results);

    public void Write(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, WorkloadCatalog.Json));
}

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal enum VerificationStatus { Passed, Skipped, Failed }
internal enum VerificationLane { ManagedPublic, ManagedPrepared, ManagedExclusion, GnuCorrectness }
internal sealed record VerificationRow(string Id, VerificationLane Lane, VerificationStatus Status, string Reason, object? Observation);

internal sealed record VerificationReport(int SchemaVersion, string CatalogSha256, string HarnessSha256,
    string InterpreterSha256, string Runtime, int RuntimeMajor, string OS, string Architecture,
    int ProcessorCount, bool ServerGC, DateTimeOffset RecordedAt, ReferencePin? Reference,
    bool HostChecksPassed, bool Complete, IReadOnlyList<VerificationRow> Results)
{
    public static VerificationReport Create(string catalog, ReferencePin? reference, bool hostChecks, bool complete,
        IReadOnlyList<VerificationRow> results) => new(1, Blob.Hash(File.ReadAllBytes(catalog)),
            Blob.Hash(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location)),
            Blob.Hash(File.ReadAllBytes(typeof(BashScript).Assembly.Location)),
            RuntimeInformation.FrameworkDescription, Environment.Version.Major, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC, DateTimeOffset.UtcNow, reference, hostChecks, complete, results);

    public void Write(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, WorkloadCatalog.Json));
}

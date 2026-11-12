using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal sealed record MicroRepeat(MicroObserved First, MicroObserved Repeated);
internal static class MicroGate
{
    public static void Read(string directory)
    {
        var path = Path.Combine(directory, $"micro-net{Environment.Version.Major}-verification.json");
        var report = JsonSerializer.Deserialize<VerificationReport>(File.ReadAllBytes(path), WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Missing micro verification evidence.");
        Validate(report, Path.Combine(directory, "micro-catalog.json"));
    }
    public static void Validate(VerificationReport report, string catalogPath)
    {
        VerificationGate.ValidateIdentity(report, catalogPath, VerificationTransport.MicroCore, false);
        if (report.Reference != null || report.Probe != null || report.HostChecksPassed)
            throw new InvalidDataException("Core micro evidence must not claim host or native qualification.");
        var expected = MicroCatalog.Read(catalogPath).Cases.ToDictionary(row => row.Id, StringComparer.Ordinal);
        if (report.Results.Count != expected.Count) throw new InvalidDataException("Incomplete micro coverage.");
        foreach (var row in report.Results)
        {
            if (!expected.Remove(row.Id, out var workload) || row.Lane != VerificationLane.MicroCore
                || row.Status != VerificationStatus.Passed || row.Reason.Length != 0 || row.Observation == null)
                throw new InvalidDataException("Missing, duplicate, failed or skipped core micro case.");
            var element = row.Observation is JsonElement value ? value : JsonSerializer.SerializeToElement(row.Observation, WorkloadCatalog.Json);
            var observed = element.Deserialize<MicroRepeat>(WorkloadCatalog.Json) ?? throw new InvalidDataException("Missing micro observations.");
            MicroInvocation.Verify(workload, observed.First);
            MicroInvocation.Verify(workload, observed.Repeated);
        }
    }
    public static void CheckRejections(VerificationReport report, string catalogPath)
    {
        Rejected(report with { Complete = false });
        Rejected(report with { HarnessSha256 = "changed" });
        Rejected(report with { CatalogSha256 = "changed" });
        Rejected(report with { RuntimeMajor = 0 });
        Rejected(report with { Results = report.Results.Skip(1).ToArray() });
        var duplicate = report.Results.ToArray(); duplicate[1] = duplicate[0];
        Rejected(report with { Results = duplicate });
        var skipped = report.Results.ToArray(); skipped[0] = skipped[0] with { Status = VerificationStatus.Skipped };
        Rejected(report with { Results = skipped });
        var wrong = report.Results.ToArray();
        var observation = JsonSerializer.SerializeToElement(wrong[0].Observation, WorkloadCatalog.Json).Deserialize<MicroRepeat>(WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Missing control observation.");
        wrong[0] = wrong[0] with { Observation = observation with { Repeated = observation.Repeated with { Checksum = long.MinValue } } };
        Rejected(report with { Results = wrong });
        foreach (var declaration in new VmDeclaration?[] { null, new(false, "machine", "isolated", "idle", "fixed"), new(true, "", "isolated", "idle", "fixed") })
        {
            var rejected = false;
            try { VmDeclaration.RequireQuiet(declaration); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Missing or incomplete quiet-VM declaration was accepted.");
        }

        void Rejected(VerificationReport invalid)
        {
            try { Validate(invalid, catalogPath); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid micro evidence was accepted.");
        }
    }
}

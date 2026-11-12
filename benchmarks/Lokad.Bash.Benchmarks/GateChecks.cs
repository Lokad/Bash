using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal static class GateChecks
{
    public static void Run(string directory)
    {
        var catalog = Path.Combine(directory, "catalog.json");
        var report = VerificationGate.Read(Path.Combine(directory, $"managed-net{Environment.Version.Major}-verification.json"),
            catalog, VerificationTransport.InProcess, false);
        Rejected(report with { Complete = false });
        Rejected(report with { CatalogSha256 = "changed" });
        Rejected(report with { HarnessSha256 = "changed" });
        Rejected(report with { InterpreterSha256 = "changed" });
        Rejected(report with { HostChecksPassed = false });
        Rejected(report with { RuntimeMajor = 0 });
        Rejected(report with { ProcessorCount = 0 });
        Rejected(report with { Results = report.Results.Skip(1).ToArray() });
        var duplicate = report.Results.ToArray(); duplicate[1] = duplicate[0];
        Rejected(report with { Results = duplicate });
        var skipped = report.Results.ToArray(); skipped[0] = skipped[0] with { Status = VerificationStatus.Skipped, Reason = "not qualified", Observation = null };
        Rejected(report with { Results = skipped });
        var malformed = report.Results.ToArray(); malformed[0] = malformed[0] with
        {
            Observation = JsonSerializer.SerializeToElement(new ObservedResult(0, new ContentDigest(0, "changed"),
                new ContentDigest(0, "changed"), new Dictionary<string, ContentDigest>(), new HostCounts(0, 0, 0, 0, 0, 0), 0), WorkloadCatalog.Json)
        };
        Rejected(report with { Results = malformed });
        var processPath = Path.Combine(directory, $"process-net{Environment.Version.Major}-verification.json");
        if (File.Exists(processPath))
        {
            var process = VerificationGate.Read(processPath, catalog, VerificationTransport.ManagedProcess, false);
            var rejected = false;
            try { VerificationGate.Validate(process with { Probe = null }, catalog, VerificationTransport.ManagedProcess, false); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Unidentified probe was accepted.");
        }
        try { new MeasurementContext(directory, directory, MeasurementLane.Public, [], null, null).Validate(true); }
        catch (InvalidDataException) { Console.WriteLine("Verification gates reject stale or partial evidence, mismatched outcomes and timing without a quiet VM declaration."); return; }
        throw new InvalidOperationException("Timing without a quiet VM declaration was accepted.");

        void Rejected(VerificationReport invalid)
        {
            try { VerificationGate.Validate(invalid, catalog, VerificationTransport.InProcess, false); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid verification evidence was accepted.");
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokad.Bash.Benchmarks;

internal enum MeasurementLane { Parse, Prepared, Public, ManagedProcess, NativeProcess }
internal sealed record VmDeclaration(bool QuietDedicated, string MachineLabel, string Isolation, string BackgroundLoad, string PowerPolicy)
{
    public static void RequireQuiet(VmDeclaration? declaration)
    {
        if (declaration is not { QuietDedicated: true } vm || string.IsNullOrWhiteSpace(vm.MachineLabel)
            || string.IsNullOrWhiteSpace(vm.Isolation) || string.IsNullOrWhiteSpace(vm.BackgroundLoad) || string.IsNullOrWhiteSpace(vm.PowerPolicy))
            throw new InvalidDataException("Comparative timing requires the supplied quiet dedicated VM and its isolation/load/power declaration.");
    }
}

internal sealed record MeasurementContext(string CatalogDirectory, string OutputDirectory, MeasurementLane Lane,
    IReadOnlyList<string> CaseIds, string? NativeExecutable, VmDeclaration? Vm)
{
    public const string EnvironmentKey = "LOKAD_BASH_BENCH_CONTEXT";
    public string CatalogPath => Path.Combine(CatalogDirectory, "catalog.json");
    [JsonIgnore] public WorkloadCatalog Catalog => WorkloadCatalog.Read(CatalogPath);
    public Workload Case(string id) => Catalog.Cases.Single(row => row.Id == id && CaseIds.Contains(id));
    public static MeasurementContext Load(bool timing)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentKey) ?? throw new InvalidOperationException("Missing measurement context.");
        var context = JsonSerializer.Deserialize<MeasurementContext>(File.ReadAllBytes(path), WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Missing measurement context.");
        context.Validate(timing);
        return context;
    }
    public void Validate(bool timing)
    {
        if (Environment.Version.Major is not (10 or 11) || !Enum.IsDefined(Lane)) throw new InvalidDataException("Unsupported benchmark runtime or lane.");
        if (timing) VmDeclaration.RequireQuiet(Vm);
        VerificationGate.Read(Path.Combine(CatalogDirectory, $"managed-net{Environment.Version.Major}-verification.json"), CatalogPath, VerificationTransport.InProcess, false);
        var selected = Catalog.Cases.Where(workload => CaseIds.Contains(workload.Id)).ToArray();
        if (selected.Length != CaseIds.Count || selected.Length == 0 || CaseIds.Distinct(StringComparer.Ordinal).Count() != CaseIds.Count
            || selected.Any(workload => workload.SkipReason.Length > 0
                || Lane is MeasurementLane.ManagedProcess or MeasurementLane.NativeProcess && workload.ProcessComparisonExclusion.Length > 0))
            throw new InvalidDataException("Measurement selection contains missing, repeated or excluded cases.");
        var nativeReports = new List<VerificationReport>();
        foreach (var id in new[] { "gnu-msys", "gnu-linux" })
        {
            var path = VerificationReport.NativePath(CatalogDirectory, id);
            if (!File.Exists(path)) continue;
            var report = JsonSerializer.Deserialize<VerificationReport>(File.ReadAllBytes(path), WorkloadCatalog.Json)
                ?? throw new InvalidDataException("Missing GNU correctness evidence.");
            if (report.Transport is not (VerificationTransport.NativeDirect or VerificationTransport.WslCorrectness) || report.Reference?.Id != id)
                throw new InvalidDataException("Wrong native evidence identity or transport.");
            VerificationGate.Validate(report, CatalogPath, report.Transport, false);
            nativeReports.Add(report);
        }
        if (Lane is MeasurementLane.ManagedProcess or MeasurementLane.NativeProcess)
        {
            VerificationGate.Read(Path.Combine(CatalogDirectory, $"process-net{Environment.Version.Major}-verification.json"), CatalogPath, VerificationTransport.ManagedProcess, false);
            nativeReports.RemoveAll(report => report.Transport != VerificationTransport.NativeDirect);
            if (nativeReports.Count == 0)
                throw new InvalidDataException("Whole-process comparison needs direct native qualification; WSL startup is excluded.");
        }
        if (Lane == MeasurementLane.NativeProcess)
        {
            if (NativeExecutable == null) throw new InvalidDataException("Missing native executable.");
            var hash = Blob.Hash(File.ReadAllBytes(NativeExecutable));
            nativeReports.RemoveAll(report => report.Reference?.Sha256 != hash);
            if (nativeReports.Count == 0)
                throw new InvalidDataException("Measured native executable differs from the pinned verified binary.");
        }
        if (selected.Any(workload => !nativeReports.Any(report => workload.NativeExclusion(
                (report.Reference ?? throw new InvalidDataException("Missing reference.")).Id).Length == 0)))
            throw new InvalidDataException("Selected cases lack native equivalence evidence for their locale/reference; UTF-8 rows need pinned GNU Linux C.utf8.");
    }

    public void Write(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, WorkloadCatalog.Json));
}

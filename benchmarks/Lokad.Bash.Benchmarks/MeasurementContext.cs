using System.Text.Json;

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
    IReadOnlyList<string> CaseIds, string? NativeExecutable, string? ProbeExecutable, VmDeclaration? Vm)
{
    public const string EnvironmentKey = "LOKAD_BASH_BENCH_CONTEXT";
    public string CatalogPath => Path.Combine(CatalogDirectory, "catalog.json");

    public static (MeasurementContext Context, Workload Workload) LoadCase(string id)
    {
        var context = ReadContext();
        var selected = context.Validate(timing: true, snapshot: null);
        return (context, selected.Single(row => row.Id == id));
    }
    public static MeasurementContext LoadMetadata()
    {
        var context = ReadContext();
        _ = context.SelectedCases(WorkloadCatalog.Read(context.CatalogPath).Cases);
        return context;
    }

    private static MeasurementContext ReadContext()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentKey) ?? throw new InvalidOperationException("Missing measurement context.");
        return JsonSerializer.Deserialize<MeasurementContext>(File.ReadAllBytes(path), WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Missing measurement context.");
    }
    public IReadOnlyList<Workload> Validate(bool timing, WorkloadCatalogSnapshot? snapshot)
    {
        if (timing) VmDeclaration.RequireQuiet(Vm);
        var catalog = snapshot ?? new WorkloadCatalogSnapshot(File.ReadAllBytes(CatalogPath));
        var selected = SelectedCases(catalog.Cases);
        VerificationGate.Read(Path.Combine(CatalogDirectory, $"managed-net{Environment.Version.Major}-verification.json"),
            catalog, VerificationTransport.InProcess, probeExecutable: null);
        var nativeReports = new List<VerificationReport>();
        foreach (var id in new[] { "gnu-msys", "gnu-linux" })
        {
            var path = VerificationReport.NativePath(CatalogDirectory, id);
            if (!File.Exists(path)) continue;
            var report = JsonSerializer.Deserialize<VerificationReport>(File.ReadAllBytes(path), WorkloadCatalog.Json)
                ?? throw new InvalidDataException("Missing GNU correctness evidence.");
            if (report.Transport is not (VerificationTransport.NativeDirect or VerificationTransport.WslCorrectness) || report.Reference?.Id != id)
                throw new InvalidDataException("Wrong native evidence identity or transport.");
            VerificationGate.Validate(report, catalog, report.Transport, probeExecutable: null,
                Path.Combine(CatalogDirectory, "reference-catalog.json"));
            nativeReports.Add(report);
        }
        if (Lane is MeasurementLane.ManagedProcess or MeasurementLane.NativeProcess)
        {
            var executable = ProbeExecutable ?? throw new InvalidDataException("Missing frozen managed process probe.");
            VerificationGate.Read(Path.Combine(CatalogDirectory, $"process-net{Environment.Version.Major}-verification.json"),
                catalog, VerificationTransport.ManagedProcess, executable);
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
        return selected;
    }

    internal IReadOnlyList<Workload> SelectedCases(IReadOnlyList<Workload> cases)
    {
        if (Environment.Version.Major is not (10 or 11) || !Enum.IsDefined(Lane)) throw new InvalidDataException("Unsupported benchmark runtime or lane.");
        var ids = CaseIds.ToHashSet(StringComparer.Ordinal);
        var selected = cases.Where(workload => ids.Contains(workload.Id)).ToArray();
        if (selected.Length != ids.Count || ids.Count == 0 || ids.Count != CaseIds.Count
            || selected.Any(workload => workload.SkipReason.Length > 0
                || Lane is MeasurementLane.ManagedProcess or MeasurementLane.NativeProcess && workload.ProcessComparisonExclusion.Length > 0))
            throw new InvalidDataException("Measurement selection contains missing, repeated or excluded cases.");
        return selected;
    }

    public void Write(string path) => EvidenceFiles.WriteJson(path, this);
}

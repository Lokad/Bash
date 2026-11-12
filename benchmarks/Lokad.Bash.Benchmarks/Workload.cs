using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokad.Bash.Benchmarks;

internal enum WorkloadDomain { Language, ByteIo, FileWorkflow }
internal enum WorkloadVariant { WholeInvocation, StartupAmortized }

internal sealed record Blob(byte[] Bytes, string Sha256)
{
    public long Length => Bytes.LongLength;
    public static Blob FromBytes(byte[] bytes) => new(bytes, Hash(bytes));
    public static Blob FromText(string text) => FromBytes(Encoding.UTF8.GetBytes(text));
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Validate()
    {
        if (Sha256 != Hash(Bytes)) throw new InvalidDataException("Workload blob hash mismatch.");
    }
}

internal sealed record ExpectedResult(int ExitCode, Blob Stdout, Blob Stderr, IReadOnlyDictionary<string, Blob> Files);

internal sealed record Workload(
    string Id, string Family, WorkloadDomain Domain, int Scale, int Seed,
    long WorkUnits, string WorkUnit, Blob Script, Blob Input,
    IReadOnlyDictionary<string, Blob> Files, ExpectedResult? Expected,
    string AmortizationExclusion, string SkipReason)
{
    public WorkloadVariant Variant { get; init; } = WorkloadVariant.WholeInvocation;
    public int Rounds { get; init; } = 1;
    public long FixtureBytes => Files.Values.Sum(file => file.Length);
    public string Locale => "C";
    public bool CanAmortize => AmortizationExclusion.Length == 0;
    public string ProcessComparisonExclusion => Domain == WorkloadDomain.Language || Family is "format-output" or "raw-output"
        ? "" : "Managed input/pipe/file workflows use the declared in-memory host; native OS input/pipes/files have a different boundary. Qualify separately without a parity ratio.";
    [JsonIgnore]
    public ExpectedResult RequiredExpected => Expected ?? throw new InvalidOperationException("An excluded workload has no expected execution result.");

    public void Validate()
    {
        if (string.IsNullOrEmpty(Id) || Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))
            || Scale <= 0 || WorkUnits < 0 || WorkUnit.Length == 0 || !Enum.IsDefined(Domain)
            || !Enum.IsDefined(Variant) || Rounds < 1 || Variant == WorkloadVariant.WholeInvocation && Rounds != 1)
            throw new InvalidDataException("Invalid workload identity or counts.");
        if ((SkipReason.Length == 0) != (Expected != null))
            throw new InvalidDataException("Runnable workloads need expectations; excluded workloads must not invent them.");
        Script.Validate(); Input.Validate();
        Expected?.Stdout.Validate(); Expected?.Stderr.Validate();
        foreach (var file in Files.Concat(Expected?.Files ?? new Dictionary<string, Blob>()))
        {
            if (file.Key.Length == 0 || file.Key[0] == '/' || file.Key.Contains('\\')
                || file.Key.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Fixture paths must remain relative to their workload.");
            file.Value.Validate();
        }
        if (Script.Bytes.Contains((byte)'\r')) throw new InvalidDataException("Workload source must use LF only.");
    }
}

internal sealed record WorkloadCatalog(int SchemaVersion, int Seed, IReadOnlyDictionary<string, int> AmortizationRounds,
    IReadOnlyList<string> BashOptions, IReadOnlyList<Workload> Cases)
{
    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Validate()
    {
        if (SchemaVersion != 2 || AmortizationRounds.Count == 0 || AmortizationRounds.Values.Any(rounds => rounds < 2)
            || !BashOptions.SequenceEqual(new[] { "--noprofile", "--norc", "-o", "pipefail" }))
            throw new InvalidDataException("Unsupported workload schema or options.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workload in Cases)
        {
            workload.Validate();
            if (!ids.Add(workload.Id)) throw new InvalidDataException("Repeated workload identifier.");
        }
    }

    public static WorkloadCatalog Read(string path)
    {
        var catalog = JsonSerializer.Deserialize<WorkloadCatalog>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Missing workload catalog.");
        catalog.Validate();
        return catalog;
    }
}

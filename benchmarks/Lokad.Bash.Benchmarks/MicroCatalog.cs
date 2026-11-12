using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokad.Bash.Benchmarks;

internal enum MicroKind
{
    Literal, Parameter, VariableLookup, PositionalFrame, Shift, ArithmeticValue,
    ArithmeticPrepared, GlobUtf8, GlobBytes, GlobAdverse, SparseWindow, OperationDispatch
}
internal sealed record MicroFixture(int Scalars, int CallerArguments, int CalleeArguments, int ArrayEntries, int Stride);
internal sealed record MicroWorkload(string Id, MicroKind Kind, int Scale, int Seed, long Operations,
    string WorkUnit, string Boundary, Blob Source, Blob Input, Blob Pattern, Blob Fixture,
    long ExpectedChecksum, Blob ExpectedSample)
{
    public string CharacterPolicy => "Ordinal UTF-8 scalars; malformed operands use byte fallback. No ambient locale or filesystem.";
    public string Effects => "None: synthetic completed operation callbacks perform no host I/O.";
    [JsonIgnore] public MicroFixture Layout => JsonSerializer.Deserialize<MicroFixture>(Fixture.Bytes, WorkloadCatalog.Json)
        ?? throw new InvalidDataException("Missing micro fixture layout.");
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Scale is not (16 or 64 or 256) || Seed != 1741 || Operations <= 0
            || Id != $"{Kind}-{Scale}" || WorkUnit.Length == 0 || Boundary.Length == 0)
            throw new InvalidDataException("Unsupported micro workload.");
        foreach (var blob in new[] { Source, Input, Pattern, Fixture, ExpectedSample }) blob.Validate();
        var layout = Layout;
        var expected = new MicroFixture(Kind == MicroKind.VariableLookup ? Scale * 16 : 0,
            Kind is MicroKind.PositionalFrame or MicroKind.Shift ? Scale * 16 : 0,
            Kind == MicroKind.PositionalFrame ? 3 : Kind == MicroKind.Shift ? Scale : 0,
            Kind == MicroKind.SparseWindow ? Scale * 64 : 0, 1024);
        if (layout != expected || Operations != (Kind is MicroKind.GlobUtf8 or MicroKind.GlobBytes or MicroKind.GlobAdverse ? 1 : Scale))
            throw new InvalidDataException("Invalid micro fixture geometry.");
    }
}
internal sealed record MicroCatalog(int SchemaVersion, IReadOnlyList<MicroWorkload> Cases)
{
    public void Write(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, WorkloadCatalog.Json));
    public static MicroCatalog Read(string path)
    {
        var catalog = JsonSerializer.Deserialize<MicroCatalog>(File.ReadAllBytes(path), WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Missing micro catalog.");
        if (catalog.SchemaVersion != 1 || catalog.Cases.Count != 36 || catalog.Cases.Select(row => row.Id).Distinct().Count() != 36)
            throw new InvalidDataException("Incomplete or unsupported micro catalog.");
        foreach (var row in catalog.Cases) row.Validate();
        return catalog;
    }
    public static MicroCatalog Create()
    {
        var rows = new List<MicroWorkload>();
        foreach (var n in new[] { 16, 64, 256 })
        foreach (var kind in Enum.GetValues<MicroKind>())
        {
            var source = "";
            var input = Blob.FromText("é🙂");
            var pattern = Blob.FromText("");
            var operations = n;
            var units = "completed core calls";
            var checksum = 6L * n;
            var sample = input;
            var layout = new MicroFixture(0, 0, 0, 0, 1024);
            string boundary;
            switch (kind)
            {
                case MicroKind.Literal:
                case MicroKind.Parameter:
                    source = kind == MicroKind.Literal ? "'é🙂'" : "\"${value}\"";
                    boundary = "WordExpansion.ExpandToWordsAsync plus one command-workspace lifetime per call; prepared word and UTF-8 binding excluded; exact bytes checked separately.";
                    break;
                case MicroKind.VariableLookup:
                    layout = layout with { Scalars = n * 16 };
                    boundary = "TryGetScalar of the last prepared binding, including nameref resolution; store construction and value encoding excluded.";
                    break;
                case MicroKind.PositionalFrame:
                    layout = layout with { CallerArguments = n * 16, CalleeArguments = 3 };
                    checksum = 3L * n;
                    boundary = "Enter a three-argument positional frame, read its count and restore a prepared scale*16 caller; callee frame installation included.";
                    break;
                case MicroKind.Shift:
                    layout = layout with { CallerArguments = n * 16, CalleeArguments = n };
                    checksum = n * (n + 1L) / 2;
                    units = "single-argument shifts";
                    boundary = "One scale-argument frame installation, scale count/read/shift steps, and caller restoration per invocation; argument bytes prepared outside.";
                    break;
                case MicroKind.ArithmeticValue:
                case MicroKind.ArithmeticPrepared:
                    source = string.Join('+', Enumerable.Repeat("value", n));
                    input = Blob.FromText("7");
                    checksum = 7L * n;
                    sample = Blob.FromText(checksum.ToString(CultureInfo.InvariantCulture));
                    units = "arithmetic binding terms in one expression";
                    boundary = "One existing arithmetic evaluator call over declared terms with immediate constant-7 bindings and no writes; operand construction excluded. "
                        + (kind == MicroKind.ArithmeticPrepared ? "Source-owned lexical metadata is prepared outside." : "Value operand has no prepared lexical metadata.");
                    break;
                case MicroKind.GlobUtf8:
                    input = Blob.FromText(string.Concat(Enumerable.Repeat("é🙂", n)));
                    pattern = Blob.FromText("*🙂");
                    operations = 1;
                    checksum = input.Length;
                    sample = Blob.FromText($"Match:{input.Length}");
                    units = "full UTF-8 glob match over scale*2 scalars";
                    boundary = "MatchUtf8 includes profile validation and full matching; input/pattern encoding excluded; no host or shell expansion.";
                    break;
                case MicroKind.GlobBytes:
                    input = Blob.FromBytes(Enumerable.Repeat((byte)0xff, n).Append((byte)'z').ToArray());
                    pattern = Blob.FromText("*?z");
                    operations = 1;
                    checksum = input.Length;
                    sample = Blob.FromText($"Match:{input.Length}");
                    units = "full malformed-byte glob match";
                    boundary = "MatchUtf8 includes malformed-profile selection and byte matching; source buffers prepared outside.";
                    break;
                case MicroKind.GlobAdverse:
                    input = Blob.FromText(new string('a', n * 128));
                    pattern = Blob.FromText("a*b");
                    operations = 1;
                    checksum = -1;
                    sample = Blob.FromText("NoMatch:-1");
                    units = "adverse full no-match over scale*128 bytes";
                    boundary = "MatchUtf8 includes profile validation and adverse wildcard scan; source buffers prepared outside.";
                    break;
                case MicroKind.SparseWindow:
                    layout = layout with { ArrayEntries = n * 64 };
                    units = "late one-element indexed windows";
                    boundary = "ValuesFrom seeks and enumerates a one-element window at the final sparse position; scale*64 entries with stride 1024 prepared outside.";
                    break;
                case MicroKind.OperationDispatch:
                    input = Blob.FromText("1");
                    sample = input;
                    checksum = n;
                    units = "immediately completed RunCommand operations";
                    boundary = "RunCommand.AsTask allocates and publishes to an immediate synthetic completion callback, then consumes Completed(1); invocation setup and all host I/O excluded.";
                    break;
                default: throw new InvalidOperationException("Unknown micro kind.");
            }
            rows.Add(new($"{kind}-{n}", kind, n, 1741, operations, units, boundary, Blob.FromText(source), input, pattern,
                Blob.FromBytes(JsonSerializer.SerializeToUtf8Bytes(layout, WorkloadCatalog.Json)), checksum, sample));
        }
        return new(1, rows);
    }
}

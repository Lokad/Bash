using System.Text;
using System.Text.Json;
using Lokad.Bash;
using Lokad.Bash.Benchmarks;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

if (args.Length != 3) throw new ArgumentException("Probe requires source, request and result paths.");
var request = JsonSerializer.Deserialize<ProbeRequest>(File.ReadAllBytes(args[1]), WorkloadCatalog.Json)
    ?? throw new InvalidDataException("Missing probe request.");
request.Validate();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var memory = new MemoryGovernor(256 * 1024 * 1024);
int exitCode;
using (var inputOwner = memory.CreateScope())
{
    inputOwner.Reserve(checked(16 * 1024L + 4L * request.SourceBytes));
    var source = new UTF8Encoding(false, true).GetString(ReadBounded(args[0], request.SourceBytes, inputOwner));
    var input = ReadBounded("../stdin.bin", request.InputBytes, inputOwner);
    var files = request.Files.ToDictionary(pair => pair.Key,
        pair => (ReadOnlyMemory<byte>)ReadBounded(pair.Key, pair.Value, inputOwner), StringComparer.Ordinal);
    using var stdout = Console.OpenStandardOutput();
    using var stderr = Console.OpenStandardError();
    await using var host = new BenchmarkHost(memory, input, files, stdout, stderr);
    using var evaluationOwner = memory.CreateScope();
    var evaluation = request.Mode == ProbeMode.Prepared ? new Evaluation(Parser.Parse(source)) : null;
    var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
    var before = request.AllocationCounters ? GC.GetTotalAllocatedBytes(precise: true) : 0;
    var result = evaluation == null
        ? await BashScript.RunAsync(host, source, EvaluationOptions.Default, deadline.Token)
        : await new Engine().RunAsync(evaluation, host, evaluationOwner, deadline.Token);
    evaluationOwner.Dispose();
    var allocated = request.AllocationCounters ? GC.GetTotalAllocatedBytes(precise: true) - before : 0;
    var allocations = request.AllocationCounters ? new ProbeAllocations(allocated,
        GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2) : null;
    var observed = new ProbeResult(Environment.Version.Major, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), System.Runtime.GCSettings.IsServerGC,
        request.Mode, host.FileDigests, host.Counts, memory.PeakBytes, allocations);
    File.WriteAllBytes(args[2], JsonSerializer.SerializeToUtf8Bytes(observed, WorkloadCatalog.Json));
    exitCode = result.ExitCode;
}
if (memory.ReservedBytes != 0) throw new InvalidOperationException("Probe teardown retained admitted storage.");
return exitCode;

static byte[] ReadBounded(string path, int length, IMemoryBudget owner)
{
    using var stream = File.OpenRead(path);
    if (stream.Length != length) throw new InvalidDataException("Probe input length changed after preparation.");
    var bytes = ScopedMemory.Allocate(length, owner);
    stream.ReadExactly(bytes);
    return bytes;
}

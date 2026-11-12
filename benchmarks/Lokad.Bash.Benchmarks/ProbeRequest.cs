namespace Lokad.Bash.Benchmarks;

internal enum ProbeMode { Public, Prepared }
internal sealed record ProbeRequest(ProbeMode Mode, int SourceBytes, int InputBytes,
    IReadOnlyDictionary<string, int> Files, bool AllocationCounters)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || SourceBytes < 0 || SourceBytes > Syntax.Parser.MaximumScriptBytes || InputBytes < 0)
            throw new InvalidDataException("Invalid probe input bounds.");
        foreach (var file in Files)
        {
            if (file.Value < 0 || Path.IsPathRooted(file.Key) || file.Key.Contains('\\')
                || file.Key.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Invalid probe fixture.");
        }
    }
}
internal sealed record ProbeAllocations(long ManagedBytes, int Gen0, int Gen1, int Gen2);
internal sealed record ProbeResult(int RuntimeMajor, string Runtime, string Architecture, bool ServerGC,
    ProbeMode Mode, IReadOnlyDictionary<string, ContentDigest> Files,
    HostCounts Host, long EstimatedPeakBytes, ProbeAllocations? Allocations);

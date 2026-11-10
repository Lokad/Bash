using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Reads one raw delimiter-separated record without consuming the following record.</summary>
internal sealed class ReadRecordOperation(
    TaskCompletionSource<ReadRecordResult> completion,
    FileDescriptor descriptor,
    byte delimiter,
    int maximumBytes,
    MemoryCapacity memory) : EvaluationOperation<ReadRecordResult>(completion)
{
    public FileDescriptor Descriptor { get; } = descriptor;
    public byte Delimiter { get; } = delimiter;
    public int MaximumBytes { get; } = maximumBytes;
    public MemoryCapacity Memory { get; } = memory;
}

/// <summary>A retained record, normal EOF without a record, or a descriptor read failure.</summary>
internal readonly record struct ReadRecordResult(DelimitedRecord? Record, ByteReadResult Failure);

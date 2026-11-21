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

/// <summary>Normalizes a counted read into the evaluator's reusable input buffer.</summary>
internal sealed class ReadCountedRecordOperation(
    TaskCompletionSource<(bool Complete, OperationError? Error)> completion,
    FileDescriptor descriptor,
    byte? delimiter,
    int count,
    ReadBackslashMode backslashMode,
    ByteMemoryBuilder content) : EvaluationOperation<(bool Complete, OperationError? Error)>(completion)
{
    public FileDescriptor Descriptor { get; } = descriptor;
    public byte? Delimiter { get; } = delimiter;
    public int Count { get; } = count;
    public ReadBackslashMode BackslashMode { get; } = backslashMode;
    public ByteMemoryBuilder Content { get; } = content;
}

/// <summary>Reads a bounded batch of raw records into caller-owned storage.</summary>
internal sealed class ReadRecordBatchOperation(
    TaskCompletionSource<DescriptorRecordBatch> completion,
    FileDescriptor descriptor,
    byte delimiter,
    int limit,
    IMemoryBudget destinationBudget) : EvaluationOperation<DescriptorRecordBatch>(completion)
{
    public FileDescriptor Descriptor { get; } = descriptor;
    public byte Delimiter { get; } = delimiter;
    public int Limit { get; } = limit;
    public IMemoryBudget DestinationBudget { get; } = destinationBudget;
}

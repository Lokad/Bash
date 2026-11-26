using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Normalizes input through the count or delimiter into the evaluator's reusable buffer.</summary>
internal sealed class ReadInputOperation(
    TaskCompletionSource<ReadInputResult> completion,
    FileDescriptor descriptor,
    byte? delimiter,
    int? count,
    ReadBackslashMode backslashMode,
    ByteMemoryBuilder content) : EvaluationOperation<ReadInputResult>(completion)
{
    public FileDescriptor Descriptor { get; } = descriptor;
    public byte? Delimiter { get; } = delimiter;
    public int? Count { get; } = count;
    public ReadBackslashMode BackslashMode { get; } = backslashMode;
    public ByteMemoryBuilder Content { get; } = content;
}

internal readonly record struct ReadInputResult(bool Complete, ReadBackslashMode FieldBackslashMode, OperationError? Error);

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

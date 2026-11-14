using System.Threading.Tasks;

namespace Lokad.Bash.Evaluation;

/// <summary>Starts an independently owned here-input producer before its consumer runs.</summary>
internal sealed class StartPipeInputOperation(
    TaskCompletionSource<OperationError?> completion,
    PipeInputWriter writer) : EvaluationOperation<OperationError?>(completion)
{
    public PipeInputWriter Writer { get; } = writer;
}

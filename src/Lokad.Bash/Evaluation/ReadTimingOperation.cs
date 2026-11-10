using System;
using System.Threading.Tasks;

namespace Lokad.Bash.Evaluation;

/// <summary>Requests timing counters without accessing a clock in evaluation.</summary>
public sealed class ReadTimingOperation(TaskCompletionSource<ShellTimingSnapshot> completion)
    : EvaluationOperation<ShellTimingSnapshot>(completion)
{
    public static Task<ShellTimingSnapshot> AsTask(Action<EvaluationOperation> publish)
    {
        var completion = new TaskCompletionSource<ShellTimingSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        publish(new ReadTimingOperation(completion));
        return completion.Task;
    }
}

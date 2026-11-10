using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// A typed request emitted by evaluation. Request data and completion belong to
/// the evaluator; the hosting layer claims and executes the request.
/// </summary>
public abstract class EvaluationOperation
{
    private int _accepted;

    /// <summary>Claims execution once, including failed or canceled attempts.</summary>
    internal void ClaimExecution()
    {
        if (Interlocked.Exchange(ref _accepted, 1) != 0)
            throw new InvalidOperationException("An evaluation operation can be accepted only once.");
    }
}

/// <summary>Owns the result channel that resumes evaluation after a request.</summary>
public abstract class EvaluationOperation<TResult>(TaskCompletionSource<TResult> completion) : EvaluationOperation
{
    private readonly TaskCompletionSource<TResult> _completion = completion;

    /// <summary>
    /// Completes the request with its result. Overrides must validate the result before
    /// calling this implementation; rejected results must leave completion unsettled.
    /// </summary>
    public virtual void SetResult(TResult result) => _completion.SetResult(result);

    /// <summary>
    /// Faults an unsettled request with the original exception. Returns false if an
    /// outcome has already been delivered, leaving that outcome unchanged.
    /// </summary>
    public bool TrySetException(Exception exception) => _completion.TrySetException(exception);

    /// <summary>
    /// Cancels an unsettled request with the original cancellation token. Returns false
    /// if an outcome has already been delivered, leaving that outcome unchanged.
    /// </summary>
    public bool TrySetCanceled(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);
}

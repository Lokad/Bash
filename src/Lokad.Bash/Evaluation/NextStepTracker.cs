using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Tracks the next pending operation produced by an evaluation loop.
/// </summary>
internal sealed class NextStepTracker
{
    /// <summary> Synchronizes access to the completion source and pending state. </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Holds the completion source for the next step; recreated after each GetNextStep call.
    /// </summary>
    private TaskCompletionSource<EvaluationOperation?> _tcs;

    /// <summary>
    /// Stores a terminal exception to surface after the pending result is consumed.
    /// </summary>
    private Exception? _terminalException;

    /// <summary>
    /// Stores a terminal cancellation to surface after the pending result is consumed.
    /// </summary>
    private CancellationToken? _terminalCancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextStepTracker"/> class.
    /// </summary>
    public NextStepTracker()
    {
        _tcs = CreateSource();
    }

    /// <summary>
    /// Publishes one pending value; rejects duplicate or competing operations until collection.
    /// </summary>
    /// <param name="operation">The next operation to execute, or null when complete.</param>
    public void SetNextStep(EvaluationOperation? operation)
    {
        lock (_lock)
        {
            if (HasNextStep)
            {
                // A pending value has a completed result under this lock.
                if (operation != null && ReferenceEquals(operation, _tcs.Task.Result))
                    throw new InvalidOperationException("The same evaluation operation cannot be published twice.");

                throw new InvalidOperationException(operation != null
                    ? "Another operation is already pending."
                    : "The previous next-step value has not been collected.");
            }

            HasNextStep = true;
            _tcs.SetResult(operation);
        }
    }

    /// <summary>
    /// Publishes a terminal exception for the next step.
    /// </summary>
    /// <param name="exception">The exception to surface.</param>
    public void SetException(Exception exception)
    {
        lock (_lock)
        {
            if (HasNextStep)
            {
                _terminalException = exception;
                _terminalCancellation = null;
                return;
            }

            _tcs.SetException(exception);
        }
    }

    /// <summary>
    /// Publishes a terminal cancellation for the next step.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token that triggered the stop.</param>
    public void SetCanceled(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (HasNextStep)
            {
                _terminalCancellation = cancellationToken;
                _terminalException = null;
                return;
            }

            _tcs.SetCanceled(cancellationToken);
        }
    }

    /// <summary>
    /// Gets whether a next-step value is currently pending collection.
    /// </summary>
    private bool HasNextStep { get; set; }

    /// <summary>
    /// Returns the next operation once it is published, then resets the tracker.
    /// </summary>
    /// <returns>The next operation to execute, or null when complete.</returns>
    public async Task<EvaluationOperation?> GetNextStep(CancellationToken cancellationToken)
    {
        Task<EvaluationOperation?> task;
        lock (_lock)
        {
            task = _tcs.Task;
        }

        var result = cancellationToken.CanBeCanceled
            ? await task.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await task.ConfigureAwait(false);

        lock (_lock)
        {
            _tcs = CreateSource();
            HasNextStep = false;
            if (_terminalException != null)
            {
                var exception = _terminalException;
                _terminalException = null;
                _terminalCancellation = null;
                _tcs.SetException(exception);
            }
            else if (_terminalCancellation.HasValue)
            {
                var token = _terminalCancellation.Value;
                _terminalCancellation = null;
                _tcs.SetCanceled(token);
            }
        }

        return result;
    }

    /// <summary> Creates a completion source for the next step with async continuations. </summary>
    private static TaskCompletionSource<EvaluationOperation?> CreateSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

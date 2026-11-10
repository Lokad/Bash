using System;
using System.Threading.Tasks;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash;

/// <summary>
/// Settles evaluator requests with the existing dispatch error policies. Explicit callback
/// state keeps synchronous host throws inside the completion guard without captured delegates.
/// </summary>
internal static class OperationCompletionExtensions
{
    /// <summary>Delivers the outcome to evaluation and propagates failures to the engine.</summary>
    public static Task UseResult<T, TRequest, TState>(
        this TRequest request,
        TState state,
        Func<TRequest, TState, Task<T>> invoke)
        where TRequest : EvaluationOperation<T> =>
        CompleteAsync<T, TRequest, TState, Func<TRequest, TState, Task<T>>>(
            request, state, invoke, static (action, request, state) => new ValueTask<T>(action(request, state)));

    /// <summary>Consumes a value task once, also propagating failures to the engine.</summary>
    public static Task UseResult<T, TRequest, TState>(
        this TRequest request,
        TState state,
        Func<TRequest, TState, ValueTask<T>> invoke)
        where TRequest : EvaluationOperation<T> =>
        CompleteAsync<T, TRequest, TState, Func<TRequest, TState, ValueTask<T>>>(
            request, state, invoke, static (action, request, state) => action(request, state));

    private static async Task CompleteAsync<T, TRequest, TState, TAction>(
        TRequest request,
        TState state,
        TAction action,
        Func<TAction, TRequest, TState, ValueTask<T>> invoke)
        where TRequest : EvaluationOperation<T>
    {
        try
        {
            var result = await invoke(action, request, state).ConfigureAwait(false);
            request.SetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            request.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            request.TrySetException(ex);
            throw;
        }
    }
}

using System;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Starts nested numeric evaluations from one pump, not from a parent's binding callback stack.
/// Only the outer evaluation pumps; nested callers await their queued result.
/// </summary>
internal sealed class ArithmeticWorkQueue
{
    private readonly Channel<Action> _ready = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    public async Task<long?> RunAsync(Func<Task<long?>> evaluate)
    {
        var result = Enqueue(evaluate);
        while (!result.IsCompleted)
        {
            var step = await _ready.Reader.ReadAsync().ConfigureAwait(false);
            step();
        }
        return await result.ConfigureAwait(false);
    }

    public Task<long?> Enqueue(Func<Task<long?>> evaluate)
    {
        var result = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready.Writer.TryWrite(() => _ = CompleteAsync());
        return result.Task;

        async Task CompleteAsync()
        {
            // Numeric work is not a host request: settle it here without entering the host dispatcher.
            try
            {
                result.SetResult(await evaluate().ConfigureAwait(false));
            }
            catch (OperationCanceledException error)
            {
                result.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                result.TrySetException(error);
            }
            finally
            {
                // Wake a pump waiting on an external effect after publishing completion.
                _ready.Writer.TryWrite(static () => { });
            }
        }
    }
}

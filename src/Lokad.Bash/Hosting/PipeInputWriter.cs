using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash;

/// <summary>
/// Owns a here-input payload and its pipe endpoints. The write progresses independently
/// of the evaluator; its reader's last owner settles both endpoints before returning.
/// </summary>
internal sealed class PipeInputWriter
{
    private readonly PipeDescriptors _pipe;
    private readonly IMemoryScope _memory;
    private readonly ByteMemoryBuilder _content;
    private CancellationTokenSource? _lifetime;
    private CancellationToken _callerCancellation;
    private Task<DescriptorCloseResult>? _completion;
    private TaskCompletionSource<DescriptorCloseResult>? _readerClose;
    private bool _stopping;

    public PipeInputWriter(PipeDescriptors pipe, ReadOnlyMemory<byte> content, bool appendNewline, IMemoryBudget budget)
    {
        _pipe = pipe;
        _memory = budget.CreateScope();
        try
        {
            _memory.Reserve(512); // Pump, completion channels and retained descriptor registration.
            _content = new ByteMemoryBuilder(checked(content.Length + (appendNewline ? 1 : 0)), _memory);
            _content.Append(content);
            if (appendNewline) _content.Append((byte)'\n');
        }
        catch
        {
            _memory.Dispose();
            throw;
        }
    }

    /// <summary>Starts host work without waiting for pipe capacity; reports an already completed failure.</summary>
    public OperationError? Start(IBashHost host, CancellationToken cancellationToken)
    {
        _callerCancellation = cancellationToken;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _completion = WriteAndCloseAsync(host, _lifetime.Token);
        if (!_completion.IsCompletedSuccessfully) return null;
        var result = _completion.Result;
        result.Exception?.Throw();
        return result.Error;

        async Task<DescriptorCloseResult> WriteAndCloseAsync(IBashHost writerHost, CancellationToken token)
        {
            var failure = new DescriptorCloseResult(true, null, null);
            try
            {
                var write = await writerHost.WriteAsync(_pipe.Write, _content.ToMemory(), token).ConfigureAwait(false);
                if (write.IsError) failure = new DescriptorCloseResult(false, write.Error, null);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _stopping) && !_callerCancellation.IsCancellationRequested)
            {
                // Closing an unused/overridden here-input stops its producer without failing the command.
            }
            catch (Exception exception)
            {
                failure = new DescriptorCloseResult(false, null, ExceptionDispatchInfo.Capture(exception));
            }
            var close = await ShellDescriptorCleanup.TryCloseOwnedAsync(writerHost, _pipe.Write).ConfigureAwait(false);
            failure = DescriptorCloseResult.FirstFailure(failure, close);
            if (failure.Error != null || failure.Exception != null)
            {
                // A writer-close failure must not strand a consumer still waiting for EOF.
                failure = DescriptorCloseResult.FirstFailure(failure, await CloseReaderAsync(writerHost).ConfigureAwait(false));
            }
            _content.Dispose();
            return failure;
        }
    }

    /// <summary>Retires the reader, stops an unused producer and awaits every borrowed write and close.</summary>
    public async Task<OperationError?> CloseAsync(IBashHost host)
    {
        Volatile.Write(ref _stopping, true);
        var reader = CloseReaderAsync(host);
        ExceptionDispatchInfo? cancellationFailure = null;
        try { _lifetime?.Cancel(); }
        catch (Exception exception) { cancellationFailure = ExceptionDispatchInfo.Capture(exception); }
        try
        {
            var writer = _completion is { } completion
                ? await completion.ConfigureAwait(false)
                : await ShellDescriptorCleanup.TryCloseOwnedAsync(host, _pipe.Write).ConfigureAwait(false);
            var failure = DescriptorCloseResult.FirstFailure(writer, await reader.ConfigureAwait(false));
            if (cancellationFailure != null)
                failure = DescriptorCloseResult.FirstFailure(failure, new DescriptorCloseResult(false, null, cancellationFailure));
            failure.Exception?.Throw();
            return failure.Error;
        }
        finally
        {
            // An operation canceled before dispatch still owns the admitted payload.
            _content.Dispose();
            _memory.Dispose();
            _lifetime?.Dispose();
        }
    }

    private Task<DescriptorCloseResult> CloseReaderAsync(IBashHost host)
    {
        var candidate = new TaskCompletionSource<DescriptorCloseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _readerClose, candidate, null);
        if (existing != null) return existing.Task;
        _ = CompleteAsync();
        return candidate.Task;

        async Task CompleteAsync() => candidate.SetResult(
            await ShellDescriptorCleanup.TryCloseOwnedAsync(host, _pipe.Read).ConfigureAwait(false));
    }
}

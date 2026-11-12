using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Benchmarks;

/// <summary>Serializes aliases of one handle; a returned borrow holds the gate through disposal.</summary>
internal abstract class BenchDescriptor
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<OperationError?>? _close;

    public async ValueTask<DescriptorWriteResult> WriteAsync(ReadOnlyMemory<byte> content, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return Volatile.Read(ref _close) != null ? DescriptorWriteResult.WriteFailure : await WriteCoreAsync(content, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask<DescriptorReadBuffer> ReadAsync(DescriptorReadRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var borrowed = false;
        try
        {
            if (Volatile.Read(ref _close) != null) return DescriptorReadBuffer.Failure(ByteReadResult.ReadFailure);
            var result = await ReadCoreAsync(request, token).ConfigureAwait(false);
            if (result.HasOwner)
            {
                result.ReleaseWith(() => _gate.Release());
                borrowed = true;
            }
            return result;
        }
        finally { if (!borrowed) _gate.Release(); }
    }

    public Task<OperationError?> CloseAsync()
    {
        var completion = new TaskCompletionSource<OperationError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.CompareExchange(ref _close, completion, null);
        if (previous != null) return previous.Task;
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try
            {
                // Closing a pipe reader must wake an outstanding read before acquiring its gate.
                var signal = BeginClose();
                await _gate.WaitAsync().ConfigureAwait(false);
                try { await signal.ConfigureAwait(false); await CloseCoreAsync().ConfigureAwait(false); }
                finally { _gate.Release(); }
                completion.SetResult(null);
            }
            catch (Exception error) { completion.SetException(error); }
        }
    }

    protected virtual Task BeginClose() => Task.CompletedTask;
    protected virtual Task CloseCoreAsync() => Task.CompletedTask;
    protected virtual ValueTask<DescriptorWriteResult> WriteCoreAsync(ReadOnlyMemory<byte> content, CancellationToken token) =>
        ValueTask.FromResult(DescriptorWriteResult.WriteFailure);
    protected virtual ValueTask<DescriptorReadBuffer> ReadCoreAsync(DescriptorReadRequest request, CancellationToken token) =>
        ValueTask.FromResult(DescriptorReadBuffer.Failure(ByteReadResult.ReadFailure));
}

internal sealed class MemoryInput : BenchDescriptor
{
    private readonly ReadOnlyMemory<byte> _content;
    private readonly DescriptorReadBufferOwner _owner;
    private int _position;
    public MemoryInput(ReadOnlyMemory<byte> content, IMemoryBudget memory)
    {
        _content = content;
        _owner = new DescriptorReadBufferOwner(count => _position += count, memory);
    }
    protected override ValueTask<DescriptorReadBuffer> ReadCoreAsync(DescriptorReadRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var count = Math.Min(request.MaximumBytes, _content.Length - _position);
        return ValueTask.FromResult(count == 0 ? DescriptorReadBuffer.EndOfFile
            : _owner.Borrow(_content.Slice(_position, count), ByteReadResult.Success(count, _position + count == _content.Length), true));
    }
}

/// <summary>Admitted consumer lookahead remains distinct from the pipe's bounded transport capacity.</summary>
internal sealed class PipeInput : BenchDescriptor
{
    private readonly BoundedBytePipe _pipe;
    private readonly IMemoryScope _memory;
    private readonly byte[] _scratch;
    private readonly ByteMemoryBuilder _buffer;
    private readonly DescriptorReadBufferOwner _owner;
    private int _position;
    private bool _eof;
    public PipeInput(BoundedBytePipe pipe, IMemoryBudget memory)
    {
        _pipe = pipe;
        _memory = memory.CreateScope();
        try
        {
            _scratch = ScopedMemory.Allocate(BoundedBytePipe.BlockBytes, _memory);
            _buffer = new ByteMemoryBuilder(BoundedBytePipe.BlockBytes, _memory);
            // Retain copies may survive descriptor closure; their budget belongs to the host invocation.
            _owner = new DescriptorReadBufferOwner(count => _position += count, memory);
        }
        catch { _memory.Dispose(); throw; }
    }
    protected override async ValueTask<DescriptorReadBuffer> ReadCoreAsync(DescriptorReadRequest request, CancellationToken token)
    {
        try
        {
            _pipe.CheckReadState();
            // Short records borrow a window. Move survivors only when a refill needs room.
            if (_buffer.Length - _position < request.MinimumBytes && !_eof && _position > 0)
            {
                var remaining = _buffer.ToMemory()[_position..];
                _buffer.Truncate(0);
                _buffer.Append(remaining);
                _position = 0;
            }
            while (_buffer.Length - _position < request.MinimumBytes && !_eof)
            {
                var count = await _pipe.ReadAsync(_scratch, token).ConfigureAwait(false);
                if (count == 0) _eof = true;
                else _buffer.Append(_scratch.AsSpan(0, count));
            }
            _pipe.CheckReadState();
        }
        catch (ShellDescriptorReadException error)
        {
            return DescriptorReadBuffer.Failure(ByteReadResult.Failure(0, error.Error, true));
        }
        var available = _buffer.Length - _position;
        var length = Math.Min(available, request.MaximumBytes);
        return length == 0 ? DescriptorReadBuffer.EndOfFile
            : _owner.Borrow(_buffer.ToMemory().Slice(_position, length), ByteReadResult.Success(length, _eof && length == available), false);
    }
    protected override Task BeginClose() => _pipe.CloseReaderAsync();
    protected override Task CloseCoreAsync() { _buffer.Dispose(); _memory.Dispose(); return Task.CompletedTask; }
}

internal sealed class PipeOutput(BoundedBytePipe pipe) : BenchDescriptor
{
    protected override ValueTask<DescriptorWriteResult> WriteCoreAsync(ReadOnlyMemory<byte> content, CancellationToken token) => pipe.WriteAsync(content, token);
    protected override Task CloseCoreAsync() { pipe.CloseWriter(); return Task.CompletedTask; }
}

internal sealed class DigestOutput : BenchDescriptor, IDisposable
{
    private readonly Digest? _digest;
    private readonly Stream? _forward;
    public DigestOutput(Stream? forward)
    {
        _forward = forward;
        if (forward == null) _digest = new Digest();
    }
    public ContentDigest Snapshot() => (_digest ?? throw new InvalidOperationException("Forwarded bytes are observed by the parent collector.")).Snapshot();
    protected override async ValueTask<DescriptorWriteResult> WriteCoreAsync(ReadOnlyMemory<byte> content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _digest?.Append(content.Span);
        if (_forward != null) await _forward.WriteAsync(content, token).ConfigureAwait(false);
        return DescriptorWriteResult.Open;
    }
    public void Dispose() => _digest?.Dispose();
}

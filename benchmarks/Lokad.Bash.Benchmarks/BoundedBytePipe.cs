using System.Threading.Channels;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Benchmarks;

/// <summary>One serialized producer and consumer, with 64 KiB of owned transport blocks.</summary>
internal sealed class BoundedBytePipe
{
    public const int BlockBytes = 4096;
    private const int BlockCount = 16;
    private readonly IMemoryScope _memory;
    private readonly Channel<int> _free = Channel.CreateBounded<int>(BlockCount);
    private readonly Channel<(int Index, int Count)> _ready = Channel.CreateBounded<(int, int)>(BlockCount);
    private readonly CancellationTokenSource _readerClosed = new();
    private readonly CancellationTokenSource _aborted = new();
    private readonly CancellationTokenRegistration _lifetime;
    private byte[][] _blocks;
    private int _closedEndpoints;
    private bool _writeClosed;

    public BoundedBytePipe(IMemoryBudget budget, CancellationToken lifetime)
    {
        _memory = budget.CreateScope();
        if (!_memory.TryReserve(96 * 1024, out _))
        {
            _memory.Dispose();
            throw new MemoryBudgetExceededException();
        }
        try
        {
            _blocks = Enumerable.Range(0, BlockCount).Select(_ => new byte[BlockBytes]).ToArray();
            for (var index = 0; index < BlockCount; index++) _free.Writer.TryWrite(index);
            _lifetime = lifetime.Register(() => _aborted.Cancel());
        }
        catch { _memory.Dispose(); throw; }
    }

    // The writer endpoint serializes whole writes; the reader endpoint serializes reads.
    public async ValueTask<DescriptorWriteResult> WriteAsync(ReadOnlyMemory<byte> content, CancellationToken token)
    {
        if (_aborted.IsCancellationRequested) return AbortedWrite();
        if (_readerClosed.IsCancellationRequested) return DescriptorWriteResult.Closed;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _readerClosed.Token, _aborted.Token);
        while (!content.IsEmpty)
        {
            int block;
            try { block = await _free.Reader.ReadAsync(cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_aborted.IsCancellationRequested) { return AbortedWrite(); }
            catch (OperationCanceledException) when (_readerClosed.IsCancellationRequested) { return DescriptorWriteResult.Closed; }
            var count = Math.Min(content.Length, BlockBytes);
            content.Span[..count].CopyTo(_blocks[block]);
            // A free block guarantees a ready slot. The byte copy ends before publication.
            if (!_ready.Writer.TryWrite((block, count)))
                throw new InvalidOperationException("Pipe writer was closed while still in use.");
            content = content[count..];
        }
        return DescriptorWriteResult.Open;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
    {
        CheckReadState();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _readerClosed.Token, _aborted.Token);
        (int Index, int Count) block;
        try { block = await _ready.Reader.ReadAsync(cancellation.Token).ConfigureAwait(false); }
        catch (ChannelClosedException) { return 0; }
        catch (OperationCanceledException) when (_aborted.IsCancellationRequested) { throw AbortedRead(); }
        catch (OperationCanceledException) when (_readerClosed.IsCancellationRequested) { throw new ShellDescriptorReadException(OperationError.BadFileDescriptor); }
        if (destination.Length < block.Count)
            throw new ArgumentException("Transport reads require one full block.", nameof(destination));
        _blocks[block.Index].AsMemory(0, block.Count).CopyTo(destination);
        _free.Writer.TryWrite(block.Index);
        return block.Count;
    }

    public void CheckReadState()
    {
        if (_aborted.IsCancellationRequested) throw AbortedRead();
        if (_readerClosed.IsCancellationRequested) throw new ShellDescriptorReadException(OperationError.BadFileDescriptor);
    }

    public async Task CloseReaderAsync()
    {
        _readerClosed.Cancel();
        await _lifetime.DisposeAsync().ConfigureAwait(false);
        CloseEndpoint();
    }

    public void CloseWriter()
    {
        if (_writeClosed) return;
        _writeClosed = true;
        _ready.Writer.TryComplete();
        CloseEndpoint();
    }

    private void CloseEndpoint()
    {
        if (Interlocked.Increment(ref _closedEndpoints) != 2) return;
        _blocks = [];
        _memory.Dispose();
    }

    private static DescriptorWriteResult AbortedWrite() => DescriptorWriteResult.Failure(new OperationError("Pipe lifetime cancelled\n"));
    private static ShellDescriptorReadException AbortedRead() => new(new OperationError("Pipe lifetime cancelled\n"));
}

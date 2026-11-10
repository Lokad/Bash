using System;
using System.Threading;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Files;

/// <summary>Reusable ownership state for one descriptor's successive byte-buffer borrows.</summary>
/// <remarks>
/// The descriptor serializes Borrow with its other operations. Consumers must release each borrow
/// before another operation. The generation prevents a stale copy from advancing or releasing a later read.
/// The commit callback synchronously advances the descriptor by the marked count, without I/O.
/// </remarks>
public sealed class DescriptorReadBufferOwner(Action<int> commit, IMemoryBudget memoryBudget)
{
    private readonly Lock _gate = new();
    private long _generation;
    private bool _released = true;
    private int _consumed;
    private Action? _release;
    public long Position { get; private set; }
    public IMemoryBudget MemoryBudget => memoryBudget;
    public bool WasRetained { get; private set; }

    /// <summary>Exposes one bounded window; the supplied outcome must describe its exact length.</summary>
    /// <param name="canRetain">Whether the offered memory remains immutable after disposal and descriptor closure.</param>
    /// <param name="content">The input byte content.</param>
    /// <param name="result">The outcome describing the supplied byte buffer.</param>
    public DescriptorReadBuffer Borrow(ReadOnlyMemory<byte> content, ByteReadResult result, bool canRetain)
    {
        if (content.Length != result.BytesRead)
            throw new ArgumentException("The read outcome must describe the offered bytes.", nameof(result));
        lock (_gate)
        {
            if (!_released)
                throw new InvalidOperationException("The preceding read buffer is still borrowed.");
            _released = false;
            _consumed = 0;
            WasRetained = false;
            return new DescriptorReadBuffer(this, ++_generation, content, result, canRetain);
        }
    }

    /// <summary>Invalidates read continuations when a serialized write advances the shared cursor.</summary>
    public void AdvancePosition(int count) => Position += count;

    public void Validate(long generation)
    {
        if (generation != _generation || _released)
            throw new ObjectDisposedException(nameof(DescriptorReadBuffer));
    }

    public void MarkRetained(long generation)
    {
        Validate(generation);
        WasRetained = true;
    }

    public void Consume(long generation, int byteCount)
    {
        lock (_gate)
        {
            Validate(generation);
            if (byteCount < _consumed)
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            _consumed = byteCount;
        }
    }

    public void ReleaseWith(long generation, Action release)
    {
        lock (_gate)
        {
            Validate(generation);
            _release += release;
        }
    }

    public void Release(long generation)
    {
        Action? release;
        int consumed;
        lock (_gate)
        {
            if (generation != _generation || _released)
                return;
            _released = true;
            consumed = _consumed;
            release = _release;
            _release = null;
        }
        try
        {
            commit(consumed);
            AdvancePosition(consumed);
        }
        finally
        {
            release?.Invoke();
        }
    }
}

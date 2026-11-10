using System;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Files;

/// <summary>A borrowed byte buffer whose consumed prefix is committed on disposal.</summary>
/// <remarks>
/// Content is valid only until disposal. Consume marks an absolute prefix, never moving backward.
/// Dispose before awaiting another operation on this descriptor. Disposal without consumption leaves
/// all bytes available. Copies share the same borrow; disposing one retires them all.
/// Consumers serialize access to the borrow; disposal of stale copies is harmless.
/// </remarks>
public readonly struct DescriptorReadBuffer : IDisposable
{
    private readonly DescriptorReadBufferOwner? _owner;
    private readonly long _generation;
    private readonly ReadOnlyMemory<byte> _content;
    private readonly bool _canRetain;

    public DescriptorReadBuffer(DescriptorReadBufferOwner? owner, long generation,
        ReadOnlyMemory<byte> content, ByteReadResult result, bool canRetain)
    {
        _owner = owner;
        _generation = generation;
        _content = content;
        Result = result;
        _canRetain = canRetain;
    }

    /// <summary>The offered bytes, valid until disposal. Use Retain for a longer lifetime.</summary>
    public ReadOnlyMemory<byte> Content
    {
        get
        {
            _owner?.Validate(_generation);
            return _content;
        }
    }

    /// <summary>The outcome at the end of the offered data, independent of consumption.</summary>
    public ByteReadResult Result { get; }

    /// <summary>Retains an immutable prefix past disposal, copying only when the provider reuses its storage.</summary>
    public ReadOnlyMemory<byte> Retain(int length)
    {
        var content = Content[..length];
        if (_canRetain)
        {
            _owner?.MarkRetained(_generation);
            return content;
        }
        var owner = _owner ?? throw new InvalidOperationException("Reusable storage requires a borrow owner.");
        var copy = ScopedMemory.Allocate(length, owner.MemoryBudget);
        content.CopyTo(copy);
        return copy;
    }

    /// <summary>Marks the length of the prefix to commit on disposal.</summary>
    public void Consume(int byteCount)
    {
        if ((uint)byteCount > (uint)_content.Length)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        _owner?.Consume(_generation, byteCount);
    }

    /// <summary>Commits the marked prefix and releases ownership once, without cancellation.</summary>
    public void Dispose() => _owner?.Release(_generation);

    public bool HasOwner => _owner != null;
    public IMemoryBudget MemoryBudget => _owner?.MemoryBudget
        ?? throw new InvalidOperationException("A nonempty read buffer requires an owner.");
    public (DescriptorReadBufferOwner? Owner, long Offset) Position => (_owner, _owner?.Position ?? 0);
    public void ReleaseWith(Action release) => _owner?.ReleaseWith(_generation, release);

    /// <summary>Normal EOF without bytes or an outstanding borrow.</summary>
    public static DescriptorReadBuffer EndOfFile => default;

    /// <summary>A descriptor failure without bytes or an outstanding borrow.</summary>
    public static DescriptorReadBuffer Failure(ByteReadResult failure)
    {
        if (!failure.IsError || failure.BytesRead != 0)
            throw new ArgumentException("An empty failure must contain no bytes.", nameof(failure));
        return new DescriptorReadBuffer(null, 0, ReadOnlyMemory<byte>.Empty, failure, canRetain: true);
    }
}

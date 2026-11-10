using System;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Builds a byte buffer without ever exceeding an explicit maximum length.</summary>
public sealed class BoundedByteMemoryBuilder : IDisposable, IByteMemoryWriter
{
    private readonly ByteMemoryBuilder _builder;
    private int _maximumLength;

    // TODO: Use the budgeted constructor for parser and diagnostic callers once
    // they receive a memory budget. The length limit alone does not reserve memory.
    public static BoundedByteMemoryBuilder CreateUnaccounted(int initialCapacity, int maximumLength) =>
        new(initialCapacity, maximumLength);

    private BoundedByteMemoryBuilder(int initialCapacity, int maximumLength)
    {
        ValidateLengths(initialCapacity, maximumLength);
        _builder = ByteMemoryBuilder.CreateUnaccounted(Math.Min(initialCapacity, maximumLength));
        _maximumLength = maximumLength;
    }

    public BoundedByteMemoryBuilder(int initialCapacity, int maximumLength, IMemoryBudget memoryBudget)
    {
        ValidateLengths(initialCapacity, maximumLength);
        _builder = new ByteMemoryBuilder(Math.Min(initialCapacity, maximumLength), memoryBudget);
        _maximumLength = maximumLength;
    }

    private static void ValidateLengths(int initialCapacity, int maximumLength)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        if (maximumLength < 0 || maximumLength >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
    }

    /// <summary>Gets the number of bytes written to the buffer.</summary>
    public int Length => _builder.Length;

    /// <summary>Gets the number of bytes that can still be appended.</summary>
    public int RemainingLength => _maximumLength - Length;

    /// <summary>Attempts to append one byte without exceeding the configured maximum.</summary>
    public bool TryAppend(byte value) => CanAppend(1) && _builder.TryAppend(value);

    /// <summary>Attempts to append bytes without exceeding the configured maximum.</summary>
    public bool TryAppend(ReadOnlySpan<byte> values) => CanAppend(values.Length) && _builder.TryAppend(values);

    /// <summary>Attempts to append a repeated byte without exceeding the configured maximum.</summary>
    public bool TryAppend(int count, byte value) => CanAppend(count) && _builder.TryAppend(count, value);

    /// <summary>Returns the written portion of the buffer.</summary>
    public ReadOnlyMemory<byte> ToMemory() => _builder.ToMemory();

    /// <summary>Starts another borrowed output, retaining admitted storage and replacing its length bound.</summary>
    /// <remarks>Consumers must finish reading the previous output before resetting this builder.</remarks>
    public void Reset(int maximumLength)
    {
        ValidateLengths(0, maximumLength);
        _builder.Truncate(0);
        _maximumLength = maximumLength;
    }

    public void Dispose() => _builder.Dispose();

    public ReadOnlyMemory<byte> DetachSnapshot() => _builder.DetachSnapshot();

    private bool CanAppend(int count) => count >= 0 && count <= RemainingLength;
}

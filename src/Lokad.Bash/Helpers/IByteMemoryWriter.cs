using System;

namespace Lokad.Bash.Helpers;

/// <summary>Shares byte rendering between display buffers and explicitly bounded payloads.</summary>
public interface IByteMemoryWriter
{
    /// <summary>Appends the bytes, or returns false without appending when they exceed the writer's limit.</summary>
    bool TryAppend(ReadOnlySpan<byte> bytes);

    /// <summary>Appends the byte, or returns false when it exceeds the writer's limit.</summary>
    bool TryAppend(byte value);

    /// <summary>Appends the repeated byte, or returns false when the count is negative or exceeds the writer's limit.</summary>
    bool TryAppend(int count, byte value);
}

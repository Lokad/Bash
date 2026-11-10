using System;

namespace Lokad.Bash.Files;

/// <summary>Bounds a byte read, optionally waiting for more unconsumed input.</summary>
/// <remarks>EOF or failure may return fewer than MinimumBytes. The default requests one byte.</remarks>
public readonly record struct DescriptorReadRequest
{
    private readonly int _minimumMinusOne;
    private readonly int _maximumMinusOne;
    private readonly (DescriptorReadBufferOwner? Owner, long Offset) _position;

    public DescriptorReadRequest(int minimumBytes, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumBytes);
        if (maximumBytes < minimumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _minimumMinusOne = minimumBytes - 1;
        _maximumMinusOne = maximumBytes - 1;
    }

    // Shared framing continues a particular cursor position. Another alias may consume that
    // prefix before this request acquires serialization, in which case read available data again.
    public DescriptorReadRequest(int minimumBytes, int maximumBytes,
        (DescriptorReadBufferOwner? Owner, long Offset) position) : this(minimumBytes, maximumBytes)
    {
        _position = position;
    }

    /// <summary>The effective minimum, evaluated while the provider owns descriptor serialization.</summary>
    /// <remarks>A continued read restarts at one byte if another alias advanced its cursor.</remarks>
    public int MinimumBytes => _position.Owner != null && _position.Owner.Position != _position.Offset
        ? 1 : _minimumMinusOne + 1;
    public int MaximumBytes => _maximumMinusOne + 1;

    /// <summary>Returns available bytes without waiting to fill the maximum.</summary>
    public static DescriptorReadRequest UpTo(int maximumBytes) => new(1, maximumBytes);
}

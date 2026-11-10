using System;
using System.Collections.Generic;

namespace Lokad.Bash.Helpers;

/// <summary>
/// Compares <see cref="ReadOnlyMemory{Byte}"/> values by byte content rather than reference identity.
/// </summary>
public sealed class ByteMemoryComparer :
    IEqualityComparer<ReadOnlyMemory<byte>>
{
    public static ByteMemoryComparer Instance { get; } = new();

    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(ReadOnlyMemory<byte> value)
    {
        var hash = new HashCode();
        foreach (var b in value.Span)
            hash.Add(b);

        return hash.ToHashCode();
    }

    public static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => left.SequenceCompareTo(right);
}

using System;
using System.Collections.Generic;
using System.Text.Unicode;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>Immutable shell data whose bytes need not form valid UTF-8.</summary>
public readonly struct ShellValue : IEquatable<ShellValue>
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private ShellValue(ReadOnlyMemory<byte> bytes)
    {
        _bytes = bytes;
    }

    /// <summary>Gets the empty value.</summary>
    public static ShellValue Empty => default;

    /// <summary>Orders shell data lexicographically by unsigned bytes, independently of culture.</summary>
    public static IComparer<ShellValue> ByteOrder { get; } =
        Comparer<ShellValue>.Create((left, right) => ByteMemoryComparer.Compare(left.Bytes.Span, right.Bytes.Span));

    /// <summary>Gets the value bytes without text conversion.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Gets whether the value contains no bytes.</summary>
    public bool IsEmpty => _bytes.IsEmpty;

    /// <summary>Retains value bytes; the caller must not mutate their storage afterward.</summary>
    public static ShellValue FromBytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>Encodes source or host text as UTF-8 value bytes.</summary>
    public static ShellValue FromText(string text) => new(Utf8Text.Encode(text));

    /// <summary>Decodes a value at a text boundary, throwing when its bytes are not valid UTF-8.</summary>
    public string ToUtf8String() => Utf8Text.Decode(_bytes);

    /// <summary>Decodes text only when every value byte forms valid UTF-8.</summary>
    public bool TryGetUtf8Text(out string text)
    {
        if (!Utf8.IsValid(_bytes.Span))
        {
            text = string.Empty;
            return false;
        }

        text = Utf8Text.Decode(_bytes);
        return true;
    }

    /// <summary>Returns the prefix before the first NUL, retaining the original storage.</summary>
    public ShellValue BeforeFirstNul()
    {
        var nul = Bytes.Span.IndexOf((byte)0);
        return nul < 0 ? this : FromBytes(Bytes[..nul]);
    }

    /// <summary>Discards NUL bytes, as required by read and command substitution.</summary>
    public ShellValue WithoutNuls()
    {
        if (_bytes.Span.IndexOf((byte)0) < 0)
            return this;

        var filtered = ByteMemoryBuilder.CreateUnaccounted(0);
        AppendWithoutNuls(filtered);
        return FromBytes(filtered.ToMemory());
    }

    /// <summary>Appends non-NUL bytes into the caller's builder without an intermediate copy.</summary>
    public void AppendWithoutNuls(ByteMemoryBuilder output)
    {
        var content = _bytes;
        var nul = content.Span.IndexOf((byte)0);
        while (nul >= 0)
        {
            output.Append(content[..nul]);
            content = content[(nul + 1)..];
            nul = content.Span.IndexOf((byte)0);
        }
        output.Append(content);
    }

    /// <summary>Compares byte content, independently of storage identity.</summary>
    public bool Equals(ShellValue other) => _bytes.Span.SequenceEqual(other._bytes.Span);

    public override bool Equals(object? obj) => obj is ShellValue other && Equals(other);

    public override int GetHashCode() => ByteMemoryComparer.Instance.GetHashCode(_bytes);

    public static bool operator ==(ShellValue left, ShellValue right) => left.Equals(right);

    public static bool operator !=(ShellValue left, ShellValue right) => !left.Equals(right);
}

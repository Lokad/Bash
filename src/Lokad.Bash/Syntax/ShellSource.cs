using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Immutable shell syntax, including generated source that need not be valid UTF-8.</summary>
public readonly struct ShellSource
{
    private ShellSource(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    /// <summary>Physical byte offsets are used throughout tokenization and nested scanning.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    public int Length => Bytes.Length;
    public bool IsEmpty => Bytes.IsEmpty;
    public byte this[int index] => Bytes.Span[index];

    public static ShellSource Empty => default;

    /// <summary>Retains source storage; callers must not mutate it afterward.</summary>
    public static ShellSource FromBytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>Encodes text once at the script boundary.</summary>
    public static ShellSource FromText(string text) => new(Utf8Text.Encode(text));

    public ShellSource Slice(int start) => new(Bytes[start..]);
    public ShellSource Slice(int start, int length) => new(Bytes.Slice(start, length));
    public ReadOnlySpan<byte> AsSpan() => Bytes.Span;
    public ReadOnlySpan<byte> AsSpan(int start, int length) => Bytes.Span.Slice(start, length);

    /// <summary>Trims shell blanks from a clause boundary; CR remains source data.</summary>
    internal ShellSource TrimBlanks()
    {
        var span = Bytes.Span;
        var start = span.Length - span.TrimStart(" \t\n"u8).Length;
        return Slice(start, span[start..].TrimEnd(" \t\n"u8).Length);
    }

    public ShellValue ToValue() => ShellValue.FromBytes(Bytes);

    /// <summary>Decodes source at a text boundary; malformed UTF-8 is rejected.</summary>
    public string ToUtf8String() => Utf8Text.Decode(Bytes);
}

using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

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

    /// <summary>Encodes text with its previously measured UTF-8 byte count.</summary>
    internal static ShellSource FromMeasuredText(string text, int byteCount)
    {
        if (byteCount == 0)
            return Empty;
        var bytes = new byte[byteCount];
        Utf8Text.StrictUtf8.GetBytes(text.AsSpan(), bytes);
        return FromBytes(bytes);
    }

    public ShellSource Slice(int start) => new(Bytes[start..]);
    public ShellSource Slice(int start, int length) => new(Bytes.Slice(start, length));
    public ReadOnlySpan<byte> AsSpan() => Bytes.Span;
    public ReadOnlySpan<byte> AsSpan(int start, int length) => Bytes.Span.Slice(start, length);

    /// <summary>Returns the physical source line at a read position, excluding its terminating LF.</summary>
    internal ShellSource GetPhysicalLine(int position, out int start)
    {
        var source = Bytes.Span;
        start = source[..position].LastIndexOf((byte)'\n') + 1;
        var end = source[position..].IndexOf((byte)'\n');
        return Slice(start, (end < 0 ? source.Length : position + end) - start);
    }

    /// <summary>Selects the diagnostic token at the end of a physical line prefix.</summary>
    internal ShellSource GetNearToken(int length)
    {
        var preceding = AsSpan(0, length).TrimEnd(" \t"u8);
        if (preceding.IsEmpty)
            return Empty;
        var start = Math.Max(0, preceding.LastIndexOfAny(" \t;|&"u8));
        if (preceding[start] is (byte)' ' or (byte)'\t')
            start++;
        return Slice(start, preceding.Length - start);
    }

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

/// <summary>Source spelling with selected nested captures rendered on demand.</summary>
internal readonly record struct SourceSpelling(ShellSource Source, IReadOnlyList<SpellingCapture>? Captures)
{
    internal void CopyCaptures(int offset, ref List<SpellingCapture>? destination, MemoryCapacity? memory)
    {
        if (Captures == null)
            return;
        if (destination == null)
        {
            memory?.Grow(32);
            destination = [];
        }
        memory?.PrepareListCapacity(destination, Captures.Count);
        foreach (var capture in Captures)
            destination.Add(capture with { Offset = offset + capture.Offset });
    }
}

internal readonly record struct SpellingCapture(int Offset, int Length, CommandSubstitutionSyntax Syntax);

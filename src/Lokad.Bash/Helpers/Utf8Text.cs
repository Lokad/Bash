using System;
using System.Buffers;
using System.Text;

namespace Lokad.Bash.Helpers;

public static class Utf8Text
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly ReadOnlyMemory<byte> Newline = "\n"u8.ToArray();

    /// <summary>Consumes one UTF-8 character from nonempty input, or one byte if malformed or incomplete.</summary>
    public static int FirstCharacterLength(ReadOnlySpan<byte> bytes) =>
        Rune.DecodeFromUtf8(bytes, out _, out var length) == OperationStatus.Done ? length : 1;

    public static string Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        return StrictUtf8.GetString(bytes.Span);
    }

    public static ReadOnlyMemory<byte> Encode(string text)
    {
        return string.IsNullOrEmpty(text) ? ReadOnlyMemory<byte>.Empty : StrictUtf8.GetBytes(text);
    }

    public static ReadOnlyMemory<byte> EncodeLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Newline;

        var byteCount = StrictUtf8.GetByteCount(text);
        var buffer = new byte[byteCount + 1];
        StrictUtf8.GetBytes(text.AsSpan(), buffer.AsSpan());
        buffer[^1] = (byte)'\n';
        return buffer;
    }

    /// <summary>Appends encoded chunks, returning false when the destination refuses a chunk.</summary>
    public delegate bool TryAppendBytes(ReadOnlySpan<byte> bytes);

    /// <summary>Encodes complete text runs while allowing the destination to preflight each byte copy.</summary>
    public static bool TryEncode(StringBuilder text, TryAppendBytes append)
    {
        var encoder = StrictUtf8.GetEncoder();
        Span<byte> encoded = stackalloc byte[4096];
        foreach (var chunk in text.GetChunks())
        {
            var remaining = chunk.Span;
            bool completed;
            do
            {
                // Convert preserves a surrogate split across chunks; GetByteCount does not
                // advance encoder state. Fixed scratch space also avoids a second encoding pass.
                encoder.Convert(remaining, encoded, flush: false, out var charsUsed, out var bytesUsed, out completed);
                if (!append(encoded[..bytesUsed]))
                    return false;
                remaining = remaining[charsUsed..];
            }
            while (!completed);
        }

        var finalBytes = encoder.GetBytes(ReadOnlySpan<char>.Empty, encoded, flush: true);
        return append(encoded[..finalBytes]);
    }

}

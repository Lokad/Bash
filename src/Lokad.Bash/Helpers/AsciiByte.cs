using System;

namespace Lokad.Bash.Helpers;

public static class AsciiByte
{
    /// <summary>Compares borrowed source bytes with fixed ASCII grammar without decoding a word.</summary>
    internal static bool EqualsAscii(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> text)
    {
        if (bytes.Length != text.Length)
            return false;
        for (var index = 0; index < bytes.Length; index++)
            if (text[index] > 127 || bytes[index] != text[index])
                return false;
        return true;
    }

    public static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    /// <summary>Matches the six whitespace bytes recognized by POSIX tools in the C locale.</summary>
    public static bool IsPosixWhitespace(byte value) =>
        IsWhitespace(value) || value is (byte)'\v' or (byte)'\f';

    internal static ReadOnlySpan<byte> TrimLeadingPosixWhitespace(ReadOnlySpan<byte> value) =>
        value.TrimStart(" \t\r\n\v\f"u8);

    /// <summary>GNU sort recognizes LF as a blank even in NUL-terminated records.</summary>
    public static bool IsSortBlank(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n';

    public static ReadOnlySpan<byte> TrimLeadingSortBlanks(ReadOnlySpan<byte> value)
    {
        var index = 0;
        while (index < value.Length && IsSortBlank(value[index]))
            index++;
        return value[index..];
    }

    public static byte ToLowerAscii(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + 32)
            : value;
    }
}

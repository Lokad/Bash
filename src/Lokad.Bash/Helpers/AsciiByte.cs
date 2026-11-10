using System;

namespace Lokad.Bash.Helpers;

public static class AsciiByte
{
    public static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    /// <summary>Matches the six whitespace bytes recognized by POSIX tools in the C locale.</summary>
    public static bool IsPosixWhitespace(byte value) =>
        IsWhitespace(value) || value is (byte)'\v' or (byte)'\f';

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

using System;

namespace Lokad.Bash.Helpers;

/// <summary>Fixed ASCII/C character-class vocabulary shared by shell brackets and regex translation.</summary>
internal static class ShellCharacterClass
{
    internal enum Kind { None, Alnum, Alpha, Ascii, Blank, Control, Digit, Graph, Lower, Print, Punctuation, Space, Upper, Word, HexDigit }

    internal static bool TryParse(ReadOnlySpan<byte> name, out Kind kind)
    {
        kind = name switch
        {
            _ when name.SequenceEqual("alnum"u8) => Kind.Alnum,
            _ when name.SequenceEqual("alpha"u8) => Kind.Alpha,
            _ when name.SequenceEqual("ascii"u8) => Kind.Ascii,
            _ when name.SequenceEqual("blank"u8) => Kind.Blank,
            _ when name.SequenceEqual("cntrl"u8) => Kind.Control,
            _ when name.SequenceEqual("digit"u8) => Kind.Digit,
            _ when name.SequenceEqual("graph"u8) => Kind.Graph,
            _ when name.SequenceEqual("lower"u8) => Kind.Lower,
            _ when name.SequenceEqual("print"u8) => Kind.Print,
            _ when name.SequenceEqual("punct"u8) => Kind.Punctuation,
            _ when name.SequenceEqual("space"u8) => Kind.Space,
            _ when name.SequenceEqual("upper"u8) => Kind.Upper,
            _ when name.SequenceEqual("word"u8) => Kind.Word,
            _ when name.SequenceEqual("xdigit"u8) => Kind.HexDigit,
            _ => Kind.None
        };
        return kind != Kind.None;
    }

    internal static bool Matches(Kind kind, int value)
    {
        if (value is < 0 or > 127)
            return false;
        var lower = value is >= 'a' and <= 'z';
        var upper = value is >= 'A' and <= 'Z';
        var digit = value is >= '0' and <= '9';
        return kind switch
        {
            Kind.Alnum => lower || upper || digit,
            Kind.Alpha => lower || upper,
            Kind.Ascii => true,
            Kind.Blank => value is ' ' or '\t',
            Kind.Control => value < 32 || value == 127,
            Kind.Digit => digit,
            Kind.Graph => value is >= 33 and <= 126,
            Kind.Lower => lower,
            Kind.Print => value is >= 32 and <= 126,
            Kind.Punctuation => value is >= 33 and <= 126 && !(lower || upper || digit),
            Kind.Space => AsciiByte.IsPosixWhitespace((byte)value),
            Kind.Upper => upper,
            Kind.Word => lower || upper || digit || value == '_',
            Kind.HexDigit => digit || value is >= 'a' and <= 'f' or >= 'A' and <= 'F',
            _ => false
        };
    }

    /// <summary>Preserves the regex profile's twelve supported classes; shell-only ascii/word remain excluded.</summary>
    internal static ReadOnlySpan<byte> RegexMembers(Kind kind) => kind switch
    {
        Kind.Alnum => "A-Za-z0-9"u8,
        Kind.Alpha => "A-Za-z"u8,
        Kind.Blank => " \\t"u8,
        Kind.Control => "\\x00-\\x1F\\x7F"u8,
        Kind.Digit => "0-9"u8,
        Kind.Graph => "\\x21-\\x7E"u8,
        Kind.Lower => "a-z"u8,
        Kind.Print => "\\x20-\\x7E"u8,
        Kind.Punctuation => "\\x21-\\x2F\\x3A-\\x40\\x5B-\\x60\\x7B-\\x7E"u8,
        Kind.Space => "\\x09-\\x0D\\x20"u8,
        Kind.Upper => "A-Z"u8,
        Kind.HexDigit => "A-Fa-f0-9"u8,
        _ => default
    };
}

using System;

namespace Lokad.Bash.Helpers;

public enum DecimalPrefixSyntax
{
    Numeric,
    GeneralNumeric
}

/// <summary>Scans a decimal mantissa once for numeric, human-numeric and general-numeric sort keys.</summary>
public readonly ref struct DecimalPrefix
{
    private DecimalPrefix(
        ReadOnlySpan<byte> whole,
        ReadOnlySpan<byte> fraction,
        ReadOnlySpan<byte> unsigned,
        ReadOnlySpan<byte> remainder,
        bool negative,
        bool hasDigits)
    {
        Whole = whole;
        Fraction = fraction;
        Unsigned = unsigned;
        Remainder = remainder;
        Negative = negative;
        HasDigits = hasDigits;
    }

    public ReadOnlySpan<byte> Whole { get; }
    public ReadOnlySpan<byte> Fraction { get; }
    public ReadOnlySpan<byte> Unsigned { get; }
    public ReadOnlySpan<byte> Remainder { get; }
    public bool Negative { get; }
    public bool HasDigits { get; }
    public int Sign => Whole.IsEmpty && Fraction.IsEmpty ? 0 : Negative ? -1 : 1;
    public byte Suffix => Remainder.IsEmpty ? (byte)0 : Remainder[0];

    public static DecimalPrefix Parse(ReadOnlySpan<byte> value, DecimalPrefixSyntax syntax)
    {
        value = syntax == DecimalPrefixSyntax.GeneralNumeric
            ? value.TrimStart(" \t\r\n\v\f"u8)
            : AsciiByte.TrimLeadingSortBlanks(value);
        var negative = !value.IsEmpty && value[0] == (byte)'-';
        if (negative || syntax == DecimalPrefixSyntax.GeneralNumeric && !value.IsEmpty && value[0] == (byte)'+')
            value = value[1..];

        var index = 0;
        while (index < value.Length && Integers.IsDigit(value[index]))
            index++;
        var hasDigits = index > 0;
        var whole = value[..index].TrimStart((byte)'0');
        ReadOnlySpan<byte> fraction = default;
        if (index < value.Length && value[index] == (byte)'.')
        {
            var start = ++index;
            while (index < value.Length && Integers.IsDigit(value[index]))
                index++;
            hasDigits |= index > start;
            fraction = value[start..index].TrimEnd((byte)'0');
        }

        return new DecimalPrefix(whole, fraction, value, value[index..], negative, hasDigits);
    }
}

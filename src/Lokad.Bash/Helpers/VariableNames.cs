using System;

namespace Lokad.Bash.Helpers;

/// <summary>Recognizes ASCII variable identifiers shared by parsing, evaluation and formatting.</summary>
public static class VariableNames
{
    public static bool IsValidVariableName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        if (!IsNameStart(name[0]))
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            if (!IsNamePart(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidVariableName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty || !IsNameStart((char)name[0]))
            return false;
        foreach (var value in name[1..])
            if (!IsNamePart((char)value))
                return false;
        return true;
    }

    public static bool IsNameStart(char value) =>
        value is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    public static bool IsNamePart(char value) =>
        IsNameStart(value) || value is >= '0' and <= '9';
}

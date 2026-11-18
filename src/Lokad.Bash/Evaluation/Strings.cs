using System;

namespace Lokad.Bash.Evaluation;

/// <summary> Helpers for string manipulation. </summary>
public static class Strings
{
    /// <summary>
    /// Trims space, tab, CR and LF from the ends of a character span.
    /// </summary>
    /// <param name="input">The input characters.</param>
    /// <returns>The trimmed slice.</returns>
    public static ReadOnlySpan<char> TrimWhitespace(ReadOnlySpan<char> input)
    {
        var start = 0;
        while (start < input.Length && IsWhitespace(input[start]))
            start++;

        var end = input.Length - 1;
        while (end >= start && IsWhitespace(input[end]))
            end--;

        return end < start ? ReadOnlySpan<char>.Empty : input.Slice(start, end - start + 1);
    }

    /// <summary> Is a character a whitespace character? </summary>
    public static bool IsWhitespace(char value) =>
        value is ' ' or '\t' or '\r' or '\n';
}

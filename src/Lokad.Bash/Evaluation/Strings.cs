using System;

namespace Lokad.Bash.Evaluation;

/// <summary> Helpers for string manipulation. </summary>
public static class Strings
{
    /// <summary>
    /// Trims ASCII whitespace from a UTF-8 memory slice.
    /// </summary>
    /// <param name="input">The UTF-8 input.</param>
    /// <returns>The trimmed slice.</returns>
    /// <summary> Trims white-space from the beginning and end of a span. </summary>
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

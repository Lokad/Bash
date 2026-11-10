using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Expands brace expressions in unquoted source before other expansions.
/// </summary>
/// <remarks>
/// This implementation follows the brace expansion rules from BASH.md,
/// including comma lists and simple sequence expressions.
/// </remarks>
internal static class BraceExpansion
{
    /// <summary>
    /// Expands brace expressions in the provided input.
    /// </summary>
    /// <param name="input">The input to expand.</param>
    /// <returns>The expanded source variants, or the original input if no expansion applies.</returns>
    /// <param name="budget">The cumulative allocation allowance shared by related evaluations.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    public static IReadOnlyList<ShellSource> Expand(
        ShellSource input, AllocationAllowance budget, CancellationToken cancellationToken) =>
        ExpandSource(input, budget, cancellationToken, 0);

    /// <summary>Recursively expands generated source while sharing the brace allowance.</summary>
    private static IReadOnlyList<ShellSource> ExpandSource(
        ShellSource input, AllocationAllowance budget, CancellationToken cancellationToken, int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth >= 256)
            throw new ExpansionBudget.LimitExceededException("shell: brace expansion exceeds the 256-step nesting limit\n");

        if (!TryFindExpansion(input.AsSpan(), budget, cancellationToken, out var openIndex, out var closeIndex, out var expansions))
            return [input];

        var prefix = input.AsSpan()[..openIndex];
        var suffix = input.AsSpan()[(closeIndex + 1)..];
        var results = new List<ShellSource>();

        foreach (var expansion in expansions)
        {
            budget.Charge(1, (long)prefix.Length + expansion.Length + suffix.Length);
            var combined = Combine(prefix, expansion.AsSpan(), suffix);
            var nested = ExpandSource(combined, budget, cancellationToken, depth + 1);
            budget.Charge(nested.Count, 0);
            results.AddRange(nested);
        }

        return results;

        static ShellSource Combine(
            ReadOnlySpan<byte> prefix,
            ReadOnlySpan<byte> middle,
            ReadOnlySpan<byte> suffix)
        {
            var bytes = ByteMemoryBuilder.CreateUnaccounted(prefix.Length + middle.Length + suffix.Length);
            bytes.Append(prefix);
            bytes.Append(middle);
            bytes.Append(suffix);
            return ShellSource.FromBytes(bytes.ToMemory());
        }
    }

    /// <summary>
    /// Finds the first brace expansion candidate and returns its bounds and expanded elements.
    /// </summary>
    /// <param name="input">The source bytes to scan.</param>
    /// <param name="openIndex">The index of the opening brace.</param>
    /// <param name="closeIndex">The index of the closing brace.</param>
    /// <param name="expansions">The expanded elements found within the braces.</param>
    /// <returns>True when a valid expansion candidate is found.</returns>
    /// <param name="budget">The cumulative allocation allowance shared by related evaluations.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    private static bool TryFindExpansion(
        ReadOnlySpan<byte> input,
        AllocationAllowance budget,
        CancellationToken cancellationToken,
        out int openIndex,
        out int closeIndex,
        out IReadOnlyList<ShellSource> expansions)
    {
        openIndex = -1;
        closeIndex = -1;
        expansions = [];

        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] != (byte)'{')
                continue;

            if (index > 0 && input[index - 1] == (byte)'$')
            {
                if (!TryFindClosingBrace(input, index, out var skipClose))
                    return false;

                index = skipClose;
                continue;
            }

            if (!TryFindClosingBrace(input, index, out var candidateClose))
                return false;

            var content = input.Slice(index + 1, candidateClose - index - 1);
            if (TryExpandContent(content, budget, cancellationToken, out expansions))
            {
                openIndex = index;
                closeIndex = candidateClose;
                return true;
            }

            index = candidateClose;
        }

        return false;
    }

    /// <summary>
    /// Finds the matching closing brace for an opening brace, tracking nested braces.
    /// </summary>
    /// <param name="input">The source bytes to scan.</param>
    /// <param name="start">The index of the opening brace.</param>
    /// <param name="closeIndex">The index of the matching closing brace.</param>
    /// <returns>True when a matching closing brace is found.</returns>
    private static bool TryFindClosingBrace(ReadOnlySpan<byte> input, int start, out int closeIndex)
    {
        var depth = 0;
        for (var index = start; index < input.Length; index++)
        {
            var value = input[index];
            if (value == (byte)'{')
            {
                if (index <= 0 || input[index - 1] != (byte)'$')
                    depth++;

                continue;
            }

            if (value == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    closeIndex = index;
                    return true;
                }
            }
        }

        closeIndex = -1;
        return false;
    }

    /// <summary>
    /// Expands the brace content into either a comma list or a sequence expansion.
    /// </summary>
    /// <param name="content">The source bytes inside braces.</param>
    /// <param name="expansions">The expanded values when parsing succeeds.</param>
    /// <returns>True when the content represents a valid expansion.</returns>
    /// <param name="budget">The cumulative allocation allowance shared by related evaluations.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    private static bool TryExpandContent(
        ReadOnlySpan<byte> content,
        AllocationAllowance budget,
        CancellationToken cancellationToken,
        out IReadOnlyList<ShellSource> expansions)
    {
        expansions = [];

        if (!HasTopLevelComma(content))
            return TryExpandSequence(content, budget, cancellationToken, out expansions);

        expansions = SplitCommaList(content, budget);
        return true;
    }

    /// <summary>
    /// Determines whether the content contains a top-level comma outside nested braces.
    /// </summary>
    /// <param name="content">The source bytes inside braces.</param>
    /// <returns>True when a top-level comma is present.</returns>
    private static bool HasTopLevelComma(ReadOnlySpan<byte> content)
    {
        var depth = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var value = content[index];
            if (value == (byte)'{' && !(index > 0 && content[index - 1] == (byte)'$'))
            {
                depth++;
                continue;
            }

            if (value == (byte)'}' && depth > 0)
            {
                depth--;
                continue;
            }

            if (value == (byte)',' && depth == 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Splits a comma-separated list into top-level segments, preserving nested brace content.
    /// </summary>
    /// <param name="content">The source bytes inside braces.</param>
    /// <returns>The top-level comma-separated segments.</returns>
    /// <param name="budget">The cumulative allocation allowance shared by related evaluations.</param>
    private static IReadOnlyList<ShellSource> SplitCommaList(ReadOnlySpan<byte> content, AllocationAllowance budget)
    {
        var results = new List<ShellSource>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < content.Length; index++)
        {
            var value = content[index];
            if (value == (byte)'{' && !(index > 0 && content[index - 1] == (byte)'$'))
            {
                depth++;
                continue;
            }

            if (value == (byte)'}' && depth > 0)
            {
                depth--;
                continue;
            }

            if (value == (byte)',' && depth == 0)
            {
                budget.Charge(1, index - start);
                results.Add(ShellSource.FromBytes(content.Slice(start, index - start).ToArray()));
                start = index + 1;
            }
        }

        budget.Charge(1, content.Length - start);
        results.Add(ShellSource.FromBytes(content[start..].ToArray()));
        return results;
    }

    /// <summary>
    /// Expands a sequence expression like <c>{1..3}</c> or <c>{a..c}</c>.
    /// </summary>
    /// <param name="source">The source bytes inside braces.</param>
    /// <param name="expansions">The expanded sequence values.</param>
    /// <returns>True when the content is a valid sequence expression.</returns>
    /// <param name="budget">The cumulative allocation allowance shared by related evaluations.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    private static bool TryExpandSequence(
        ReadOnlySpan<byte> source,
        AllocationAllowance budget,
        CancellationToken cancellationToken,
        out IReadOnlyList<ShellSource> expansions)
    {
        expansions = [];
        // Sequence endpoints and decimal increments are a text grammar; comma-list data is not.
        budget.Charge(0, 2L * source.Length);
        if (!ShellValue.FromBytes(source.ToArray()).TryGetUtf8Text(out var text))
            return false;
        var content = text.AsSpan();

        foreach (var character in content)
            if (character == ',')
                return false;

        var first = IndexOfDoubleDot(content, 0);
        if (first < 0) return false;

        var second = IndexOfDoubleDot(content, first + 2);
        if (second >= 0 && IndexOfDoubleDot(content, second + 2) >= 0)
            return false;

        var startSpan = content[..first];
        var endSpan = second >= 0
            ? content.Slice(first + 2, second - first - 2)
            : content[(first + 2)..];
        var incrementSpan = second >= 0 ? content[(second + 2)..] : ReadOnlySpan<char>.Empty;

        if (startSpan.IsEmpty || endSpan.IsEmpty)
            return false;

        if (Integers.TryParseNumberToken(startSpan, out var startValue, out var startDigits, out var padStart)
            && Integers.TryParseNumberToken(endSpan, out var endValue, out var endDigits, out var padEnd))
        {
            var padWidth = (padStart || padEnd) ? Math.Max(startDigits, endDigits) : 0;
            if (!TryParseIncrement(incrementSpan, startValue, endValue, out var increment))
                return false;

            if ((increment > 0 && startValue > endValue) || (increment < 0 && startValue < endValue))
                return false;

            var format = "D" + padWidth.ToString(CultureInfo.InvariantCulture);
            expansions = ExpandRange(startValue, endValue, increment, 2 * Math.Max(11L, (long)padWidth + 1),
                value => value.ToString(format, CultureInfo.InvariantCulture));
            return true;
        }

        if (startSpan.Length == 1 && endSpan.Length == 1)
        {
            var startChar = (int)startSpan[0];
            var endChar = (int)endSpan[0];
            if (!TryParseIncrement(incrementSpan, startChar, endChar, out var increment))
                return false;

            if ((increment > 0 && startChar > endChar) || (increment < 0 && startChar < endChar))
                return false;

            expansions = ExpandRange(startChar, endChar, increment, 4, static value => new string((char)value, 1));
            return true;
        }

        return false;

        IReadOnlyList<ShellSource> ExpandRange(int start, int end, int increment, long maximumCopiedUnits, Func<int, string> format)
        {
            // Widen before subtraction/absolute value, including int.MinValue increments.
            // Enumerate by count so stepping past either int endpoint cannot wrap.
            var count = Math.Abs((long)end - start) / Math.Abs((long)increment) + 1;
            // Reject excessive counts before preflighting formatted text and encoded byte copies.
            budget.Charge(count, 0);
            budget.Charge(0, count * maximumCopiedUnits);
            var results = new List<ShellSource>((int)count);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(ShellSource.FromText(format((int)(start + (long)index * increment))));
            }
            return results;
        }
    }

    /// <summary>
    /// Parses an optional sequence increment and chooses the default based on direction.
    /// </summary>
    /// <param name="span">The decimal increment text.</param>
    /// <param name="start">The start value of the sequence.</param>
    /// <param name="end">The end value of the sequence.</param>
    /// <param name="increment">The parsed increment value.</param>
    /// <returns>True when the increment is valid for the sequence direction.</returns>
    private static bool TryParseIncrement<T>(ReadOnlySpan<char> span, T start, T end, out int increment)
        where T : IComparable<T>
    {
        increment = 0;
        if (span.IsEmpty)
        {
            increment = start.CompareTo(end) <= 0 ? 1 : -1;
            return true;
        }

        if (!Integers.TryParseInt(span, out increment))
            return false;

        return increment != 0;
    }

    /// <summary>
    /// Finds the next ".." sequence starting at the provided index.
    /// </summary>
    /// <param name="span">The sequence text to scan.</param>
    /// <param name="start">The index to start scanning.</param>
    /// <returns>The index of the first dot in a ".." sequence, or -1 if none found.</returns>
    private static int IndexOfDoubleDot(ReadOnlySpan<char> span, int start)
    {
        for (var index = start; index + 1 < span.Length; index++)
        {
            if (span[index] == '.' && span[index + 1] == '.')
            {
                return index;
            }
        }

        return -1;
    }
}

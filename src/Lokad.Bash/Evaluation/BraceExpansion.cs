using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Expands brace expressions in unquoted source before other expansions.
/// </summary>
/// <remarks>
/// Supports comma lists and sequence expressions.
/// </remarks>
internal static class BraceExpansion
{
    /// <summary>
    /// Expands brace expressions in the provided input.
    /// </summary>
    /// <param name="input">The input to expand.</param>
    /// <returns>The expanded source variants, or the original input if no expansion applies.</returns>
    /// <param name="memory">Owns expansion workspace until the enclosing command completes.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    public static IReadOnlyList<ShellSource> Expand(
        ShellSource input, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var pending = new Stack<ShellSource>();
        pending.Push(input);
        var results = new List<ShellSource>();
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindExpansion(current.AsSpan(), memory, cancellationToken,
                    out var openIndex, out var closeIndex, out var expansions))
            {
                memory.Charge(256);
                results.Add(current);
                continue;
            }

            var prefix = current.AsSpan()[..openIndex];
            var suffix = current.AsSpan()[(closeIndex + 1)..];
            // Reverse the push order to retain left-to-right expansion without recursive calls.
            for (var index = expansions.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expansion = expansions[index];
                memory.Charge(checked(256L + 4L * ((long)prefix.Length + expansion.Length + suffix.Length)));
                pending.Push(Combine(prefix, expansion.AsSpan(), suffix));
            }
        }
        return results;

        static ShellSource Combine(
            ReadOnlySpan<byte> prefix,
            ReadOnlySpan<byte> middle,
            ReadOnlySpan<byte> suffix)
        {
            var bytes = ByteMemoryBuilder.CreateUnaccounted(checked(prefix.Length + middle.Length + suffix.Length));
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
    /// <param name="memory">Owns expansion workspace until the enclosing command completes.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    private static bool TryFindExpansion(
        ReadOnlySpan<byte> input,
        EvaluationMemory memory,
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
            cancellationToken.ThrowIfCancellationRequested();
            if (input[index] != (byte)'{')
                continue;

            if (index > 0 && input[index - 1] == (byte)'$')
            {
                if (!TryFindClosingBrace(input, index, cancellationToken, out var skipClose))
                    return false;

                index = skipClose;
                continue;
            }

            if (!TryFindClosingBrace(input, index, cancellationToken, out var candidateClose))
                return false;

            var content = input.Slice(index + 1, candidateClose - index - 1);
            if (TryExpandContent(content, memory, cancellationToken, out expansions))
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
    /// <param name="cancellationToken">Cancels scanning nested source.</param>
    /// <param name="closeIndex">The index of the matching closing brace.</param>
    /// <returns>True when a matching closing brace is found.</returns>
    private static bool TryFindClosingBrace(ReadOnlySpan<byte> input, int start, CancellationToken cancellationToken, out int closeIndex)
    {
        var depth = 0;
        for (var index = start; index < input.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    /// <param name="memory">Owns expansion workspace until the enclosing command completes.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    private static bool TryExpandContent(
        ReadOnlySpan<byte> content,
        EvaluationMemory memory,
        CancellationToken cancellationToken,
        out IReadOnlyList<ShellSource> expansions)
    {
        expansions = [];

        if (!HasTopLevelComma(content, cancellationToken))
            return TryExpandSequence(content, memory, cancellationToken, out expansions);

        expansions = SplitCommaList(content, memory, cancellationToken);
        return true;
    }

    /// <summary>
    /// Determines whether the content contains a top-level comma outside nested braces.
    /// </summary>
    /// <param name="content">The source bytes inside braces.</param>
    /// <returns>True when a top-level comma is present.</returns>
    /// <param name="cancellationToken">Cancels scanning the source.</param>
    private static bool HasTopLevelComma(ReadOnlySpan<byte> content, CancellationToken cancellationToken)
    {
        var depth = 0;
        for (var index = 0; index < content.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    /// <param name="memory">Owns expansion workspace until the enclosing command completes.</param>
    /// <param name="cancellationToken">Cancels scanning the source.</param>
    private static IReadOnlyList<ShellSource> SplitCommaList(ReadOnlySpan<byte> content, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var results = new List<ShellSource>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < content.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                memory.Charge(checked(256L + 4L * (index - start)));
                results.Add(ShellSource.FromBytes(content.Slice(start, index - start).ToArray()));
                start = index + 1;
            }
        }

        memory.Charge(checked(256L + 4L * (content.Length - start)));
        results.Add(ShellSource.FromBytes(content[start..].ToArray()));
        return results;
    }

    /// <summary>
    /// Expands a sequence expression like <c>{1..3}</c> or <c>{a..c}</c>.
    /// </summary>
    /// <param name="source">The source bytes inside braces.</param>
    /// <param name="expansions">The expanded sequence values.</param>
    /// <returns>True when the content is a valid sequence expression.</returns>
    /// <param name="memory">Owns expansion workspace until the enclosing command completes.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    private static bool TryExpandSequence(
        ReadOnlySpan<byte> source,
        EvaluationMemory memory,
        CancellationToken cancellationToken,
        out IReadOnlyList<ShellSource> expansions)
    {
        expansions = [];
        var first = IndexOfDoubleDot(source, 0, cancellationToken);
        if (first < 0) return false;

        var second = IndexOfDoubleDot(source, first + 2, cancellationToken);
        var startSpan = source[..first];
        var endSpan = second >= 0
            ? source.Slice(first + 2, second - first - 2)
            : source[(first + 2)..];

        long increment = 1;
        if (second >= 0 && !Integers.TryParseNumber(source[(second + 2)..], out increment))
            return false;
        var step = increment == 0 ? 1 : Int128.Abs(increment);

        if (Integers.TryParseNumber(startSpan, out var startValue)
            && Integers.TryParseNumber(endSpan, out var endValue))
        {
            var difference = (Int128)endValue - startValue;
            var distance = Int128.Abs(difference);
            // The Bash 5.2 subtraction guard leaves room for the first value and terminator.
            if (distance > long.MaxValue
                || startValue < 0 && difference > long.MaxValue - 2
                || startValue > 0 && difference < long.MinValue + 3)
                return false;

            var width = IsPadded(startSpan) || IsPadded(endSpan) ? Math.Max(startSpan.Length, endSpan.Length) : 0;
            return TryExpandRange(startValue, endValue, distance, Math.Max(20, width),
                value => FormatNumber(value, width), out expansions);
        }

        if (IsLetter(startSpan) && IsLetter(endSpan))
        {
            var start = startSpan[0];
            var end = endSpan[0];
            return TryExpandRange(start, end, Math.Abs(end - start), 1,
                static value => ShellSource.FromBytes(new byte[] { (byte)value }), out expansions);
        }

        return false;

        bool TryExpandRange(long start, long end, Int128 distance, int maximumValueBytes,
            Func<long, ShellSource> format, out IReadOnlyList<ShellSource> sequence)
        {
            sequence = [];
            // An ascending range must be able to reverse a negative increment.
            if (start < end && increment == long.MinValue)
                return false;
            // Widen magnitude and count to cover long.MinValue and avoid an overflowing terminal step.
            var count = distance / step + 1;
            if (count > int.MaxValue - 2)
                return false;
            if (count >= Array.MaxLength)
                throw new ByteMemoryBuilder.LimitExceededException();

            var length = (int)count;
            memory.Charge(checked(length * (256L + 4L * maximumValueBytes)));
            var results = new List<ShellSource>(length);
            var directedStep = start <= end ? step : -step;
            for (var index = 0; index < length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(format((long)(start + index * directedStep)));
            }
            sequence = results;
            return true;
        }

        static bool IsPadded(ReadOnlySpan<byte> endpoint)
        {
            var digits = endpoint[0] == (byte)'-' ? endpoint[1..] : endpoint;
            return digits.Length > 1 && digits[0] == (byte)'0';
        }

        static bool IsLetter(ReadOnlySpan<byte> token) =>
            token.Length == 1 && AsciiByte.ToLowerAscii(token[0]) is >= (byte)'a' and <= (byte)'z';

        static ShellSource FormatNumber(long value, int width)
        {
            // The pinned Bash 5.2 builds narrow padded output to signed int after stepping in intmax_t.
            if (width != 0)
                value = unchecked((int)value);
            Span<byte> text = stackalloc byte[20];
            if (!Utf8Formatter.TryFormat(value, text, out var written))
                throw new InvalidOperationException("A signed decimal must fit its format buffer.");

            var padding = Math.Max(0, width - written);
            var bytes = new byte[written + padding];
            var signWidth = value < 0 ? 1 : 0;
            if (signWidth != 0)
                bytes[0] = (byte)'-';
            bytes.AsSpan(signWidth, padding).Fill((byte)'0');
            text[signWidth..written].CopyTo(bytes.AsSpan(signWidth + padding));
            return ShellSource.FromBytes(bytes);
        }
    }

    /// <summary>
    /// Finds the next ".." sequence starting at the provided index.
    /// </summary>
    /// <param name="span">The sequence text to scan.</param>
    /// <param name="start">The index to start scanning.</param>
    /// <returns>The index of the first dot in a ".." sequence, or -1 if none found.</returns>
    /// <param name="cancellationToken">Cancels scanning the source.</param>
    private static int IndexOfDoubleDot(ReadOnlySpan<byte> span, int start, CancellationToken cancellationToken)
    {
        for (var index = start; index + 1 < span.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (span[index] == (byte)'.' && span[index + 1] == (byte)'.')
            {
                return index;
            }
        }

        return -1;
    }
}

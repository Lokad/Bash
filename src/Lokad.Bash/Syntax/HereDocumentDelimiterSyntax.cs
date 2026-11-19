using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Original delimiter spelling and the literal bytes used to end its body.</summary>
internal sealed record HereDocumentDelimiterSyntax(ShellSource Spelling, ShellSource Value, bool AllowsExpansion)
{
    internal static HereDocumentDelimiterSyntax? Prepare(Word word, ShellSource source, ShellScanContext context)
    {
        // Ordinary delimiters already have all their spelling and quoting in the word parts.
        if (!ContainsStructuredSpelling(source.AsSpan()))
            return null;

        var allowsExpansion = true;
        foreach (var part in word.Parts)
            allowsExpansion &= part.Kind is WordPartKind.Unquoted or WordPartKind.CommandSubstitution;

        context.SyntaxMemory?.Grow(96);
        ShellSource spelling;
        if (word.Parts is [SourceWordPart plain] && plain.Source.AsSpan().SequenceEqual(source.AsSpan()))
            spelling = plain.Source;
        else
        {
            context.SyntaxMemory?.Grow(32L + source.Length);
            spelling = ShellSource.FromBytes(source.Bytes.ToArray());
        }

        var logical = NormalizeSpelling(source, spelling, context);
        var value = allowsExpansion ? logical : RemoveQuotes(logical, context);
        return new HereDocumentDelimiterSyntax(spelling, value, allowsExpansion);

        static bool ContainsStructuredSpelling(ReadOnlySpan<byte> bytes)
        {
            for (var position = 0; position < bytes.Length;)
            {
                var relative = bytes[position..].IndexOfAny((byte)'$', (byte)'`');
                if (relative < 0) return false;
                var start = position + relative;
                if (bytes[start] == '`') return true;
                position = ShellSourceCursor.SkipLineContinuations(bytes, start + 1);
                if (position < bytes.Length && bytes[position] is (byte)'(' or (byte)'{' or (byte)'[')
                    return true;
            }
            return false;
        }
    }

    // Preserve the text of complete nested forms while normalizing their lexical
    // escapes and quote prefixes. The ordinary scanners still select their bounds.
    private static ShellSource NormalizeSpelling(ShellSource source, ShellSource spelling, ShellScanContext context)
    {
        var nextEdit = FindEdit(0);
        if (nextEdit == source.Length)
            return spelling;

        using var builder = context.CreateByteBuilder(source.Length);
        AppendRange(0, source.Length);
        return ShellSource.FromBytes(builder.ToCompactMemory(context.SyntaxMemory));

        int FindEdit(int start)
        {
            var bytes = source.AsSpan();
            for (var index = start; index + 1 < bytes.Length; index++)
                if ((bytes[index] == '\\' && bytes[index + 1] == '\n')
                    || (bytes[index] == '$' && bytes[index + 1] is (byte)'\'' or (byte)'"'))
                    return index;
            return bytes.Length;
        }

        bool ContainsEdit(int start, int end)
        {
            while (nextEdit < start)
                nextEdit = FindEdit(nextEdit + 1);
            return nextEdit < end;
        }

        void AppendRange(int start, int end)
        {
            var bytes = source.AsSpan();
            var cursor = new ShellSourceCursor(source, start, context);
            while (cursor.Position < end)
            {
                var position = cursor.Position;
                if (cursor.TrySkipLineContinuations())
                    continue;

                if (!cursor.IsQuoted && cursor.Current == (byte)'$')
                {
                    if (ShellSourceCursor.TryMatchLogicalText(bytes, position, "$'", out _))
                    {
                        if (!cursor.TryReadQuotedLiteral(out var literal))
                            builder.Append(bytes[position..Math.Min(cursor.Position, end)]);
                        else if (!ShellArgumentEscaping.TryAppendLiteralSingleQuoted(builder, literal.Bytes.Span))
                            throw new ByteMemoryBuilder.LimitExceededException();
                        continue;
                    }
                    if (ShellSourceCursor.TryMatchLogicalText(bytes, position, "$\"", out var afterQuote))
                    {
                        while (cursor.Position < afterQuote - 1)
                            cursor.Advance();
                        cursor.TryConsumeQuoteOrEscape();
                        builder.Append((byte)'"');
                        continue;
                    }
                }

                if (!cursor.TryConsumeQuoteOrEscape())
                {
                    if (cursor.SkipNestedExpansion(BracedParameterRecognition.All) is not ShellNestedExpansionScan.Complete)
                        cursor.Advance();
                    else if (ContainsEdit(position, cursor.Position))
                    {
                        if (bytes[position] == '`')
                            AppendBacktickSpelling(bytes[position..cursor.Position]);
                        else
                        {
                            var opening = ShellSourceCursor.SkipLineContinuations(bytes, position + 1);
                            builder.Append((byte)'$');
                            builder.Append(bytes[opening]);
                            AppendRange(opening + 1, cursor.Position);
                        }
                        continue;
                    }
                }
                builder.Append(bytes[position..Math.Min(cursor.Position, end)]);
            }
        }

        void AppendBacktickSpelling(ReadOnlySpan<byte> bytes)
        {
            // The legacy delimiter scanner treats its payload as backtick text.
            // An escape protects the next byte, and escaped LF disappears.
            for (var index = 0; index < bytes.Length; index++)
            {
                var current = bytes[index];
                if (current == '\\' && index + 1 < bytes.Length)
                {
                    var next = bytes[++index];
                    if (next == '\n') continue;
                    builder.Append(current);
                    builder.Append(next);
                }
                else builder.Append(current);
            }
        }
    }

    // Outer quoting selects quote removal on the entire word. The nested syntax
    // then contributes ordinary spelling bytes, including its inner quotes.
    private static ShellSource RemoveQuotes(ShellSource spelling, ShellScanContext context)
    {
        using var builder = context.CreateByteBuilder(spelling.Length);
        var bytes = spelling.AsSpan();
        var doubleQuoted = false;
        for (var position = 0; position < bytes.Length; position++)
        {
            var current = bytes[position];
            if (current == '"')
            {
                doubleQuoted = !doubleQuoted;
                continue;
            }
            if (current == '\'' && !doubleQuoted)
            {
                var tail = bytes[(position + 1)..];
                var end = tail.IndexOf((byte)'\'');
                if (end < 0)
                {
                    builder.Append(tail);
                    break;
                }
                builder.Append(tail[..end]);
                position += end + 1;
                continue;
            }
            if (current == '\\' && position + 1 < bytes.Length)
            {
                var escaped = bytes[++position];
                if (doubleQuoted && !ShellSourceCursor.IsDoubleQuoteEscape(escaped))
                    builder.Append(current);
                current = escaped;
            }
            builder.Append(current);
        }
        return ShellSource.FromBytes(builder.ToCompactMemory(context.SyntaxMemory));
    }
}

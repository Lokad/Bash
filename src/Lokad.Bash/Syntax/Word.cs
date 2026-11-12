using System;
using System.Collections.Generic;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a shell word with preserved quoting context.
/// </summary>
/// <remarks>
/// Words are stored as parts so later expansion can respect quoting
/// and escape behavior per BASH.md.
/// </remarks>
public sealed class Word
{
    public Word(IReadOnlyList<WordPart> parts)
    {
        Parts = parts;
        LiteralFields = PrepareLiteral(parts);

        static IReadOnlyList<ExpandedWord>? PrepareLiteral(IReadOnlyList<WordPart> parts)
        {
            ShellValue value;
            bool quoted;
            if (parts is [LiteralWordPart literal])
            {
                value = literal.Value;
                quoted = true;
            }
            else if (parts is [SourceWordPart source])
            {
                var bytes = source.Source.AsSpan();
                if (source.Kind == WordPartKind.DoubleQuoted && bytes.IndexOfAny("$\\`"u8) < 0)
                    quoted = true;
                else if (source.Kind == WordPartKind.Unquoted && bytes.Length > 0 && bytes[0] != (byte)'~'
                    && bytes.IndexOfAny("$\\`{}"u8) < 0)
                    quoted = false;
                else
                    return null;
                value = source.Source.ToValue();
            }
            else
                return null;
            return [new ExpandedWord([new ExpandedSegment(value, quoted)])];
        }
    }

    /// <summary>Frozen literal expansion scaffolding borrowing immutable syntax bytes.</summary>
    internal IReadOnlyList<ExpandedWord>? LiteralFields { get; }

    /// <summary>
    /// Gets the parts that compose this word.
    /// </summary>
    public IReadOnlyList<WordPart> Parts { get; }
}

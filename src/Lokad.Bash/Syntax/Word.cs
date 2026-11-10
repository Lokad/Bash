using System;
using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a shell word with preserved quoting context.
/// </summary>
/// <remarks>
/// Words are stored as parts so later expansion can respect quoting
/// and escape behavior per BASH.md.
/// </remarks>
public sealed class Word(IReadOnlyList<WordPart> parts)
{
    /// <summary>
    /// Gets the parts that compose this word.
    /// </summary>
    public IReadOnlyList<WordPart> Parts { get; } = parts;
}

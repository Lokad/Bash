using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

internal enum CaseContinuation { Stop, FallThrough, Retest }

/// <summary>
/// Represents a single case item with patterns and a body.
/// </summary>
public sealed class CaseItem(IReadOnlyList<Word> patterns, ICommand body)
{
    /// <summary> Gets the pattern words for this item. </summary>
    public IReadOnlyList<Word> Patterns { get; } = patterns;

    /// <summary> Gets the body list for this item. </summary>
    public ICommand Body { get; } = body;

    internal CaseContinuation Continuation { get; init; }
}

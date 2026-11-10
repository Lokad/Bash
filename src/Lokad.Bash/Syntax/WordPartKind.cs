namespace Lokad.Bash.Syntax;

/// <summary>
/// Indicates the quoting context of a word segment.
/// </summary>
/// <remarks>
/// Quote context must be preserved for later expansion and quote removal
/// decisions, per BASH.md.
/// </remarks>
public enum WordPartKind
{
    /// <summary>
    /// Unquoted text.
    /// </summary>
    Unquoted,

    /// <summary>
    /// Text within single quotes.
    /// </summary>
    SingleQuoted,

    /// <summary>
    /// Text within double quotes.
    /// </summary>
    DoubleQuoted,

    /// <summary>
    /// An escaped literal character, protected from expansion and splitting.
    /// </summary>
    Escaped,

    /// <summary>
    /// A command substitution of the form <c>$(...)</c> or <c>`...`</c>.
    /// </summary>
    CommandSubstitution,

    /// <summary>
    /// A command substitution of the form <c>$(...)</c> or <c>`...`</c> within double quotes.
    /// </summary>
    CommandSubstitutionQuoted,

    /// <summary>
    /// An expandable here-document body.
    /// </summary>
    HereDocument,

    /// <summary>A direct arithmetic selector whose expansion precedes numeric evaluation.</summary>
    ArithmeticSubscript
}

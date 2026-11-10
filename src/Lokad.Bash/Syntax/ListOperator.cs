namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents control operators that connect list elements.
/// </summary>
/// <remarks>
/// Sequence maps to semicolons and newlines per the list grammar in BASH.md.
/// </remarks>
public enum ListOperator
{
    /// <summary>
    /// Sequential execution: ";" or newline.
    /// </summary>
    Sequence,

    /// <summary>
    /// Logical AND list: "&amp;&amp;".
    /// </summary>
    And,

    /// <summary>
    /// Logical OR list: "||".
    /// </summary>
    Or
}

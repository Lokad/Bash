namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents control operators that connect list elements.
/// </summary>
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

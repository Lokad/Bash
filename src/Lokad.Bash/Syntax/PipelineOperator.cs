namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents pipeline operators that connect pipeline segments.
/// </summary>
/// <remarks>
/// The PipeAndError operator corresponds to |&amp; as described in BASH.md.
/// </remarks>
public enum PipelineOperator
{
    /// <summary>
    /// Pipe standard output: |.
    /// </summary>
    Pipe,

    /// <summary>
    /// Pipe standard output and standard error: |&amp;.
    /// </summary>
    PipeAndError
}

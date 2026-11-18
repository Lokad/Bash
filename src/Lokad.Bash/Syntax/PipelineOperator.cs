namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents pipeline operators that connect pipeline segments.
/// </summary>
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

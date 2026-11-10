namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a pipeline of commands connected by pipe operators.
/// </summary>
/// <remarks>
/// Pipelines are left-associative and may be negated with the ! reserved word,
/// per BASH.md.
/// </remarks>
/// <param name="Left">The left pipeline segment.</param>
/// <param name="Operator">The pipeline operator connecting the segments.</param>
/// <param name="Right">The right pipeline segment.</param>
/// <param name="Negated">Whether the pipeline is negated by !.</param>
public sealed record PipelineCommand(
    ICommand Left,
    PipelineOperator Operator,
    ICommand Right,
    bool Negated) : ICommand;

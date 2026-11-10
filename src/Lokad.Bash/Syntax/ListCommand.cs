namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a left-associative list of commands connected by a control operator.
/// </summary>
/// <remarks>
/// Lists follow the operator rules described in BASH.md.
/// </remarks>
/// <param name="Left">The left operand command.</param>
/// <param name="Operator">The list operator connecting the commands.</param>
/// <param name="Right">The right operand command.</param>
public sealed record ListCommand(ICommand Left, ListOperator Operator, ICommand Right) : ICommand;

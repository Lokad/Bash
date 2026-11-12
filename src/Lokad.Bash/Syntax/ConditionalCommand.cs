namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a conditional command of the form [[ expression ]].
/// </summary>
public sealed class ConditionalCommand : ICommand
{
    /// <summary>Parses the conditional structure, preserving word syntax for later expansion.</summary>
    public ConditionalCommand(ShellSource expression)
    {
        Expression = expression;
        if (!ConditionalParser.TryParse(expression, out var condition, out var error))
            throw error.ToException();
        Condition = condition;
    }

    internal ConditionalCommand(ShellSource expression, ConditionalExpression condition)
    {
        Expression = expression;
        Condition = condition;
    }

    /// <summary>The original, unexpanded conditional source.</summary>
    public ShellSource Expression { get; }
    internal ConditionalExpression Condition { get; }
}

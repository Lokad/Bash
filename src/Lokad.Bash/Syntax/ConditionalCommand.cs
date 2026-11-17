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
        if (!ConditionalParser.TryParse(expression, out var condition, out var spelling, out var error))
            throw error.ToException();
        Condition = condition;
        Spelling = spelling;
    }

    internal ConditionalCommand(ShellSource expression, ConditionalExpression condition)
    {
        Expression = expression;
        Condition = condition;
        Spelling = expression;
    }

    /// <summary>The original, unexpanded conditional source.</summary>
    public ShellSource Expression { get; }
    /// <summary>Lexically normalized spelling for replay, without a trailing source comment swallowing ]].</summary>
    internal ShellSource Spelling { get; }
    internal ConditionalExpression Condition { get; }
}

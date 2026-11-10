namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a conditional command of the form [[ expression ]].
/// </summary>
/// <param name="Expression">The unexpanded conditional source.</param>
public sealed record ConditionalCommand(ShellSource Expression) : ICommand;

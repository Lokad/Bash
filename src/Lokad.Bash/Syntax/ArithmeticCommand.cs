namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents an arithmetic command of the form ((expression)).
/// </summary>
/// <param name="Expression">The unexpanded arithmetic source.</param>
public sealed record ArithmeticCommand(ShellSource Expression) : ICommand;

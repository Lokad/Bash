namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents an until loop compound command.
/// </summary>
/// <param name="Condition">The condition list for the loop.</param>
/// <param name="Body">The body list for the loop.</param>
public sealed record UntilCommand(ICommand Condition, ICommand Body) : ICommand;

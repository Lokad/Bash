namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a command whose exit status is logically negated by the ! reserved word.
/// </summary>
/// <param name="Inner">The command being negated.</param>
public sealed record NegatedCommand(ICommand Inner) : ICommand;

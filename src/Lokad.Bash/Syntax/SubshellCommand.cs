namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a subshell command of the form (list).
/// </summary>
/// <remarks>
/// Subshells execute lists in a separate environment per BASH.md.
/// </remarks>
/// <param name="Body">The list body executed in the subshell.</param>
public sealed record SubshellCommand(ICommand Body) : ICommand;

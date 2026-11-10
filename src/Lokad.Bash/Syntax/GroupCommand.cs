namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a group command of the form { list; }.
/// </summary>
/// <remarks>
/// Group commands execute lists in the current environment and require
/// a terminating semicolon or newline before the closing brace, per BASH.md.
/// </remarks>
/// <param name="Body">The list body executed in the group.</param>
public sealed record GroupCommand(ICommand Body) : ICommand;

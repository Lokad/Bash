using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a command with trailing redirections applied to its execution scope.
/// </summary>
/// <param name="Body">The command body being redirected.</param>
/// <param name="Redirections">The redirections applied to the command.</param>
public sealed record RedirectedCommand(ICommand Body, IReadOnlyList<Redirection> Redirections) : ICommand;

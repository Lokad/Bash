using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents an if/elif/else compound command.
/// </summary>
/// <remarks>
/// The structure follows the grammar defined in BASH.md.
/// </remarks>
/// <param name="Condition">The condition list for the if clause.</param>
/// <param name="ThenBody">The body list for the then clause.</param>
/// <param name="ElifClauses">The optional elif clauses.</param>
/// <param name="ElseBody">The optional else body.</param>
public sealed record IfCommand(
    ICommand Condition,
    ICommand ThenBody,
    IReadOnlyList<ElifClause> ElifClauses,
    ICommand? ElseBody) : ICommand;

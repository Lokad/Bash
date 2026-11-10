using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a case compound command.
/// </summary>
/// <param name="Subject">The subject word to match.</param>
/// <param name="Items">The case items.</param>
public sealed record CaseCommand(Word Subject, IReadOnlyList<CaseItem> Items) : ICommand;

using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a for-in loop compound command.
/// </summary>
/// <param name="Name">The loop variable name.</param>
/// <param name="Items">The word list to iterate over.</param>
/// <param name="Body">The loop body list.</param>
public sealed record ForCommand(string Name, IReadOnlyList<Word> Items, ICommand Body) : ICommand
{
    // An invalid name still forms a complete loop. Retain its spelling for a reached diagnostic and replay.
    internal ShellValue InvalidName { get; init; }
}

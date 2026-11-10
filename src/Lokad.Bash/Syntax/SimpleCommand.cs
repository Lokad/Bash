using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a simple command made of assignments, words, and redirections.
/// </summary>
/// <remarks>
/// Captures the syntactic form before any expansions are applied.
/// Callers must not mutate the lists or declaration map after construction.
/// Declaration entries retain the parsed assignment independently of the word's ordinary form;
/// a compound operand's word contains only its leading target, not serialized list syntax.
/// </remarks>
/// <param name="Assignments">The leading assignment words.</param>
/// <param name="Words">The command name and arguments.</param>
/// <param name="Redirections">The redirections associated with the command.</param>
/// <param name="DeclarationAssignments">Assignments recognized in declaration position, keyed by their word identity.</param>
public sealed record SimpleCommand(
    IReadOnlyList<AssignmentWord> Assignments,
    IReadOnlyList<Word> Words,
    IReadOnlyList<Redirection> Redirections,
    IReadOnlyDictionary<Word, AssignmentWord> DeclarationAssignments) : ICommand
{
    internal static IReadOnlyDictionary<Word, AssignmentWord> NoDeclarationAssignments { get; } =
        new Dictionary<Word, AssignmentWord>();
}

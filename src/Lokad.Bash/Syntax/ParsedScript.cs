using System;
using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Holds the AST for a parsed script.
/// </summary>
/// <remarks>
/// The AST contains the syntax needed for evaluation and definition output without the original source.
/// Callers must not mutate retained collections anywhere in the AST.
/// </remarks>
public sealed class ParsedScript
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ParsedScript"/> class.
    /// </summary>
    /// <param name="commands">The top-level commands in the script.</param>
    public ParsedScript(IReadOnlyList<ICommand> commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    /// <summary>
    /// Gets the top-level commands in the script.
    /// </summary>
    public IReadOnlyList<ICommand> Commands { get; }
}

using System;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents an elif clause within an if command.
/// </summary>
public sealed class ElifClause
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ElifClause"/> class.
    /// </summary>
    /// <param name="condition">The condition list for the elif clause.</param>
    /// <param name="body">The body list for the elif clause.</param>
    public ElifClause(ICommand condition, ICommand body)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Body = body ?? throw new ArgumentNullException(nameof(body));
    }

    /// <summary>
    /// Gets the condition list for the elif clause.
    /// </summary>
    public ICommand Condition { get; }

    /// <summary>
    /// Gets the body list for the elif clause.
    /// </summary>
    public ICommand Body { get; }
}

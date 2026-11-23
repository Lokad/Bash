using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a conditional command of the form [[ expression ]].
/// </summary>
public sealed class ConditionalCommand : ICommand
{
    /// <summary>Parses the conditional structure, preserving word syntax for later expansion.</summary>
    public ConditionalCommand(ShellSource expression)
    {
        Expression = expression;
        if (!ConditionalParser.TryParse(expression, out var condition, out var spelling, out var error))
            throw error.ToException();
        Condition = condition;
        Spelling = spelling;
    }

    internal ConditionalCommand(ShellSource expression, ConditionalExpression condition, ConditionalSpelling spelling, bool hasNormalizedSpelling)
    {
        Expression = expression;
        Condition = condition;
        Spelling = spelling;
        HasNormalizedSpelling = hasNormalizedSpelling;
    }

    /// <summary>The original, unexpanded conditional source.</summary>
    public ShellSource Expression { get; }
    /// <summary>Canonical conditional syntax for definition display and export.</summary>
    internal ConditionalSpelling Spelling { get; }
    internal bool HasNormalizedSpelling { get; }
    internal ConditionalExpression Condition { get; }
}

/// <summary>Spelling at one conditional level with nested captures rendered on demand.</summary>
internal readonly record struct ConditionalSpelling(ShellSource Source, IReadOnlyList<ConditionalSpellingCapture>? Captures);

internal readonly record struct ConditionalSpellingCapture(int Offset, CommandSubstitutionSyntax Syntax);

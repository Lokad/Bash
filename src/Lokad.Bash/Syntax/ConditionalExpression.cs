using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Syntax;

/// <summary>Immutable conditional structure; operands retain shell words until execution reaches them.</summary>
internal abstract record ConditionalExpression
{
    internal sealed record NonEmpty(Word Operand) : ConditionalExpression;
    internal sealed record Unary(ShellPredicates.UnaryPredicate Predicate, Word Operand) : ConditionalExpression;
    internal sealed record Binary(ShellPredicates.BinaryPredicate Predicate, Word Left, Word Right) : ConditionalExpression;
    internal sealed record Negated(ConditionalExpression Inner) : ConditionalExpression;
    internal sealed record Logical(bool And, ConditionalExpression Left, ConditionalExpression Right) : ConditionalExpression;
}

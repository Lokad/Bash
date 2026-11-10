namespace Lokad.Bash.Evaluation;

/// <summary>Shell predicate operands before path resolution or descriptor parsing in the evaluator.</summary>
/// <param name="SecondaryOperand">The second raw operand for comparisons, or null for unary predicates.</param>
/// <param name="Kind">The filesystem predicate to evaluate.</param>
/// <param name="Operand">The original predicate operand.</param>
internal readonly record struct UnresolvedFileTest(FilePredicateKind Kind, string Operand, string? SecondaryOperand);

namespace Lokad.Bash.Evaluation;

/// <summary>Shell predicate operands before path or descriptor resolution in the evaluator.</summary>
/// <param name="SecondaryOperand">The second raw operand for comparisons, or null for unary predicates.</param>
/// <param name="Kind">The filesystem predicate to evaluate.</param>
/// <param name="Operand">The original predicate operand.</param>
internal readonly record struct UnresolvedFileTest(FilePredicateKind Kind, string Operand, string? SecondaryOperand)
{
    internal LogicalDescriptor TerminalDescriptor { get; private init; }

    internal static bool TryCreateTerminal(long number, out UnresolvedFileTest test)
    {
        test = default;
        if (number is < 0 or > int.MaxValue)
            return false;

        test = new(FilePredicateKind.Terminal, string.Empty, null)
        {
            TerminalDescriptor = new LogicalDescriptor((int)number)
        };
        return true;
    }
}

using System;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Implements evaluation rules shared by control flow constructs.
/// </summary>
internal static class EvaluationRules
{
    public static int ApplyNegation(int exitCode, bool negated)
    {
        if (!negated) return exitCode;
        return exitCode == 0 ? 1 : 0;
    }

    public static bool ShouldExecuteRight(ListOperator @operator, int exitCode)
    {
        return @operator switch
        {
            ListOperator.Sequence => true,
            ListOperator.And => exitCode == 0,
            ListOperator.Or => exitCode != 0,
            _ => throw new NotSupportedException($"Unsupported list operator '{@operator}'.")
        };
    }
}

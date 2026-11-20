using System.Threading.Tasks;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    /// <summary>Evaluates the stored addend and the new value in shell arithmetic order.</summary>
    internal static async ValueTask<ShellValue?> EvaluateIntegerAssignmentAsync(
        ShellValue value, ShellValue? addend, ScopedEvaluation scope, ShellValue command)
    {
        var previous = 0L;
        if (addend is { } left)
        {
            var evaluated = await EvaluateAsync(left).ConfigureAwait(false);
            if (!evaluated.HasValue)
                return null;
            previous = evaluated.Value;
        }
        var number = await EvaluateAsync(value).ConfigureAwait(false);
        if (!number.HasValue)
            return null;
        return ShellValue.FromInteger(unchecked(previous + number.Value));

        ValueTask<long?> EvaluateAsync(ShellValue operand)
        {
            var payload = Integers.TrimWhitespace(operand.Bytes.Span);
            if (payload.IsEmpty)
                return ValueTask.FromResult((long?)0);
            if (ArithmeticEvaluation.TryParseIntegerLiteral(payload, scope.Effects.CancellationToken, out var number))
                return ValueTask.FromResult((long?)number);
            return new ValueTask<long?>(EvaluateArithmeticAsync(
                ArithmeticOperand.FromValue(operand), scope, ArithmeticFailureMode.IntegerAssignment, null, command));
        }
    }
}

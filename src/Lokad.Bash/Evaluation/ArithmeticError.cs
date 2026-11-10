using System;

namespace Lokad.Bash.Evaluation;

/// <summary>Why arithmetic evaluation stopped; earlier shell assignments remain applied.</summary>
internal enum ArithmeticErrorKind
{
    InvalidExpression,
    DivisionByZero,
    NegativeExponent,
    NestingLimit,
    ReadonlyVariable,
    InvalidTarget,
    BadArraySubscript,
}

/// <summary>An arithmetic failure and the expression or assignment target to report.</summary>
internal sealed record ArithmeticError(ArithmeticErrorKind Kind, ShellValue Subject)
{
    public string Message => Kind switch
    {
        ArithmeticErrorKind.InvalidExpression => "invalid arithmetic expression",
        ArithmeticErrorKind.DivisionByZero => "division by 0",
        ArithmeticErrorKind.NegativeExponent => "exponent less than 0",
        ArithmeticErrorKind.NestingLimit => "arithmetic nesting limit exceeded",
        ArithmeticErrorKind.ReadonlyVariable => "readonly variable",
        ArithmeticErrorKind.InvalidTarget => "invalid arithmetic assignment target",
        ArithmeticErrorKind.BadArraySubscript => "bad array subscript",
        _ => throw new NotSupportedException($"Unsupported arithmetic error '{Kind}'.")
    };
}

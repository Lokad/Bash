using System;

namespace Lokad.Bash.Evaluation;

/// <summary>Why arithmetic evaluation stopped; earlier shell assignments remain applied.</summary>
internal enum ArithmeticErrorKind
{
    InvalidExpression,
    UnexpectedToken,
    OperandExpected,
    InvalidOperator,
    MissingParenthesis,
    MissingConditionalColon,
    ExpressionExpected,
    AssignmentToNonVariable,
    IncrementRequiresVariable,
    DecrementRequiresVariable,
    InvalidNumber,
    InvalidArithmeticBase,
    InvalidIntegerConstant,
    DigitOutsideBase,
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
    /// <summary>The borrowed spelling of a parser error token, when supplied by arithmetic evaluation.</summary>
    public ShellValue? Token { get; init; }

    public string Message => Kind switch
    {
        ArithmeticErrorKind.InvalidExpression => "invalid arithmetic expression",
        ArithmeticErrorKind.UnexpectedToken => "syntax error in expression",
        ArithmeticErrorKind.OperandExpected => "syntax error: operand expected",
        ArithmeticErrorKind.InvalidOperator => "syntax error: invalid arithmetic operator",
        ArithmeticErrorKind.MissingParenthesis => "missing `)'",
        ArithmeticErrorKind.MissingConditionalColon => "`:' expected for conditional expression",
        ArithmeticErrorKind.ExpressionExpected => "expression expected",
        ArithmeticErrorKind.AssignmentToNonVariable => "attempted assignment to non-variable",
        ArithmeticErrorKind.IncrementRequiresVariable => "++: assignment requires lvalue",
        ArithmeticErrorKind.DecrementRequiresVariable => "--: assignment requires lvalue",
        ArithmeticErrorKind.InvalidNumber => "invalid number",
        ArithmeticErrorKind.InvalidArithmeticBase => "invalid arithmetic base",
        ArithmeticErrorKind.InvalidIntegerConstant => "invalid integer constant",
        ArithmeticErrorKind.DigitOutsideBase => "value too great for base",
        ArithmeticErrorKind.DivisionByZero => "division by 0",
        ArithmeticErrorKind.NegativeExponent => "exponent less than 0",
        ArithmeticErrorKind.NestingLimit => "arithmetic nesting limit exceeded",
        ArithmeticErrorKind.ReadonlyVariable => "readonly variable",
        ArithmeticErrorKind.InvalidTarget => "invalid arithmetic assignment target",
        ArithmeticErrorKind.BadArraySubscript => "bad array subscript",
        _ => throw new NotSupportedException($"Unsupported arithmetic error '{Kind}'.")
    };
}

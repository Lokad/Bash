using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary> Describes whether a variable operand is a plain name or an explicit assignment. </summary>
internal enum VariableOperandKind
{
    Name,
    Assignment
}

/// <summary> Captures the normalized pieces of a variable declaration operand. </summary>
internal readonly record struct VariableOperand(
    VariableOperandKind Kind,
    VariableReference Reference,
    ShellValue Value);

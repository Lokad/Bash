using System.Collections.Generic;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Expanded arithmetic bytes with the source boundaries of eagerly prepared selectors.</summary>
internal sealed class ArithmeticOperand(
    ShellValue value, IReadOnlyDictionary<int, ArithmeticOperand.Subscript> subscripts, ArithmeticBracketMode brackets)
{
    private static readonly IReadOnlyDictionary<int, Subscript> NoSubscripts = new Dictionary<int, Subscript>();

    public ShellValue Value { get; } = value;
    public ArithmeticBracketMode Brackets { get; } = brackets;
    public IReadOnlyDictionary<int, Subscript> Subscripts { get; } = subscripts;

    /// <summary>Operand bytes without prepared shell selectors, as used by let and text adapters.</summary>
    public static ArithmeticOperand FromValue(ShellValue value) => new(value, NoSubscripts, ArithmeticBracketMode.References);

    public static ArithmeticOperand FromIndex(ExpandedArraySubscript subscript) =>
        new(subscript.Value, NoSubscripts, subscript.Brackets);

    /// <summary>Original syntax for diagnostics and the literal bytes produced by its one expansion.</summary>
    internal sealed record Subscript(ShellSource Source, ExpandedArraySubscript Expansion);
}

/// <summary>A parsed reference with optional, already expanded selector bytes.</summary>
internal readonly record struct ArithmeticReference(VariableReference Syntax, ExpandedArraySubscript? ExpandedSubscript);

/// <summary>Whether brackets in numeric operand bytes retain array-reference syntax.</summary>
internal enum ArithmeticBracketMode { References, Literal }

/// <summary>One expanded selector value and its numeric bracket provenance; associative keys use the bytes literally.</summary>
internal readonly record struct ExpandedArraySubscript(ShellValue Value, ArithmeticBracketMode Brackets);

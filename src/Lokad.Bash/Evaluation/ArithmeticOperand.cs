using System.Collections.Generic;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Expanded arithmetic bytes with the source boundaries of eagerly prepared selectors.</summary>
internal sealed class ArithmeticOperand
{
    public ArithmeticOperand(ShellValue value, IReadOnlyDictionary<int, Subscript> subscripts, ArithmeticBracketMode brackets)
        : this(value, subscripts, brackets, null) { }

    public ArithmeticOperand(ShellValue value, IReadOnlyDictionary<int, Subscript> subscripts, ArithmeticBracketMode brackets,
        ArithmeticEvaluation.LexicalSyntax? syntax)
    {
        Value = value;
        Subscripts = subscripts;
        Brackets = brackets;
        Syntax = syntax;
    }
    private static readonly IReadOnlyDictionary<int, Subscript> NoSubscripts = new Dictionary<int, Subscript>();

    public ShellValue Value { get; }
    public ArithmeticBracketMode Brackets { get; }
    public IReadOnlyDictionary<int, Subscript> Subscripts { get; }
    public ArithmeticEvaluation.LexicalSyntax? Syntax { get; }

    /// <summary>Operand bytes without prepared shell selectors, as used by let and text adapters.</summary>
    public static ArithmeticOperand FromValue(ShellValue value) => new(value, NoSubscripts, ArithmeticBracketMode.References);

    internal static ArithmeticOperand FromSyntax(ShellSource source, ArithmeticEvaluation.LexicalSyntax syntax) =>
        new(source.ToValue(), NoSubscripts, ArithmeticBracketMode.References, syntax);

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

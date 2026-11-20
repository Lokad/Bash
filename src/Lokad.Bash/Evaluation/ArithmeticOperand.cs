using System.Collections.Generic;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Expanded arithmetic bytes with the source boundaries of eagerly prepared selectors.</summary>
internal sealed class ArithmeticOperand
{
    public ArithmeticOperand(ShellValue value, IReadOnlyDictionary<int, Subscript> subscripts, ArithmeticEvaluation.LexicalSyntax? syntax)
    {
        Value = value;
        Subscripts = subscripts;
        Syntax = syntax;
    }
    private static readonly IReadOnlyDictionary<int, Subscript> NoSubscripts = new Dictionary<int, Subscript>();

    public ShellValue Value { get; }
    public bool IsExpandedIndex { get; private init; }
    public IReadOnlyDictionary<int, Subscript> Subscripts { get; }
    public ArithmeticEvaluation.LexicalSyntax? Syntax { get; }

    /// <summary>Operand bytes without prepared shell selectors, as used by let and text adapters.</summary>
    public static ArithmeticOperand FromValue(ShellValue value) => new(value, NoSubscripts, null);

    internal static ArithmeticOperand FromSyntax(ShellSource source, ArithmeticEvaluation.LexicalSyntax syntax) =>
        new(source.ToValue(), NoSubscripts, syntax);

    public static ArithmeticOperand FromExpandedIndex(ShellValue value) => new(value, NoSubscripts, null) { IsExpandedIndex = true };

    /// <summary>Original syntax for diagnostics and the literal bytes produced by its one expansion.</summary>
    internal sealed record Subscript(ShellSource Source, ExpandedArraySubscript Expansion);
}

/// <summary>A parsed reference with optional, already expanded selector bytes.</summary>
internal readonly record struct ArithmeticReference(VariableReference Syntax, ExpandedArraySubscript? ExpandedSubscript);

/// <summary>Prepared selector bytes and whether to quote them for indexed arithmetic. Associative keys use the bytes literally.</summary>
internal readonly record struct ExpandedArraySubscript(ShellValue Value, bool QuoteForArithmetic);

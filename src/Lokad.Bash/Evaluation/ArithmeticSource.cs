using System;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Syntax;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Script-owned arithmetic syntax. Expansion and binding outcomes are never cached.</summary>
internal sealed class ArithmeticSource
{
    public ShellSource Source { get; }
    public ArithmeticEvaluation.LexicalSyntax? Syntax { get; }
    public ArithmeticOperand? DirectOperand { get; }

    public ArithmeticSource(ShellSource source) : this(source, null)
    {
    }

    internal ArithmeticSource(ShellSource source, MemoryCapacity? memory)
    {
        Source = source;
        var bytes = source.AsSpan();
        // A bracket rules out the direct operand. If it is the first marker,
        // check only its suffix for expansion that may change the arithmetic syntax.
        var markers = "[$'\"\\`"u8;
        var markerOffset = bytes.IndexOfAny(markers);
        if (markerOffset >= 0
            && (bytes[markerOffset] != (byte)'[' || bytes[(markerOffset + 1)..].ContainsAny(markers[1..])))
            return;
        Syntax = new ArithmeticEvaluation.LexicalSyntax(source, memory);
        if (markerOffset < 0)
            DirectOperand = ArithmeticOperand.FromSyntax(source, Syntax);
    }
}

/// <summary>Immutable loop clauses, split once with the same quote/substitution boundary rules.</summary>
internal sealed class ArithmeticForSyntax
{
    public ShellSource Source { get; }
    public ArithmeticSource Initialization { get; }
    public ArithmeticSource Condition { get; }
    public ArithmeticSource Update { get; }

    public ArithmeticForSyntax(ShellSource source)
        : this(source, Split(source, ShellSourceCursor.FindArithmeticForSeparators(source), false), null)
    {
    }

    private ArithmeticForSyntax(ShellSource source,
        (ShellSource Initialization, ShellSource Condition, ShellSource Update) clauses, MemoryCapacity? memory)
    {
        Source = source;
        Initialization = new ArithmeticSource(clauses.Initialization, memory);
        Condition = new ArithmeticSource(clauses.Condition, memory);
        Update = new ArithmeticSource(clauses.Update, memory);
    }

    internal static bool TryParse(ShellSource source, ArithmeticForSeparators separators, MemoryCapacity? memory,
        [NotNullWhen(true)] out ArithmeticForSyntax? syntax, [NotNullWhen(false)] out string? error)
    {
        error = separators.Second == null ? "Arithmetic for header requires two semicolons."
            : separators.HasExtra ? "Arithmetic for header contains an extra semicolon." : null;
        if (error != null)
        {
            syntax = null;
            return false;
        }
        syntax = new ArithmeticForSyntax(source, Split(source, separators, true), memory);
        return true;
    }

    private static (ShellSource Initialization, ShellSource Condition, ShellSource Update) Split(
        ShellSource expression, ArithmeticForSeparators separators, bool preserveLineOnlyCondition)
    {
        if (separators.First is not int firstSeparator)
            return (PrepareClause(expression, preserveLineOnly: false), ShellSource.Empty, ShellSource.Empty);

        var init = PrepareClause(expression.Slice(0, firstSeparator), preserveLineOnly: false);
        var conditionEnd = separators.Second ?? expression.Length;
        var conditionSource = expression.Slice(firstSeparator + 1, conditionEnd - firstSeparator - 1);
        var condition = PrepareClause(conditionSource, preserveLineOnlyCondition);
        var update = separators.Second is int secondSeparator
            ? PrepareClause(expression.Slice(secondSeparator + 1), preserveLineOnly: false) : ShellSource.Empty;
        return (init, condition, update);

        static ShellSource PrepareClause(ShellSource clause, bool preserveLineOnly)
        {
            var text = clause.AsSpan().TrimStart(" \t"u8);
            // A condition containing only blanks and LF still evaluates as an explicit zero.
            if (text.TrimEnd(" \t\n"u8).IsEmpty)
                return preserveLineOnly && !text.IsEmpty ? clause : ShellSource.Empty;
            return clause.Slice(clause.Length - text.Length);
        }
    }
}

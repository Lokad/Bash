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
        // Shell expansion can splice fresh arithmetic syntax. Retain lexical structure
        // only when expansion cannot change these bytes; selectors still prepare at runtime.
        if (bytes.IndexOfAny("$'\"\\`"u8) >= 0)
            return;
        Syntax = new ArithmeticEvaluation.LexicalSyntax(source, memory);
        if (!bytes.Contains((byte)'['))
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
            return (expression.TrimBlanks(), ShellSource.Empty, ShellSource.Empty);

        var init = expression.Slice(0, firstSeparator).TrimBlanks();
        var conditionEnd = separators.Second ?? expression.Length;
        var conditionSource = expression.Slice(firstSeparator + 1, conditionEnd - firstSeparator - 1);
        var condition = conditionSource.TrimBlanks();
        // A clause consisting of blanks and physical LF still evaluates as an explicit zero.
        if (preserveLineOnlyCondition && condition.IsEmpty && conditionSource.AsSpan().Contains((byte)'\n'))
            condition = conditionSource;
        var update = separators.Second is int secondSeparator
            ? expression.Slice(secondSeparator + 1).TrimBlanks() : ShellSource.Empty;
        return (init, condition, update);
    }
}

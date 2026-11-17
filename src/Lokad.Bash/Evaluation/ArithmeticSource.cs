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

    public ArithmeticForSyntax(ShellSource source) : this(source, null)
    {
    }

    internal ArithmeticForSyntax(ShellSource source, MemoryCapacity? memory)
    {
        Source = source;
        Split(source, memory, out var init, out var condition, out var update);
        Initialization = new ArithmeticSource(init, memory);
        Condition = new ArithmeticSource(condition, memory);
        Update = new ArithmeticSource(update, memory);

        static void Split(ShellSource expression, MemoryCapacity? memory,
            out ShellSource init, out ShellSource condition, out ShellSource update)
        {
            var cursor = new ShellSourceCursor(expression, 0, new ShellScanContext { SyntaxMemory = memory });
            var firstSeparator = ReadSeparator();
            if (firstSeparator < 0)
            {
                init = expression.TrimBlanks();
                condition = ShellSource.Empty;
                update = ShellSource.Empty;
                return;
            }
            init = expression.Slice(0, firstSeparator).TrimBlanks();
            var secondSeparator = ReadSeparator();
            condition = expression.Slice(firstSeparator + 1,
                (secondSeparator < 0 ? expression.Length : secondSeparator) - firstSeparator - 1).TrimBlanks();
            update = secondSeparator < 0 ? ShellSource.Empty : expression.Slice(secondSeparator + 1).TrimBlanks();

            int ReadSeparator()
            {
                while (!cursor.IsAtEnd)
                {
                    // A substitution's command-list semicolons do not separate loop clauses.
                    if (cursor.TryConsumeQuoteOrEscape()
                        || cursor.SkipNestedExpansion(BracedParameterRecognition.All) is ShellNestedExpansionScan.Complete)
                        continue;
                    var position = cursor.Position;
                    var separator = !cursor.IsQuoted && cursor.Current == (byte)';';
                    cursor.Advance();
                    if (separator)
                        return position;
                }
                return -1;
            }
        }
    }
}

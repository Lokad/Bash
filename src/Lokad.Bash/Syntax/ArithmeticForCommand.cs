namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents an arithmetic for loop of the form for ((expression)); do ...; done.
/// </summary>
/// <param name="Expression">The unexpanded arithmetic clauses.</param>
/// <param name="Body">The loop body list.</param>
public sealed record ArithmeticForCommand(ShellSource Expression, ICommand Body) : ICommand
{
    /// <summary>Decomposes loop clauses without splitting quotes or nested substitutions.</summary>
    internal void Split(out ShellSource init, out ShellSource condition, out ShellSource update)
    {
        var expression = Expression;
        var cursor = new ShellSourceCursor(Expression, 0);
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

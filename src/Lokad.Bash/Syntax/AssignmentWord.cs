using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a variable assignment word in a simple command.
/// </summary>
public readonly struct AssignmentWord
{
    /// <summary> Stores an empty word used when representing compound array assignments. </summary>
    private static readonly Word EmptyWord = new([]);

    /// <summary>Creates a scalar or element assignment with an unexpanded RHS.</summary>
    public AssignmentWord(ShellSource target, Word value, AssignmentOperator operation)
    {
        Target = target;
        Value = value;
        Operator = operation;
        ArrayValues = null;
    }

    /// <summary>Creates a compound assignment. The caller must not mutate the item list.</summary>
    public AssignmentWord(ShellSource target, IReadOnlyList<CompoundArrayItem> arrayValues, AssignmentOperator operation)
    {
        Target = target;
        Value = EmptyWord;
        Operator = operation;
        ArrayValues = arrayValues;
    }

    /// <summary>Finds assignment punctuation outside selectors without validating or expanding the target.</summary>
    internal static bool TrySplitOperand(
        ShellSource source, out ShellSource target, out ShellSource value, out AssignmentOperator operation)
    {
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == (byte)'[')
            {
                if (!ArraySubscriptScanner.TryScan(source, index, out var closing))
                    break;
                index = closing;
                continue;
            }
            if (source[index] != (byte)'=') continue;

            var append = index > 0 && source[index - 1] == (byte)'+';
            target = source.Slice(0, append ? index - 1 : index);
            value = source.Slice(index + 1);
            operation = append ? AssignmentOperator.Append : AssignmentOperator.Replace;
            return true;
        }
        target = source;
        value = ShellSource.Empty;
        operation = AssignmentOperator.Replace;
        return false;
    }

    /// <summary>
    /// Gets the unexpanded variable name and optional selector.
    /// </summary>
    public ShellSource Target { get; }

    /// <summary>
    /// Gets the value word.
    /// </summary>
    public Word Value { get; }

    /// <summary>Gets whether to replace or append to the selected value.</summary>
    public AssignmentOperator Operator { get; }

    /// <summary>
    /// Gets the compound array values, if any.
    /// </summary>
    public IReadOnlyList<CompoundArrayItem>? ArrayValues { get; }

    /// <summary>
    /// Gets whether this assignment is a compound array assignment.
    /// </summary>
    public bool IsCompoundArrayAssignment => ArrayValues != null;
}

/// <summary>The operation written in an assignment.</summary>
public enum AssignmentOperator
{
    Replace,
    Append,
}

/// <summary>Retains the whole word for pair-form arrays and any syntactic element assignment.</summary>
public sealed record CompoundArrayItem(Word Word, CompoundArrayElement? Element);

/// <summary>Retains selector source until the destination array kind is known.</summary>
public sealed record CompoundArrayElement(ShellSource Subscript, Word Value, AssignmentOperator Operator);

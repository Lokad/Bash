using System.Collections.Generic;
using System.Threading;

namespace Lokad.Bash.Syntax;

/// <summary>Immutable list navigation; leaves still run through the ordinary evaluator.</summary>
internal sealed class ListTraversal
{
    // List commands mark branches. Every executable step holds a non-list command.
    internal readonly struct Step(ICommand command, int endExclusive, bool errexitIgnored, bool startsCommandUnit, bool startsReaderUnit)
    {
        public ICommand Command { get; } = command;
        public int EndExclusive { get; } = endExclusive;
        public bool ErrexitIgnored { get; } = errexitIgnored;
        public bool StartsCommandUnit { get; } = startsCommandUnit;
        public bool StartsReaderUnit { get; } = startsReaderUnit;
    }

    public IReadOnlyList<Step> Steps { get; }

    private const int AfterLeft = -1;
    private readonly record struct Continuation(ListCommand Command, int BranchStep, bool ErrexitIgnored);

    public ListTraversal(ListCommand root, CancellationToken cancellationToken)
    {
        var steps = new List<Step>();
        var pending = new Stack<Continuation>();
        ICommand? command = root;
        var errexitIgnored = false;
        var startsCommandUnit = false;
        var startsReaderUnit = false;
        while (command != null || pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command is ListCommand nested)
            {
                pending.Push(new Continuation(nested, AfterLeft, errexitIgnored));
                errexitIgnored = errexitIgnored || nested.Operator is ListOperator.And or ListOperator.Or;
                command = nested.Left;
                continue;
            }
            if (command != null)
            {
                steps.Add(new Step(command, endExclusive: 0, errexitIgnored, startsCommandUnit, startsReaderUnit));
                startsCommandUnit = false;
                startsReaderUnit = false;
                command = null;
                continue;
            }

            var continuation = pending.Pop();
            var list = continuation.Command;
            if (continuation.BranchStep != AfterLeft)
            {
                steps[continuation.BranchStep] = new Step(list, steps.Count, errexitIgnored: false,
                    startsCommandUnit: false, startsReaderUnit: false);
                continue;
            }

            startsCommandUnit |= list.StartsCommandUnit;
            startsReaderUnit |= list.StartsReaderUnit;
            if (list.Operator != ListOperator.Sequence)
            {
                // Fill the branch target when its right subtree ends.
                pending.Push(continuation with { BranchStep = steps.Count });
                steps.Add(default);
            }
            command = list.Right;
            errexitIgnored = continuation.ErrexitIgnored;
        }
        Steps = steps;
    }
}

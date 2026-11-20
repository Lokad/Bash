using System.Collections.Generic;
using System.Threading;

namespace Lokad.Bash.Syntax;

/// <summary>Immutable list navigation; leaves still run through the ordinary evaluator.</summary>
internal sealed class ListTraversal
{
    // List commands mark branches. Every executable step holds a non-list command.
    internal readonly struct Step(ICommand command, int endExclusive, bool errexitIgnored, bool startsCommandUnit)
    {
        public ICommand Command { get; } = command;
        public int EndExclusive { get; } = endExclusive;
        public bool ErrexitIgnored { get; } = errexitIgnored;
        public bool StartsCommandUnit { get; } = startsCommandUnit;
    }

    public IReadOnlyList<Step> Steps { get; }

    private enum Preparation { Visit, Right, Finish }

    public ListTraversal(ListCommand root, CancellationToken cancellationToken)
    {
        var steps = new List<Step>();
        var pending = new Stack<(ICommand Command, bool Ignored, Preparation Phase, int Branch)>();
        pending.Push((root, false, Preparation.Visit, 0));
        var startsCommandUnit = false;
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Phase == Preparation.Finish)
            {
                steps[item.Branch] = new Step(item.Command, steps.Count, errexitIgnored: false, startsCommandUnit: false);
                continue;
            }
            if (item.Phase == Preparation.Right)
            {
                var list = (ListCommand)item.Command;
                startsCommandUnit |= list.StartsCommandUnit;
                if (list.Operator != ListOperator.Sequence)
                {
                    var branch = steps.Count;
                    // Finish fills the reserved slot before the traversal can be published.
                    steps.Add(default);
                    pending.Push((list, item.Ignored, Preparation.Finish, branch));
                }
                pending.Push((list.Right, item.Ignored, Preparation.Visit, 0));
                continue;
            }
            if (item.Command is ListCommand nested)
            {
                pending.Push((nested, item.Ignored, Preparation.Right, 0));
                pending.Push((nested.Left, item.Ignored || nested.Operator is ListOperator.And or ListOperator.Or,
                    Preparation.Visit, 0));
            }
            else
            {
                steps.Add(new Step(item.Command, endExclusive: 0, item.Ignored, startsCommandUnit));
                startsCommandUnit = false;
            }
        }
        Steps = steps;
    }
}

using System.Collections.Generic;
using System.Threading;

namespace Lokad.Bash.Syntax;

/// <summary>Immutable list navigation; leaves still run through the ordinary evaluator.</summary>
internal sealed class ListTraversal
{
    internal abstract record Step
    {
        public sealed record Execute(ICommand Command, bool ErrexitIgnored) : Step;
        public sealed record Select(ListOperator Operator, int EndExclusive) : Step;
    }

    public IReadOnlyList<Step> Steps { get; }

    private enum Preparation { Visit, Right, Finish }

    public ListTraversal(ListCommand root, CancellationToken cancellationToken)
    {
        var steps = new List<Step>();
        var pending = new Stack<(ICommand Command, bool Ignored, Preparation Phase, int Branch)>();
        pending.Push((root, false, Preparation.Visit, 0));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Phase == Preparation.Finish)
            {
                steps[item.Branch] = new Step.Select(((ListCommand)item.Command).Operator, steps.Count);
                continue;
            }
            if (item.Phase == Preparation.Right)
            {
                var list = (ListCommand)item.Command;
                if (list.Operator != ListOperator.Sequence)
                {
                    var branch = steps.Count;
                    steps.Add(new Step.Select(list.Operator, 0));
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
                steps.Add(new Step.Execute(item.Command, item.Ignored));
        }
        Steps = steps.ToArray();
    }
}

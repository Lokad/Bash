using System;
using System.Threading;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a left-associative list of commands connected by a control operator.
/// </summary>
/// <remarks>
/// Lists follow the operator rules described in BASH.md.
/// </remarks>
public sealed record ListCommand : ICommand
{
    private ICommand _left;
    private ListOperator _operator;
    private ICommand _right;
    private ListTraversal? _traversal;

    public ICommand Left { get => _left; init { _left = value; _traversal = null; } }
    public ListOperator Operator { get => _operator; init { _operator = value; _traversal = null; } }
    public ICommand Right { get => _right; init { _right = value; _traversal = null; } }

    public ListCommand(ICommand Left, ListOperator Operator, ICommand Right)
    {
        _left = Left;
        _operator = Operator;
        _right = Right;
    }

    internal ListTraversal GetTraversal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _traversal) is { } traversal)
            return traversal;
        var prepared = new ListTraversal(this, cancellationToken);
        // Concurrent readers can prepare independently; publication never waits for an evaluator.
        return Interlocked.CompareExchange(ref _traversal, prepared, null) ?? prepared;
    }

    public void Deconstruct(out ICommand Left, out ListOperator Operator, out ICommand Right) =>
        (Left, Operator, Right) = (this.Left, this.Operator, this.Right);
    public bool Equals(ListCommand? other) => other != null && Equals(Left, other.Left)
        && Operator == other.Operator && Equals(Right, other.Right);
    public override int GetHashCode() => HashCode.Combine(Left, Operator, Right);
}

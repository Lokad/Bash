using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ListTraversalReuseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeepSourceTreesExecuteRepeatedlyWithoutRetainingCursorOrScope(bool rightAssociated)
    {
        var increment = Assert.Single(Parser.Parse("((count+=1))").Commands);
        ICommand tree = increment;
        for (var index = 1; index < 5_000; index++)
            tree = rightAssociated ? new ListCommand(increment, ListOperator.Sequence, tree)
                : new ListCommand(tree, ListOperator.Sequence, increment);
        tree = new ListCommand(tree, ListOperator.Sequence,
            Assert.Single(Parser.Parse("printf '%s' \"$count\"").Commands));
        var script = new ParsedScript([tree]);
        for (var repetition = 0; repetition < 2; repetition++)
        {
            await using var host = new TestHost();
            var result = await new Engine().RunAsync(new Evaluation.Evaluation(script), host, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("5000", host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
        }
    }

    [Theory]
    [InlineData("false", ListOperator.And, 1)]
    [InlineData("true", ListOperator.Or, 0)]
    public async Task ShortCircuitSkipsAnEntireNestedRightTree(string left, ListOperator op, int expectedExit)
    {
        var leaf = Assert.Single(Parser.Parse("printf wrong").Commands);
        var tree = new ListCommand(Assert.Single(Parser.Parse(left).Commands), op,
            new ListCommand(new ListCommand(leaf, ListOperator.Or, leaf), ListOperator.Sequence, leaf));
        await using var host = new TestHost();
        var result = await new Engine().RunAsync(new Evaluation.Evaluation(new ParsedScript([tree])), host, CancellationToken.None);
        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task SharedTraversalKeepsPendingOperationsAndStatusesIndependent()
    {
        var script = Parser.Parse("first && second || third");
        var first = new Evaluation.Evaluation(script);
        var second = new Evaluation.Evaluation(script);
        var firstPending = Assert.IsType<RunCommand>(await first.GetNextStep());
        var secondPending = Assert.IsType<RunCommand>(await second.GetNextStep());
        Assert.Equal("first", firstPending.Command.CommandName);
        Assert.Equal("first", secondPending.Command.CommandName);
        firstPending.SetResult(new CommandExecutionResult.Completed(0));
        secondPending.SetResult(new CommandExecutionResult.Completed(1));
        var firstNext = Assert.IsType<RunCommand>(await first.GetNextStep());
        var secondNext = Assert.IsType<RunCommand>(await second.GetNextStep());
        Assert.Equal("second", firstNext.Command.CommandName);
        Assert.Equal("third", secondNext.Command.CommandName);
        secondNext.SetResult(new CommandExecutionResult.Completed(7));
        firstNext.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await first.GetNextStep());
        Assert.Null(await second.GetNextStep());
        Assert.Equal(0, first.LastExitCode);
        Assert.Equal(7, second.LastExitCode);
    }

    [Fact]
    public void RecordCopiesInvalidateTraversalForChangedOperandsOrOperator()
    {
        var left = Assert.Single(Parser.Parse("true").Commands);
        var right = Assert.Single(Parser.Parse("false").Commands);
        var tree = new ListCommand(Left: left, Operator: ListOperator.And, Right: right);
        var copy = tree with { };
        var originalHash = tree.GetHashCode();
        var traversal = tree.GetTraversal(CancellationToken.None);
        Assert.Equal(tree, copy);
        Assert.Equal(originalHash, tree.GetHashCode());
        Assert.Same(traversal, tree.GetTraversal(CancellationToken.None));
        Assert.Same(traversal, (tree with { }).GetTraversal(CancellationToken.None));
        Assert.NotSame(traversal, (tree with { Left = right }).GetTraversal(CancellationToken.None));
        Assert.NotSame(traversal, (tree with { Right = left }).GetTraversal(CancellationToken.None));
        Assert.NotSame(traversal, (tree with { Operator = ListOperator.Or }).GetTraversal(CancellationToken.None));
        tree.Deconstruct(out var originalLeft, out var originalOperator, out var originalRight);
        Assert.Same(left, originalLeft);
        Assert.Equal(ListOperator.And, originalOperator);
        Assert.Same(right, originalRight);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => tree.GetTraversal(cancellation.Token));
    }
}

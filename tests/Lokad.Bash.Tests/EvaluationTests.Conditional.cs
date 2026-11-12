using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Fact]
    public async Task ReachedFileChecksObserveTheHostAtEachPredicate()
    {
        var state = new Eval(Parser.Parse("[[ -f file && -f file ]]"));
        var first = await NextPathKindsAsync(state);
        Assert.Equal("/file", Assert.Single(first.Paths).Path);
        first.SetResult([PathKindResult(FilePathKind.File)]);
        var second = await NextPathKindsAsync(state);
        Assert.Equal("/file", Assert.Single(second.Paths).Path);
        second.SetResult([PathKindResult(FilePathKind.Missing)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task ReachedTerminalChecksResolveAliasesBeforeIndividualQueries()
    {
        var state = new Eval(Parser.Parse("[[ -t 3 && ! -t 4 ]] 3<&0 4>&1"));
        var first = await NextTerminalStatusesAsync(state);
        Assert.Equal(FileDescriptor.StdIn, Assert.Single(first.Descriptors));
        first.SetResult([true]);
        var second = await NextTerminalStatusesAsync(state);
        Assert.Equal(FileDescriptor.StdOut, Assert.Single(second.Descriptors));
        second.SetResult([false]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task ConditionalFilePredicatesDispatchOnlyReachedChecksInSourceOrder()
    {
        var state = new Eval(Parser.Parse("[[ -f first && -r second || -s third ]]; after=yes"));
        var first = await NextPathKindsAsync(state);
        Assert.Equal("/first", Assert.Single(first.Paths).Path);
        first.SetResult([PathKindResult(FilePathKind.Missing)]);

        var third = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, third.Request);
        Assert.Equal("/third", Assert.Single(third.Paths).Path);
        third.SetResult([SizeInfo(FilePathKind.File, 1)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("yes"), state.Variables["after"]);
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("[[ x || -f $(printf unexpected >&2) ]]", 0)]
    [InlineData("[[ '' && -f $(printf unexpected >&2) ]]", 1)]
    [InlineData("[[ x || -t 0 ]]", 0)]
    [InlineData("[[ '' && -r file ]]", 1)]
    public async Task UnreachedOperandsDoNotDispatchSubstitutionsOrHostQueries(string script, int status)
    {
        var state = new Eval(Parser.Parse(script));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(status, state.LastExitCode);
    }

    [Fact]
    public async Task ConditionalCancellationDrainsAReachedQueryBeforeLaterOperands()
    {
        var state = new Eval(Parser.Parse("[[ -s first && -r second ]]; after=yes"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, operation.Request);
        Assert.Equal("/first", Assert.Single(operation.Paths).Path);

        cancellation.Cancel();
        operation.SetResult([SizeInfo(FilePathKind.File, 1)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }
}

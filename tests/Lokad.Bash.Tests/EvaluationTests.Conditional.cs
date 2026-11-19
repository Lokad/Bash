using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlatConditionalKeepsItsFramesUntilTheReachedQuerySettles(bool cancel)
    {
        var baseline = await MeasureAsync(0, cancel);
        var wide = await MeasureAsync(1023, cancel);
        Assert.True(wide.Pending - baseline.Pending >= 1023 * IntPtr.Size);
        Assert.Equal(baseline.Settled, wide.Settled);

        static async Task<(long Pending, long Settled)> MeasureAsync(int skipped, bool cancel)
        {
            var budget = new MemoryGovernor(128 * 1024);
            using var owner = budget.CreateScope();
            using var cancellation = new CancellationTokenSource();
            var tail = string.Concat(Enumerable.Repeat(" || -f skipped", skipped));
            var state = new Eval(Parser.Parse($"[[ -f first{tail} ]]"));
            state.Memory.Budget = owner;

            var completion = state.EnsureRunning(cancellation.Token);
            var first = await NextPathKindsAsync(state);
            Assert.Equal("/first", Assert.Single(first.Paths).Path);
            var pending = budget.ReservedBytes;
            if (cancel)
            {
                cancellation.Cancel();
                Assert.False(completion.IsCompleted);
                Assert.Equal(pending, budget.ReservedBytes);
            }
            first.SetResult([PathKindResult(FilePathKind.File)]);

            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
            else
            {
                Assert.Null(await NextStepAsync(state));
                Assert.Equal(0, state.LastExitCode);
            }
            return (pending, budget.ReservedBytes);
        }
    }

    [Fact]
    public async Task BalancedConditionalReusesFramesAcrossReachedOperands()
    {
        var budget = new MemoryGovernor(32 * 1024);
        using var owner = budget.CreateScope();
        var state = new Eval(Parser.Parse($"[[ {Group(1024, finalEmpty: true)} ]]"));
        state.Memory.Budget = owner;

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);

        static string Group(int count, bool finalEmpty) => count == 1
            ? finalEmpty ? "''" : "x"
            : $"( {Group(count / 2, finalEmpty: false)} && {Group(count / 2, finalEmpty)} )";
    }

    [Fact]
    public async Task RefusedConditionalFramesPreventTheQueryAndLeaveMemoryForTheNextCommand()
    {
        var budget = new MemoryGovernor(32 * 1024);
        using var owner = budget.CreateScope();
        var tail = string.Concat(Enumerable.Repeat(" || -f skipped", 1023));
        var state = new Eval(Parser.Parse($"[[ -f first{tail} ]]; after=$?"));
        state.Memory.Budget = owner;

        var diagnostic = Assert.IsType<WriteOperation>(await NextStepAsync(state));
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("shell: shared memory budget exhausted\n"u8.ToArray(), diagnostic.Content.ToArray());
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("1"), state.Variables["after"]);
        Assert.Equal(0, state.LastExitCode);
    }
}

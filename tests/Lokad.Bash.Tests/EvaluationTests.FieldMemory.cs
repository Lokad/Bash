using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Fact]
    public async Task FieldMemory_ExpandedCommandNameBorrowsTheUntouchedArgumentTail()
    {
        const string Literal = "printf ";
        const string Expanded = "${tool}";
        var tail = string.Concat(Enumerable.Repeat(" tail", 256));
        var narrowGap = await MeasureAsync(Expanded, "") - await MeasureAsync(Literal, "");
        var wideGap = await MeasureAsync(Expanded, tail) - await MeasureAsync(Literal, tail);

        Assert.Equal(narrowGap, wideGap);

        async Task<long> MeasureAsync(string name, string words)
        {
            var state = new Eval(Parser.Parse($"tool=printf\n{name} body{words} >out"));
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            long? workspaceAtOpen = null;
            host.BeforeWritableOpen = path =>
            {
                if (path.Path == "/out")
                    workspaceAtOpen = state.Memory.Workspace.Bytes;
            };

            var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.Equal("body", host.ReadFile("/out"));
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            Assert.NotNull(workspaceAtOpen);
            return workspaceAtOpen.Value;
        }
    }

    [Fact]
    public async Task FieldMemory_RetainsExternalArgumentsUntilTheHostSettles()
    {
        var state = new Eval(Parser.Parse("tool \"$@\"; after=1"));
        state.ReplacePositionalParameters(Enumerable.Repeat(ShellValue.FromText("argument"), 128).ToArray());
        var budget = new MemoryGovernor(256 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<RunCommand>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(128, operation.Command.Arguments.Count);

        var reserved = budget.ReservedBytes;
        cancellation.Cancel();
        var completedEarly = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMilliseconds(100))) == completion;
        var whilePending = budget.ReservedBytes;
        operation.SetResult(new CommandExecutionResult.Completed(0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(completedEarly);
        Assert.Equal(reserved, whilePending);
        Assert.True(budget.ReservedBytes < whilePending);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Theory]
    [InlineData("\"$@\"", false, 4, false)]
    [InlineData("\"$@\"", false, 512, true)]
    [InlineData("command \"$@\"", false, 4, false)]
    [InlineData("command \"$@\"", false, 512, true)]
    [InlineData("tool \"$@\"", true, 512, false)]
    public async Task FieldMemory_AdmitsCapturedCommandWordsBeforeLaterEffects(
        string invocation, bool nameFixed, int count, bool refused)
    {
        var state = new Eval(Parser.Parse(invocation + " \"${late:=value}\" >target"));
        var fields = Enumerable.Repeat(ShellValue.FromText("operand"), count);
        state.ReplacePositionalParameters((nameFixed ? fields : fields.Prepend(ShellValue.FromText("tool"))).ToArray());
        var budget = new MemoryGovernor(448 * 1024);
        using var owner = budget.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueFileOpenResult(FileOpenResult.Failed(new OperationError("Test open rejected\n")));

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(!refused, state.Variables.ContainsKey("late"));
        var diagnostic = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        if (refused)
        {
            Assert.Empty(host.WritableOpenRequests);
            Assert.Equal("shell: shared memory budget exhausted\n", Text(diagnostic.Content));
        }
        else
        {
            Assert.Equal("/target", Assert.Single(host.WritableOpenRequests).Path.Path);
            Assert.Contains("Test open rejected", Text(diagnostic.Content));
        }
    }

    [Theory]
    [InlineData(": >\"$@\"")]
    [InlineData(": >&\"$@\"")]
    public async Task FieldMemory_ValidatesAllRedirectFieldsBeforeReportingAmbiguity(string script)
    {
        var state = new Eval(Parser.Parse(script));
        state.ReplacePositionalParameters([ShellValue.FromText("target"), ShellValue.FromBytes(new byte[] { 0xff })]);
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, state.LastExitCode);
        Assert.Empty(host.WritableOpenRequests);
        Assert.Equal("shell: text value must be valid UTF-8\n", Text(Assert.Single(host.Appends).Content));
    }
}

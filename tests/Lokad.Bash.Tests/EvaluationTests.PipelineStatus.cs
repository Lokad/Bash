using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("", 1)]
    [InlineData("! ", 0)]
    public async Task PipelineStatusPublishesAFailedSimpleCommandAfterItsDiagnosticAndClose(string negation, int expectedStatus)
    {
        var state = new Eval(Parser.Parse(negation + ": 2>errors >operand"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        var stderr = new FileDescriptor(17);
        var open = Assert.IsType<OpenFileOperation>(await NextStepAsync(state));
        open.SetResult(FileOpenResult.Opened(stderr, readSize: null));
        var operand = Assert.IsType<OpenFileOperation>(await NextStepAsync(state));
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        operand.SetResult(FileOpenResult.Failed(new OperationError("provider refused\n")));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(stderr, diagnostic.FileDescriptor);
        Assert.Equal("operand: provider refused\n", Text(diagnostic.Content));
        Assert.False(state.IndexedArrays.ContainsKey("PIPESTATUS"));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        var close = Assert.IsType<CloseDescriptorOperation>(await NextStepAsync(state));
        Assert.Equal(stderr, close.Descriptor);
        Assert.False(state.IndexedArrays.ContainsKey("PIPESTATUS"));
        close.SetResult(new OperationError("secondary close failure\n"));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(expectedStatus, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("1"), Assert.Single(state.IndexedArrays["PIPESTATUS"]));
    }

    [Fact]
    public async Task PipelineStatusPublishesAllSettledStagesWhileTheAllowanceIsFull()
    {
        var state = new Eval(Parser.Parse(":; ! first | second | third"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        for (var index = 0; index < 2; index++)
        {
            var creation = Assert.IsType<CreatePipeOperation>(await NextStepAsync(state));
            creation.SetResult(PipeCreationResult.Created(new PipeDescriptors(10 + 2 * index, 11 + 2 * index)));
        }
        var pipeline = Assert.IsType<RunPipelineEvaluations>(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("0"), Assert.Single(state.IndexedArrays["PIPESTATUS"]));
        state.Memory.ReleaseUnusedCapacity();
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        pipeline.SetResult([7, 0, 4]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new[] { ShellValue.FromText("7"), ShellValue.FromText("0"), ShellValue.FromText("4") },
            state.IndexedArrays["PIPESTATUS"]);
    }

    [Fact]
    public async Task PipelineStatusRemainsAvailableUntilTheNextCommandCompletes()
    {
        var state = new Eval(Parser.Parse("false | true; printf '%s' \"${PIPESTATUS[*]}\""));
        var creation = Assert.IsType<CreatePipeOperation>(await NextStepAsync(state));
        creation.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));
        var pipeline = Assert.IsType<RunPipelineEvaluations>(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("PIPESTATUS"));
        pipeline.SetResult([1, 0]);

        var write = await NextWriteAsync(state);
        Assert.Equal("1 0", Text(write.Content));
        Assert.Equal(new[] { ShellValue.FromText("1"), ShellValue.FromText("0") }, state.IndexedArrays["PIPESTATUS"]);
        Assert.False(state.Completion.IsCompleted);
        write.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("0"), Assert.Single(state.IndexedArrays["PIPESTATUS"]));
    }
}

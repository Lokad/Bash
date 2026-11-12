using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Evaluation_ReadDescriptor_UsesExistingMappingWithoutTakingOwnership(int logicalDescriptor)
    {
        var table = new DescriptorTable();
        var handle = new FileDescriptor(9);
        table.Set(new LogicalDescriptor(logicalDescriptor), handle);
        var descriptors = new EvaluationDescriptorSetup(table.Capture(), [handle]);
        var state = new Eval(Parser.Parse(Utf8($"read -u{logicalDescriptor} value")), descriptors,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, [], new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));

        var read = await NextReadAsync(state);
        Assert.Equal(handle, read.FileDescriptor);
        read.SetResult(ReadResult.Success(Utf8Bytes("selected\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("selected", Text(GetVariable(state, "value")));
        Assert.Equal(handle, table.Resolve(new LogicalDescriptor(logicalDescriptor)));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluation_ReadDescriptor_DoesNotExposeBorrowedHostNumbers(bool explicitlyClosed)
    {
        var table = new DescriptorTable();
        var handle = new FileDescriptor(9);
        table.Set(new LogicalDescriptor(3), handle);
        if (explicitlyClosed)
            table.Set(new LogicalDescriptor(9), FileDescriptor.Closed);
        var descriptors = new EvaluationDescriptorSetup(table.Capture(), [handle]);
        var state = new Eval(Parser.Parse(Utf8("value=old; read -u9 value")), descriptors,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, [], new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("read: 9: invalid file descriptor: Bad file descriptor\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("old", Text(GetVariable(state, "value")));
        Assert.Equal(explicitlyClosed, table.HasMapping(new LogicalDescriptor(9)));
        Assert.Equal(handle, table.Resolve(new LogicalDescriptor(3)));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Evaluation_ReadDescriptor_PreservesHostFailureWithoutAssigningOrClosing()
    {
        var table = new DescriptorTable();
        var handle = new FileDescriptor(9);
        table.Set(new LogicalDescriptor(3), handle);
        var descriptors = new EvaluationDescriptorSetup(table.Capture(), [handle]);
        var state = new Eval(Parser.Parse(Utf8("value=old; read -u3 value")), descriptors,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, [], new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));

        var read = await NextReadAsync(state);
        Assert.Equal(handle, read.FileDescriptor);
        read.SetResult(ReadResult.Failure(error: new OperationError("read error\n")));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("read: read error: 3: read error\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("old", Text(GetVariable(state, "value")));
        Assert.Equal(handle, table.Resolve(new LogicalDescriptor(3)));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }
}

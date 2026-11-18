using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData(false, 8, false)]
    [InlineData(false, 2048, true)]
    [InlineData(true, 8, false)]
    [InlineData(true, 2048, true)]
    public async Task DeclarationMemory_AdmitsPreparedOperandsBeforeOpeningRedirections(
        bool compound, int count, bool refused)
    {
        var operands = string.Join(' ', Enumerable.Repeat("'value'", count));
        var prefix = compound ? "declare value=(seed) " : "declare ";
        var state = new Eval(Parser.Parse(prefix + operands + " >target"));
        var budget = new MemoryGovernor(32 * 1024);
        using var owner = budget.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueFileOpenResult(FileOpenResult.Failed(new OperationError("Test open rejected\n")));

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, state.LastExitCode);
        if (compound)
            Assert.Equal(ShellValue.FromText("seed"), Assert.Single(state.IndexedArrays["value"]));
        else
            Assert.False(state.Variables.ContainsKey("value"));
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
}

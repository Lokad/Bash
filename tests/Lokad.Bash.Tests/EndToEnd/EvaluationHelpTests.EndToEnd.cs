using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationHelpTestsEndToEnd
{
    [Theory]
    [InlineData("help true", "true: true\n    Return success.\n")]
    [InlineData("help -d true tru", "true - Return success.\ntrue - Return success.\n")]
    [InlineData("help -s '['", "[: [ expression ]\n")]
    [InlineData("help -s 'tr?e'", "true: true\n")]
    [InlineData("help -m true", "NAME\n    true - Return success.\n\nSYNOPSIS\n    true\n\nDESCRIPTION\n    Return success.\n\n")]
    public async Task HelpKeepsTopicOrderAndExactRecordBytes(string source, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(source);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HelpKeepsTheAdmittedOutputUntilTheWriteSettles()
    {
        const int topics = 512;
        var source = "help -d" + string.Concat(Enumerable.Repeat(" true", topics));
        var record = "true - Return success.\n"u8.ToArray();
        const int allowance = 72 * 1024;
        var governor = new MemoryGovernor(allowance);
        using var owner = governor.CreateScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var evaluation = new Eval(Parser.Parse(source));
        evaluation.Memory.Budget = owner;

        var write = Assert.IsType<WriteOperation>(await evaluation.GetNextStep(deadline.Token));
        var pending = governor.ReservedBytes;
        try
        {
            Assert.True(write.FileDescriptor == FileDescriptor.StdOut, "Expected pending help output on stdout.");
            Assert.Equal(topics * record.Length, write.Content.Length);
            for (var offset = 0; offset < write.Content.Length; offset += record.Length)
                Assert.True(write.Content.Span.Slice(offset, record.Length).SequenceEqual(record));
            Assert.False(evaluation.Completion.IsCompleted);
            Assert.InRange(pending, write.Content.Length, allowance);
        }
        finally
        {
            write.SetResult(DescriptorWriteResult.Open);
            await evaluation.Completion.WaitAsync(deadline.Token);
        }

        Assert.Null(await evaluation.GetNextStep(deadline.Token));
        Assert.Equal(0, evaluation.LastExitCode);
        Assert.True(governor.ReservedBytes < pending);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}

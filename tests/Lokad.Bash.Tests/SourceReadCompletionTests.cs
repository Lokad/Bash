using Lokad.Bash.Files;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class SourceReadCompletionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SourceEnforcesTheParserByteLimitBeforeExecuting(int extraBytes)
    {
        var first = "value=changed\n"u8.ToArray();
        var tail = new byte[Parser.MaximumScriptBytes - first.Length + extraBytes];
        Array.Fill(tail, (byte)' ');
        tail[0] = (byte)'#';
        var state = new Eval(Parser.Parse("source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueReadResult(ReadResult.Success(first, [], reachedEoF: false));
        host.EnqueueReadResult(ReadResult.Success(tail, [], reachedEoF: true));
        host.EnqueueCloseDescriptor();
        if (extraBytes > 0)
            host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        Assert.Equal(extraBytes == 0 ? 0 : 2, state.LastExitCode);
        Assert.Equal(extraBytes == 0, state.Variables.ContainsKey("value"));
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceReadsAllBatchesBeforeExecutingAndClosesOnlyItsFile(bool finalData)
    {
        var state = new Eval(Parser.Parse("source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueReadResult(ReadResult.Success(Convert.FromHexString("66697273743D27FF0D270A"), [], reachedEoF: false));
        host.EnqueueReadResult(ReadResult.Success("last=done"u8.ToArray(), [], reachedEoF: finalData));
        if (!finalData)
            host.EnqueueReadResult(ReadResult.EndOfFile);
        host.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(ShellValue.FromBytes(new byte[] { 0xff, 13 }), state.Variables["first"]);
        Assert.Equal(ShellValue.FromText("done"), state.Variables["last"]);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }

    [Fact]
    public async Task SourceEmptyEofClosesTheFileAndSucceeds()
    {
        var state = new Eval(Parser.Parse("source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueReadResult(ReadResult.EndOfFile);
        host.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }

    [Fact]
    public async Task SourceReadFailureDiscardsEarlierInputAndRestoresPrefixState()
    {
        var state = new Eval(Parser.Parse("value=outer; value=prefix source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueReadResult(ReadResult.Success("value=changed; external\n"u8.ToArray(), [], reachedEoF: false));
        host.EnqueueReadResult(ReadResult.Failure(error: new OperationError("read error\n")));
        host.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("outer"), state.Variables["value"]);
        Assert.Empty(host.CommandNames);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }

    [Fact]
    public async Task SourceReadCancellationStillClosesItsFileWithoutExecutingPartialInput()
    {
        var state = new Eval(Parser.Parse("source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueReadResult(ReadResult.Success("value=changed\n"u8.ToArray(), [], reachedEoF: false));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        host.EnqueueReadTask(Task.FromCanceled<ReadResult>(cancellation.Token));
        host.EnqueueCloseDescriptor();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Engine().RunAsync(state, host));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(state.Variables.ContainsKey("value"));
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceReadExceptionStillClosesItsFile(bool closeThrows)
    {
        var state = new Eval(Parser.Parse("source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        var failure = new IOException("Read failed.");
        host.EnqueueReadTask(Task.FromException<ReadResult>(failure));
        if (closeThrows)
            host.EnqueueCloseDescriptorException(new IOException("Close failed."));
        host.EnqueueCloseDescriptor();

        var error = await Assert.ThrowsAsync<IOException>(() => new Engine().RunAsync(state, host));

        Assert.Same(failure, error);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }

    [Fact]
    public async Task SourceReadFailureSurvivesTerminalCloseFailure()
    {
        var state = new Eval(Parser.Parse("source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueReadResult(ReadResult.Failure(new OperationError("read error\n")));
        host.EnqueueCloseDescriptorException(new IOException("close failed"));
        host.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }

    [Theory]
    [InlineData("source")]
    [InlineData(".")]
    public async Task SourceAliasesExecuteEveryLine(string builtin)
    {
        var tree = EndToEndHarness.BuildTree([("/script", "printf first\nprintf second\n")]);
        var result = await EndToEndHarness.RunScriptAsync($"{builtin} /script", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("firstsecond", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task SourceReloadsPrintedByteValuedFunctionDefinitions()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "probe_f(){ printf '%s' $'\\377'; }; declare -f probe_f > /script; unset -f probe_f; source /script; probe_f",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { 0xff }, result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task SourceSpansSeveralBatchesAndKeepsFinalUnterminatedLine()
    {
        var script = "count=0\n" + string.Concat(Enumerable.Repeat("count=$((count+1))\n", 260)) + "printf '%s' \"$count\"";
        var result = await EndToEndHarness.RunScriptAsync("source /script", EndToEndHarness.BuildTree([("/script", script)]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("260", result.StdOut);
        Assert.Empty(result.StdErr);
    }
}

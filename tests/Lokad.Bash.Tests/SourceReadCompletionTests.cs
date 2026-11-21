using Lokad.Bash.Files;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class SourceReadCompletionTests
{
    [Fact]
    public async Task SourceSearchSkipsUnavailableCandidatesAndClosesTheSelectedFile()
    {
        var state = new Eval(Parser.Parse("PATH=first::second; source item argument"));
        var first = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        Assert.Equal("/first/item", first.Path.Path);
        first.SetResult(FileOpenResult.Failed(new OperationError("Permission denied\n")
            { Kind = OperationErrorKind.PermissionDenied }));
        var current = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        Assert.Equal("/item", current.Path.Path);
        current.SetResult(FileOpenResult.Failed(new OperationError("No such file or directory\n")
            { Kind = OperationErrorKind.NotFound }));
        var second = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        Assert.Equal("/second/item", second.Path.Path);
        var descriptor = new FileDescriptor(10);
        second.SetResult(FileOpenResult.Opened(descriptor, readSize: null));

        var read = Assert.IsType<ReadLineOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, read.FileDescriptor);
        read.SetResult(ReadResult.Success("selected=$1"u8.ToArray(), [], reachedEoF: true));
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, close.Descriptor);
        Assert.False(state.Variables.ContainsKey("selected"));
        close.SetResult(null);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(ShellValue.FromText("argument"), state.Variables["selected"]);
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task SourceSearchReusesCandidateStorageAndTheCurrentDirectoryMiss()
    {
        var directories = Enumerable.Range(0, 32).Select(index => $"dir{index:00}").ToArray();
        var state = new Eval(Parser.Parse("PATH=:" + string.Join(':', directories) + ": source item"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        var missing = FileOpenResult.Failed(new OperationError("No such file or directory\n")
            { Kind = OperationErrorKind.NotFound });

        var current = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        Assert.Equal("/item", current.Path.Path);
        current.SetResult(missing);
        long? admitted = null;
        foreach (var directory in directories)
        {
            var candidate = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
            Assert.Equal($"/{directory}/item", candidate.Path.Path);
            admitted ??= governor.ReservedBytes;
            Assert.Equal(admitted.Value, governor.ReservedBytes);
            candidate.SetResult(missing);
        }

        var diagnostic = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal("item: No such file or directory\n"u8.ToArray(), diagnostic.Content.ToArray());
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task SourceOpenFailureReportsTheHostCauseBeforeContinuing()
    {
        var state = new Eval(Parser.Parse("source ./target temporary; after=$?"));
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        Assert.Equal(FileOpenMode.Read, open.Mode);
        open.SetResult(FileOpenResult.Failed(new OperationError("input policy refused\n")
            { Kind = OperationErrorKind.PermissionDenied }));

        var diagnostic = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("./target: input policy refused\n"u8.ToArray(), diagnostic.Content.ToArray());
        Assert.False(state.Variables.ContainsKey("after"));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(ShellValue.FromText("1"), state.Variables["after"]);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task CancelledSourceReadKeepsTheBatchBudgetAndDescriptorUntilTheHostSettles()
    {
        var state = new Eval(Parser.Parse("source file; after=1"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var descriptor = new FileDescriptor(10);
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep(CancellationToken.None));
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var read = Assert.IsType<ReadLineOperation>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(descriptor, read.FileDescriptor);
        var destination = Assert.IsAssignableFrom<IMemoryBudget>(read.DestinationBudget);
        using var batch = destination.Reserve(4096);

        cancellation.Cancel();
        var next = state.GetNextStep(CancellationToken.None);
        var publishedEarly = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100))) == next;
        var pending = batch.Bytes;
        read.SetResult(ReadResult.Success("sourced=changed\n"u8.ToArray(), [], reachedEoF: true));

        var close = Assert.IsType<CloseDescriptorOperation>(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(descriptor, close.Descriptor);
        Assert.Equal(0, batch.Bytes);
        close.SetResult(null);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(publishedEarly, $"Close was published during the read with {pending} bytes still admitted for its batch.");
        Assert.Equal(4096, pending);
        Assert.False(state.Variables.ContainsKey("sourced"));
        Assert.False(state.Variables.ContainsKey("after"));
    }

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

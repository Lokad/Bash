using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public class EngineTests
{
    [Theory]
    [InlineData("first | second | third", 1, false)]
    [InlineData("! first | second | third", 0, false)]
    [InlineData("first | second | third || recover", 0, true)]
    [InlineData("first | second | third && unexpected", 1, false)]
    public async Task PipeCreationFailureClosesEarlierEndpointsAndParticipatesInControlFlow(
        string script, int exitCode, bool recovered)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([0]) { RecordAppends = true };
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueuePipeResult(PipeCreationResult.Failed(new OperationError("pipe quota reached\n")));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptorResult(new OperationError("secondary close failure\n"));
        visitor.EnqueueCloseDescriptorException(new IOException("secondary close exception"));

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(recovered ? ["recover"] : Array.Empty<string>(), visitor.CommandNames);
        var diagnostic = Assert.Single(visitor.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("shell: pipe quota reached\n"u8.ToArray(), diagnostic.Content);
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task CommandSubstitutionPipeFailureReportsFailedStatusWithoutRunningTheProducer()
    {
        var state = new Eval(Parser.Parse("{ value=$(unexpected); } >/out"));
        var visitor = new TestOperationVisitor([]) { RecordAppends = true };
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueuePipeResult(PipeCreationResult.Failed(new OperationError("capture pipe unavailable\n")));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptorException(new IOException("secondary close failure"));

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(visitor.CommandNames);
        var diagnostic = Assert.Single(visitor.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("shell: capture pipe unavailable\n"u8.ToArray(), diagnostic.Content);
        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("unexpected 2>/err 3<<<first <<<second")]
    [InlineData("unexpected 2>/err 3<<FIRST <<SECOND\nfirst\nFIRST\nsecond\nSECOND\n")]
    public async Task HereInputPipeFailureUsesRedirectedErrorAndClosesEarlierAcquisitions(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]) { RecordAppends = true };
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueuePipeResult(PipeCreationResult.Failed(new OperationError("input pipe unavailable\n")));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueCloseDescriptorResult(new OperationError("secondary close failure\n"));
        visitor.EnqueueCloseDescriptorException(new IOException("secondary close exception"));

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(visitor.CommandNames);
        Assert.Equal(2, visitor.Appends.Count);
        Assert.Equal(new FileDescriptor(9), visitor.Appends[1].Descriptor);
        Assert.Equal("input pipe unavailable\n"u8.ToArray(), visitor.Appends[1].Content);
        Assert.Equal(3, visitor.ClosedDescriptors.Count);
        Assert.Equal([new FileDescriptor(9), new FileDescriptor(10), new FileDescriptor(11)],
            visitor.ClosedDescriptors.OrderBy(descriptor => descriptor.Id));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task CommandSubstitution_PreservesProducerFailureWithProviderLinkedReadCancellation()
    {
        var state = new Eval(Parser.Parse("value=$(external)"));
        var failure = new IOException("producer failed");
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitor = new TestOperationVisitor([])
        {
            RunCommandOverride = async () =>
            {
                await readStarted.Task;
                throw failure;
            },
            ReadOverride = async token =>
            {
                using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                readStarted.SetResult();
                await Task.Delay(Timeout.Infinite, providerCancellation.Token);
                return DescriptorReadBuffer.EndOfFile;
            }
        };
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueCloseDescriptor();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.Same(failure, await Record.ExceptionAsync(() => new Engine().RunAsync(state, visitor, timeout.Token)));

        Assert.False(state.Variables.ContainsKey("value"));
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task RunCapturedEvaluation_PreservesCallerCancellationAfterEndOfFile()
    {
        var stage = new Eval(Parser.Parse("external"), new FileDescriptor(11));
        var completion = new TaskCompletionSource<CapturedEvaluationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new RunCapturedEvaluation(completion,
            new PipelineEvaluationStage(stage, [new FileDescriptor(11)]), new FileDescriptor(10), 1024,
            new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var readToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitor = new TestOperationVisitor([])
        {
            RunCommandOverride = async () =>
            {
                var token = await readToken.Task;
                producerStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new CommandExecutionResult.Completed(0);
            },
            ReadOverride = token =>
            {
                readToken.SetResult(token);
                return ValueTask.FromResult(DescriptorReadBuffer.EndOfFile);
            }
        };
        visitor.EnqueueCloseDescriptor();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The synchronous read has returned EOF before dispatch yields to the caller.
        var dispatch = ShellOperationDispatcher.DispatchAsync(operation, visitor, cancellation.Token);
        await producerStarted.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(10)));
        var delivered = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Task);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(cancellation.Token, delivered.CancellationToken);
        Assert.True(stage.Completion.IsCompleted);
        Assert.Equal([new FileDescriptor(11)], visitor.ClosedDescriptors);
        Assert.Empty(stage.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("{ echo \"$(:)\"; } >/out")]
    [InlineData("{ value=$(:); } >/out")]
    [InlineData("exec 9> /out; value=$(:)")]
    [InlineData("(exec 9> /out; value=$(:))")]
    public async Task CommandSubstitution_PreservesReadFailureThroughEnclosingCleanup(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]);
        var cause = new OperationError("original read failure\n");
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueReadResult(ReadResult.Failure(cause));
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueCloseDescriptorException(new IOException("scope close failed"));
        visitor.EnqueueCloseDescriptorException(new IOException("capture close failed"));
        var engine = new Engine();

        var failure = await Assert.ThrowsAsync<ShellDescriptorReadException>(() => engine.RunAsync(state, visitor));

        Assert.Equal(cause, failure.Error);
        Assert.Same(failure, await Record.ExceptionAsync(() => state.Completion));
        Assert.False(state.Variables.ContainsKey("value"));
        Assert.Empty(visitor.CommandNames);
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(9), new FileDescriptor(10)],
            visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("{ value=$(:); } >/out", false)]
    [InlineData("{ value=$(:); } >/out", true)]
    [InlineData(": {fd}>/out; value=$(:)", false)]
    [InlineData(": {fd}>/out; value=$(:)", true)]
    [InlineData(": {fd}>/out >$(:)", false)]
    [InlineData(": {fd}>/out >$(:)", true)]
    public async Task EnclosingCleanup_PreservesThrownReadFailureOrCancellation(string script, bool canceled)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception primary = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("read threw");
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueReadTask(Task.FromException<ReadResult>(primary));
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueCloseDescriptorException(new IOException("scope close failed"));
        visitor.EnqueueCloseDescriptor();

        var failure = await Record.ExceptionAsync(() => new Engine().RunAsync(state, visitor));
        var evaluationFailure = await Record.ExceptionAsync(() => state.Completion);

        Assert.Same(primary, failure);
        if (canceled)
            Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(evaluationFailure).CancellationToken);
        else
            Assert.Same(primary, evaluationFailure);
        Assert.Empty(state.GetDescriptorsForCleanup());
        Assert.Contains(new FileDescriptor(9), visitor.ClosedDescriptors);
        Assert.Contains(new FileDescriptor(10), visitor.ClosedDescriptors);
    }

    [Theory]
    [InlineData(": >/out")]
    [InlineData("{ :; } >/out")]
    [InlineData("exec 9> /out")]
    [InlineData("(exec 9> /out)")]
    [InlineData("{ exec 10>&-; : {fd}>/out; } 10>/dev/null")]
    public async Task EnclosingCleanup_ReportsCloseFailureWithoutEarlierFailure(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]);
        var primary = new IOException("scope close failed");
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueueCloseDescriptorException(primary);

        var failure = await Record.ExceptionAsync(() => new Engine().RunAsync(state, visitor));

        Assert.Same(primary, failure);
        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData(": >/out")]
    [InlineData("{ :; } >/out")]
    [InlineData("exec 9> /out")]
    public async Task ExpectedScopeCloseFailureReturnsFailedStatus(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]) { RecordAppends = true };
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueueCloseDescriptorResult(new OperationError("publication failed\n"));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(visitor.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("shell: publication failed\n"u8.ToArray(), diagnostic.Content.ToArray());
        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData(": >/out || recover")]
    [InlineData("{ :; } >/out || recover")]
    [InlineData("{ :; } >/out; recover")]
    [InlineData("value=ok >/out || recover")]
    [InlineData("(exec 3>/out) || recover")]
    [InlineData("exec 3>/out; exec 3>&- || recover")]
    [InlineData("! : >/out && recover")]
    [InlineData("! { :; } >/out && recover")]
    [InlineData("f() { return 0; }; f >/out || recover")]
    public async Task ExpectedScopeCloseFailureParticipatesInShellControlFlow(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([0]) { RecordAppends = true };
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueueCloseDescriptorResult(new OperationError("publication failed\n"));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["recover"], visitor.CommandNames);
        var diagnostic = Assert.Single(visitor.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("shell: publication failed\n"u8.ToArray(), diagnostic.Content);
        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData(": <<<text || recover")]
    [InlineData(": <<EOF || recover\ntext\nEOF\n")]
    public async Task ExpectedHereInputCloseFailureAllowsRecovery(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([0]) { RecordAppends = true };
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptorResult(new OperationError("pipe close failed\n"));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptor();

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["recover"], visitor.CommandNames);
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Equal("pipe close failed\n"u8.ToArray(), visitor.Appends[^1].Content);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task ExpectedCaptureCloseFailureAllowsRecovery()
    {
        var state = new Eval(Parser.Parse("value=$(:) || recover"));
        var visitor = new TestOperationVisitor([0]) { RecordAppends = true };
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueReadResult(ReadResult.EndOfFile);
        visitor.EnqueueCloseDescriptorResult(new OperationError("capture close failed\n"));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["recover"], visitor.CommandNames);
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Equal("shell: capture close failed\n"u8.ToArray(), Assert.Single(visitor.Appends).Content);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task ExpectedSourceCloseFailureAllowsRecovery()
    {
        var state = new Eval(Parser.Parse("source /input || recover"));
        var visitor = new TestOperationVisitor([0]) { RecordAppends = true };
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueueReadResult(ReadResult.EndOfFile);
        visitor.EnqueueCloseDescriptorResult(new OperationError("source close failed\n"));
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["recover"], visitor.CommandNames);
        Assert.Equal("shell: /input: source close failed\n"u8.ToArray(), Assert.Single(visitor.Appends).Content);
        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task ExpectedFinalCloseFailurePreservesEarlierFailedStatus()
    {
        var state = new Eval(Parser.Parse("false"));
        state.TrackOwnedDescriptor(new FileDescriptor(9));
        var visitor = new TestOperationVisitor([]) { RecordAppends = true };
        visitor.EnqueueCloseDescriptorResult(new OperationError("cleanup failed\n"));

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(visitor.Appends);
        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("value=$(:)", false)]
    [InlineData("value=$(:)", true)]
    [InlineData("value=$(:) >/out", false)]
    [InlineData("value=$(:) >/out", true)]
    [InlineData("echo \"$(:)\" >/out", false)]
    [InlineData("echo \"$(:)\" >/out", true)]
    public async Task CommandSubstitution_PreservesReadFailureThroughCaptureCleanup(string script, bool closeThrows)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]);
        var cause = new OperationError("original read failure\n");
        // No open is queued: assignment or operand expansion must fail before the command's redirections.
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueReadResult(ReadResult.Success("partial"u8.ToArray(), [], reachedEoF: false));
        visitor.EnqueueReadResult(ReadResult.Failure(cause));
        if (closeThrows)
            visitor.EnqueueCloseDescriptorException(new IOException("secondary close failure"));
        else
            visitor.EnqueueCloseDescriptor();
        var engine = new Engine();

        var failure = await Assert.ThrowsAsync<ShellDescriptorReadException>(() => engine.RunAsync(state, visitor));

        Assert.Equal(cause, failure.Error);
        Assert.False(state.Variables.ContainsKey("value"));
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task CommandSubstitution_StillReportsCleanupFailureAfterSuccessfulRead()
    {
        var state = new Eval(Parser.Parse("value=$(:)"));
        var visitor = new TestOperationVisitor([]);
        var closeFailure = new IOException("close failed");
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueReadResult(ReadResult.EndOfFile);
        visitor.EnqueueCloseDescriptorException(closeFailure);

        var failure = await Record.ExceptionAsync(() => new Engine().RunAsync(state, visitor));

        Assert.Same(closeFailure, failure);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Engine_RunAsync_ClosesPipesCreatedBeforeSetupFailure()
    {
        var state = new Eval(Parser.Parse("first | second | third"));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueCloseDescriptor();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Engine().RunAsync(state, visitor));

        Assert.Equal("No pipe descriptors are available for this operation.", exception.Message);
        Assert.Equal(
            [new FileDescriptor(11), new FileDescriptor(10)],
            visitor.ClosedDescriptors);
    }

    [Fact]
    public async Task Engine_RunAsync_VisitsCommandsInOrder()
    {
        var script = Parser.Parse("first; second");
        var state = new Eval(script);
        var visitor = new TestOperationVisitor([0, 0]);
        var engine = new Engine();

        await engine.RunAsync(state, visitor);

        Assert.Collection(
            visitor.CommandNames,
            name => Assert.Equal("first", name),
            name => Assert.Equal("second", name));
    }

    [Fact]
    public async Task Engine_RunAsync_PreservesSetupFailureWhenPipeCleanupFails()
    {
        var state = new Eval(Parser.Parse("first | second | third"));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueCloseDescriptorException(new IOException("close failed"));
        visitor.EnqueueCloseDescriptor();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Engine().RunAsync(state, visitor));

        Assert.Equal("No pipe descriptors are available for this operation.", exception.Message);
        Assert.Equal(
            [new FileDescriptor(11), new FileDescriptor(10)],
            visitor.ClosedDescriptors);
    }

    [Theory]
    [InlineData("echo value >/out 3>/fails")]
    [InlineData(": {fd}>/out 3>/fails")]
    public async Task Engine_RunAsync_PreservesRedirectionSetupFailureWhenCleanupFails(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]);
        var setupFailure = new IOException("host open failed");
        visitor.EnqueueOpenFileDescriptor(10);
        visitor.EnqueueFileOpenTask(Task.FromException<FileOpenResult>(setupFailure));
        visitor.EnqueueCloseDescriptorException(new IOException("later cleanup failed"));

        var exception = await Assert.ThrowsAsync<IOException>(
            () => new Engine().RunAsync(state, visitor));

        Assert.Same(setupFailure, exception);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Engine_RunAsync_RedirectionScopeClosesAfterFunctionReturnFlow()
    {
        var state = new Eval(Parser.Parse("f() { { return 7; } >/out; }; f"));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueOpenFileDescriptor(10);
        visitor.EnqueueCloseDescriptor();

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(7, state.LastExitCode);
        Assert.Equal(new RunResult(7, ShellPath.Root), result);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
    }

    [Fact]
    public async Task Engine_RunAsync_RedirectionScopeClosesAfterHostFailure()
    {
        var state = new Eval(Parser.Parse("external >/out"));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueOpenFileDescriptor(10);
        visitor.EnqueueCloseDescriptor();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Engine().RunAsync(state, visitor));

        Assert.Equal("No command outcomes are available for this operation.", exception.Message);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
    }

    [Fact]
    public async Task Engine_RunAsync_OuterRedirectionScopeClosesAfterNestedEvaluatorFailure()
    {
        var state = new Eval(Parser.Parse("{ : 3>/fails; } >/out"));
        var visitor = new TestOperationVisitor([]);
        var setupFailure = new IOException("nested host open failed");
        visitor.EnqueueOpenFileDescriptor(10);
        visitor.EnqueueFileOpenTask(Task.FromException<FileOpenResult>(setupFailure));
        visitor.EnqueueCloseDescriptor();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => new Engine().RunAsync(state, visitor));

        Assert.Same(setupFailure, exception);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task InvalidDescriptorWordKeepsItsShellFailureWhenCleanupThrows()
    {
        var state = new Eval(Parser.Parse(": 3>/out 2>&word; recover"));
        var visitor = new TestOperationVisitor([0]) { RecordAppends = true };
        visitor.EnqueueOpenFileDescriptor(10);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptorException(new IOException("secondary close failure"));

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["recover"], visitor.CommandNames);
        Assert.Equal("word: ambiguous redirect\n"u8.ToArray(), Assert.Single(visitor.Appends).Content);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Engine_RunAsync_PreservedExecRedirectionClosesExactlyOnceAtEvaluationEnd()
    {
        var state = new Eval(Parser.Parse("exec >/out"));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueOpenFileDescriptor(10);
        visitor.EnqueueCloseDescriptor();

        var result = await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new RunResult(0, ShellPath.Root), result);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
    }

    [Fact]
    public async Task Engine_RunAsync_ClosesPersistentDescriptorWhenExecClosesItsLogicalDescriptor()
    {
        var state = new Eval(Parser.Parse(": {fd}> /out; exec 10>&-; next"));
        var visitor = new TestOperationVisitor([0]);
        visitor.EnqueueOpenFileDescriptor(3);
        visitor.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, visitor);

        Assert.Equal([new FileDescriptor(3)], visitor.ClosedDescriptors);
        Assert.Equal(["next"], visitor.CommandNames);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(1000000)]
    public async Task Engine_RunAsync_AllocatedNumbersRouteAndCloseUnrelatedHostHandles(int handle)
    {
        var state = new Eval(Parser.Parse(
            "exec 10>/first; : {fd}>/second; echo first >&10; echo second >&$fd; exec 10>&-; echo kept >&$fd; exec 11>&-; next"));
        var visitor = new TestOperationVisitor([0]) { RecordAppends = true };
        visitor.BeforeAppend = (_, content) =>
        {
            if (content.Span.SequenceEqual("kept\n"u8))
                Assert.Equal([new FileDescriptor(700)], visitor.ClosedDescriptors);
        };
        visitor.EnqueueOpenFileDescriptor(700);
        visitor.EnqueueOpenFileDescriptor(handle);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new FileDescriptor[] { new(700), new(handle), new(handle) },
            visitor.Appends.Select(append => append.Descriptor));
        Assert.Equal(new FileDescriptor[] { new(700), new(handle) }, visitor.ClosedDescriptors);
        Assert.Equal(["next"], visitor.CommandNames);
    }

    [Theory]
    [InlineData("{fd}>/out")]
    [InlineData("{fd}</input")]
    public async Task Engine_RunAsync_ReadonlyDescriptorVariableDoesNotBindOrLeakTheOpenedHandle(string redirection)
    {
        var state = new Eval(Parser.Parse("fd=old; readonly fd; : " + redirection));
        var visitor = new TestOperationVisitor([]) { RecordAppends = true };
        visitor.EnqueueOpenFileDescriptor(900);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, visitor);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("old", state.Variables["fd"].ToUtf8String());
        var diagnostic = Assert.Single(visitor.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("fd: readonly variable\nfd: cannot assign fd to variable\n"u8.ToArray(), diagnostic.Content);
        Assert.Equal([new FileDescriptor(900)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Engine_RunAsync_ReadonlyDescriptorDiagnosticFailureStillClosesOpenedHandle()
    {
        var state = new Eval(Parser.Parse("readonly fd=old; : {fd}>/out"));
        var failure = new IOException("Diagnostic failed.");
        var visitor = new TestOperationVisitor([]) { BeforeAppend = (_, _) => throw failure };
        visitor.EnqueueOpenFileDescriptor(900);
        visitor.EnqueueCloseDescriptor();

        Assert.Same(failure, await Record.ExceptionAsync(() => new Engine().RunAsync(state, visitor)));

        Assert.Equal("old", state.Variables["fd"].ToUtf8String());
        Assert.Equal([new FileDescriptor(900)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_RunAsync_ClosesScopedAllocationUnlessAnAliasEscapes(bool escapeAlias)
    {
        var state = new Eval(Parser.Parse(
            "{ exec 10>&-; : {fd}>/inner; " + (escapeAlias ? "exec 4>&$fd; " : "")
            + "} 10>/temporary; echo after" + (escapeAlias ? "; echo kept >&4; exec 4>&-" : "")));
        var visitor = new TestOperationVisitor([]) { RecordAppends = true };
        visitor.BeforeAppend = (_, content) =>
        {
            if (content.Span.SequenceEqual("after\n"u8))
            {
                Assert.Equal(escapeAlias
                    ? new FileDescriptor[] { new(700) }
                    : new FileDescriptor[] { new(700), new(900) }, visitor.ClosedDescriptors);
            }
        };
        visitor.EnqueueOpenFileDescriptor(700);
        visitor.EnqueueOpenFileDescriptor(900);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        if (escapeAlias)
            visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, visitor);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new FileDescriptor[] { new(700), new(900) }, visitor.ClosedDescriptors);
        if (escapeAlias)
            Assert.Equal(new FileDescriptor(900), visitor.Appends.Single(append => append.Content.AsSpan().SequenceEqual("kept\n"u8)).Descriptor);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Evaluation_WaitTokenDoesNotReplaceRunningEvaluationToken()
    {
        var state = new Eval(Parser.Parse("external"));
        using var cancellation = new CancellationTokenSource();

        var operation = Assert.IsType<RunCommand>(await state.GetNextStep(cancellation.Token));
        var completion = state.GetNextStep(CancellationToken.None);

        Assert.Equal(cancellation.Token, state.CancellationToken);
        operation.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await completion);
    }

    [Fact]
    public async Task RunPipelineEvaluations_PreservesStageFailureWhenCleanupFails()
    {
        var stage = new Eval(Parser.Parse("echo value"), new FileDescriptor(10));
        var operation = new RunPipelineEvaluations(
            new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously),
            [
                new PipelineEvaluationStage(stage, [new FileDescriptor(10)]),
                new PipelineEvaluationStage(new Eval(Parser.Parse("true")), [])
            ]);
        var visitor = new TestOperationVisitor([]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None));

        Assert.Equal("No append results are available for this operation.", exception.Message);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
    }

    [Fact]
    public async Task RunPipelineEvaluations_AwaitsEndpointCleanupToCompletion()
    {
        var descriptor = new FileDescriptor(10);
        var operation = new RunPipelineEvaluations(
            new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously),
            [
                new PipelineEvaluationStage(new Eval(Parser.Parse(":")), [descriptor]),
                new PipelineEvaluationStage(new Eval(Parser.Parse(":")), [])
            ]);
        var closeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueCloseDescriptorTask(closeCompletion.Task);

        var dispatch = ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None);
        await Task.Yield();

        Assert.Contains(descriptor, visitor.ClosedDescriptors);
        Assert.False(dispatch.IsCompleted);

        closeCompletion.SetResult();
        await dispatch;
    }

    [Fact]
    public async Task RunPipelineEvaluations_PreservesStageFailureWhenCloseFails()
    {
        var stage = new Eval(Parser.Parse("echo value"), new FileDescriptor(10));
        var operation = new RunPipelineEvaluations(
            new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously),
            [
                new PipelineEvaluationStage(stage, [new FileDescriptor(10)]),
                new PipelineEvaluationStage(new Eval(Parser.Parse("true")), [])
            ]);
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueCloseDescriptorException(new IOException("close failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellOperationDispatcher.DispatchAsync(operation, visitor, CancellationToken.None));

        Assert.Equal("No append results are available for this operation.", exception.Message);
        Assert.Equal([new FileDescriptor(10)], visitor.ClosedDescriptors);
    }

    [Fact]
    public async Task Engine_RunAsync_DoesNotWrapLoggingHostTwice()
    {
        var options = new EvaluationOptions(1, ShellPath.Root);
        var state = new Eval(Parser.Parse("echo value"), options: options);
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        var output = new StringWriter();
        var previousOutput = Console.Out;

        try
        {
            Console.SetOut(output);
            await new Engine().RunAsync(state, new LoggingBashHost(visitor));
        }
        finally
        {
            Console.SetOut(previousOutput);
        }

        var starts = output.ToString().Split("IBashHost.WriteAsync start").Length - 1;
        Assert.Equal(1, starts);
    }

    [Fact]
    public void RunPipelineEvaluations_RejectsEmptyAndSingleStageShapes()
    {
        var stage = new PipelineEvaluationStage(new Eval(Parser.Parse("true")), []);
        var completion = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Throws<ArgumentException>(() => new RunPipelineEvaluations(completion, []));
        Assert.Throws<ArgumentException>(() => new RunPipelineEvaluations(completion, [stage]));
    }

    [Fact]
    public void RunPipelineEvaluations_RejectsIncompleteStatusVector()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new RunPipelineEvaluations(completion,
            [
                new PipelineEvaluationStage(new Eval(Parser.Parse("true")), []),
                new PipelineEvaluationStage(new Eval(Parser.Parse("false")), [])
            ]);

        Assert.Throws<ArgumentException>(() => operation.SetResult([0]));
        Assert.False(completion.Task.IsCompleted);
    }

    [Fact]
    public async Task RunPipelineEvaluations_RetainsExactStatusVectorAfterRejectedLengths()
    {
        var stages = new List<PipelineEvaluationStage>
        {
            new(new Eval(Parser.Parse("true")), []),
            new(new Eval(Parser.Parse("false")), [])
        };
        var completion = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new RunPipelineEvaluations(completion, stages);

        Assert.Throws<ArgumentException>(() => operation.SetResult([]));
        Assert.Throws<ArgumentException>(() => operation.SetResult([0, 1, 0]));
        Assert.False(completion.Task.IsCompleted);
        IReadOnlyList<int> statuses = new List<int> { 0, 1 };
        operation.SetResult(statuses);

        Assert.Same(stages, operation.Stages);
        Assert.Same(statuses, await completion.Task);
    }

    [Fact]
    public void Evaluation_RejectsPublishingSameOperationTwice()
    {
        var state = new Eval(Parser.Parse(":"));
        var completion = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new WriteOperation(completion, FileDescriptor.StdOut, ReadOnlyMemory<byte>.Empty);

        state.PublishOperation(operation);
        var exception = Assert.Throws<InvalidOperationException>(() => state.PublishOperation(operation));

        Assert.Equal("The same evaluation operation cannot be published twice.", exception.Message);
    }

    [Fact]
    public async Task Evaluation_RejectedPublicationPreservesPendingOperation()
    {
        var state = new Eval(Parser.Parse(":"));
        var first = new WriteOperation(new(TaskCreationOptions.RunContinuationsAsynchronously),
            FileDescriptor.StdOut, ReadOnlyMemory<byte>.Empty);
        var second = new WriteOperation(new(TaskCreationOptions.RunContinuationsAsynchronously),
            FileDescriptor.StdOut, ReadOnlyMemory<byte>.Empty);
        state.PublishOperation(first);

        var exception = Assert.Throws<InvalidOperationException>(() => state.PublishOperation(second));
        Assert.Equal("Another operation is already pending.", exception.Message);
        Assert.Same(first, await state.WaitForNextStepAsync(CancellationToken.None));

        state.PublishOperation(second);
        Assert.Same(second, await state.WaitForNextStepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Evaluation_ForwardedPublicationUsesRootPendingIdentity()
    {
        var root = new Eval(Parser.Parse(":"));
        var child = new Eval(Parser.Parse(":"), publishNextStep: root.PublishOperation);
        var operation = new WriteOperation(new(TaskCreationOptions.RunContinuationsAsynchronously),
            FileDescriptor.StdOut, ReadOnlyMemory<byte>.Empty);
        child.PublishOperation(operation);

        var exception = Assert.Throws<InvalidOperationException>(() => child.PublishOperation(operation));
        Assert.Equal("The same evaluation operation cannot be published twice.", exception.Message);
        Assert.Same(operation, await root.WaitForNextStepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationWaitsForInProgressCloseWithoutRetrying()
    {
        var state = new Eval(Parser.Parse(": >/out"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([]) { BeforeDescriptorClose = _ => entered.SetResult() };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueCloseDescriptorTask(completion.Task);
        using var cancellation = new CancellationTokenSource();
        var running = new Engine().RunAsync(state, host, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.False(state.Completion.IsCompleted);
        }
        finally
        {
            completion.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Engine_ReportsTerminalCleanupFailureWithoutRetainingTheHandle()
    {
        var state = new Eval(Parser.Parse("exec 3> /out"));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueOpenFileDescriptor(9);
        var failure = new IOException("close failed");
        visitor.EnqueueCloseDescriptorException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new Engine().RunAsync(state, visitor)));

        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("exec 3> /out; fail")]
    [InlineData(": {fd}>/out; fail")]
    public async Task Engine_RunAsync_RetiresDescriptorsAfterCommandAndCleanupFailure(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueueCommandException(new InvalidOperationException("Command failure."));
        visitor.EnqueueCloseDescriptorException(new IOException("Transient close failure."));
        visitor.EnqueueCloseDescriptor();
        var engine = new Engine();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(state, visitor));

        Assert.Equal("Command failure.", exception.Message);
        Assert.Equal([new FileDescriptor(9)], visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData(": | :", new[] { 10, 11 })]
    [InlineData("exec 3> /out | :", new[] { 9, 10, 11 })]
    public async Task Engine_FailedPipelineCleanupRetiresEachOwnedHandle(string script, int[] expectedDescriptors)
    {
        var state = new Eval(Parser.Parse(script));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueOpenFileDescriptor(9);
        foreach (var descriptor in expectedDescriptors)
            visitor.EnqueueCloseDescriptorException(new IOException("close failed"));

        await Assert.ThrowsAsync<IOException>(() => new Engine().RunAsync(state, visitor));

        Assert.Equal(expectedDescriptors, visitor.ClosedDescriptors.Select(descriptor => descriptor.Id).Order());
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Evaluation_PipelineWaitsForStageSettlementBeforeTransferringCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var state = new Eval(Parser.Parse(": | :"));
        var pipe = Assert.IsType<CreatePipeOperation>(await state.GetNextStep(cancellation.Token));
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));
        var pipeline = Assert.IsType<RunPipelineEvaluations>(await state.GetNextStep(cancellation.Token));

        await cancellation.CancelAsync();
        Assert.False(state.Completion.IsCompleted);
        var descriptor = new FileDescriptor(11);
        pipeline.Stages[0].Evaluation.TrackOwnedDescriptor(descriptor);
        pipeline.SetResult([0, 0]);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            state.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);

        Assert.Equal(descriptor, Assert.Single(state.GetDescriptorsForCleanup()));
        Assert.Empty(pipeline.Stages[0].Evaluation.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Engine_RunAsync_ClosesNestedCommandSubstitutionDescriptorsOnce()
    {
        var state = new Eval(Parser.Parse("echo \"$(exec 3> /out)\""));
        var visitor = new TestOperationVisitor([]);
        visitor.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        visitor.EnqueueOpenFileDescriptor(9);
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueReadResult(ReadResult.EndOfFile);
        visitor.EnqueueCloseDescriptor();
        visitor.EnqueueAppendResult(DescriptorWriteResult.Open);
        visitor.EnqueueCloseDescriptor();

        await new Engine().RunAsync(state, visitor);

        Assert.Equal(
            [
                new FileDescriptor(9),
                new FileDescriptor(11),
                new FileDescriptor(10)
            ],
            visitor.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }
}

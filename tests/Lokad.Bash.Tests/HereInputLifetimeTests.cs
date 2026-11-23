using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class HereInputLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HereStringsRetainBraceTextAndExpandEffectsOnceWithinTheAllowance(bool prepared)
    {
        var literal = string.Concat(Enumerable.Repeat("{a,b}", 10));
        var source = "i=0; read -r value <<<" + literal + "$((i++)); printf '%s:<%s>' \"$i\" \"$value\"";
        var governor = new MemoryGovernor(64 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RunResult result;
        using (var owner = governor.CreateScope())
            result = prepared
                ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(source)), host, owner, deadline.Token)
                : await host.RunAsync(source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:<" + literal + "0>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("set -- a '' b; cat <<<\"$@\"", "612020620A")]
    [InlineData("IFS=:; set -- a '' b; cat <<<$*", "613A3A620A")]
    [InlineData("v='*'; cat <<<$v", "2A0A")]
    [InlineData("v='a b'; cat <<<$v", "6120620A")]
    [InlineData("HOME=synthetic; cat <<<~", "73796E7468657469630A")]
    [InlineData("unset v; cat <<<$v", "0A")]
    [InlineData("v=$'\\377'; cat <<<\"$v\"", "FF0A")]
    public async Task HereStringScalarExpansionPreservesJoiningQuotingAndBytes(string source, string expectedHex)
    {
        await using var host = new TestHost();
        host.AddFile("/match", "");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await host.RunAsync(source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(expectedHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("IFS=:", "\"${!a[@]}\"", "2:10:50")]
    [InlineData("IFS=:", "${!a[@]}", "2 10 50")]
    [InlineData("IFS=:", "\"${a[@]}\"", "wide text  last:value")]
    [InlineData("IFS=:", "\"${a[@]@U}\"", "WIDE TEXT::LAST:VALUE")]
    [InlineData("IFS=:", "${a[@]@U}", "WIDE TEXT::LAST:VALUE")]
    [InlineData("IFS=:", "\"${a[@]//text/line}\"", "wide line::last:value")]
    [InlineData("IFS=:", "\"${a[@]#wide }\"", "text::last:value")]
    [InlineData("IFS=", "\"${a[@]@k}\"", "2 wide text 10  50 last:value")]
    [InlineData("IFS=", "\"${a[@]@a}\"", "a a a")]
    [InlineData("IFS=", "\"${a[*]@k}\"", "2wide text1050last:value")]
    [InlineData("IFS=", "\"${a[@]@U}${IFS:=:}x\"", "WIDE TEXT  LAST:VALUE:x")]
    public async Task HereStringsAndAssignmentsJoinSelectedAggregatesAtExpansionTime(string ifs, string value, string expected)
    {
        var source = $$"""
            a=([2]='wide text' [10]='' [50]='last:value')
            {{ifs}}
            assigned={{value}}
            printf '%s\n' "$assigned"
            {{ifs}}
            cat <<< {{value}}
            """;
        await using var host = new TestHost();
        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected + "\n" + expected + "\n", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("""printf '<%s>' "${dst:="${a[@]@U}"}" "$dst" """, "<WIDE TEXT::LAST:VALUE><WIDE TEXT::LAST:VALUE>")]
    [InlineData("""declare -A b=([key]="${a[@]@U}"); printf '<%s>' "${b[key]}" """, "<WIDE TEXT  LAST:VALUE>")]
    [InlineData("cat <<EOF\n${a[@]@U}\nEOF", "WIDE TEXT  LAST:VALUE\n")]
    public async Task TransformedAggregatesUseTheSelectedOperandContext(string command, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("a=([2]='wide text' [10]='' [50]='last:value'); IFS=:\n" + command,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(0, host.OpenFileCount);
    }

    public static IEnumerable<object[]> BufferedInputs()
    {
        foreach (var count in new[] { 1000, 70000 })
        foreach (var prepared in new[] { false, true })
        foreach (var document in new[] { false, true })
            yield return [count, prepared, document];
    }

    [Theory]
    [MemberData(nameof(BufferedInputs))]
    public async Task ReaderRunsAcrossPipeBackpressure(int count, bool prepared, bool document)
    {
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = document ? "<<EOF\n$p\nEOF\n" : "<<<\"$p\"; ";
        var script = $"printf -v p '%{count}s' x; IFS= read -r value {input}printf '%s' \"${{#value}}\"";
        RunResult result;
        using (var memory = governor.CreateScope())
            result = prepared
                ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(script)), host, memory, deadline.Token)
                : await host.RunAsync(script, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(count.ToString(System.Globalization.CultureInfo.InvariantCulture), host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(": <<<\"$p\"; printf done", "done", 0)]
    [InlineData("IFS= read -r value <<<\"$p\" <<<tail; printf '%s' \"$value\"", "tail", 0)]
    [InlineData("exec 3<<<\"$p\"; unset p; IFS= read -r value <&3; exec 3<&-; printf '%s' \"${#value}\"", "70000", 0)]
    [InlineData("exec 3<<<\"$p\"\n)\n", "", 2)]
    public async Task UnusedOverriddenAndPersistentInputsSettleTheirProducers(string body, string output, int exitCode)
    {
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await host.RunAsync("printf -v p '%70000s' x; " + body, deadline.Token);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(exitCode == 2, !host.StandardError.Content.IsEmpty);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AllocatedInputsRetainTheirPayloadAcrossCommandWorkspaceReuse(bool prepared, bool document)
    {
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = document ? "{fd}<<END\n$p\nEND\n" : "{fd}<<<\"$p\"; ";
        var script = "printf -v p '%70000s' x; : " + input
            + "unset p; IFS= read -r value <&$fd; exec {fd}<&-; printf '%s' \"${#value}\"";
        RunResult result;
        using (var memory = governor.CreateScope())
            result = prepared
                ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(script)), host, memory, deadline.Token)
                : await host.RunAsync(script, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("70000", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RejectedAllocatedInputSettlesThePendingWriterAndBothEndpoints()
    {
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await host.RunAsync("readonly fd=old; printf -v p '%70000s' x; : {fd}<<<\"$p\"; printf '%s:%s' \"$?\" \"$fd\"", deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:old", host.StandardOutput.TextContent);
        Assert.Equal("fd: readonly variable\nfd: cannot assign fd to variable\n", host.StandardError.TextContent);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ConsumerCanReadBeforeTheBorrowedWriteCompletes()
    {
        var governor = new MemoryGovernor(64 * 1024);
        var write = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = governor,
            BeforeDescriptorRead = descriptor =>
            {
                Assert.Equal(new FileDescriptor(10), descriptor);
                readEntered.TrySetResult();
            }
        };
        host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        host.EnqueueAppendTask(write.Task);
        host.EnqueueReadResult(ReadResult.EndOfFile);
        host.EnqueueCloseDescriptor();
        host.EnqueueCloseDescriptor();
        var running = BashScript.RunAsync(host, "read -r value <<<payload; :", EvaluationOptions.Default, CancellationToken.None);
        try
        {
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(write.Task.IsCompleted);
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
        }
        finally { write.TrySetResult(DescriptorWriteResult.Open); }

        Assert.Equal(0, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        Assert.Equal(new[] { new FileDescriptor(10), new FileDescriptor(11) }, host.ClosedDescriptors.OrderBy(descriptor => descriptor.Id));
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HereInputExpansionWorkspaceRetiresBeforeConsumption(bool document)
    {
        var small = await ObserveAsync(64);
        var large = await ObserveAsync(128 * 1024);

        Assert.Equal(small, large);

        async Task<long> ObserveAsync(int length)
        {
            var literal = new string('x', length);
            var input = document ? $"<<END\n{literal}${{tail:=end}}\nEND\n" : $"<<<{literal}\"${{tail:=end}}\"; ";
            var state = new Evaluation.Evaluation(Parser.Parse("read -r value " + input + ":"));
            var governor = new MemoryGovernor(8 * 1024 * 1024);
            using var owner = governor.CreateScope();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var observed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var payload = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var write = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new TestOperationVisitor([])
            {
                MemoryBudget = governor,
                BeforeAppend = (descriptor, content) =>
                {
                    Assert.Equal(new FileDescriptor(11), descriptor);
                    payload.SetResult(content);
                },
                BeforeDescriptorRead = descriptor =>
                {
                    Assert.Equal(new FileDescriptor(10), descriptor);
                    observed.SetResult(state.Memory.Workspace.Bytes);
                }
            };
            host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
            host.EnqueueAppendTask(write.Task);
            host.EnqueueReadResult(ReadResult.EndOfFile);
            host.EnqueueCloseDescriptor();
            host.EnqueueCloseDescriptor();
            var running = new Engine().RunAsync(state, host, owner, deadline.Token);
            long workspace;
            try
            {
                workspace = await observed.Task.WaitAsync(deadline.Token);
                var pending = await payload.Task.WaitAsync(deadline.Token);
                Assert.Equal(length + 4, pending.Length);
                Assert.Equal(-1, pending.Span[..length].IndexOfAnyExcept((byte)'x'));
                Assert.True(pending.Span[length..].SequenceEqual("end\n"u8));
                Assert.False(write.Task.IsCompleted);
                Assert.True(governor.ReservedBytes >= pending.Length);
            }
            finally { write.TrySetResult(DescriptorWriteResult.Open); }

            Assert.Equal(0, (await running.WaitAsync(deadline.Token)).ExitCode);
            Assert.Equal(ShellValue.FromText("end"), state.Variables["tail"]);
            Assert.Equal(new[] { new FileDescriptor(10), new FileDescriptor(11) }, host.ClosedDescriptors.OrderBy(descriptor => descriptor.Id));
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return workspace;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsPayloadThroughPendingWriteAndBothCloses(bool allocated)
    {
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(64 * 1024);
        var borrowed = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerCloseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerCloseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = governor,
            BeforeAppend = (_, content) => borrowed.TrySetResult(content),
            ReadOverride = async token =>
            {
                readEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return DescriptorReadBuffer.EndOfFile;
            },
            BeforeDescriptorClose = descriptor =>
            {
                if (descriptor.Id == 10) readerCloseEntered.TrySetResult();
                else if (descriptor.Id == 11) writerCloseEntered.TrySetResult();
                else throw new InvalidOperationException("Unexpected close.");
            }
        };
        host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        host.EnqueueAppendTask(write.Task);
        host.EnqueueCloseDescriptorTask(readerClose.Task);
        host.EnqueueCloseDescriptorTask(writerClose.Task);
        var script = allocated ? ": {fd}<<<payload; read -r value <&$fd" : "read -r value <<<payload";
        var running = BashScript.RunAsync(host, script, EvaluationOptions.Default, cancellation.Token);
        try
        {
            var content = await borrowed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await readerCloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.Equal("payload\n"u8.ToArray(), content.ToArray());
            Assert.True(governor.ReservedBytes > 0);

            write.SetResult(DescriptorWriteResult.Open);
            await writerCloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
            writerClose.SetResult();
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
        }
        finally
        {
            write.TrySetResult(DescriptorWriteResult.Open);
            writerClose.TrySetResult();
            readerClose.TrySetResult();
        }

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(new[] { new FileDescriptor(10), new FileDescriptor(11) }, host.ClosedDescriptors);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayloadAdmissionRefusalClosesBothEndpointsWithoutWritingThePayload(bool allocated)
    {
        var governor = new MemoryGovernor(32 * 1024);
        var host = new TestOperationVisitor([]) { MemoryBudget = governor, RecordAppends = true };
        host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        host.EnqueueCloseDescriptor();
        host.EnqueueCloseDescriptor();
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        // Caller-owned prepared syntax keeps this refusal at the payload handoff,
        // after pipe creation, rather than at the public runner's source admission.
        var script = Parser.Parse(": " + (allocated ? "{fd}" : "") + "<<<'" + new string('x', 70000) + "'");
        using var owner = governor.CreateScope();
        var result = await new Engine().RunAsync(new Evaluation.Evaluation(script), host, owner, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        var diagnostic = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal(new[] { new FileDescriptor(11), new FileDescriptor(10) }, host.ClosedDescriptors);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceAdmissionRefusalDoesNotCreateTheHereInputPipe(bool allocated)
    {
        var governor = new MemoryGovernor(32 * 1024);
        var host = new TestOperationVisitor([]) { MemoryBudget = governor, RecordAppends = true };
        var descriptors = new PipeDescriptors(10, 11);
        host.EnqueuePipeDescriptors(descriptors);
        host.EnqueueCloseDescriptor();
        host.EnqueueCloseDescriptor();
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        var result = await BashScript.RunAsync(host,
            ": " + (allocated ? "{fd}" : "") + "<<<'" + new string('x', 70000) + "'",
            EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
        Assert.Empty(host.ClosedDescriptors);
        // The mock's acquisition remains queued: source refusal never requested it.
        Assert.Equal(descriptors, (await host.CreatePipeAsync(CancellationToken.None, CancellationToken.None)).Descriptors);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ProducerFailureWakesTheConsumerAndPreservesTheFirstCause()
    {
        var governor = new MemoryGovernor(64 * 1024);
        var write = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeFailure = new IOException("input write failed");
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = governor,
            RunCommandOverride = async () =>
            {
                consumerEntered.TrySetResult();
                await readerClosed.Task;
                return new Commands.CommandExecutionResult.Completed(0);
            },
            BeforeDescriptorClose = descriptor =>
            {
                if (descriptor.Id == 10) readerClosed.TrySetResult();
            }
        };
        host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        host.EnqueueAppendTask(write.Task);
        host.EnqueueCloseDescriptorException(new IOException("later writer close failed"));
        host.EnqueueCloseDescriptor();
        var running = BashScript.RunAsync(host, "consumer <<<payload", EvaluationOptions.Default, CancellationToken.None);
        try { await consumerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { write.TrySetException(writeFailure); }

        var failure = await Assert.ThrowsAsync<IOException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(writeFailure, failure);
        Assert.Equal(new[] { new FileDescriptor(11), new FileDescriptor(10) }, host.ClosedDescriptors);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HereInputReturnsPayloadAllowanceBeforeWriterCloseSettles(bool writeFails)
    {
        const int payloadLength = 32 * 1024;
        var governor = new MemoryGovernor(1024 * 1024);
        var pipe = new PipeDescriptors(10, 11);
        var write = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new TaskCompletionSource<Commands.CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Synthetic here-input write failure.");
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = governor,
            BeforeAppend = (descriptor, content) =>
            {
                Assert.Equal(pipe.Write, descriptor);
                Assert.Equal(payloadLength + 1, content.Length);
                Assert.Equal((byte)'\n', content.Span[^1]);
            },
            RunCommandOverride = () =>
            {
                consumerEntered.TrySetResult();
                return consumer.Task;
            },
            BeforeDescriptorClose = descriptor =>
            {
                if (descriptor == pipe.Write)
                    closeEntered.TrySetResult();
            }
        };
        host.EnqueuePipeDescriptors(pipe);
        host.EnqueueAppendTask(write.Task);
        host.EnqueueCloseDescriptorTask(close.Task);
        host.EnqueueCloseDescriptor();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = BashScript.RunAsync(host, "consumer <<<'" + new string('x', payloadLength) + "'",
            EvaluationOptions.Default, deadline.Token);
        try
        {
            await consumerEntered.Task.WaitAsync(deadline.Token);
            var whileBorrowed = governor.ReservedBytes;
            if (writeFails)
                write.SetException(failure);
            else
                write.SetResult(DescriptorWriteResult.Open);

            await closeEntered.Task.WaitAsync(deadline.Token);
            Assert.Equal(payloadLength + 1, whileBorrowed - governor.ReservedBytes);
            Assert.False(running.IsCompleted);
            Assert.Equal([pipe.Write], host.ClosedDescriptors);
        }
        finally
        {
            write.TrySetResult(DescriptorWriteResult.Open);
            close.TrySetResult();
            consumer.TrySetResult(new Commands.CommandExecutionResult.Completed(0));
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) when (writeFails) { }
        }

        if (writeFails)
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => running));
        else
            Assert.Equal(0, (await running).ExitCode);
        Assert.Equal([pipe.Write, pipe.Read], host.ClosedDescriptors);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillSettlesTheWriteAndBothEndpoints()
    {
        var governor = new MemoryGovernor(64 * 1024);
        var callbackFailure = new IOException("host cancellation callback failed");
        await using var host = new FailingCancellationHost(callbackFailure) { MemoryBudget = governor };
        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            BashScript.RunAsync(host, ": <<<payload", EvaluationOptions.Default, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(callbackFailure, Assert.Single(failure.InnerExceptions));
        Assert.Equal(new[] { new FileDescriptor(10), new FileDescriptor(11) }, host.Closed);
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class FailingCancellationHost(Exception callbackFailure) : MockFileSystem
    {
        private readonly TaskCompletionSource<DescriptorWriteResult> _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FileDescriptor> Closed { get; } = [];

        public override Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetimeToken, CancellationToken cancellationToken) =>
            Task.FromResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));

        public override async ValueTask<DescriptorWriteResult> WriteAsync(FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                _write.TrySetResult(DescriptorWriteResult.Closed);
                throw callbackFailure;
            });
            return await _write.Task;
        }

        public override Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            Closed.Add(descriptor);
            return Task.FromResult<OperationError?>(null);
        }
    }
}

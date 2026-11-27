using System.Runtime.InteropServices;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class CommandSubstitutionCaptureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaptureWithOnlyTrailingBytesWarnsOnlyForNuls(bool pipe, bool hasNuls)
    {
        var state = new Eval(Parser.Parse(pipe ? "value=$(cmd)" : "value=$(<input)"));
        var content = hasNuls ? "\n\0\n\0\n"u8.ToArray() : "\n\n"u8.ToArray();
        var host = QueueCapture(content, pipe, Task.CompletedTask);
        if (hasNuls)
            host.EnqueueAppendResult(DescriptorWriteResult.Open);
        var governor = new MemoryGovernor(128 * 1024);
        using var budget = governor.CreateScope();

        await new Engine().RunAsync(state, host, budget, CancellationToken.None);

        Assert.Equal(pipe ? 7 : 0, state.LastExitCode);
        Assert.True(state.Variables["value"].IsEmpty);
        if (hasNuls)
        {
            var warning = Assert.Single(host.Appends);
            Assert.Equal(FileDescriptor.StdErr, warning.Descriptor);
            Assert.Equal("line 1: warning: command substitution: ignored null byte in input\n"u8.ToArray(), warning.Content);
        }
        else
            Assert.Empty(host.Appends);
        Assert.Equal(pipe ? [new FileDescriptor(11), new FileDescriptor(10)] : new[] { new FileDescriptor(10) },
            host.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
        budget.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrailingLfsAreTrimmedBeforeCaptureTransfer(bool pipe)
    {
        var content = new byte[65536];
        content.AsSpan().Fill((byte)'\n');
        content[0] = 0xff;
        var state = new Eval(Parser.Parse(pipe ? "value=$(cmd)" : "value=$(<input)"));
        var host = QueueCapture(content, pipe, Task.CompletedTask);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        var governor = new MemoryGovernor(192 * 1024);
        using var budget = governor.CreateScope();

        await new Engine().RunAsync(state, host, budget, CancellationToken.None);

        Assert.Equal(pipe ? 7 : 0, state.LastExitCode);
        Assert.Equal(new byte[] { 0xff }, state.Variables["value"].Bytes.ToArray());
        Assert.Empty(host.Appends);
        Assert.Equal(pipe ? [new FileDescriptor(11), new FileDescriptor(10)] : new[] { new FileDescriptor(10) },
            host.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
        budget.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteriorNulCaptureOwnsExactlyTheNormalizedBytes(bool pipe)
    {
        var content = new byte[4096];
        content.AsSpan().Fill((byte)'a');
        content[0] = 0xff;
        content[128] = 0;
        "\r\0\n\n"u8.CopyTo(content.AsSpan(content.Length - 4));
        var state = new Eval(Parser.Parse(pipe ? "value=$(cmd)" : "value=$(<input)"));
        var host = QueueCapture(content, pipe, Task.CompletedTask);
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var budget = governor.CreateScope();
        var started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeAppend = (_, _) => started.SetResult(governor.ReservedBytes);
        host.EnqueueAppendTask(settle.Task);

        var execution = new Engine().RunAsync(state, host, budget, CancellationToken.None);
        try
        {
            var reserved = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(execution.IsCompleted);
            Assert.Equal(pipe ? [new FileDescriptor(11), new FileDescriptor(10)] : new[] { new FileDescriptor(10) },
                host.ClosedDescriptors);
            Assert.False(state.Variables.ContainsKey("value"));
            Assert.True(reserved >= 4092);
            Assert.Equal(reserved, governor.ReservedBytes);
        }
        finally
        {
            settle.SetResult(DescriptorWriteResult.Open);
            await execution;
        }

        var expected = Enumerable.Repeat((byte)'a', 4091).Append((byte)'\r').ToArray();
        expected[0] = 0xff;
        var result = state.Variables["value"].Bytes;
        Assert.Equal(expected, result.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(result, out var stored));
        Assert.Equal(expected.Length, Assert.IsType<byte[]>(stored.Array).Length);
        Assert.Equal(pipe ? 7 : 0, state.LastExitCode);
        Assert.Empty(state.GetDescriptorsForCleanup());
        var warning = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, warning.Descriptor);
        Assert.Equal("line 1: warning: command substitution: ignored null byte in input\n"u8.ToArray(), warning.Content);
        budget.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureRefundsUnneededBackingBeforeTheFinalClose(bool pipe)
    {
        var wide = new byte[16 * 1024];
        wide.AsSpan().Fill((byte)'\n');
        wide[0] = 0xff;

        var compact = await ObservePendingCloseAsync([0xff]);
        var trimmed = await ObservePendingCloseAsync(wide);

        Assert.Equal(compact, trimmed);

        async Task<long> ObservePendingCloseAsync(byte[] content)
        {
            var state = new Eval(Parser.Parse(pipe ? "value=$(cmd)" : "value=$(<input)"));
            var governor = new MemoryGovernor(128 * 1024);
            using var budget = governor.CreateScope();
            var closing = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = QueueCapture(content, pipe, settle.Task);
            host.BeforeDescriptorClose = descriptor =>
            {
                if (descriptor == new FileDescriptor(10))
                    closing.SetResult(governor.ReservedBytes);
            };

            var execution = new Engine().RunAsync(state, host, budget, CancellationToken.None);
            long reserved;
            try
            {
                reserved = await closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(execution.IsCompleted);
                Assert.False(state.Variables.ContainsKey("value"));
            }
            finally
            {
                settle.SetResult();
                await execution;
            }

            Assert.Equal(pipe ? 7 : 0, state.LastExitCode);
            Assert.Equal(new byte[] { 0xff }, state.Variables["value"].Bytes.ToArray());
            Assert.Equal(pipe ? [new FileDescriptor(11), new FileDescriptor(10)] : new[] { new FileDescriptor(10) },
                host.ClosedDescriptors);
            Assert.Empty(state.GetDescriptorsForCleanup());
            budget.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return reserved;
        }
    }

    private static TestOperationVisitor QueueCapture(byte[] content, bool pipe, Task finalClose)
    {
        var host = new TestOperationVisitor(pipe ? [7] : []) { RecordAppends = true };
        if (pipe)
        {
            host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
            host.EnqueueCloseDescriptor();
            var starts = Enumerable.Range(1, content.Length - 1)
                .Where(start => content[start - 1] == (byte)'\n').ToArray();
            host.EnqueueReadResult(ReadResult.Success(content, starts, reachedEoF: true));
        }
        else
        {
            host.EnqueueOpenFileDescriptor(10);
            host.EnqueueByteReadResult(content, ByteReadResult.Success(content.Length, reachedEoF: true));
        }
        host.EnqueueCloseDescriptorTask(finalClose);
        return host;
    }

    [Fact]
    public async Task FinalCaptureBytesNeedNoExtraReadAndKeepCommandStatus()
    {
        var state = new Eval(Parser.Parse("value=$(cmd)"));
        var host = new TestOperationVisitor([7]);
        host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        host.EnqueueCloseDescriptor();
        host.EnqueueCloseDescriptor();
        host.EnqueueReadResult(ReadResult.Success(new byte[] { 0xff, 13, 10 }, [], reachedEoF: true));

        await new Engine().RunAsync(state, host);

        Assert.Equal(7, state.LastExitCode);
        Assert.Equal(ShellValue.FromBytes(new byte[] { 0xff, 13 }), state.Variables["value"]);
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(10)], host.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task InputShorthandReadsRawBytesAndClosesOnlyTheOpenedFile()
    {
        var state = new Eval(Parser.Parse("value=$(<input)"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueCloseDescriptor();
        byte[] content = [0xff, (byte)'a', 0, 13, 10, 10];
        host.EnqueueByteReadResult(content, ByteReadResult.Success(content.Length, reachedEoF: true));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new byte[] { 0xff, (byte)'a', 13 }, state.Variables["value"].Bytes.ToArray());
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
        var warning = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, warning.Descriptor);
        Assert.Equal("line 1: warning: command substitution: ignored null byte in input\n"u8.ToArray(), warning.Content);
    }

    [Theory]
    [InlineData("$(<input)", "one\ntwo")]
    [InlineData("$(0<input;)", "one\ntwo")]
    [InlineData("$( ! <input )", "one\ntwo")]
    [InlineData("`< input`", "one\ntwo")]
    [InlineData("$(<input <input)", "")]
    [InlineData("$(x=y <input)", "")]
    [InlineData("$($unset <input)", "")]
    [InlineData("$(3<input)", "")]
    [InlineData("$(<<<data)", "")]
    [InlineData("$({ <input; })", "")]
    [InlineData("$(<input; printf tail)", "tail")]
    public async Task OnlyInputFileSubstitutionSyntaxReadsContents(string substitution, string expected)
    {
        var host = EndToEndHarness.BuildTree([("/input", "one\ntwo\n\n")]);
        var result = await EndToEndHarness.RunScriptAsync($"x={substitution}; printf '<%s>' \"$x\"", host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<{expected}>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("$(<missing\nprintf skip\n<\"$((i+=1))\")", "<0:input:1>", "")]
    [InlineData("$(0<missing\n<\"$((i+=1))\")", "<0:input:1>", "")]
    [InlineData("$(printf prefix\n<\"$((i+=1))\")", "<0:prefixinput:0>", "")]
    [InlineData("$(printf prefix; <1)", "<0:prefix:0>", "")]
    [InlineData("$(printf prefix\n! <missing)", "<1:prefix:0>", "line 4: missing: No such file or directory\n")]
    [InlineData("$(! <\"$((i+=1))\")", "<0:input:0>", "")]
    [InlineData("`0<\"$((i+=1))\"`", "<0:input:0>", "")]
    [InlineData("`printf prefix\n<1 # inline\n`", "<0:prefixinput:0>", "")]
    [InlineData("`printf prefix\n<1\n# after\n`", "<0:prefix:0>", "")]
    [InlineData("$(exec >&-\n<1)", "<1::0>", "")]
    [InlineData("$(exec >&-\n</dev/null)", "<0::0>", "")]
    public async Task FinalInputFileUsesTheReachedReaderAndBindings(string substitution, string stdout, string stderr)
    {
        var script = $"i=0\nv={substitution}\nprintf '<%s:%s:%s>' \"$?\" \"$v\" \"$i\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = EndToEndHarness.BuildTree([("/1", "input\n")]);
            var exitCode = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, exitCode);
            Assert.Equal(stdout, host.StandardOutput.TextContent);
            Assert.Equal(stderr, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task FinalInputFileStreamsRawBytesBeforeTheChildExitAction()
    {
        const string script = "v=$(trap 'printf end' EXIT; printf prefix\n<input)\nprintf '%s' \"$v\"";
        byte[] payload = [.. Enumerable.Repeat((byte)'x', 20000), 0xFF, 13, 10, 10];
        await using var host = new TestHost();
        host.AddFile("/input", payload);
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal<byte>([.. "prefix"u8, .. payload, .. "end"u8], host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CancelingAReachedFileCopySettlesBothDescriptors()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var host = new TestHost { MemoryBudget = governor };
        host.AddFile("/input", new string('x', 20000));
        var writes = 0;
        host.BeforeAppend = (_, _, _) =>
        {
            writes++;
            cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RunAsync("v=$(exec >selected\n<input)", cancellation.Token));

        Assert.Equal(1, writes);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileTargetExpansionRunsOnceInItsCallerAndPreservesBorrowedInput(bool prepared)
    {
        const string script = """
            exec 3<input
            part=1
            { x=$(<"$(printf hit >>effects)$((part=2))"); read -r line; } <&3
            modern=$part
            part=1
            y=`<"$((part=2))"`
            printf '<%s><%s><%s><%s><%s>' "$modern" "$part" "$x" "$y" "$line"
            """;
        var host = EndToEndHarness.BuildTree([("/2", "file\n"), ("/input", "borrowed\n")]);
        var exitCode = prepared
            ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
            : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

        Assert.Equal(0, exitCode);
        Assert.Equal("<2><2><file><file><borrowed>", host.StandardOutput.TextContent);
        Assert.Equal("hit", host.ReadFile("/effects"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task FailedReadDiscardsPartialInputAndPreservesReadDiagnostic()
    {
        var state = new Eval(Parser.Parse("value=$(<input)"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueByteReadResult("prefix"u8.ToArray(), ByteReadResult.Success(6, reachedEoF: false));
        host.EnqueueByteReadResult(ReadOnlyMemory<byte>.Empty,
            ByteReadResult.Failure(0, new OperationError("read failed\n"), reachedEoF: false));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueCloseDescriptorResult(new OperationError("close failed\n"));

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.Empty, state.Variables["value"]);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
        var diagnostic = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("shell: read failed\n"u8.ToArray(), diagnostic.Content);
    }

    [Theory]
    [InlineData("set -e; if value=$(<missing); then printf wrong; fi; printf after", 1, "")]
    [InlineData("set -e; printf '<%s>' \"$(<missing)\"; printf after", 1, "")]
    [InlineData("printf '<%s><%s>' \"$(<missing)\" \"$?\"; printf after", 0, "<><1>after")]
    public async Task FileOpenFailureKeepsTheCallerStatusPolicy(string script, int exitCode, string output)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal("line 1: missing: No such file or directory\n", result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Theory]
    [InlineData("unset path; printf '<%s>' \"$(<\"${path:?required}\")\"; printf after", 1, "", "line 1: path: required\n")]
    [InlineData("set -e; path=; printf '<%s>' \"$(<$path)\"; printf after", 0, "<>after", "$path: ambiguous redirect\n")]
    public async Task FileTargetExpansionRetainsTheErrorOrigin(string script, int status, string stdout, string stderr)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(stdout, result.StdOut);
        Assert.Equal(stderr, result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task CancellationSettlesTheBorrowBeforeClosingTheFile()
    {
        var state = new Eval(Parser.Parse("value=$(<input)"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;
        var host = new TestOperationVisitor([])
        {
            ReadOverride = async token =>
            {
                started.SetResult();
                await settle.Task;
                settled = true;
                token.ThrowIfCancellationRequested();
                return DescriptorReadBuffer.EndOfFile;
            },
            BeforeDescriptorClose = _ => Assert.True(settled)
        };
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueCloseDescriptor();
        using var cancellation = new CancellationTokenSource();
        var evaluation = new Engine().RunAsync(state, host, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        finally
        {
            settle.SetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task FileOpenedDuringCancellationIsClosedBeforeTheEvaluationSettles()
    {
        var state = new Eval(Parser.Parse("value=$(<input)"));
        using var cancellation = new CancellationTokenSource();
        var evaluation = state.EnsureRunning(cancellation.Token);
        var descriptor = new FileDescriptor(10);
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());

        cancellation.Cancel();
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));

        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task CaptureAdmissionRefusalClosesTheFileWithoutPublishingItsValue()
    {
        var state = new Eval(Parser.Parse("value=old; value=$(<input)"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueOpenFileDescriptor(10);
        var bytes = new byte[64 * 1024];
        host.EnqueueByteReadResult(bytes, ByteReadResult.Success(bytes.Length, reachedEoF: true));
        host.EnqueueCloseDescriptor();
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        var governor = new MemoryGovernor(48 * 1024);

        using (var budget = governor.CreateScope())
        {
            await new Engine().RunAsync(state, host, budget, CancellationToken.None);
            Assert.Equal(1, state.LastExitCode);
            Assert.Equal(ShellValue.FromText("old"), state.Variables["value"]);
        }

        Assert.Equal(0, governor.ReservedBytes);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }
}

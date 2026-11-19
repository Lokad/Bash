using System.Runtime.InteropServices;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ExecutionLifetimeTests
{
    [Theory]
    [InlineData("echo", "\n")]
    [InlineData("printf '%s'", "")]
    public Task CancelledWriteStaysChargedUntilTheHostSettles(string command, string suffix)
    {
        var payload = new string('x', 4096);
        return VerifyPendingWriteAsync($"{command} '{payload}' >out; after=1", payload + suffix);
    }

    [Fact]
    public Task CancelledExpansionDiagnosticStaysChargedUntilTheHostSettles()
    {
        var subject = "${" + new string('x', 4096) + " name}";
        return VerifyPendingWriteAsync("{ : \"" + subject + "\"; } 2>out; after=1", subject + ": bad substitution\n");
    }

    [Fact]
    public async Task AmbiguousRedirectionWritesOneExactBufferToReachedDescriptor()
    {
        var operand = new byte[4096];
        operand.AsSpan().Fill((byte)'x');
        operand[^1] = 0xff;
        byte[] expected = [.. operand, .. ": ambiguous redirect\n"u8];
        var state = new Eval(Parser.Parse("a=('two fields' \"$1\"); i=0; : 2>out >${a[i++]} >later"));
        state.ReplacePositionalParameters([ShellValue.FromBytes(operand)]);
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        var descriptor = new FileDescriptor(9);

        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, write.FileDescriptor);
        Assert.Equal(expected, write.Content.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(write.Content, out var backing));
        Assert.NotNull(backing.Array);
        Assert.Equal(expected.Length, backing.Array.Length);
        var pending = governor.ReservedBytes;
        write.SetResult(DescriptorWriteResult.Open);

        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, close.Descriptor);
        Assert.True(governor.ReservedBytes <= pending - expected.Length);
        close.SetResult(null);
        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("2"), state.Variables["i"]);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public Task CancelledPreparedTerminalDiagnosticStaysChargedUntilTheHostSettles()
    {
        var target = new string('x', 4096);
        return VerifyPendingWriteAsync($"builtin '{target}' 2>out; after=1", $"builtin: {target}: not a shell builtin\n");
    }

    [Fact]
    public Task CancelledTrapListingStaysChargedUntilTheHostSettles()
    {
        var action = new string('x', 4096);
        return VerifyPendingWriteAsync($"trap '{action}' INT; trap -p INT >out; after=1", $"trap -- '{action}' SIGINT\n");
    }

    [Fact]
    public Task CancelledPrintfDiagnosticStaysChargedUntilTheHostSettles()
    {
        var number = new string('x', 4096);
        return VerifyPendingWriteAsync($"printf '%d' '{number}' 2>out; after=1", $"printf: {number}: invalid number\n");
    }

    [Fact]
    public async Task ClosedPrintfStderrDoesNotReserveDiscardedConversionDiagnostics()
    {
        var operand = new string('x', 4095);
        var state = new Eval(Parser.Parse("printf 'ab%n%d%d%n' before \"$1\" \"$2\" after 2>&-"));
        state.ReplacePositionalParameters([ShellValue.FromText("1" + operand), ShellValue.FromText("2" + operand)]);
        var governor = new MemoryGovernor(44 * 1024);
        using var owner = governor.CreateScope();
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        var write = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdOut, write.Descriptor);
        Assert.Equal("ab12"u8.ToArray(), write.Content);
        Assert.Equal(ShellValue.FromText("2"), state.Variables["before"]);
        Assert.Equal(ShellValue.FromText("4"), state.Variables["after"]);
        Assert.Equal(1, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task PrintfReleasesEachDiagnosticBeforeTheNextConversion()
    {
        var firstOperand = MakeOperand((byte)'1', 0xff);
        var secondOperand = MakeOperand((byte)'2', 0xfe);
        var state = new Eval(Parser.Parse("printf 'ab%n%d%d%n' before \"$1\" \"$2\" after 2>out"));
        state.ReplacePositionalParameters([ShellValue.FromBytes(firstOperand), ShellValue.FromBytes(secondOperand)]);
        var governor = new MemoryGovernor(512 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        var descriptor = new FileDescriptor(9);

        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var first = await ReceiveDiagnosticAsync(firstOperand);
        var second = await ReceiveDiagnosticAsync(secondOperand);
        var output = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(FileDescriptor.StdOut, output.FileDescriptor);
        Assert.Equal("ab12"u8.ToArray(), output.Content.ToArray());
        output.SetResult(DescriptorWriteResult.Open);
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("4"), state.Variables["after"]);
        Assert.Equal(first, second);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);

        async Task<long> ReceiveDiagnosticAsync(byte[] operand)
        {
            var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
            Assert.Equal(descriptor, write.FileDescriptor);
            byte[] expected = [.. "printf: "u8, .. operand, .. ": invalid number\n"u8];
            Assert.Equal(expected, write.Content.ToArray());
            Assert.Equal(ShellValue.FromText("2"), state.Variables["before"]);
            Assert.False(state.Variables.ContainsKey("after"));
            var reserved = governor.ReservedBytes;
            write.SetResult(DescriptorWriteResult.Open);
            return reserved;
        }

        static byte[] MakeOperand(byte first, byte last)
        {
            var bytes = new byte[4096];
            bytes.AsSpan().Fill((byte)'x');
            bytes[0] = first;
            bytes[^1] = last;
            return bytes;
        }
    }

    [Theory]
    [InlineData("cd \"-$1\"", "cd: -", ": invalid option\n", 2, 56 * 1024)]
    [InlineData("trap -p \"$1\"", "trap: ", ": invalid signal specification\n", 1, 32 * 1024)]
    public async Task RejectedOperandFitsWithOneDiagnosticBuffer(
        string command, string prefix, string suffix, int status, int allowance)
    {
        var operand = new byte[4096];
        operand.AsSpan().Fill((byte)'x');
        operand[^1] = 0xff;
        byte[] expected = [.. System.Text.Encoding.ASCII.GetBytes(prefix), .. operand, .. System.Text.Encoding.ASCII.GetBytes(suffix)];
        var state = new Eval(Parser.Parse(command + " 2>out"));
        state.ReplacePositionalParameters([ShellValue.FromBytes(operand)]);
        var governor = new MemoryGovernor(allowance);
        using var owner = governor.CreateScope();
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        var write = Assert.Single(host.Appends);
        Assert.Equal(new FileDescriptor(9), write.Descriptor);
        Assert.Equal(expected, write.Content);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Equal(status, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private static async Task VerifyPendingWriteAsync(string script, string expected)
    {
        var state = new Eval(Parser.Parse(script));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var descriptor = new FileDescriptor(9);
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep(CancellationToken.None));
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var write = Assert.IsType<WriteOperation>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(descriptor, write.FileDescriptor);

        var before = governor.ReservedBytes;
        cancellation.Cancel();
        var next = state.GetNextStep(CancellationToken.None);
        var publishedEarly = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100))) == next;
        var pending = governor.ReservedBytes;
        var content = System.Text.Encoding.UTF8.GetString(write.Content.Span);
        write.SetResult(DescriptorWriteResult.Open);

        var close = Assert.IsType<CloseDescriptorOperation>(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(expected, content);
        Assert.False(publishedEarly, $"Close was published while the write was pending. Allowance: {before} before cancellation and {pending} while pending.");
        Assert.Equal(before, pending);
        Assert.True(governor.ReservedBytes < pending);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Theory]
    [InlineData("first | second | third", true)]
    [InlineData("value=$(printf nested)", false)]
    [InlineData("read value <<<'value'", false)]
    public async Task CancelledPipeAcquisitionClosesEveryLateEndpoint(string command, bool earlierPipe)
    {
        var state = new Eval(Parser.Parse(command + "; after=1"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var creation = Assert.IsType<CreatePipeOperation>(await state.GetNextStep(CancellationToken.None));
        if (earlierPipe)
        {
            creation.SetResult(PipeCreationResult.Created(new PipeDescriptors(30, 31)));
            creation = Assert.IsType<CreatePipeOperation>(await state.GetNextStep(CancellationToken.None));
        }

        var before = governor.ReservedBytes;
        cancellation.Cancel();
        var next = state.GetNextStep(CancellationToken.None);
        var publishedEarly = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100))) == next;
        var pending = governor.ReservedBytes;
        creation.SetResult(PipeCreationResult.Created(new PipeDescriptors(32, 33)));

        var closed = new List<FileDescriptor>();
        while (true)
        {
            EvaluationOperation? operation;
            try { operation = await next.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException e) when (e.CancellationToken == cancellation.Token) { break; }
            var close = Assert.IsType<CloseDescriptorOperation>(operation);
            closed.Add(close.Descriptor);
            close.SetResult(closed.Count == 1 ? new OperationError("Synthetic close failure\n") : null);
            next = state.GetNextStep(CancellationToken.None);
        }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(earlierPipe
            ? [new FileDescriptor(33), new FileDescriptor(32), new FileDescriptor(31), new FileDescriptor(30)]
            : new[] { new FileDescriptor(33), new FileDescriptor(32) }, closed);
        Assert.False(publishedEarly);
        Assert.Equal(before, pending);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepresentationOverflowReportsOnceBeforeAllocatingTheExpansion(bool redirected)
    {
        await using var host = new TestHost();
        // Bash's sequence count limit is higher than the CLR backing-array limit.
        var command = $"printf '%s' {{0..{Array.MaxLength - 1}}}";
        var script = redirected ? "{ " + command + "; } 2>/errors" : command + " >/out";
        var result = await host.RunAsync(script, CancellationToken.None);
        var diagnostic = redirected ? host.ReadFile("/errors") : host.StandardError.TextContent;

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("CLR array length limit", diagnostic);
        Assert.Single(diagnostic.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (redirected)
            Assert.Empty(host.StandardError.Content.ToArray());
        else
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(20000)]
    public async Task CompletedIterationsReuseLiveMemory(int iterations)
    {
        var governor = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync(
            $"for ((i=0; i<{iterations}; i++)); do :; done; printf 'done:%s' \"$i\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"done:{iterations}", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
        Assert.InRange(governor.PeakBytes, 1, governor.LimitBytes);
    }

    [Fact]
    public async Task CpuOnlyExpansionObservesCancellationAndRefundsItsOwner()
    {
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(64 * 1024 * 1024);
        var memory = new CountingMemoryBudget(governor);
        var admissions = 0;
        memory.AdmissionSucceeded += () =>
        {
            if (++admissions == 16)
                cancellation.Cancel();
        };
        await using var host = new TestHost { MemoryBudget = memory };

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RunAsync("x=a; while :; do x=$x$x; done; printf unexpected", cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.True(admissions >= 16);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ArithmeticObservesCancellationDuringSustainedBinding()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var expression = ArithmeticOperand.FromValue(ShellValue.FromText(
            string.Join('+', Enumerable.Repeat("value", 10000))));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ArithmeticEvaluation.EvaluateAsync<string>(expression, Bind,
                (_, _, _) => throw new InvalidOperationException("Unexpected assignment"), null, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(500, reads);

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            if (++reads == 500)
                cancellation.Cancel();
            return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Bound("value", 1));
        }
    }

    [Theory]
    [InlineData("echo borrowed >/out", "borrowed\n")]
    [InlineData("echo borrowed >/out\n)\n", "borrowed\n")]
    [InlineData("trap 'echo borrowed >/out' EXIT; exit 7", "borrowed\n")]
    [InlineData("trap $'trap - EXIT\\necho borrowed >/out\\n' EXIT; exit 7", "borrowed\n")]
    [InlineData("a=borrowed; f(){ local -n a=a; echo \"$a\" >/out; }; f 2>/dev/null", "borrowed\n")]
    [InlineData("declare -n a=a 2>/out", "declare: a: nameref variable self references not allowed\n")]
    [InlineData("printf '%d' $'\\377' 2>/out", "printf: ÿ: invalid number\n")]
    public async Task CancellationRetainsBorrowedOutputUntilWriteAndCloseSettle(string source, string expected)
    {
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(64 * 1024);
        var writeEntered = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeComplete = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = governor,
            BeforeAppend = (_, content) => writeEntered.SetResult(content),
            BeforeDescriptorClose = _ => closeEntered.SetResult()
        };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendTask(writeComplete.Task);
        host.EnqueueCloseDescriptorTask(closeComplete.Task);
        var running = BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);
        try
        {
            var borrowed = await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
            // Latin1 names each expected byte directly, including malformed UTF-8 diagnostics.
            Assert.Equal(System.Text.Encoding.Latin1.GetBytes(expected), borrowed.ToArray());

            writeComplete.SetResult(DescriptorWriteResult.Open);
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes > 0);
        }
        finally
        {
            writeComplete.TrySetResult(DescriptorWriteResult.Open);
            closeComplete.TrySetResult();
        }

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Equal(0, governor.ReservedBytes);
    }
}

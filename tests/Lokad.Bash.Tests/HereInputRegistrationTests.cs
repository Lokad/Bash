using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class HereInputRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAliasRetainsItsInputAfterTheOriginalBindingCloses(bool prepared)
    {
        const string Source = "exec 3<<<one; exec 4<&3; exec 3<&-; IFS= read -r value <&4; exec 4<&-; printf '<%s>' \"$value\"";
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(Source)), host, owner, deadline.Token)
            : await host.RunAsync(Source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<one>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RegistrationStorageIsAdmittedBeforeTheHostCreatesAPipe()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(": <<<payload; printf ready"));
        await using var host = new ObservingPipeHost { MemoryBudget = owner };
        var observed = false;
        host.BeforePipe = () =>
        {
            var registrations = Registrations(evaluation);
            Assert.NotNull(registrations);
            Assert.Empty(registrations);
            var payload = ArrayPayload(registrations);
            Assert.True(payload > 0);
            Assert.True(CleanupBytes(evaluation) >= PendingBytes(evaluation) + payload);
            observed = true;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(evaluation, host, owner, deadline.Token);

        Assert.True(observed);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Null(Registrations(evaluation));
        Assert.Equal(0, CleanupBytes(evaluation));
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task HighWaterRegistrationCapacityStaysChargedAfterMostInputsClose()
    {
        const int Count = 128;
        var source = $"for ((i=0;i<{Count};i++)); do : {{fd}}<<<x; fds[i]=$fd; done; "
            + $"for ((i=0;i<{Count - 1};i++)); do fd=${{fds[i]}}; exec {{fd}}<&-; done; "
            + "[[ -e /probe ]]; printf ready";
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(source));
        await using var host = new ObservingPipeHost { MemoryBudget = owner };
        host.AddFile("/probe", "synthetic");
        var observed = false;
        host.BeforeGetPathInfo = path =>
        {
            if (path.Path != "/probe") return;
            var registrations = Registrations(evaluation);
            Assert.NotNull(registrations);
            Assert.Single(registrations);
            var payload = ArrayPayload(registrations);
            Assert.True(payload > 16L * Count);
            Assert.True(CleanupBytes(evaluation) >= PendingBytes(evaluation) + payload);
            observed = true;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(evaluation, host, owner, deadline.Token);

        Assert.True(observed);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(Count, host.Pipes);
        Assert.Null(Registrations(evaluation));
        Assert.Equal(0, CleanupBytes(evaluation));
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task BoundedInputsReuseTheirChargedRegistrationCapacity()
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse("for ((i=0;i<256;i++)); do : <<<x; done; printf ready"));
        await using var host = new ObservingPipeHost { MemoryBudget = owner };
        IDictionary? first = null;
        long payload = 0;
        host.BeforePipe = () =>
        {
            var registrations = Registrations(evaluation);
            Assert.NotNull(registrations);
            if (first == null) { first = registrations; payload = ArrayPayload(registrations); }
            Assert.Same(first, registrations);
            Assert.Equal(payload, ArrayPayload(registrations));
            Assert.True(CleanupBytes(evaluation) >= PendingBytes(evaluation) + payload);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(evaluation, host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(256, host.Pipes);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Null(Registrations(evaluation));
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationCapacitySurvivesTheLastPendingClose(bool cancel)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var evaluation = new Eval(Parser.Parse("exec 3<<<payload; printf ready"));
        await using var host = new ObservingPipeHost { MemoryBudget = owner, DelayReaderClose = true };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new Engine().RunAsync(evaluation, host, owner, cancellation.Token);
        try
        {
            await host.Closing.Task.WaitAsync(deadline.Token);
            if (cancel) cancellation.Cancel();
            Assert.False(running.IsCompleted);
            var registrations = Registrations(evaluation);
            Assert.NotNull(registrations);
            Assert.Empty(registrations);
            Assert.True(CleanupBytes(evaluation) >= PendingBytes(evaluation) + ArrayPayload(registrations));
        }
        finally
        {
            host.Release.TrySetResult();
            try { await running.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (cancel) { }
        }

        if (cancel)
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        else Assert.Equal(0, (await running).ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Null(Registrations(evaluation));
        Assert.Equal(0, CleanupBytes(evaluation));
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void RefusedRegistrationPreparationAllocatesNoBackingArrays()
    {
        var governor = new MemoryGovernor(0);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(":"));
        evaluation.Memory.Budget = owner;

        Assert.Throws<MemoryBudgetExceededException>(() => evaluation.PreparePipeInput());

        var registrations = Registrations(evaluation);
        Assert.NotNull(registrations);
        Assert.Empty(registrations);
        Assert.Equal(0, ArrayPayload(registrations));
        Assert.Equal(0, governor.ReservedBytes);
        evaluation.ReleaseDescriptorCleanupMemory();
    }

    // Observe actual storage and the existing execution owner, without production accessors.
    private static object? Field(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);

    private static IDictionary? Registrations(Eval evaluation) => Field(evaluation, "_pipeInputs") as IDictionary;

    private static long CleanupBytes(Eval evaluation) => Field(evaluation, "_descriptorCleanupMemory")
        is ValueTuple<IMemoryBudget, MemoryCapacity> owner ? owner.Item2.Bytes : 0;

    private static long PendingBytes(Eval evaluation)
    {
        var pending = Field(evaluation, "_pendingDescriptorCleanup") ?? throw new InvalidOperationException();
        return (long)(pending.GetType().GetProperty("EstimatedMemoryBytes")?.GetValue(pending)
            ?? throw new InvalidOperationException());
    }

    private static long ArrayPayload(IDictionary dictionary)
    {
        long bytes = 0;
        foreach (var name in new[] { "_entries", "_buckets" })
        {
            if (Field(dictionary, name) is not Array array) continue;
            var element = array.GetType().GetElementType() ?? throw new InvalidOperationException();
            var size = (int)(typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))?.MakeGenericMethod(element).Invoke(null, null)
                ?? throw new InvalidOperationException());
            bytes += (long)array.Length * size;
        }
        return bytes;
    }

    private sealed class ObservingPipeHost : TestHost
    {
        public Action? BeforePipe { get; set; }
        public bool DelayReaderClose { get; init; }
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pipes { get; private set; }
        private FileDescriptor? _reader;

        public override async Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetimeToken, CancellationToken cancellationToken)
        {
            BeforePipe?.Invoke();
            Pipes++;
            var result = await base.CreatePipeAsync(lifetimeToken, cancellationToken);
            _reader = result.Descriptors?.Read;
            return result;
        }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            if (DelayReaderClose && descriptor == _reader)
            {
                Closing.TrySetResult();
                await Release.Task;
            }
            return await base.CloseDescriptorAsync(descriptor);
        }

        public override async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await base.DisposeAsync();
        }
    }
}

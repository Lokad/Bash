using System.Reflection;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DescriptorOwnershipLifetimeTests
{
    [Fact]
    public async Task SuccessfulSetupKeepsItsAdmittedFrameThroughCommandExecution()
    {
        using var owner = new MemoryGovernor(1024 * 1024).CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        host.AddFile("/unit", "synthetic");
        var evaluation = new Eval(Parser.Parse("[[ -e /unit ]] >out"));
        object? setup = null;
        object? executing = null;
        host.BeforeWritableOpen = _ => setup = Frame(evaluation);
        host.BeforeGetPathInfo = path => { if (path.Path == "/unit") executing = Frame(evaluation); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(evaluation, host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(setup);
        Assert.NotNull(executing);
        Assert.Same(setup, executing);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("exec 3>one {fd}>two; printf ready")]
    [InlineData("exec {fd}>one 3>two; printf ready")]
    [InlineData("exec 3>one {a}>two {b}>three; printf ready")]
    [InlineData("exec {a}>one 3>two {b}>three; printf ready")]
    [InlineData("exec {fd}>one 10>two; printf ready")]
    [InlineData("exec 3>one 4>two; f(){ exec 3>three; }; f 10>&- {fd}>&3; printf ready")]
    [InlineData("exec 3>one; f(){ (exec 4>&3; (:); printf child >&4); }; f 10>&-; printf parent >&3; printf ready")]
    [InlineData("exec 3>one; f(){ (: {fd}>&3; (:); printf child >&$fd); }; f 10>&-; printf parent >&3; printf ready")]
    public async Task PersistentOwnershipKeepsTransferOrderAndClosesEachAcquisitionOnce(string source)
    {
        using var owner = new MemoryGovernor(4 * 1024 * 1024).CreateScope();
        await using var host = new RecordingCloseHost { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(host.Opened, host.Closed);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task NestedPersistenceClosesOnlyHandlesOwnedByTheCurrentSubshell()
    {
        const string source = """
            exec 3>root
            (
                exec 4>middle
                (
                    exec 6>&3
                    : {alias}>&4
                    exec 5>leaf
                    exec 8>&5
                    exec 8>replacement
                    exec 5>&-
                    printf leaf >&8
                    printf root >&6
                    printf middle >&"$alias"
                    exec 6>&-
                    exec {alias}>&-
                )
                printf outer >&4
            )
            printf parent >&3
            """;
        using var owner = new MemoryGovernor(1024 * 1024).CreateScope();
        await using var host = new RecordingCloseHost { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal("rootparent", host.ReadFile("/root"));
        Assert.Equal("middleouter", host.ReadFile("/middle"));
        Assert.Empty(host.ReadFile("/leaf"));
        Assert.Equal("leaf", host.ReadFile("/replacement"));
        Assert.Equal(4, host.Opened.Count);
        Assert.Equal(new[] { host.Opened[2], host.Opened[3], host.Opened[1], host.Opened[0] }, host.Closed);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(": 3>replacement", 1, "body", "")]
    [InlineData("exec 3>replacement", 0, "", "body")]
    [InlineData("exec 4>&3; exec 3>replacement; printf retained >&4; exec 4>&-", 0, "retained", "body")]
    public async Task ReplacingAPersistentBindingClosesOnlyTheHandleThatLosesItsLastReference(
        string replacement, int firstClosed, string initialContent, string replacementContent)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new RecordingCloseHost { MemoryBudget = owner };
        FileDescriptor[]? closedBeforeNextOpen = null;
        host.BeforeWritableOpen = path =>
        {
            if (path.Path == "/marker")
                closedBeforeNextOpen = host.Closed.ToArray();
        };
        var source = $"exec 3>initial; {replacement}; printf ready >marker; printf body >&3";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(initialContent, host.ReadFile("/initial"));
        Assert.Equal(replacementContent, host.ReadFile("/replacement"));
        Assert.Equal("ready", host.ReadFile("/marker"));
        Assert.Equal(3, host.Opened.Count);
        Assert.Equal(new[] { host.Opened[firstClosed] }, closedBeforeNextOpen);
        Assert.Equal(new[] { host.Opened[firstClosed], host.Opened[2], host.Opened[1 - firstClosed] }, host.Closed);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredHereDocumentSettlesReplacementsAtTheLastReference(bool keepsAlias)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new RecordingCloseHost { MemoryBudget = owner };
        FileDescriptor[]? closedBeforeMarker = null;
        host.BeforeWritableOpen = path =>
        {
            if (path.Path == "/marker") closedBeforeMarker = host.Closed.ToArray();
        };
        var alias = keepsAlias ? " 4>&3" : "";
        var useAlias = keepsAlias ? "printf alias >&4; exec 4>&-\n" : "";
        var script = "exec 3>initial" + alias + "\nread -r value 3>replacement <<EOF\nX$(!)Y\nEOF\n"
            + "printf marker >marker\n" + useAlias + "printf kept >&3\n";
        var result = await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(keepsAlias ? "alias" : "", host.ReadFile("/initial"));
        Assert.Equal("kept", host.ReadFile("/replacement"));
        Assert.Equal(3, host.Opened.Count);
        Assert.NotNull(closedBeforeMarker);
        Assert.Equal(keepsAlias ? [] : new[] { host.Opened[0] }, closedBeforeMarker);
        Assert.Equal(keepsAlias ? new[] { host.Opened[2], host.Opened[0], host.Opened[1] }
            : new[] { host.Opened[0], host.Opened[2], host.Opened[1] }, host.Closed);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CancellationAwaitsTheHereDocumentReplacementClose()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new PendingCloseHost { MemoryBudget = owner };
        const string script = "exec 3>initial\nread -r value 3>replacement <<EOF\nX$(!)Y\nEOF\nprintf unreached\n";
        var running = new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancellation.Token);
        try
        {
            await host.Entered.Task.WaitAsync(deadline.Token);
            Assert.Equal(2, host.Opened.Count);
            Assert.Equal(host.Opened[0], host.ClosingHandle);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.Empty(host.StandardOutput.Content.ToArray());
        }
        finally
        {
            host.Release.TrySetResult();
        }

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(deadline.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, host.Closes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" 7>&99", "line 1: 99: Bad file descriptor\n")]
    public async Task ConsumedHandleClosesBeforeTheNextCommandBegins(string laterRedirection, string error)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new RecordingCloseHost { MemoryBudget = owner };
        var closedBeforeNextOpen = -1;
        host.BeforeWritableOpen = path =>
        {
            if (path.Path == "/next")
                closedBeforeNextOpen = host.Closed.Count;
        };
        var evaluation = new Eval(Parser.Parse("exec 3>out; : 4>&3-" + laterRedirection + "; printf ready >next"));

        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, closedBeforeNextOpen);
        Assert.Equal(2, host.Opened.Count);
        Assert.Equal(host.Opened, host.Closed);
        Assert.Equal(error, host.StandardError.TextContent);
        Assert.Equal("ready", host.ReadFile("/next"));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CancellationAwaitsTheConsumedHandleClose()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new PendingCloseHost { MemoryBudget = owner };
        var evaluation = new Eval(Parser.Parse("exec 3>out; : 4>&3-; printf unreached"));
        var running = new Engine().RunAsync(evaluation, host, owner, cancellation.Token);

        try
        {
            await host.Entered.Task.WaitAsync(deadline.Token);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.Empty(host.StandardOutput.Content.ToArray());
        }
        finally
        {
            host.Release.TrySetResult();
        }

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(deadline.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, host.Closes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedAllocationsAdmitReceivingOwnershipWhileBoundedHandlesReuseIt(bool bounded)
    {
        const int Count = 64;
        var suffix = bounded ? " exec {fd}>&-;" : new string(' ', " exec {fd}>&-;".Length);
        var source = $"fd=0; set -e; for ((i=0;i<{Count};i++)); do : {{fd}}>out;" + suffix + " done; printf ready";
        var governor = new MemoryGovernor(32 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var opened = 0;
        host.BeforeWritableOpen = _ => opened++;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, deadline.Token);

        Assert.Equal(bounded ? 0 : 1, result.ExitCode);
        Assert.Equal(bounded ? "ready" : "", host.StandardOutput.TextContent);
        Assert.Equal(!bounded, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Empty(host.ReadFile("/out"));
        if (bounded) Assert.Equal(Count, opened);
        else Assert.InRange(opened, 1, Count - 1);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task FinalCloseRetainsOwnershipIndexUntilItsConsumerSettlesThenRetiresIt()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new PendingCloseHost { MemoryBudget = owner };
        var evaluation = new Eval(Parser.Parse("exec 3>out; printf ready"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new Engine().RunAsync(evaluation, host, owner, deadline.Token);
        long during = 0;
        try
        {
            await host.Entered.Task.WaitAsync(deadline.Token);
            Assert.False(running.IsCompleted);
            Assert.Single(Handles(evaluation));
            during = governor.ReservedBytes;
        }
        finally
        {
            host.Release.TrySetResult();
            await running;
        }

        Assert.Equal(0, (await running).ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(Handles(evaluation));
        Assert.True(governor.ReservedBytes < during);
        Assert.Equal(1, host.Closes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    // Observe the actual retained container without adding a production accessor.
    private static IEnumerable<FileDescriptor> Handles(Eval evaluation)
    {
        var state = typeof(Eval).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(evaluation)
            ?? throw new InvalidOperationException();
        var handles = state.GetType().GetProperty("CurrentRedirectionOpenedDescriptors")?.GetValue(state)
            ?? throw new InvalidOperationException();
        return Assert.IsAssignableFrom<IEnumerable<FileDescriptor>>(handles);
    }

    private static object Frame(Eval evaluation)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var state = typeof(Eval).GetField("_state", flags)?.GetValue(evaluation) ?? throw new InvalidOperationException();
        var redirections = state.GetType().GetField("_redirections", flags)?.GetValue(state) ?? throw new InvalidOperationException();
        var frames = redirections.GetType().GetField("_frames", flags)?.GetValue(redirections) ?? throw new InvalidOperationException();
        return frames.GetType().GetMethod("Peek")?.Invoke(frames, null) ?? throw new InvalidOperationException();
    }

    private sealed class PendingCloseHost : RecordingCloseHost
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Closes { get; private set; }
        public FileDescriptor ClosingHandle { get; private set; }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            Closes++;
            ClosingHandle = descriptor;
            Entered.TrySetResult();
            await Release.Task;
            return await base.CloseDescriptorAsync(descriptor);
        }

        public override async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await base.DisposeAsync();
        }
    }

    private class RecordingCloseHost : TestHost, IBashHost
    {
        public List<FileDescriptor> Opened { get; } = [];
        public List<FileDescriptor> Closed { get; } = [];

        async Task<FileOpenResult> IBashHost.OpenWritableFileAsync(
            FilePathLookup path, WritableFileOpenMode mode, FileCreationMode creationMode, FileWriteRetention retention,
            CancellationToken cancellationToken)
        {
            var result = await base.OpenWritableFileAsync(path, mode, creationMode, retention, cancellationToken);
            if (result.FileDescriptor is { } descriptor) Opened.Add(descriptor);
            return result;
        }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            Closed.Add(descriptor);
            return await base.CloseDescriptorAsync(descriptor);
        }
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class NestedDescriptorCollectionTests
{
    [Fact]
    public async Task NearestEntryPreservesHandlesAcquiredBetweenAncestorForks()
    {
        const string Source = "exec 3>one; (exec 4>two; (exec 5>three; (printf a >&3; printf b >&4; printf c >&5)); printf d >&4); printf e >&3; cat one two three";
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new ObservingHost { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(Source)), host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("aebdc", host.StandardOutput.TextContent);
        Assert.Equal("ae", host.ReadFile("/one"));
        Assert.Equal("bd", host.ReadFile("/two"));
        Assert.Equal("c", host.ReadFile("/three"));
        Assert.Empty(host.StandardError.Content.ToArray());
        // Three shell acquisitions and three read handles used by the final cat.
        Assert.Equal(6, host.Closed.Count);
        Assert.Equal(6, host.Closed.Distinct().Count());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("subshell", false)]
    [InlineData("subshell", true)]
    [InlineData("substitution", false)]
    [InlineData("substitution", true)]
    [InlineData("pipeline", false)]
    [InlineData("pipeline", true)]
    [InlineData("external", false)]
    [InlineData("external", true)]
    public async Task EqualSizedInputsAdmitDistinctCollectionsWhileRepeatedAliasesStayBounded(string route, bool dense)
    {
        var borrowed = Enumerable.Range(0, 1024).Select(i => new FileDescriptor(10000 + (dense ? i : 0))).ToArray();
        var body = route switch
        {
            "subshell" => "(printf child)",
            "substitution" => "x=$(printf child); printf '%s' \"$x\"",
            "pipeline" => "printf child | cat",
            "external" => "cat; printf child",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        // Keep room for both new option bindings in the parent and its child copy.
        var governor = new MemoryGovernor(24 * 1024 + 2 * 290);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse("set -e; " + body + "; printf tail"),
            new EvaluationDescriptorSetup(DescriptorTable.Snapshot.Standard, borrowed), null, null,
            EvaluationOptions.Default, owner, null);
        await using var host = new ObservingHost { MemoryBudget = owner };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(evaluation, host, owner, deadline.Token);

        Assert.Equal(dense ? 1 : 0, result.ExitCode);
        Assert.Equal(dense ? "" : "childtail", host.StandardOutput.TextContent);
        Assert.Equal(dense, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
        Assert.Equal(host.Pipes.Count * 2, host.Closed.Count);
        Assert.All(host.Pipes.SelectMany(p => new[] { p.Read, p.Write }), fd => Assert.Single(host.Closed, c => c == fd));
        if (route == "external") Assert.Equal(dense ? 0 : 1, host.Commands);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubshellEntryIndexStaysChargedThroughDelayedCleanup(bool cancel)
    {
        var borrowed = Enumerable.Range(10000, 1024).Select(id => new FileDescriptor(id)).ToArray();
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var evaluation = new Eval(Parser.Parse("(exec 3>out); printf tail"),
            new EvaluationDescriptorSetup(DescriptorTable.Snapshot.Standard, borrowed), null, null,
            EvaluationOptions.Default, owner, null);
        await using var host = new ObservingHost { MemoryBudget = owner, DelayClose = true };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new Engine().RunAsync(evaluation, host, owner, cancellation.Token);
        try
        {
            await host.Closing.Task.WaitAsync(deadline.Token);
            if (cancel) cancellation.Cancel();
            Assert.False(running.IsCompleted);
            var index = Assert.Single(Scopes(evaluation));
            Assert.Equal(1027, index.Count);
            var payload = ArrayPayload(index);
            Assert.True(payload > 16 * 1024);
            Assert.True(governor.ReservedBytes >= payload);
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
        Assert.Equal(cancel ? "" : "tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Single(host.Closed);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Empty(Scopes(evaluation));
        Assert.Equal(0, Scopes(evaluation).EnsureCapacity(0));
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    // Observe existing entry metadata and its actual payload, without adding a production accessor.
    private static Stack<HashSet<FileDescriptor>> Scopes(Eval evaluation) =>
        Assert.IsType<Stack<HashSet<FileDescriptor>>>(typeof(Eval)
            .GetField("_subshellBorrowedDescriptorScopes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(evaluation));

    private static long ArrayPayload(HashSet<FileDescriptor> index)
    {
        long bytes = 0;
        foreach (var name in new[] { "_entries", "_buckets" })
        {
            if (index.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(index) is not Array array)
                continue;
            var element = array.GetType().GetElementType() ?? throw new InvalidOperationException();
            var size = (int)(typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))?.MakeGenericMethod(element).Invoke(null, null)
                ?? throw new InvalidOperationException());
            bytes += (long)array.Length * size;
        }
        return bytes;
    }

    private sealed class ObservingHost : TestHost
    {
        public List<PipeDescriptors> Pipes { get; } = [];
        public List<FileDescriptor> Closed { get; } = [];
        public int Commands { get; private set; }
        public bool DelayClose { get; init; }
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetimeToken, CancellationToken cancellationToken)
        {
            var result = await base.CreatePipeAsync(lifetimeToken, cancellationToken);
            if (result.Descriptors is { } pipe) Pipes.Add(pipe);
            return result;
        }

        public override async Task<CommandExecutionResult> RunCommandAsync(
            CommandInvocation command, CommandLaunchFailureReporting reporting, CancellationToken cancellationToken)
        {
            Commands++;
            return await base.RunCommandAsync(command, reporting, cancellationToken);
        }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            Closed.Add(descriptor);
            if (DelayClose)
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

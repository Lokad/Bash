using System.Reflection;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DescriptorReferenceTests
{
    [Fact]
    public void RevertedDeltasAndFrozenCountsKeepIndependentReferenceAnswers()
    {
        var bindings = Bindings(128, distinct: true);
        var original = bindings.Capture().Fork();
        var number = new LogicalDescriptor(10);
        for (var index = 0; index < 32; index++)
        {
            bindings.Set(number, new FileDescriptor(2000));
            Assert.True(bindings.References(new FileDescriptor(2000)));
            Assert.False(bindings.References(new FileDescriptor(1000)));
            var captured = bindings.Capture().Fork();
            bindings.Set(number, new FileDescriptor(3000));
            Assert.False(bindings.References(new FileDescriptor(2000)));
            Assert.True(captured.References(new FileDescriptor(2000)));
            Assert.False(captured.References(new FileDescriptor(3000)));
            bindings.Set(number, new FileDescriptor(1000));
            Assert.True(bindings.References(new FileDescriptor(1000)));
            Assert.False(bindings.References(new FileDescriptor(3000)));
            Assert.True(original.References(new FileDescriptor(1000)));
            Assert.False(original.References(new FileDescriptor(2000)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliasCountsTrackReplacementRemovalAndCapture(bool distinct)
    {
        var bindings = Bindings(128, distinct);
        var original = bindings.Capture().Fork();
        for (var index = 0; index < 64; index++)
            bindings.Set(new LogicalDescriptor(10 + index), new FileDescriptor(3000));
        Assert.True(bindings.References(new FileDescriptor(3000)));
        Assert.True(bindings.References(new FileDescriptor(distinct ? 1127 : 1000)));
        var captured = bindings.Capture().Fork();
        for (var index = 0; index < 128; index++)
            Assert.True(bindings.Remove(new LogicalDescriptor(10 + index)));
        Assert.False(bindings.References(new FileDescriptor(3000)));
        Assert.False(bindings.References(new FileDescriptor(1000)));
        Assert.True(captured.References(new FileDescriptor(3000)));
        Assert.False(original.References(new FileDescriptor(3000)));
        Assert.True(original.References(new FileDescriptor(1000)));
    }

    [Fact]
    public void StandardAndExplicitClosedAliasesKeepTheirDistinctMembership()
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        bindings.Set(new LogicalDescriptor(10), FileDescriptor.StdOut);
        bindings.Set(new LogicalDescriptor(11), FileDescriptor.StdOut);
        bindings.Set(LogicalDescriptor.StdOut, new FileDescriptor(30));
        Assert.True(bindings.References(FileDescriptor.StdOut));
        bindings.Remove(new LogicalDescriptor(10));
        Assert.True(bindings.References(FileDescriptor.StdOut));
        var captured = bindings.Capture().Fork();
        bindings.Remove(new LogicalDescriptor(11));
        Assert.False(bindings.References(FileDescriptor.StdOut));
        Assert.True(captured.References(FileDescriptor.StdOut));
        bindings.Set(new LogicalDescriptor(12), FileDescriptor.Closed);
        Assert.True(bindings.HasMapping(new LogicalDescriptor(12)));
        Assert.True(bindings.References(FileDescriptor.Closed));
        bindings.Set(new LogicalDescriptor(12), FileDescriptor.DevNull);
        Assert.False(bindings.References(FileDescriptor.Closed));
        Assert.True(bindings.References(FileDescriptor.DevNull));
        Assert.True(bindings.References(new FileDescriptor(30)));
    }

    [Fact]
    public void PendingReferenceChangesStayBoundedAndCompactionDropsWideArrays()
    {
        var bindings = Bindings(16, distinct: true);
        _ = bindings.Capture();
        for (var index = 0; index < 4096; index++)
            bindings.Set(new LogicalDescriptor(10), new FileDescriptor(10000 + index));
        var changes = Changes(bindings);
        Assert.InRange(changes.Count, 1, 2);
        Assert.InRange(changes.EnsureCapacity(0), 1, 37);
        Assert.False(bindings.References(new FileDescriptor(1000)));
        Assert.True(bindings.References(new FileDescriptor(14095)));

        bindings = Bindings(256, distinct: true);
        _ = bindings.Capture();
        for (var index = 0; index < 255; index++)
            bindings.Remove(new LogicalDescriptor(10 + index));
        var sparse = bindings.Capture().Fork();
        var wide = Changes(bindings).EnsureCapacity(0);
        Assert.True(wide >= 256);
        bindings.Set(new LogicalDescriptor(400), new FileDescriptor(5000));
        Assert.True(Changes(bindings).EnsureCapacity(0) < wide);
        Assert.True(sparse.References(new FileDescriptor(1255)));
        Assert.False(sparse.References(new FileDescriptor(5000)));
        Assert.True(bindings.References(new FileDescriptor(5000)));
        Assert.False(bindings.References(new FileDescriptor(1000)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceQueriesBorrowExistingAdmissionWithoutAllocating(bool captured)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var table = new DescriptorTable();
        for (var index = 0; index < 128; index++)
            table.Set(new LogicalDescriptor(10 + index), new FileDescriptor(1000 + index), memory);
        if (captured) table = table.Capture().Fork();
        var absent = new FileDescriptor(9000);
        var last = new FileDescriptor(1127);
        Assert.False(table.References(absent));
        Assert.True(table.References(last));
        using var blocker = owner.Reserve(governor.LimitBytes - governor.ReservedBytes);
        AllocationAssert.SustainedReuse(CheckReferences, iterations: 1024, maximumBytes: 0);

        void CheckReferences()
        {
            if (table.References(absent) || !table.References(last))
                throw new InvalidOperationException("Reference membership changed without a mutation.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceBackingRemainsAdmittedThroughPendingHostWork(bool cancel)
    {
        const int Count = 1024;
        var script = "exec " + string.Join(' ', Enumerable.Range(0, Count).Select(i => $"{10 + i}>index-{i}"))
            + "; exec " + string.Join(' ', Enumerable.Range(0, Count).Select(i => $"{10 + i}>&1"))
            + "; [[ -e /gate ]]; printf ready";
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var evaluation = new Eval(Parser.Parse(script));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterGetPathInfo = async (path, _) =>
        {
            if (path.Path != "/gate") return;
            entered.TrySetResult();
            await release.Task;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = new Engine().RunAsync(evaluation, host, owner, deadline.Token);
        long payload = 0;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var state = Field(evaluation, "_state");
            var table = Assert.IsType<DescriptorTable>(state.GetType().GetProperty("CurrentRedirectionTable")?.GetValue(state));
            var bindings = Assert.IsType<DescriptorBindings>(Field(table, "_bindings"));
            var changes = Changes(bindings);
            Assert.True(changes.EnsureCapacity(0) >= Count);
            payload = CollectionBacking.ArrayPayloadBytes(changes);
            Assert.True(payload > 16 * 1024);
            Assert.True(table.OwnedMemoryBytes >= payload);
            Assert.True(governor.ReservedBytes >= evaluation.RetainedDescriptorMemoryBytes);
            Assert.Equal(0, host.OpenFileCount);
            if (cancel) deadline.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes >= payload);
        }
        finally
        {
            release.TrySetResult();
            try { await running; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        else
        {
            Assert.Equal(0, (await running).ExitCode);
            Assert.Equal("ready", host.StandardOutput.TextContent);
        }
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        Assert.True(governor.ReservedBytes >= payload);
        var entries = host.CaptureEntries();
        Assert.Equal(Enumerable.Range(0, Count).Select(i => "index-" + i).Order(StringComparer.Ordinal), entries.Keys.Order(StringComparer.Ordinal));
        Assert.All(entries.Values, entry =>
        {
            Assert.Equal(FilePathKind.File, entry.Kind);
            Assert.Empty(entry.ContentHex);
        });
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private static DescriptorBindings Bindings(int count, bool distinct)
    {
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        for (var index = 0; index < count; index++)
            bindings.Set(new LogicalDescriptor(10 + index), new FileDescriptor(1000 + (distinct ? index : 0)));
        return bindings;
    }

    private static Dictionary<FileDescriptor, (int Count, int Position)> Changes(DescriptorBindings bindings) =>
        Assert.IsType<Dictionary<FileDescriptor, (int Count, int Position)>>(Field(Field(bindings, "_writer"), "_referenceChanges"));

    private static object Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException(instance.GetType().Name + "." + name);
}

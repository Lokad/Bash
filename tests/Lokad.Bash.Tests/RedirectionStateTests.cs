using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class RedirectionStateTests
{
    [Fact]
    public void ReceivingOwnershipRefusalPrecedesPersistentMapPublication()
    {
        var original = new FileDescriptor(40);
        var acquired = new FileDescriptor(41);
        var root = new DescriptorTable();
        root.Set(LogicalDescriptor.StdOut, original);
        var child = new DescriptorTable();
        child.Set(LogicalDescriptor.StdOut, acquired);
        var handles = new[] { acquired };
        var state = new RedirectionState(root);
        state.Push(child, new DescriptorHandleOwner(handles), new HashSet<LogicalDescriptor> { LogicalDescriptor.StdOut }, null);
        var counted = new CountingMemoryBudget(new MemoryGovernor(0));
        using var owner = counted.CreateScope();
        var memory = new EvaluationMemory(owner);

        Assert.Throws<MemoryBudgetExceededException>(() => state.Preserve(memory, null, null));

        Assert.Equal(original, root.StdOut);
        Assert.Same(child, state.CurrentTable);
        Assert.Equal(acquired, child.StdOut);
        Assert.Same(handles, state.Pop());
        var calls = counted.AdmissionCalls;
        var batch = new OrderedDescriptorSet();
        batch.PrepareAdditional(handles.Length, null); // Caller-owned manual cleanup storage.
        Assert.Equal(handles, state.TransferHandles(handles, memory, batch));
        Assert.Equal(calls, counted.AdmissionCalls); // Failed setup cleanup acquires no receiving storage.
        Assert.Empty(state.OpenedDescriptors);
    }

    [Fact]
    public void SharedRemovalStorageIsPreparedBeforeClosingTheMapping()
    {
        var handle = new FileDescriptor(40);
        var root = new DescriptorTable();
        root.Set(LogicalDescriptor.StdOut, handle);
        var original = new[] { handle };
        var state = new RedirectionState(inheritedTable: null);
        state.Push(root, new DescriptorHandleOwner(original), new HashSet<LogicalDescriptor>(), null);
        using var refused = new MemoryGovernor(0).CreateScope();
        Assert.Throws<MemoryBudgetExceededException>(() => state.SetPersistentDescriptor(
            LogicalDescriptor.StdOut, DescriptorBinding.Closed, new EvaluationMemory(refused), acquired: false));
        Assert.Equal(handle, root.StdOut);
        Assert.Same(original, state.OpenedDescriptors);

        var governor = new MemoryGovernor(1024 * 1024);
        var counted = new CountingMemoryBudget(governor);
        using var owner = counted.CreateScope();
        var memory = new EvaluationMemory(owner);
        state.SetPersistentDescriptor(LogicalDescriptor.StdOut, DescriptorBinding.Closed, memory, acquired: false);
        var calls = counted.AdmissionCalls;
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);

        Assert.True(state.TryTakeHandle(handle, memory));

        Assert.Equal(calls, counted.AdmissionCalls);
        Assert.Empty(state.OpenedDescriptors);
        Assert.Equal(new[] { handle }, original);
        Assert.True(governor.ReservedBytes > 0);
    }

    [Fact]
    public void HandleTraversalPreservesScopeOrderAndRepeatedAliases()
    {
        var state = new RedirectionState(inheritedTable: null);
        var borrowed = new FileDescriptor(7);
        var excluded = new FileDescriptor(9);
        var outer = new FileDescriptor(20);
        var inner = new FileDescriptor(21);
        var shared = new FileDescriptor(22);
        var last = new FileDescriptor(23);
        state.Push(new DescriptorTable(), new DescriptorHandleOwner([outer, shared, borrowed, excluded, last]),
            new HashSet<LogicalDescriptor>(), null);
        state.Push(new DescriptorTable(), new DescriptorHandleOwner([inner, shared, excluded]),
            new HashSet<LogicalDescriptor>(), null);
        var handles = state.EnumerateHandles().ToArray();

        Assert.Equal(new[] { inner, shared, excluded, outer, shared, borrowed, excluded, last }, handles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForksKeepIndependentMappingsAndDiscardParentRestorationFrames(bool nestedEvaluation)
    {
        var number = new LogicalDescriptor(10);
        var inherited = new FileDescriptor(20);
        var temporary = new FileDescriptor(21);
        var replacement = new FileDescriptor(22);
        var root = new DescriptorTable();
        root.Set(number, inherited);
        var state = new RedirectionState(root);
        var redirected = root.Clone();
        redirected.Set(number, temporary);
        var opened = new List<FileDescriptor> { temporary };
        state.Push(redirected, new DescriptorHandleOwner(opened), new HashSet<LogicalDescriptor> { number }, null);

        var child = nestedEvaluation
            ? state.ForkForNestedEvaluation(redirected.Capture().WithStandardStreams(
                FileDescriptor.StdIn, FileDescriptor.DevNull, FileDescriptor.StdErr), inheritedExitBindings: null)
            : state.ForkForSubshell(inheritedExitBindings: null);
        var childTable = Assert.IsType<DescriptorTable>(child.CurrentTable);

        Assert.Equal(temporary, childTable.Resolve(number));
        Assert.Equal(nestedEvaluation ? FileDescriptor.DevNull : FileDescriptor.StdOut, childTable.StdOut);
        Assert.Equal(FileDescriptor.StdOut, redirected.StdOut);
        Assert.Same(opened, child.OpenedDescriptors);

        child.SetPersistentDescriptor(number, new DescriptorBinding(replacement), null, acquired: false);
        Assert.Equal(replacement, childTable.Resolve(number));
        Assert.Equal(temporary, redirected.Resolve(number));
        Assert.Equal(inherited, root.Resolve(number));

        Assert.True(child.TryTakeHandle(temporary, null));
        Assert.Empty(child.OpenedDescriptors);
        Assert.Same(opened, state.OpenedDescriptors);
        Assert.Equal(new[] { temporary }, opened);
        Assert.Empty(child.Pop());
        Assert.Null(child.CurrentTable);

        Assert.Same(opened, state.Pop());
        Assert.Same(root, state.CurrentTable);
        Assert.Equal(inherited, root.Resolve(number));
    }
}

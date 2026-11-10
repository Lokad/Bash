using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class RedirectionStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandleCollectionPreservesExistingHandlesAndScopeOrder(bool excludeInherited)
    {
        var state = new RedirectionState(inheritedTable: null);
        var borrowed = new FileDescriptor(7);
        var excluded = new FileDescriptor(9);
        var outer = new FileDescriptor(20);
        var inner = new FileDescriptor(21);
        var shared = new FileDescriptor(22);
        var last = new FileDescriptor(23);
        state.Push(new DescriptorTable(), [outer, shared, borrowed, excluded, last],
            new HashSet<LogicalDescriptor>());
        state.Push(new DescriptorTable(), [inner, shared, excluded],
            new HashSet<LogicalDescriptor>());
        var handles = new List<FileDescriptor> { FileDescriptor.StdOut, borrowed, outer };
        var collector = new DescriptorHandleCollector(handles);

        state.AddHandlesTo(ref collector, excludeInherited ? [borrowed, excluded] : []);

        Assert.Equal(excludeInherited
            ? new[] { FileDescriptor.StdOut, borrowed, outer, inner, shared, last }
            : new[] { FileDescriptor.StdOut, borrowed, outer, inner, shared, excluded, last }, handles);
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
        state.Push(redirected, opened, new HashSet<LogicalDescriptor> { number });

        var child = nestedEvaluation
            ? state.ForkForNestedEvaluation(redirected.Capture().WithStandardStreams(
                FileDescriptor.StdIn, FileDescriptor.DevNull, FileDescriptor.StdErr))
            : state.ForkForSubshell();
        var childTable = Assert.IsType<DescriptorTable>(child.CurrentTable);

        Assert.Equal(temporary, childTable.Resolve(number));
        Assert.Equal(nestedEvaluation ? FileDescriptor.DevNull : FileDescriptor.StdOut, childTable.StdOut);
        Assert.Equal(FileDescriptor.StdOut, redirected.StdOut);
        Assert.Same(opened, child.OpenedDescriptors);

        child.SetPersistentDescriptor(number, replacement);
        Assert.Equal(replacement, childTable.Resolve(number));
        Assert.Equal(temporary, redirected.Resolve(number));
        Assert.Equal(inherited, root.Resolve(number));

        Assert.True(child.TryTakeHandle(temporary));
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

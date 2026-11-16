using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DirectoryNavigationHostTests
{
    [Theory]
    [InlineData(false, "PWD")]
    [InlineData(true, "PWD")]
    [InlineData(false, "OLDPWD")]
    [InlineData(true, "OLDPWD")]
    public async Task ReadonlyDiagnosticRetainsPublishedCwdAndOwnersThroughCancellation(bool prepared, string variable)
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        var entered = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = owner,
            BeforeAppend = (_, content) => entered.SetResult(content),
            BeforeDescriptorClose = _ => closeEntered.SetResult()
        };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueuePathInfo(FilePathInfo.FromKind(FilePathKind.Directory));
        host.EnqueueAppendTask(write.Task);
        host.EnqueueCloseDescriptorTask(close.Task);
        var script = "readonly " + variable + "; set -e; cd /a 2>/out";
        var evaluation = new Eval(Parser.Parse(script));
        var running = prepared
            ? new Engine().RunAsync(evaluation, host, owner, cancellation.Token)
            : BashScript.RunAsync(host, script, EvaluationOptions.Default, cancellation.Token);
        try
        {
            var borrowed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Encoding.UTF8.GetBytes(variable + ": readonly variable\n"), borrowed.ToArray());
            if (prepared) Assert.Equal("/a", evaluation.CurrentDirectory.Path);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes > 0);
            Assert.Empty(host.ClosedDescriptors);
            write.SetResult(DescriptorWriteResult.Open);
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes > 0);
        }
        finally
        {
            write.TrySetResult(DescriptorWriteResult.Open);
            close.TrySetResult();
        }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Equal("/a", Assert.Single(host.PathInfoRequests).Path);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    public static IEnumerable<object[]> Rejections()
    {
        foreach (var prepared in new[] { false, true })
        foreach (var command in new[] { "cd /a", "popd", "pushd" })
        foreach (var kind in new[] { OperationErrorKind.PermissionDenied, OperationErrorKind.NotDirectory, OperationErrorKind.Unknown })
            yield return [prepared, command, kind];
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task RejectedLookupKeepsCwdAndReportsHostDetail(bool prepared, string command, OperationErrorKind kind)
    {
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        host.EnqueuePathInfo(FilePathInfo.FromKind(FilePathKind.Directory));
        host.EnqueuePathInfo(FilePathInfo.FromKind(FilePathKind.Directory));
        host.EnqueuePathInfoError(new OperationError("synthetic denial\n") { Kind = kind });
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        var script = "cd /a; pushd /b >/dev/null; " + command + "; status=$?; dirs; exit $status";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancel.Token)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, cancel.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/b", result.CurrentDirectory.Path);
        Assert.Equal(["/a", "/b", "/a"], host.PathInfoRequests.Select(p => p.Path));
        var prefix = command.StartsWith("cd", StringComparison.Ordinal) ? "cd" : command;
        Assert.Equal(prefix + ": /a: synthetic denial\n", Encoding.UTF8.GetString(
            host.Appends.Where(a => a.Descriptor == FileDescriptor.StdErr).SelectMany(a => a.Content).ToArray()));
        Assert.Equal(command == "pushd" ? "/b /b\n" : "/b /a\n", Encoding.UTF8.GetString(
            host.Appends.Where(a => a.Descriptor == FileDescriptor.StdOut).SelectMany(a => a.Content).ToArray()));
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false, "cd /a")]
    [InlineData(true, "cd /a")]
    [InlineData(false, "popd")]
    [InlineData(true, "popd")]
    [InlineData(false, "pushd")]
    [InlineData(true, "pushd")]
    public async Task CancellationAwaitsThePendingLookupAndDescriptorCleanup(bool prepared, string command)
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        host.AddDirectory("/a");
        host.AddDirectory("/b");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        host.AfterGetPathInfo = (path, _) =>
        {
            if (++requests != 3) return Task.CompletedTask;
            Assert.Equal("/a", path.Path);
            entered.SetResult();
            return release.Task;
        };
        var script = "cd /a; pushd /b >/dev/null; " + command + " >/out";
        var evaluation = new Eval(Parser.Parse(script));
        var running = prepared
            ? new Engine().RunAsync(evaluation, host, owner, cancellation.Token)
            : BashScript.RunAsync(host, script, EvaluationOptions.Default, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes > 0);
            Assert.Equal(1, host.OpenFileCount);
            if (prepared) Assert.Equal("/b", evaluation.CurrentDirectory.Path);
        }
        finally
        {
            release.TrySetResult();
        }

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        if (prepared) Assert.Equal("/b", evaluation.CurrentDirectory.Path);
        Assert.Equal(3, requests);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(string.Empty, host.ReadFile("/out"));
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("cd /a")]
    [InlineData("popd")]
    [InlineData("pushd")]
    public async Task UnexpectedLookupFailurePropagatesAfterCleanup(string command)
    {
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        host.AddDirectory("/a");
        host.AddDirectory("/b");
        var expected = new InvalidOperationException("synthetic lookup failure");
        var requests = 0;
        host.BeforeGetPathInfo = _ => { if (++requests == 3) throw expected; };
        var evaluation = new Eval(Parser.Parse("cd /a; pushd /b >/dev/null; " + command + " >/out"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Engine().RunAsync(evaluation, host, owner, cancel.Token));

        Assert.Same(expected, actual);
        Assert.Equal("/b", evaluation.CurrentDirectory.Path);
        Assert.Equal(3, requests);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(string.Empty, host.ReadFile("/out"));
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("popd", "/a")]
    [InlineData("pushd", "/a")]
    public async Task OutputFailureDoesNotUndoSuccessfulNavigation(string command, string destination)
    {
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner, AppendError = new OperationError("synthetic output failure\n") };
        host.AddDirectory("/a");
        host.AddDirectory("/b");
        var evaluation = new Eval(Parser.Parse("cd /a; pushd /b >/dev/null; " + command));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new Engine().RunAsync(evaluation, host, owner, cancel.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(destination, result.CurrentDirectory.Path);
        Assert.Equal(ShellValue.FromText(destination), evaluation.Variables["PWD"]);
        Assert.Equal(ShellValue.FromText("/b"), evaluation.Variables["OLDPWD"]);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }
}

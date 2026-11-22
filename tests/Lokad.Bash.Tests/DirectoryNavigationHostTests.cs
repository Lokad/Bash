using System.Globalization;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task CdPathSettlesCandidatesInOrderAndReportsOnlyTheFinalOutcome(bool fallback)
    {
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueuePathInfoError(new OperationError("candidate denied\n") { Kind = OperationErrorKind.PermissionDenied });
        host.EnqueuePathInfo(FilePathInfo.FromKind(FilePathKind.File));
        host.EnqueuePathInfo(fallback ? FilePathInfo.FromKind(FilePathKind.Missing) : FilePathInfo.FromKind(FilePathKind.Directory));
        if (fallback)
            host.EnqueuePathInfoError(new OperationError("fallback denied\n") { Kind = OperationErrorKind.PermissionDenied });
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        var result = await BashScript.RunAsync(host, "CDPATH=first:second:third cd target",
            EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(fallback ? 1 : 0, result.ExitCode);
        Assert.Equal(fallback ? "/" : "/third/target", result.CurrentDirectory.Path);
        string[] expectedPaths = fallback ? ["/first/target", "/second/target", "/third/target", "/target"]
            : ["/first/target", "/second/target", "/third/target"];
        Assert.Equal(expectedPaths, host.PathInfoRequests.Select(path => path.Path));
        var append = Assert.Single(host.Appends);
        Assert.Equal(fallback ? FileDescriptor.StdErr : FileDescriptor.StdOut, append.Descriptor);
        Assert.Equal(fallback ? "cd: target: fallback denied\n" : "/third/target\n", Encoding.UTF8.GetString(append.Content));
    }

    [Fact]
    public async Task CdPathReusesCapacityAfterEachCandidateLookup()
    {
        const int misses = 96;
        var padding = new string('x', 80);
        var components = Enumerable.Range(0, misses).Select(index => index.ToString("D3", CultureInfo.InvariantCulture) + padding);
        var path = string.Join(':', components.Append("/"));
        var evaluation = new Eval(Parser.Parse("CDPATH=" + path + " cd target"));
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        evaluation.Memory.Budget = owner;
        await using var host = new TestHost();
        host.AddDirectory("/target");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = 0;
        long firstReservation = 0;
        while (await evaluation.GetNextStep(deadline.Token) is { } operation)
        {
            if (operation is PathInfosOperation lookup && ++requests <= misses)
            {
                Assert.Equal(FilePathInfoRequest.KindOnly, lookup.Request);
                if (requests == 1) firstReservation = memory.ReservedBytes;
                Assert.Equal(firstReservation, memory.ReservedBytes);
            }
            await ShellOperationDispatcher.DispatchAsync(operation, host, deadline.Token);
        }
        await evaluation.Completion;

        Assert.Equal(misses + 1, requests);
        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal("/target\n", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task CdReusesPathScratchAcrossSuccessiveParentComponents()
    {
        const int checkpoints = 64;
        var operand = string.Concat(Enumerable.Repeat("step/../", checkpoints)) + "finish";
        var evaluation = new Eval(Parser.Parse("cd " + operand));
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        evaluation.Memory.Budget = owner;
        await using var host = new TestHost();
        host.AddDirectory("/step");
        host.AddDirectory("/finish");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = 0;
        long scratch = 0;
        while (await evaluation.GetNextStep(deadline.Token) is { } operation)
        {
            if (operation is PathInfosOperation lookup)
            {
                requests++;
                Assert.Equal(FilePathInfoRequest.KindOnly, lookup.Request);
                Assert.Equal(requests <= checkpoints ? "/step" : "/finish", Assert.Single(lookup.Paths).Path);
                if (requests == 2)
                    scratch = evaluation.Memory.Workspace.Bytes;
                if (requests > 2 && requests <= checkpoints)
                    Assert.Equal(scratch, evaluation.Memory.Workspace.Bytes);
            }
            await ShellOperationDispatcher.DispatchAsync(operation, host, deadline.Token);
        }
        await evaluation.Completion;

        Assert.Equal(checkpoints + 1, requests);
        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal("/finish", evaluation.CurrentDirectory.Path);
        Assert.True(host.StandardError.Content.IsEmpty);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryStackMemoryFollowsLivePathsAndPendingOutput(bool measureOutput)
    {
        var paths = Enumerable.Range(0, 24).Select(index => "/" + index + new string('é', 640)).ToArray();
        await using var host = new TestHost();
        foreach (var path in paths)
            host.AddDirectory(path);
        var setup = string.Join('\n', paths.Select(path => "pushd " + path + " >/dev/null"));
        var evaluation = new Eval(Parser.Parse(setup + "\n: @full; printf @full; dirs; dirs -c; printf @idle"));
        var budget = new MemoryGovernor(1024 * 1024);
        using var owner = budget.CreateScope();
        evaluation.Memory.Budget = owner;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long retainedBeforeClear = 0, retainedAfterClear = 0, pendingOutput = 0;
        var expectedOutput = string.Join(' ', paths.Reverse()) + " /\n";
        while (await evaluation.GetNextStep(deadline.Token) is { } operation)
        {
            if (operation is WriteOperation write && write.FileDescriptor == FileDescriptor.StdOut)
            {
                var bytes = evaluation.Memory.Workspace.Bytes;
                if (write.Content.Span.SequenceEqual("@full"u8))
                    retainedBeforeClear = bytes;
                else if (write.Content.Span.SequenceEqual("@idle"u8))
                    retainedAfterClear = bytes;
                else
                {
                    Assert.Equal(expectedOutput, Encoding.UTF8.GetString(write.Content.Span));
                    pendingOutput = bytes;
                }
            }
            await ShellOperationDispatcher.DispatchAsync(operation, host, deadline.Token);
        }
        await evaluation.Completion;

        Assert.True(retainedBeforeClear > 0 && retainedAfterClear > 0 && pendingOutput > 0);
        if (measureOutput)
        {
            var outputBytes = Encoding.UTF8.GetByteCount(expectedOutput);
            var delta = pendingOutput - retainedBeforeClear;
            Assert.True(delta >= outputBytes - 1024, $"Pending output changed admission by {delta} B for {outputBytes} B of output.");
        }
        else
        {
            var lowerBound = 2 * paths.Take(paths.Length - 1).Sum(path => path.Length);
            var delta = retainedBeforeClear - retainedAfterClear;
            Assert.True(delta >= lowerBound, $"Clearing the stack retired {delta} B for {lowerBound} B of path text.");
        }
        Assert.Equal("@full" + expectedOutput + "@idle", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(false, "PWD")]
    [InlineData(true, "PWD")]
    [InlineData(false, "OLDPWD")]
    [InlineData(true, "OLDPWD")]
    public Task ReadonlyDiagnosticRetainsPublishedCwdAndOwnersThroughCancellation(bool prepared, string variable) =>
        VerifyPendingDirectoryWriteAsync(prepared, "readonly " + variable + "; set -e; cd /a 2>/out",
            variable + ": readonly variable\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PreviousDirectoryAnnouncementStaysBorrowedUntilTheHostSettles(bool prepared) =>
        VerifyPendingDirectoryWriteAsync(prepared, "OLDPWD=a; cd - >/out; printf tail", "a\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CdPathAnnouncementStaysBorrowedUntilTheHostSettles(bool prepared) =>
        VerifyPendingDirectoryWriteAsync(prepared, "CDPATH=/ cd a >/out; printf tail", "/a\n");

    [Theory]
    [InlineData(false, "dirs", "/a\n")]
    [InlineData(true, "dirs", "/a\n")]
    [InlineData(false, "HOME=/a dirs -v +0", " 0  ~\n")]
    [InlineData(true, "HOME=/a dirs -v +0", " 0  ~\n")]
    public Task DirectoryStackOutputStaysBorrowedUntilTheHostSettles(bool prepared, string command, string expected) =>
        VerifyPendingDirectoryWriteAsync(prepared, "cd /a; " + command + " >/out; printf tail", expected);

    private static async Task VerifyPendingDirectoryWriteAsync(bool prepared, string script, string expected)
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
        var evaluation = new Eval(Parser.Parse(script));
        var running = prepared
            ? new Engine().RunAsync(evaluation, host, owner, cancellation.Token)
            : BashScript.RunAsync(host, script, EvaluationOptions.Default, cancellation.Token);
        try
        {
            var borrowed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Encoding.UTF8.GetBytes(expected), borrowed.ToArray());
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
        foreach (var command in new[] { "cd ../a", "popd", "pushd" })
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
        var shownPath = prefix == "cd" ? "../a" : "/a";
        Assert.Equal($"{prefix}: {shownPath}: synthetic denial\n", Encoding.UTF8.GetString(
            host.Appends.Where(a => a.Descriptor == FileDescriptor.StdErr).SelectMany(a => a.Content).ToArray()));
        Assert.Equal(command == "pushd" ? "/b /b\n" : "/b /a\n", Encoding.UTF8.GetString(
            host.Appends.Where(a => a.Descriptor == FileDescriptor.StdOut).SelectMany(a => a.Content).ToArray()));
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false, "cd /a")]
    [InlineData(true, "cd /a")]
    [InlineData(false, "cd /a/../b")]
    [InlineData(true, "cd /a/../b")]
    [InlineData(false, "popd")]
    [InlineData(true, "popd")]
    [InlineData(false, "pushd")]
    [InlineData(true, "pushd")]
    [InlineData(false, "CDPATH=/:/unused cd a")]
    [InlineData(true, "CDPATH=/:/unused cd a")]
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

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(destination, result.CurrentDirectory.Path);
        Assert.Equal(ShellValue.FromText(destination), evaluation.Variables["PWD"]);
        Assert.Equal(ShellValue.FromText("/b"), evaluation.Variables["OLDPWD"]);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("pushd -n ../missing 1>&-", "0:/a:sentinel\n/a ../missing\n", "pushd")]
    [InlineData("pushd -n ../b >/dev/null; popd -n +1 1>&-", "0:/a:sentinel\n/a\n", "popd")]
    [InlineData("pushd -n ../b >/dev/null; popd 1>&-", "0:/b:/a\n/b\n", "popd")]
    public async Task StackOnlyEditsKeepWorkingDirectoryAndTheDeferredPath(
        string command, string output, string diagnosticName)
    {
        await using var host = new TestHost();
        host.AddDirectory("/a");
        host.AddDirectory("/b");
        var result = await host.RunAsync("cd /a; OLDPWD=sentinel; " + command
            + """; printf '%s:%s:%s\n' "$?" "$PWD" "$OLDPWD"; dirs""", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(diagnosticName + ": write error: Bad file descriptor\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("pushd +1 1>&-", "0:/b\n/b /a /c\n", "pushd: write error: Bad file descriptor\n")]
    [InlineData("pushd -n +1 1>&-", "0:/c\n/c /a /c\n", "")]
    [InlineData("popd -0 1>&-", "0:/c\n/c /b\n", "popd: write error: Bad file descriptor\n")]
    public async Task DirectoryStackIndicesSelectFromEitherEnd(string command, string output, string error)
    {
        await using var host = new TestHost();
        host.AddDirectory("/a");
        host.AddDirectory("/b");
        host.AddDirectory("/c");
        var result = await host.RunAsync("cd /a; pushd /b >/dev/null; pushd /c >/dev/null; " + command
            + """; printf '%s:%s\n' "$?" "$PWD"; dirs""", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(error, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task DirectoryStackIndexFailureKeepsTheEmptyStackDiagnostic()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("pushd +1 1>&-", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("pushd: directory stack empty\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task DeferredStackEntryRetainsByteValuesAcrossForkAndTildeSelection()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            """pushd -n $'\377' >/dev/null; (dirs; printf '<%s>' ~+1); popd -n >/dev/null; dirs""",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("2F20FF0A3CFF3E2F0A"), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }
}

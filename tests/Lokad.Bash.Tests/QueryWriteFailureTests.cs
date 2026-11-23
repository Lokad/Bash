using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class QueryWriteFailureTests
{
    [Theory]
    [InlineData("printf x 1>&-", "printf: write error: Bad file descriptor\n")]
    [InlineData("printf x 1>&-3", "printf: write error: Bad file descriptor\n")]
    [InlineData("printf x 1>&-0", "printf: write error: Bad file descriptor\n")]
    [InlineData("echo x 1>&-", "echo: write error: Bad file descriptor\n")]
    [InlineData(@"echo -e 'x\c' later 1>&-", "echo: write error: Bad file descriptor\n")]
    public async Task FormattingBuiltinsReportClosedOutput(string script, string error)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal(error, host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("pwd", "line 1: pwd")]
    [InlineData("dirs", "line 1: dirs")]
    [InlineData("label=sample; declare -p label", "declare")]
    [InlineData("f(){ :; }; declare -f f", "declare")]
    [InlineData("export label=sample; export -p", "export")]
    [InlineData("readonly label=sample; readonly -p", "readonly")]
    [InlineData("set -o", "set")]
    [InlineData("set +o", "set")]
    public async Task ListingsReportClosedOutputThroughTheReachedErrorDescriptor(string script, string diagnosticName)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script + " 1>&- 2>errors", CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(diagnosticName + ": write error: Bad file descriptor\n", host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("f(){ caller 1>&-; }; f", 0)]
    [InlineData("f(){ caller 8 1>&-; }; f", 1)]
    [InlineData("f(){ caller 0 1>&-; }; f", 1)]
    [InlineData("g(){ caller 0 1>&-; }; f(){ g; }; f", 0)]
    [InlineData("f(){ x=value eval 'caller 1>&-'; }; f", 0)]
    [InlineData("caller 1>&-", 1)]
    public async Task CallerStatusDependsOnFrameSelection(string script, int status)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(status, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("printf '' 1>&-")]
    [InlineData("echo -n 1>&-")]
    [InlineData("echo -nn -n 1>&-")]
    [InlineData(@"echo -e '\c' later 1>&-")]
    [InlineData("printf -v value x 1>&-")]
    public async Task FormattingWithoutOutputDoesNotTouchAClosedOutputDescriptor(string script)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("printf")]
    [InlineData("read")]
    [InlineData("mapfile")]
    [InlineData("readarray")]
    [InlineData("help")]
    [InlineData("dirs")]
    [InlineData("pushd")]
    [InlineData("popd")]
    [InlineData("eval")]
    [InlineData("source")]
    [InlineData(".")]
    public async Task BuiltinHelpKeepsUsageStatusWithShellClosedStdout(string name)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(name + " --help 1>&-", CancellationToken.None);
        Assert.Equal(2, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Theory]
    [InlineData("printf x")]
    [InlineData("echo x")]
    [InlineData("printf --help")]
    [InlineData("read --help")]
    [InlineData("mapfile --help")]
    [InlineData("readarray --help")]
    [InlineData("dirs --help")]
    [InlineData("eval --help")]
    public async Task BuiltinOutputFailureKeepsTheHostCause(string script)
    {
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("reported output failure\n")));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        var result = await BashScript.RunAsync(host, script, EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, host.Appends.Count);
        Assert.Equal(FileDescriptor.StdErr, host.Appends[1].Descriptor);
        Assert.Contains("write error: reported output failure", Encoding.UTF8.GetString(host.Appends[1].Content));
    }

    [Theory]
    [InlineData("builtin")]
    [InlineData("command")]
    [InlineData("exec")]
    public async Task PreparedHelpSkipsTheClosedOutputDescriptor(string name)
    {
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);

        var result = await BashScript.RunAsync(host, name + " --help >&-", EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.Appends);
    }

    [Fact]
    public async Task PreparedHelpReservesItsRecordAfterRedirectionAndReleasesItBeforeClose()
    {
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = new Eval(Parser.Parse("command --help >documentation"));
        state.Memory.Budget = owner;
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep(deadline.Token));
        var before = governor.ReservedBytes;
        var descriptor = new FileDescriptor(17);
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var write = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        var pending = governor.ReservedBytes;
        var output = write.Content.ToArray();
        write.SetResult(DescriptorWriteResult.WriteFailure);
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep(deadline.Token));
        var after = governor.ReservedBytes;
        close.SetResult(null);

        Assert.Null(await state.GetNextStep(deadline.Token));
        Assert.Equal(descriptor, write.FileDescriptor);
        Assert.Equal(descriptor, close.Descriptor);
        Assert.StartsWith("command: command [-pVv] command [arg ...]\n", Encoding.UTF8.GetString(output));
        Assert.Equal(output.Length, pending - before);
        Assert.Equal(before, after);
        Assert.Equal(2, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task PrintfStopsAfterAHostRejectsAConversionDiagnostic(bool failure, int status)
    {
        var state = new Eval(Parser.Parse(@"printf 'ab%n%b%n' before '\x' after"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(failure ? DescriptorWriteResult.WriteFailure : DescriptorWriteResult.Closed);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        var write = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, write.Descriptor);
        Assert.Equal("printf: missing hex digit for \\x\n"u8.ToArray(), write.Content);
        Assert.Equal(ShellValue.FromText("2"), state.Variables["before"]);
        Assert.False(state.Variables.ContainsKey("after"));
        Assert.Equal(status, state.LastExitCode);
    }

    [Theory]
    [InlineData("printf x")]
    [InlineData("echo x")]
    public async Task FormattingDoesNotRetryAMergedFailedDescriptor(string script)
    {
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);
        var result = await BashScript.RunAsync(host, script + " 2>&1", EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(FileDescriptor.StdOut, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData("printf x")]
    [InlineData("echo x")]
    public async Task FormattingDoesNotWriteADiagnosticToAClosedErrorDescriptor(string script)
    {
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);
        var result = await BashScript.RunAsync(host, script + " 2>&-", EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(FileDescriptor.StdOut, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData("printf x")]
    [InlineData("echo x")]
    public async Task FormattingDownstreamClosureHasNoFailureDiagnostic(string script)
    {
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);
        var result = await BashScript.RunAsync(host, script, EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FileDescriptor.StdOut, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData("command -x echo", 2)]
    [InlineData("command -v -x echo", 2)]
    [InlineData("builtin missing", 1)]
    [InlineData("builtin -v echo", 2)]
    [InlineData("exec -x echo", 2)]
    [InlineData("! builtin missing", 0)]
    [InlineData("exec builtin missing", 1)]
    public async Task WrapperDiagnosticWriteFailureKeepsCommandStatus(string script, int expectedStatus)
    {
        var state = new Eval(Parser.Parse(script));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("write failure\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(expectedStatus, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData("type", "Permission denied", false, 1)]
    [InlineData("type", "Bad file descriptor", false, 1)]
    [InlineData("type", null, false, 1)]
    [InlineData("type", "Input/output error", true, 0)]
    [InlineData("command -v", "Bad file descriptor", false, 0)]
    [InlineData("command -V", "Bad file descriptor", false, 0)]
    public async Task QueryReportsWriteFailureAndChoosesItsStatus(
        string command, string? message, bool negated, int expectedStatus)
    {
        var state = new Eval(Parser.Parse((negated ? "! " : "") + command + " printf :"));
        var host = new TestOperationVisitor([]);
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError((message ?? "Write failed") + "\n")));
        var report = command == "type";
        if (report)
            host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));
        var error = new StringBuilder();
        var writes = 0;
        host.BeforeAppend = (descriptor, content) =>
        {
            writes++;
            if (descriptor == FileDescriptor.StdErr)
                error.Append(Encoding.UTF8.GetString(content.Span));
            else
                Assert.Equal(FileDescriptor.StdOut, descriptor);
        };

        await new Engine().RunAsync(state, host);

        Assert.Equal(expectedStatus, state.LastExitCode);
        Assert.Equal(report ? "type: write error: " + (message ?? "Write failed") + "\n" : "", error.ToString());
        Assert.Equal(report ? 2 : 1, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedQueriesRetainTheFirstFailureWithoutBorrowingALaterCause(bool knownFirstCause)
    {
        var state = new Eval(Parser.Parse("type printf missing :"));
        var lookup = Assert.IsType<ResolveCommandsOperation>(await state.GetNextStep());
        lookup.SetResult([new CommandLookupResult.Missing()]);
        await WriteAsync(FileDescriptor.StdOut, "printf is a shell builtin\n",
            DescriptorWriteResult.Failure(new OperationError(knownFirstCause ? "first failure\n" : "Write failed\n")));
        await WriteAsync(FileDescriptor.StdErr, "type: missing: not found\n",
            DescriptorWriteResult.Failure(new OperationError("diagnostic failure\n")));
        await WriteAsync(FileDescriptor.StdOut, ": is a shell builtin\n",
            DescriptorWriteResult.Failure(new OperationError("later failure\n")));
        await WriteAsync(FileDescriptor.StdErr, knownFirstCause ? "type: write error: first failure\n" : "type: write error: Write failed\n",
                DescriptorWriteResult.Failure(new OperationError("final diagnostic failure\n")));

        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);

        async Task WriteAsync(FileDescriptor descriptor, string text, DescriptorWriteResult result)
        {
            var operation = Assert.IsType<WriteOperation>(await state.GetNextStep());
            Assert.Equal(descriptor, operation.FileDescriptor);
            Assert.Equal(text, Encoding.UTF8.GetString(operation.Content.Span));
            operation.SetResult(result);
        }
    }

    [Fact]
    public async Task QueryRunAdmissionFollowsChannelChangesAtEqualOperandWidth()
    {
        const int RowsPerChannel = 512;
        const string OutputRow = "echo is a shell builtin\n";
        const string ErrorRow = "type: missing: not found\n";
        var grouped = await MeasureAsync(alternating: false);
        var alternating = await MeasureAsync(alternating: true);
        Assert.Equal(grouped.Lookup, alternating.Lookup);
        Assert.InRange(alternating.PendingWrite - grouped.PendingWrite, 2 * 1024, 6 * 1024);

        static async Task<(long Lookup, long PendingWrite)> MeasureAsync(bool alternating)
        {
            string[] pair = ["echo", "missing"];
            var names = alternating ? Enumerable.Repeat(pair, RowsPerChannel).SelectMany(values => values)
                : Enumerable.Repeat(pair[0], RowsPerChannel).Concat(Enumerable.Repeat(pair[1], RowsPerChannel));
            var state = new Eval(Parser.Parse("type " + string.Join(' ', names)));
            var governor = new MemoryGovernor(4 * 1024 * 1024);
            using var owner = governor.CreateScope();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            state.Memory.Budget = owner;
            var lookup = Assert.IsType<ResolveCommandsOperation>(await state.GetNextStep(deadline.Token));
            Assert.Equal(RowsPerChannel, lookup.CommandNames.Count);
            Assert.All(lookup.CommandNames, name => Assert.Equal("missing", name));
            var lookupBytes = governor.ReservedBytes;
            lookup.SetResult(Enumerable.Repeat<CommandLookupResult>(new CommandLookupResult.Missing(), RowsPerChannel).ToArray());

            var output = alternating ? OutputRow : string.Concat(Enumerable.Repeat(OutputRow, RowsPerChannel));
            var error = alternating ? ErrorRow : string.Concat(Enumerable.Repeat(ErrorRow, RowsPerChannel));
            var pendingBytes = 0L;
            var runPairs = alternating ? RowsPerChannel : 1;
            for (var pairIndex = 0; pairIndex < runPairs; pairIndex++)
            {
                await CheckWriteAsync(FileDescriptor.StdOut, output);
                await CheckWriteAsync(FileDescriptor.StdErr, error);
            }
            Assert.Null(await state.GetNextStep(deadline.Token));
            Assert.Equal(1, state.LastExitCode);
            Assert.Empty(state.GetDescriptorsForCleanup());
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return (lookupBytes, pendingBytes);

            async Task CheckWriteAsync(FileDescriptor descriptor, string content)
            {
                var write = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
                try
                {
                    if (pendingBytes == 0)
                        pendingBytes = governor.ReservedBytes;
                    Assert.Equal(descriptor, write.FileDescriptor);
                    Assert.Equal(content, Encoding.UTF8.GetString(write.Content.Span));
                }
                finally { write.SetResult(DescriptorWriteResult.Open); }
            }
        }
    }

    [Fact]
    public async Task QueryKeepsUtf8OutputAdmittedThroughTheWrite()
    {
        var state = new Eval(Parser.Parse("type -p demo"));
        var budget = new MemoryGovernor(24 * 1024);
        using var owner = budget.CreateScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        state.Memory.Budget = owner;

        var resolvedName = "/sample/" + new string('\u00e9', 4096);
        var expected = Encoding.UTF8.GetBytes(resolvedName + "\n");
        var lookup = Assert.IsType<ResolveCommandsOperation>(await state.GetNextStep(deadline.Token));
        Assert.Equal("demo", Assert.Single(lookup.CommandNames));
        lookup.SetResult([new CommandLookupResult.Found(resolvedName)]);

        var write = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        var pending = budget.ReservedBytes;
        try
        {
            Assert.True(write.FileDescriptor == FileDescriptor.StdOut, "Expected the admitted query output on stdout.");
            Assert.Equal(expected, write.Content.ToArray());
            Assert.False(state.Completion.IsCompleted);
        }
        finally
        {
            write.SetResult(DescriptorWriteResult.Open);
            await state.Completion.WaitAsync(deadline.Token);
        }

        Assert.Null(await state.GetNextStep(deadline.Token));
        Assert.Equal(0, state.LastExitCode);
        Assert.True(pending - budget.ReservedBytes >= expected.Length);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task InvalidHostQueryTextFailsBeforePublishingBufferedOutput()
    {
        var state = new Eval(Parser.Parse("type echo demo"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lookup = Assert.IsType<ResolveCommandsOperation>(await state.GetNextStep(deadline.Token));
        Assert.Equal("demo", Assert.Single(lookup.CommandNames));
        lookup.SetResult([new CommandLookupResult.Found("/sample/\ud800")]);

        await Assert.ThrowsAsync<EncoderFallbackException>(() => state.GetNextStep(deadline.Token));
        await Assert.ThrowsAsync<EncoderFallbackException>(() => state.Completion.WaitAsync(deadline.Token));
    }

    [Fact]
    public async Task CanceledQuerySettlesItsWriteBeforeClosingTheOutput()
    {
        var state = new Eval(Parser.Parse("type -p cat >out"));
        var descriptor = new FileDescriptor(10);
        using var cancellation = new CancellationTokenSource();
        var evaluation = state.EnsureRunning(cancellation.Token);
        var opened = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        opened.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var lookup = Assert.IsType<ResolveCommandsOperation>(await state.GetNextStep());
        lookup.SetResult([new CommandLookupResult.Found("/fixture/cat")]);
        var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, write.FileDescriptor);

        cancellation.Cancel();
        var next = state.GetNextStep();
        var completedBeforeWrite = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100))) == next;
        write.SetResult(DescriptorWriteResult.Open);

        var close = Assert.IsType<CloseDescriptorOperation>(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.False(completedBeforeWrite);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("type printf : 1>&-", "type: write error: Bad file descriptor\n")]
    [InlineData("type -t printf : 1>&-", "type: write error: Bad file descriptor\n")]
    [InlineData("type -p cat 1>&-", "type: write error: Bad file descriptor\n")]
    [InlineData("type printf : 1</dev/null", "type: write error: Bad file descriptor\n")]
    public async Task TypeReportsTheHostWriteBoundary(string script, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Fact]
    public async Task DownstreamClosureIsNotDiagnosedAsAWriteFailure()
    {
        var state = new Eval(Parser.Parse("type printf"));
        var host = new TestOperationVisitor([]);
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("type", false, 1)]
    [InlineData("type", true, 1)]
    [InlineData("command -V", false, 0)]
    [InlineData("command -V", true, 0)]
    public async Task ClosedChannelIsNotWrittenAgainWhileTheOtherChannelContinues(
        string command, bool closeError, int expectedStatus)
    {
        var state = new Eval(Parser.Parse(command + " printf missing : absent"));
        var host = new TestOperationVisitor([])
        {
            CommandLookupResults = [new CommandLookupResult.Missing(), new CommandLookupResult.Missing()]
        };
        var closedDescriptor = closeError ? FileDescriptor.StdErr : FileDescriptor.StdOut;
        var writes = new List<FileDescriptor>();
        host.BeforeAppend = (descriptor, _) =>
        {
            if (descriptor == closedDescriptor)
                Assert.DoesNotContain(closedDescriptor, writes);
            writes.Add(descriptor);
        };
        host.EnqueueAppendResult(closeError ? DescriptorWriteResult.Open : DescriptorWriteResult.Closed);
        host.EnqueueAppendResult(closeError ? DescriptorWriteResult.Closed : DescriptorWriteResult.Open);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        Assert.Equal(expectedStatus, state.LastExitCode);
        Assert.Equal([FileDescriptor.StdOut, FileDescriptor.StdErr,
            closeError ? FileDescriptor.StdOut : FileDescriptor.StdErr], writes);
    }

    [Theory]
    [InlineData("type", 1)]
    [InlineData("command -V", 0)]
    public async Task ClosureOfMergedOutputStopsBothChannels(string command, int expectedStatus)
    {
        var state = new Eval(Parser.Parse(command + " printf missing : absent"), stdOutOverride: FileDescriptor.StdErr);
        var host = new TestOperationVisitor([])
        {
            CommandLookupResults = [new CommandLookupResult.Missing(), new CommandLookupResult.Missing()],
            RecordAppends = true
        };
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);

        await new Engine().RunAsync(state, host);

        Assert.Equal(expectedStatus, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }

    [Fact]
    public async Task ClosedErrorChannelSuppressesLaterWriteFailureDiagnostic()
    {
        var state = new Eval(Parser.Parse("type missing printf"));
        var host = new TestOperationVisitor([])
        {
            CommandLookupResults = [new CommandLookupResult.Missing()],
            RecordAppends = true
        };
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(2, host.Appends.Count);
    }
}

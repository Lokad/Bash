using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class QueryWriteFailureTests
{
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

    [Theory]
    [InlineData("type printf : 1>&-", "type: write error: Bad file descriptor\n")]
    [InlineData("type -t printf : 1>&-", "type: write error: Bad file descriptor\n")]
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

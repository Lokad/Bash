using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class BuiltinAssignmentDiagnosticTests
{
    [Theory]
    [InlineData("printf -v 'a[${missing[-1]}${changed:=2}]' body", false)]
    [InlineData("printf -v 'a[${missing[-1]}${changed:=2}]' body", true)]
    [InlineData("read 'a[${missing[-1]}${changed:=2}]'", false)]
    [InlineData("read 'a[${missing[-1]}${changed:=2}]'", true)]
    [InlineData("printf -v 'a[${changed:=${missing[-1]}}]' body", false)]
    [InlineData("printf -v 'a[${changed:=${missing[-1]}}]' body", true)]
    [InlineData("printf -v 'a[${missing[-1]}$((changed=2))]' body", false)]
    [InlineData("printf -v 'a[${missing[-1]}$(printf unexpected)]' body", true)]
    public async Task SelectorDiagnosticClosureStopsRemainingExpansion(string command, bool closed)
    {
        var state = new Eval(Parser.Parse(command));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        if (command.StartsWith("read ", StringComparison.Ordinal))
            host.EnqueueReadResult(ReadResult.Success("body\n"u8.ToArray(), [], reachedEoF: true));
        host.EnqueueAppendResult(closed ? DescriptorWriteResult.Closed : DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        var diagnostic = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("missing: bad array subscript\n", Encoding.UTF8.GetString(diagnostic.Content));
        Assert.False(state.Variables.ContainsKey("changed"));
        Assert.Empty(state.IndexedArrays);
    }

    [Theory]
    [InlineData("printf -v 'a[${missing[-1]}${changed:=2}]' body")]
    [InlineData("read 'a[${missing[-1]}${changed:=2}]'")]
    public async Task RecoverableSelectorDiagnosticKeepsExpandingWhileOutputIsOpen(string command)
    {
        var state = new Eval(Parser.Parse(command));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        if (command.StartsWith("read ", StringComparison.Ordinal))
            host.EnqueueReadResult(ReadResult.Success("body\n"u8.ToArray(), [], reachedEoF: true));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("2"), state.Variables["changed"]);
        Assert.Equal(ShellValue.FromText("body"), state.IndexedArrays["a"][2]);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData("readonly first", false)]
    [InlineData("readonly first", true)]
    [InlineData("declare -n first='arr[-1]'", false)]
    [InlineData("declare -n first='arr[-1]'", true)]
    [InlineData("declare -n arr; declare -n first='arr[0]'", false)]
    [InlineData("declare -n arr; declare -n first='arr[0]'", true)]
    public async Task CountDiagnosticClosureStopsLaterAssignments(string declaration, bool closed)
    {
        var state = new Eval(Parser.Parse(declaration + "; printf -v output 'abc%n%n' first later"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(closed ? DescriptorWriteResult.Closed : DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(closed ? 0 : 1, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
        Assert.False(state.Variables.ContainsKey("later"));
        Assert.False(state.Variables.ContainsKey("output"));
        Assert.Empty(state.IndexedArrays);
        if (declaration.StartsWith("declare -n arr;", StringComparison.Ordinal))
            Assert.Equal(ShellValue.Empty, state.Namerefs["arr"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountDiagnosticFailurePreventsStdoutAndPreservesNegation(bool negated)
    {
        var state = new Eval(Parser.Parse("readonly first; " + (negated ? "! " : "") + "printf 'abc%n' first"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(negated ? 0 : 1, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData("printf -v first value", false)]
    [InlineData("printf -v first value", true)]
    [InlineData("read first", false)]
    [InlineData("read first", true)]
    [InlineData("mapfile first", false)]
    [InlineData("mapfile first", true)]
    public async Task RejectedTargetPropagatesDiagnosticFailure(string command, bool negated)
    {
        var state = new Eval(Parser.Parse("readonly first=old; " + (negated ? "! " : "") + command));
        var host = new TestOperationVisitor([]);
        if (command == "read first")
            host.EnqueueReadResult(ReadResult.Success("new\n"u8.ToArray(), [], reachedEoF: true));
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(negated ? 0 : 1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), state.Variables["first"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MapfileWarningClosureDoesNotReadOrConvertTheBinding(bool circular, bool closed)
    {
        var declaration = circular ? "declare -n ref=other other=ref" : "declare -n ref";
        var state = new Eval(Parser.Parse(declaration + "; mapfile ref"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(closed ? DescriptorWriteResult.Closed : DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(closed ? 0 : 1, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
        Assert.Equal(ShellValue.FromText(circular ? "other" : ""), state.Namerefs["ref"]);
        Assert.Empty(state.IndexedArrays);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public async Task DiagnosticFailureDoesNotLeakIntoTheFollowingBuiltin()
    {
        var state = new Eval(Parser.Parse("readonly first; printf '%n' first; failed=$?; printf '%d' bad"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        Assert.Equal(ShellValue.FromText("1"), state.Variables["failed"]);
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(3, host.Appends.Count);
        Assert.Equal("printf: bad: invalid number\n", Encoding.UTF8.GetString(host.Appends[1].Content));
        Assert.Equal("0", Encoding.UTF8.GetString(host.Appends[2].Content));
    }
}

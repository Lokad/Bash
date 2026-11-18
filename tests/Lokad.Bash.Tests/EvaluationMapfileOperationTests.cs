using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Fact]
    public async Task Evaluation_MapfileBuiltin_ReadsAllLinesInBatches()
    {
        var script = Parser.Parse(Utf8("mapfile arr"));
        var state = new Eval(script);

        var firstRead = await NextReadAsync(state);
        Assert.Equal(0, firstRead.FileDescriptor.Id);
        Assert.Equal(128, firstRead.Limit);
        firstRead.SetResult(ReadResult.Success(Utf8Bytes("a\nb\n"), [2], reachedEoF: false));

        var secondRead = await NextReadAsync(state);
        Assert.Equal(128, secondRead.Limit);
        secondRead.SetResult(ReadResult.Success(Utf8Bytes("c"), [], reachedEoF: true));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("a\n"), ShellValue.FromText("b\n"), ShellValue.FromText("c") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_CountBoundsTheFinalBatchWithoutAnEofRead()
    {
        var state = new Eval(Parser.Parse(Utf8("arr=([0]=keep); mapfile -tn129 -O2 arr")));
        var first = await NextReadAsync(state);
        Assert.Equal(128, first.Limit);
        first.SetResult(ReadResult.Success(Utf8Bytes(new string('\n', 128)),
            Enumerable.Range(1, 127).ToArray(), reachedEoF: false));

        var last = await NextReadAsync(state);
        Assert.Equal(1, last.Limit);
        Assert.Equal(ShellValue.FromText("keep"), Assert.Single(state.IndexedArrays["arr"]));
        last.SetResult(ReadResult.Success(Utf8Bytes("last\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(131, state.IndexedArrays["arr"].Count);
        Assert.Equal(ShellValue.FromText("keep"), state.IndexedArrays["arr"][0]);
        Assert.Equal(ShellValue.FromText("last"), state.IndexedArrays["arr"][130]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_PropagatesReadFailureWithoutReplacingArray()
    {
        var script = Parser.Parse(Utf8("arr=(old); mapfile arr"));
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        read.SetResult(ReadResult.Failure(error: new OperationError("read error\n")));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("old") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_PartialContentThenFailureDoesNotReplaceArray()
    {
        var script = Parser.Parse(Utf8("arr=(old); mapfile arr"));
        var state = new Eval(script);

        var content = await NextReadAsync(state);
        content.SetResult(ReadResult.Success(Utf8Bytes("staged\n"), [], reachedEoF: false));

        var failure = await NextReadAsync(state);
        failure.SetResult(ReadResult.Failure(error: new OperationError("read error\n")));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("old") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_CancellationDoesNotReplaceArray()
    {
        var script = Parser.Parse(Utf8("arr=(old); mapfile arr"));
        var state = new Eval(script);
        var read = await NextReadAsync(state);
        var visitor = new TestOperationVisitor([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellOperationDispatcher.DispatchAsync(read, visitor, cancellation.Token));

        Assert.Equal(
            new[] { ShellValue.FromText("old") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_ReadarrayBuiltin_TrimRemovesOnlyNewlineDelimiter()
    {
        var script = Parser.Parse(Utf8("readarray -t arr"));
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        Assert.Equal(0, read.FileDescriptor.Id);
        Assert.Equal(128, read.Limit);
        read.SetResult(ReadResult.Success(Utf8Bytes("x\r\ny\n"), [3], reachedEoF: true));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("x\r"), ShellValue.FromText("y") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_ReadsCustomRecordBatchesBeforePublishing()
    {
        var state = new Eval(Parser.Parse(Utf8("arr=(old); mapfile -td: arr")));

        var first = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(FileDescriptor.StdIn, first.Descriptor);
        Assert.Equal((byte)':', first.Delimiter);
        Assert.Equal(128, first.Limit);
        var content = Utf8Bytes("a::");
        first.SetResult(new DescriptorRecordBatch(content, [2], ByteReadResult.Success(content.Length, reachedEoF: false)));

        var second = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
        second.SetResult(new DescriptorRecordBatch(default, [], ByteReadResult.EndOfFile));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new[] { ShellValue.FromText("a"), ShellValue.Empty }, state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_SettlesPendingBoundedBatchBeforeCancellation()
    {
        var state = new Eval(Parser.Parse(Utf8("arr=(old); mapfile -td: -n2 -O0 arr")));
        using var cancellation = new CancellationTokenSource();
        var evaluation = state.EnsureRunning(cancellation.Token);

        var first = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(2, first.Limit);
        first.SetResult(new DescriptorRecordBatch(Utf8Bytes("a:"), [], ByteReadResult.Success(2, reachedEoF: false)));
        var last = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(1, last.Limit);

        cancellation.Cancel();
        var next = state.GetNextStep();
        var completedBeforeRead = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100))) == next;
        last.SetResult(new DescriptorRecordBatch(Utf8Bytes("b:"), [], ByteReadResult.Success(2, reachedEoF: false)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.False(completedBeforeRead);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_EmptyInputClearsArrayAndSucceeds()
    {
        var script = Parser.Parse(Utf8("arr=(old); mapfile arr"));
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        read.SetResult(ReadResult.EndOfFile);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Empty(state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_RejectsAssociativeArrayBeforeReading()
    {
        var script = Parser.Parse(Utf8("declare -A arr; arr[key]=old; mapfile arr"));
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("mapfile: arr: not an indexed array\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("old", state.AssociativeArrays["arr"][ShellValue.FromText("key")].ToUtf8String());
    }

    [Theory]
    [InlineData("mapfile -s 1 arr")]
    [InlineData("mapfile -u 3 arr")]
    [InlineData("mapfile -C callback arr")]
    [InlineData("mapfile -c 1 arr")]
    [InlineData("mapfile -x arr")]
    public async Task Evaluation_MapfileBuiltin_RejectsUnsupportedShapeBeforeReading(string command)
    {
        var script = Parser.Parse(Utf8($"arr=(old); {command}"));
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        var reason = command.Split(' ')[1] + ": invalid option";
        Assert.Equal("mapfile: " + reason + "\nmapfile: usage: mapfile [-t] [-d delim] [-n count] [-O origin] [--] [array]\n",
            Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));

        Assert.Equal(2, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), state.IndexedArrays["arr"][0]);
    }

    [Theory]
    [InlineData("mapfile -d : -d", "mapfile", "-d")]
    [InlineData("readarray -td", "readarray", "-d")]
    [InlineData("mapfile -n", "mapfile", "-n")]
    [InlineData("readarray -tO", "readarray", "-O")]
    public async Task Evaluation_MapfileBuiltin_RejectsMissingOptionOperandBeforeReading(string command, string builtin, string option)
    {
        var state = new Eval(Parser.Parse(Utf8($"arr=(old); {command}")));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal($"{builtin}: {option}: option requires an argument\n{builtin}: usage: {builtin} [-t] [-d delim] [-n count] [-O origin] [--] [array]\n",
            Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }

    [Theory]
    [InlineData("mapfile -n -1", "mapfile: -1: invalid line count\n")]
    [InlineData("readarray -O4294967296", "readarray: 4294967296: invalid array origin\n")]
    public async Task Evaluation_MapfileBuiltin_RejectsInvalidNumbersBeforeReading(string command, string error)
    {
        var state = new Eval(Parser.Parse(Utf8($"arr=(old); {command} arr")));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal(error, Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }
}

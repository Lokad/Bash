using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;
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
    [InlineData("mapfile -d : arr")]
    [InlineData("mapfile -n 1 arr")]
    [InlineData("mapfile -s 1 arr")]
    [InlineData("mapfile -O 1 arr")]
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
        Assert.Equal("mapfile: " + reason + "\nmapfile: usage: mapfile [-t] [--] [array]\n",
            Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));

        Assert.Equal(2, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), state.IndexedArrays["arr"][0]);
    }
}

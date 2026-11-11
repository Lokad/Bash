using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class EvaluationMapfileByteTests
{

    [Fact]
    public async Task RetainedArrayStoragePreventsAnotherMapfileFromReusingItsAllowance()
    {
        const long MemoryBudgetBytes = 384 * 1024;
        var governor = new MemoryGovernor(MemoryBudgetBytes);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.ConfigureHost(host => { host.AddFile("/input", new string('\n', 1024)); }), governor);
        var state = new Eval(Parser.Parse("mapfile -t first </input; second=(old); mapfile -t second </input; status=$?"));

        await new Engine().RunAsync(state, host);

        Assert.Equal(1024, state.IndexedArrays["first"].Count);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["second"]));
        Assert.Equal(ShellValue.FromText("2"), state.Variables["status"]);
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        Assert.True(governor.ReservedBytes >= 1024 * 64);
        GC.KeepAlive(state);
    }

    [Theory]
    [InlineData("mapfile", false, false)]
    [InlineData("readarray", false, false)]
    [InlineData("mapfile", false, true)]
    [InlineData("mapfile", true, false)]
    [InlineData("readarray", true, true)]
    public async Task DescriptorLimitRetainsStatusAndPreservesTheArray(string command, bool diagnosticFailure, bool negated)
    {
        var state = new Eval(Parser.Parse("arr=(old); " + (negated ? "! " : "") + command
            + " -t arr 2>&1; status=$?; finished=yes"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueReadResult(ReadResult.Success("partial\n"u8.ToArray(), [], reachedEoF: false));
        // Exercise the descriptor's earlier rejection without allocating an oversized physical record.
        host.EnqueueReadTask(Task.FromException<ReadResult>(new ByteMemoryBuilder.LimitExceededException()));
        host.EnqueueAppendResult(diagnosticFailure ? DescriptorWriteResult.Failure(new OperationError("Write failed\n")) : DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(ShellValue.FromText(negated ? "0" : diagnosticFailure ? "1" : "2"), state.Variables["status"]);
        Assert.Equal(ShellValue.FromText("yes"), state.Variables["finished"]);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
        var diagnostic = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdOut, diagnostic.Descriptor);
        Assert.Equal("mapfile: shared memory budget exhausted\n",
            System.Text.Encoding.UTF8.GetString(diagnostic.Content));
    }

    [Theory]
    [InlineData("mapfile; printf '[%s]' \"${MAPFILE[@]}\"", "\na\nb", "[\n][a\n][b]", "")]
    [InlineData("arr=scalar; arr[1000000]=sparse; mapfile -t arr; printf '<%s><%s>' \"${arr[0]}\" \"${arr[1000000]-missing}\"", "replacement\n", "<replacement><missing>", "")]
    [InlineData("arr=(old); declare -n ref=arr; mapfile -t ref; printf '<%s><%s>' \"${arr[0]}\" \"${ref[1]}\"", "first\nsecond\n", "<first><second>", "")]
    [InlineData("readonly arr; mapfile arr; status=$?; read next; printf '%s:<%s>' \"$status\" \"$next\"", "still-available\n", "1:<still-available>", "arr: readonly variable\n")]
    [InlineData("arr=(old); mapfile arr <&-; printf '<%s>:%s' \"${arr[0]-unset}\" \"$?\"", "", "<unset>:0", "")]
    [InlineData("declare -A map=([key]=old); declare -n ref='map[key]'; mapfile ref; status=$?; read next; printf '<%s><%s><%s>' \"$status\" \"${map[key]}\" \"$next\"", "line\n", "<1><old><line>", "mapfile: `map[key]': not a valid identifier\n")]
    public async Task InputAndTargetKindsRetainTheirContracts(string script, string input, string output, string error)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]), System.Text.Encoding.UTF8.GetBytes(input));
        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("mapfile arr", "FF0A", "FF0A", "FE")]
    [InlineData("mapfile -t arr", "FF0A", "FF", "FE")]
    [InlineData("readarray -t arr", "FF0D0A", "FF0D", "FE")]
    [InlineData("mapfile -tt -t -- arr", "FF0D0A", "FF0D", "FE")]
    [InlineData("mapfile arr", "FF00410A", "FF", "FE")]
    [InlineData("mapfile -t arr", "00410A", "", "FE")]
    public async Task RecordsRetainBytesAndTruncateOnlyAtFirstNul(
        string command, string firstRecordHex, string firstValueHex, string secondValueHex)
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        host.SetStandardInputBytes(Convert.FromHexString(firstRecordHex + "FE"));
        var state = new Eval(Parser.Parse(command));

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        var values = state.IndexedArrays["arr"];
        Assert.Equal(2, values.Count);
        Assert.Equal(Convert.FromHexString(firstValueHex), values[0].Bytes.ToArray());
        Assert.Equal(Convert.FromHexString(secondValueHex), values[1].Bytes.ToArray());
        Assert.Equal(ReadResult.EndOfFile, await host.ReadLinesAsync(FileDescriptor.StdIn, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(257)]
    [InlineData(10000)]
    public async Task AllRecordsReachStorageIndependentlyOfParameterExpansion(int count)
    {
        var input = string.Concat(Enumerable.Range(0, count).Select(index => $"line{index}\n"));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        host.SetStandardInputBytes(System.Text.Encoding.UTF8.GetBytes(input));
        var state = new Eval(Parser.Parse("mapfile -t arr"));

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        var values = state.IndexedArrays["arr"];
        Assert.Equal(count, values.Count);
        for (var index = 0; index < count; index++)
            Assert.Equal(ShellValue.FromText($"line{index}"), values[index]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialReadDoesNotPublishBytesOnFailureOrCancellation(bool cancel)
    {
        var state = new Eval(Parser.Parse("arr=($'\\377'); mapfile arr"));
        var host = new TestOperationVisitor([]);
        host.EnqueueReadResult(ReadResult.Success(new byte[] { 0xFE, 10 }, [], reachedEoF: false));
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.Cancel();
            host.EnqueueReadTask(Task.FromCanceled<ReadResult>(cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Engine().RunAsync(state, host));
        }
        else
        {
            host.EnqueueReadResult(ReadResult.Failure(error: new OperationError("read error\n")));
            await new Engine().RunAsync(state, host);
            Assert.Equal(1, state.LastExitCode);
        }

        Assert.Equal(new byte[] { 0xFF }, Assert.Single(state.IndexedArrays["arr"]).Bytes.ToArray());
        Assert.Empty(host.ClosedDescriptors);
    }
}

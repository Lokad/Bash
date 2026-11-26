using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ShellTimeFormattingTests
{
    [Theory]
    [InlineData("%0lR|%1R|%2R|%3R|%9R|%P", 1_230_000L, 24_600L, 0L, "0m0s|0.1|0.12|0.123|0.123|2.00\n")]
    [InlineData("%lR|%1lR|%2lR|%0lR", 599_995_000L, 0L, 0L, "1m0.000s|1m0.0s|1m0.00s|1m0s\n")]
    [InlineData("%P", 3_000_000L, -100_000L, 0L, "-3.33\n")]
    [InlineData("%P", 3_000_000L, -1L, 0L, "0.00\n")]
    [InlineData("%P", 1L, long.MaxValue, 0L, "922337203685477580700.00\n")]
    [InlineData("%P", long.MaxValue, long.MaxValue - 1, 1L, "100.00\n")]
    [InlineData("%P", long.MaxValue, long.MaxValue, 1L, "100.00\n")]
    [InlineData("%P", long.MaxValue, long.MaxValue, long.MaxValue, "200.00\n")]
    public void NumericFragmentsPreservePrecisionAndSigns(string format, long realTicks, long userTicks, long systemTicks,
        string expected)
    {
        var timing = new ShellTimingSnapshot(TimeSpan.FromTicks(realTicks), TimeSpan.FromTicks(userTicks), TimeSpan.FromTicks(systemTicks));
        var memory = new MemoryGovernor(1024);
        using var owner = memory.CreateScope();

        Assert.True(ShellTimeFormatting.TryFormat(ShellValue.FromText(format), timing, out var result, out var error, owner));

        Assert.Equal(ShellValue.FromText(expected).Bytes.ToArray(), result.ToArray());
        Assert.True(error.IsEmpty);
        Assert.True(memory.ReservedBytes >= result.Length);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void LiteralRunsPreserveRawBytesAroundDirectivesAndTrailingPercent()
    {
        var prefix = Enumerable.Repeat((byte)0xff, 4093).ToArray();
        var suffix = Enumerable.Repeat((byte)'\r', 261).ToArray();
        byte[] format = [.. prefix, .. "%0R%%"u8, .. suffix, (byte)'%'];
        byte[] expected = [.. prefix, .. "1%"u8, .. suffix, .. "%\n"u8];
        var timing = new ShellTimingSnapshot(TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero);
        var memory = new MemoryGovernor(16 * 1024);
        using var owner = memory.CreateScope();

        Assert.True(ShellTimeFormatting.TryFormat(ShellValue.FromBytes(format), timing, out var result, out var error, owner));
        Assert.Equal(expected, result.ToArray());
        Assert.True(error.IsEmpty);
        Assert.True(memory.ReservedBytes >= result.Length);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void InvalidTimingDirectiveDiscardsTheCopiedPrefix()
    {
        var format = ShellValue.FromText(new string('x', 4093) + "%2lQ");
        var memory = new MemoryGovernor(16 * 1024);
        using var owner = memory.CreateScope();

        Assert.False(ShellTimeFormatting.TryFormat(format, default, out var result, out var error, owner));
        Assert.True(result.IsEmpty);
        Assert.Equal("TIMEFORMAT: `Q': invalid format character\n"u8.ToArray(), error.ToArray());
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void LongLiteralFitsWithoutIntermediateBacking()
    {
        const int Capacity = 4096;
        var format = ShellValue.FromText(new string('x', Capacity - 1));
        var memory = new MemoryGovernor(Capacity);
        using var owner = memory.CreateScope();

        Assert.True(ShellTimeFormatting.TryFormat(format, default, out var result, out var error, owner));
        Assert.True(error.IsEmpty);
        Assert.Equal(Capacity, result.Length);
        Assert.True(result.Span[..^1].SequenceEqual(format.Bytes.Span));
        Assert.Equal((byte)'\n', result.Span[^1]);
        Assert.Equal(Capacity, memory.PeakBytes);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("TIMEFORMAT=%2l\ntime false", 1, "line 2: TIMEFORMAT: `\0': invalid format character\n")]
    [InlineData("TIMEFORMAT=$'%\\377'\n\ntime false", 1, "line 3: TIMEFORMAT: `ÿ': invalid format character\n")]
    [InlineData("TIMEFORMAT=%Q\n:\n\n! time", 1, "line 4: TIMEFORMAT: `Q': invalid format character\n")]
    [InlineData("TIMEFORMAT=%Q\nf(){\n :\n time false\n}\nf", 1, "line 2: TIMEFORMAT: `Q': invalid format character\n")]
    [InlineData("TIMEFORMAT=%Q\n:\neval '\n :\n time false\n'", 1, "line 8: TIMEFORMAT: `Q': invalid format character\n")]
    [InlineData("TIMEFORMAT=%Q\nv=$(\n :\n time false\n)", 1, "line 6: TIMEFORMAT: `Q': invalid format character\n")]
    [InlineData("TIMEFORMAT=%Q\nfor item in suit; do\n case $item in\n (suit) time false ;;\n esac\n time false\ndone", 1,
        "line 3: TIMEFORMAT: `Q': invalid format character\nline 2: TIMEFORMAT: `Q': invalid format character\n")]
    [InlineData("TIMEFORMAT=%Q\nwhile :; do\n time false\n break\ndone", 0, "line 5: TIMEFORMAT: `Q': invalid format character\n")]
    public async Task InvalidTimingFormatsReportTheReachedLineAndPreserveStatus(
        string commands, int timedStatus, string diagnostic)
    {
        var source = commands + "\nprintf '<%s>' \"$?\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            Assert.Equal(0, await RunTimedFixtureAsync(host, source, prepared));
            Assert.Equal($"<{timedStatus}>", host.GetOutput(FileDescriptor.StdOut));
            // Latin-1 names the opaque FF byte in the expectation without decoding shell output.
            Assert.Equal(System.Text.Encoding.Latin1.GetBytes(diagnostic), host.GetOutputBytes(FileDescriptor.StdErr));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("TIMEFORMAT=%Q\ntime (\n :\n false\n) 2>out", "line 5")]
    [InlineData("TIMEFORMAT=%Q\nsource ./child\n\nf 2>out", "./child: line 2")]
    public async Task TimingDiagnosticUsesTheActiveRedirectedStream(string commands, string prefix)
    {
        var source = commands + "\nprintf '<%s>' \"$?\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new MockFileSystem();
            host.AddFile("/child", "\nf(){\n :\n time false\n}\n");
            Assert.Equal(0, await RunTimedFixtureAsync(host, source, prepared));
            Assert.Equal("<1>", host.GetOutput(FileDescriptor.StdOut));
            Assert.Empty(host.GetOutputBytes(FileDescriptor.StdErr));
            Assert.Equal($"{prefix}: TIMEFORMAT: `Q': invalid format character\n", host.ReadFile("/out"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task PendingTimingDiagnosticPrecedesTheErrexitAction()
    {
        const string source = "TIMEFORMAT=%Q\nset -e\ntrap 'printf \"exit:%s\" \"$?\" >&2' EXIT\ntime false\nprintf later\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new MockFileSystem();
            Assert.Equal(1, await RunTimedFixtureAsync(host, source, prepared));
            Assert.Empty(host.GetOutputBytes(FileDescriptor.StdOut));
            Assert.Equal("line 4: TIMEFORMAT: `Q': invalid format character\nexit:1", host.GetOutput(FileDescriptor.StdErr));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    private static async Task<int> RunTimedFixtureAsync(MockFileSystem host, string source, bool prepared)
    {
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, CancellationToken.None)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, CancellationToken.None);
        return result.ExitCode;
    }

    [Fact]
    public async Task TimingWritesTheFormattedRecordAndReturnsTheTimedStatus()
    {
        var memory = new MemoryGovernor(1024 * 1024);
        using var owner = memory.CreateScope();
        var state = new Eval(Parser.Parse("TIMEFORMAT='%0R|%1lU|%P'; time false"));
        state.Memory.Budget = owner;

        var start = Assert.IsType<ReadTimingOperation>(await state.GetNextStep());
        start.SetResult(default);
        var end = Assert.IsType<ReadTimingOperation>(await state.GetNextStep());
        var reservedBeforeFormatting = memory.ReservedBytes;
        end.SetResult(new ShellTimingSnapshot(TimeSpan.FromMilliseconds(123), TimeSpan.FromTicks(24_600), TimeSpan.Zero));

        var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(FileDescriptor.StdErr, write.FileDescriptor);
        Assert.Equal("0|0m0.0s|2.00\n"u8.ToArray(), write.Content.ToArray());
        Assert.True(memory.ReservedBytes >= reservedBeforeFormatting + write.Content.Length);
        write.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void ResultRemainsChargedUntilCallerScopeEnds()
    {
        var memory = new MemoryGovernor(16);
        var format = ShellValue.FromText("real %2R");
        var timing = new ShellTimingSnapshot(TimeSpan.FromMilliseconds(1250), TimeSpan.Zero, TimeSpan.Zero);

        for (var call = 0; call < 2; call++)
        {
            using (var owner = memory.CreateScope())
            {
                Assert.True(ShellTimeFormatting.TryFormat(format, timing, out var result, out var error, owner));
                Assert.Empty(error.ToArray());
                Assert.Equal("real 1.25\n"u8.ToArray(), result.ToArray());
                Assert.True(memory.ReservedBytes >= result.Length);
            }
            Assert.Equal(0, memory.ReservedBytes);
        }
    }
}

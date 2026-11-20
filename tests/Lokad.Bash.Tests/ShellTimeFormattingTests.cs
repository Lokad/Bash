using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ShellTimeFormattingTests
{
    [Theory]
    [InlineData("%0lR|%1R|%2R|%3R|%9R|%P", 1_230_000L, 24_600L, "0m0s|0.1|0.12|0.123|0.123|2.00\n")]
    [InlineData("%lR|%1lR|%2lR|%0lR", 599_995_000L, 0L, "1m0.000s|1m0.0s|1m0.00s|1m0s\n")]
    [InlineData("%P", 3_000_000L, -100_000L, "-3.33\n")]
    [InlineData("%P", 3_000_000L, -1L, "0.00\n")]
    [InlineData("%P", 1L, long.MaxValue, "922337203685477580700.00\n")]
    public void NumericFragmentsPreservePrecisionAndSigns(string format, long realTicks, long userTicks, string expected)
    {
        var timing = new ShellTimingSnapshot(TimeSpan.FromTicks(realTicks), TimeSpan.FromTicks(userTicks), TimeSpan.Zero);
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

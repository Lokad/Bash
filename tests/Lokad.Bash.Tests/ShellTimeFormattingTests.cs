using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class ShellTimeFormattingTests
{
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

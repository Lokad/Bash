using Lokad.Bash.Formatting;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
    [Theory]
    [InlineData("%u", '9')]
    [InlineData("%a", 'f')]
    public void NumericConversionAllocationScalesWithInputRatherThanMagnitude(string format, char digit)
    {
        _ = Measure(128);
        var smaller = Measure(1024);
        var larger = Measure(2048);
        Assert.True(larger <= smaller * 3 + 128 * 1024,
            $"Doubling numeric digits allocated {smaller:N0} then {larger:N0} bytes.");

        long Measure(int digits)
        {
            var argument = ShellValue.FromText((digit == 'f' ? "0x" : "") + new string(digit, digits));
            var pattern = ShellValue.FromText(format);
            var memory = new MemoryGovernor(1024 * 1024);
            using var owner = memory.CreateScope();
            var before = GC.GetAllocatedBytesForCurrentThread();
            // Public synchronous formatting never suspends; thread accounting is sufficient here.
            var result = PrintfFormatting.EvaluateCommand(pattern, [argument], owner);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, result.ExitCode);
            Assert.InRange(result.Output.Length, 1, 64);
            owner.Dispose();
            Assert.Equal(0, memory.ReservedBytes);
            return allocated;
        }
    }
}

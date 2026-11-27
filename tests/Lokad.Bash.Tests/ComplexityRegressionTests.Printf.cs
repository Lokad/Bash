using Lokad.Bash.Formatting;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
    [Fact]
    public void CharacterFormattingDoesNotAllocatePerEmptyField()
    {
        const int FieldCount = 1024;
        var format = ShellValue.FromText(string.Concat(Enumerable.Repeat("%2c", FieldCount)));
        var present = Enumerable.Repeat(ShellValue.FromText("x"), FieldCount).ToArray();
        var empty = new ShellValue[FieldCount];
        ShellValue[] missing = [];

        _ = Measure(present, (byte)'x');
        _ = Measure(empty, 0);
        _ = Measure(missing, 0);
        var presentBytes = Measure(present, (byte)'x');
        var emptyBytes = Measure(empty, 0);
        var missingBytes = Measure(missing, 0);

        const int Tolerance = 4096;
        Assert.True(emptyBytes <= presentBytes + Tolerance && missingBytes <= presentBytes + Tolerance,
            $"Present, empty and missing fields allocated {presentBytes:N0}, {emptyBytes:N0} and {missingBytes:N0} bytes.");

        long Measure(IReadOnlyList<ShellValue> arguments, byte expected)
        {
            var memory = new MemoryGovernor(1024 * 1024);
            using var owner = memory.CreateScope();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = PrintfFormatting.EvaluateCommand(format, arguments, owner);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Diagnostics);
            Assert.Equal(FieldCount * 2, result.Output.Length);
            var bytes = result.Output.Span;
            for (var index = 0; index < bytes.Length; index += 2)
            {
                Assert.Equal((byte)' ', bytes[index]);
                Assert.Equal(expected, bytes[index + 1]);
            }
            owner.Dispose();
            Assert.Equal(0, memory.ReservedBytes);
            return allocated;
        }
    }

    [Theory]
    [InlineData("%d", "-9223372036854775808", "-9223372036854775808")]
    [InlineData("%#o", "18446744073709551615", "01777777777777777777777")]
    [InlineData("%08x", "2748", "00000abc")]
    [InlineData("%#X", "2748", "0XABC")]
    public void IntegerFormattingDoesNotAllocateADigitStringPerField(string conversion, string argument, string expected)
    {
        const int FieldCount = 512;
        var integerFormat = RepeatedFormat(conversion);
        var textFormat = RepeatedFormat("%s");
        var integerArguments = Enumerable.Repeat(ShellValue.FromText(argument), FieldCount).ToArray();
        var textArguments = Enumerable.Repeat(ShellValue.FromText(expected), FieldCount).ToArray();
        var expectedBytes = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(expected, FieldCount)));

        _ = Measure(integerFormat, integerArguments);
        _ = Measure(textFormat, textArguments);
        var integerBytes = Measure(integerFormat, integerArguments);
        var textBytes = Measure(textFormat, textArguments);

        Assert.True(integerBytes <= textBytes + 4096,
            $"Integer and equal-output text fields allocated {integerBytes:N0} and {textBytes:N0} bytes.");

        static ShellValue RepeatedFormat(string conversion) =>
            ShellValue.FromText(string.Concat(Enumerable.Repeat(conversion, FieldCount)));

        long Measure(ShellValue format, IReadOnlyList<ShellValue> arguments)
        {
            var memory = new MemoryGovernor(1024 * 1024);
            using var owner = memory.CreateScope();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = PrintfFormatting.EvaluateCommand(format, arguments, owner);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Diagnostics);
            Assert.True(result.Output.Span.SequenceEqual(expectedBytes));
            owner.Dispose();
            Assert.Equal(0, memory.ReservedBytes);
            return allocated;
        }
    }

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

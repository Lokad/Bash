using Lokad.Bash.Formatting;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class EchoFormattingTests
{
    [Theory]
    [InlineData(new string[] { }, 0, "\n")]
    [InlineData(new[] { "-n" }, 1, "")]
    [InlineData(new[] { "-n", "-n", "value" }, 2, "value")]
    [InlineData(new[] { "-nnn", "value" }, 1, "value")]
    [InlineData(new[] { "-nn", "-n" }, 2, "")]
    [InlineData(new[] { "-n", "-nn", "", "value", "" }, 2, " value ")]
    [InlineData(new[] { "-nn", "--", "-n" }, 1, "-- -n")]
    [InlineData(new[] { "-n", "-nx", "-nn" }, 1, "-nx -nn")]
    [InlineData(new[] { "-nx", "-n" }, 0, "-nx -n\n")]
    [InlineData(new[] { "-", "-nn" }, 0, "- -nn\n")]
    [InlineData(new[] { "", "-nn" }, 0, " -nn\n")]
    [InlineData(new[] { "value", "-nn" }, 0, "value -nn\n")]
    public void FormatsLeadingNewlineOptionsAndSizesTheRemainingOperands(
        string[] arguments, int firstArgument, string expected)
    {
        var operands = arguments.Select(ShellValue.FromText).ToArray();
        var bytes = ShellValue.FromText(expected).Bytes;
        var format = EchoFormatting.GetOutputFormat(operands);

        Assert.Equal(firstArgument, format.FirstArgument);
        Assert.Equal(firstArgument != 0, format.SuppressNewline);
        Assert.Equal(bytes.Length, format.Length);
        var governor = new MemoryGovernor(bytes.Length);
        using (var output = governor.CreateScope())
        {
            Assert.Equal(bytes.ToArray(), EchoFormatting.Format(operands, format, output).ToArray());
            Assert.Equal(bytes.Length, governor.ReservedBytes);
        }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void PreservesRawBytesInTheFirstNonOption()
    {
        ShellValue[] operands =
        [
            ShellValue.FromText("-nn"),
            ShellValue.FromBytes(new byte[] { (byte)'-', (byte)'n', 0xff }),
            ShellValue.FromText("-n")
        ];
        var expected = new byte[] { (byte)'-', (byte)'n', 0xff, (byte)' ', (byte)'-', (byte)'n' };
        var format = EchoFormatting.GetOutputFormat(operands);
        using var output = new MemoryGovernor(expected.Length).CreateScope();

        Assert.Equal(1, format.FirstArgument);
        Assert.Equal(expected, EchoFormatting.Format(operands, format, output).ToArray());
    }
}

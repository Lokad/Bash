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

    [Theory]
    [InlineData(new[] { "-eE", @"a\tb" }, 1, false, "a\\tb\n")]
    [InlineData(new[] { "-E", "-e", @"a\tb" }, 2, false, "a\tb\n")]
    [InlineData(new[] { "-Enne", @"a\tb" }, 1, true, "a\tb")]
    [InlineData(new[] { "-e", "-nE", @"a\tb" }, 2, true, "a\\tb")]
    [InlineData(new[] { "-nex", @"a\tb", "-n" }, 0, false, "-nex a\\tb -n\n")]
    [InlineData(new[] { "-e", "-Ex", @"a\tb", "-n" }, 1, false, "-Ex a\tb -n\n")]
    [InlineData(new[] { "-e", "--", "-n", @"a\tb" }, 1, false, "-- -n a\tb\n")]
    [InlineData(new[] { "-e" }, 1, false, "\n")]
    [InlineData(new[] { "-en", "-E" }, 2, true, "")]
    public void EscapeOptionsUseTheLastReachedFlagAndConsumeOnlyCompleteGroups(
        string[] arguments, int first, bool suppressNewline, string expected)
    {
        var operands = arguments.Select(ShellValue.FromText).ToArray();
        var format = EchoFormatting.GetOutputFormat(operands);
        var bytes = ShellValue.FromText(expected).Bytes;
        using var owner = new MemoryGovernor(bytes.Length).CreateScope();

        Assert.Equal(new EchoFormatting.OutputFormat(first, suppressNewline, bytes.Length), format);
        Assert.Equal(bytes.ToArray(), EchoFormatting.Format(operands, format, owner).ToArray());
    }

    [Theory]
    [InlineData(@"\a\b\e\E\f\n\r\t\v\\", "07081B1B0C0A0D090B5C0A")]
    [InlineData(@"\1\12\123\777", "5C315C31325C3132335C3737370A")]
    [InlineData(@"\01\012\01234\0777\0000Z\08\09\0", "010A5334FF005A00380039000A")]
    [InlineData(@"\x4/Q\x41/Z\x4142", "042F51412F5A4134320A")]
    [InlineData(@"\xG\z\u", "5C78475C7A5C750A")]
    [InlineData(@"\?\u00E9\U0001F642", "5C3FC3A9F09F99820A")]
    [InlineData(@"\uD800\U00110000\U7FFFFFFF\UFFFFFFFF", "EDA080F4908080FDBFBFBFBFBF0A")]
    [InlineData(@"a\0b\x00c", "61006200630A")]
    public void EscapeGrammarPreservesLiteralEscapesAndWritesDecodedBytes(string operand, string expected)
    {
        var operands = new[] { ShellValue.FromText("-e"), ShellValue.FromText(operand) };
        var bytes = Convert.FromHexString(expected);
        var governor = new MemoryGovernor(bytes.Length);
        using var owner = governor.CreateScope();
        var display = EchoFormatting.GetOutputFormat(operands);

        Assert.Equal(bytes.Length, display.Length);
        Assert.Equal(bytes, EchoFormatting.Format(operands, display, owner).ToArray());
        Assert.Equal(bytes.Length, governor.ReservedBytes);
    }

    [Fact]
    public void StopEscapeAdmitsOnlyTheReachedOutputIncludingPrecedingSeparators()
    {
        var operands = new[]
        {
            ShellValue.FromText("-e"),
            ShellValue.FromText("head"),
            ShellValue.Empty,
            ShellValue.FromText(@"\c" + new string('x', 128 * 1024)),
            ShellValue.FromText(new string('y', 128 * 1024))
        };
        var governor = new MemoryGovernor(6);
        var display = EchoFormatting.GetOutputFormat(operands);
        Assert.True(display.SuppressNewline);
        Assert.Equal(6, display.Length);

        using (var owner = governor.CreateScope())
        {
            Assert.Equal("head  "u8.ToArray(), EchoFormatting.Format(operands, display, owner).ToArray());
            Assert.Equal(6, governor.ReservedBytes);
        }
        Assert.Equal(0, governor.ReservedBytes);
    }
}

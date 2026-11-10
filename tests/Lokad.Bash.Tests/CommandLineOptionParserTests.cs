using Lokad.Bash.Helpers;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class CommandLineOptionParserTests
{
    [Theory]
    [InlineData("-name", false, "")]
    [InlineData("-name==value", true, "=value")]
    public void KnownSingleDashLongNamesTakePrecedenceOverShortGroups(string argument, bool hasValue, string value)
    {
        ReadOnlyMemory<byte>[] names = ["name"u8.ToArray()];
        var parser = new CommandLineOptionParser([
            ShellValue.FromText(argument), ShellValue.FromText("-na"),
            ShellValue.FromText("--"), ShellValue.FromText("-name")
        ], false);

        Assert.True(parser.TryRead(names, out var option));
        Assert.Equal(CommandLineOptionKind.LongOption, option.Kind);
        Assert.Equal(ShellValue.FromText("name"), option.LongName);
        Assert.Equal(hasValue, option.HasValue);
        Assert.Equal(ShellValue.FromText(value), option.Value);
        foreach (var expected in new byte[] { (byte)'n', (byte)'a' })
        {
            Assert.True(parser.TryRead(names, out var shortOption));
            Assert.Equal(CommandLineOptionKind.ShortOption, shortOption.Kind);
            Assert.Equal(expected, shortOption.ShortName);
        }
        Assert.True(parser.TryRead(names, out var operand));
        Assert.Equal(CommandLineOptionKind.Argument, operand.Kind);
        Assert.Equal(ShellValue.FromText("-name"), operand.ArgumentValue);
        Assert.False(parser.TryRead(names, out _));
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("C3A9")]
    [InlineData("000D0A5C")]
    public void ShortGroupsRetainAttachedValueBytes(string valueHex)
    {
        var bytes = Convert.FromHexString("2D616E" + valueHex);
        var parser = new CommandLineOptionParser([ShellValue.FromBytes(bytes)], false);

        Assert.True(parser.TryRead(out var first));
        Assert.Equal(CommandLineOptionKind.ShortOption, first.Kind);
        Assert.Equal((byte)'a', first.ShortName);
        Assert.True(parser.TryRead(out var second));
        Assert.Equal((byte)'n', second.ShortName);
        Assert.True(parser.TryReadShortGroupRemainder(out var remainder));
        Assert.Equal(bytes.AsMemory(3), remainder.Bytes);
        Assert.False(parser.TryRead(out _));
    }

    [Fact]
    public void ShortOptionsAreIndividualBytes()
    {
        var parser = new CommandLineOptionParser(
            [ShellValue.FromBytes(new byte[] { (byte)'-', 0xc3, 0xa9, 0xff })], false);

        foreach (var expected in new byte[] { 0xc3, 0xa9, 0xff })
        {
            Assert.True(parser.TryRead(out var item));
            Assert.Equal(CommandLineOptionKind.ShortOption, item.Kind);
            Assert.Equal(expected, item.ShortName);
        }
        Assert.False(parser.TryReadShortGroupRemainder(out _));
        Assert.False(parser.TryRead(out _));
    }

    [Theory]
    [InlineData(false, "2D2DFF3DC30D3D", 2)]
    [InlineData(true, "2DFF3DC30D3D", 1)]
    public void LongNamesAndValuesRetainTheirSlices(bool singleDash, string argumentHex, int prefixLength)
    {
        var bytes = Convert.FromHexString(argumentHex);
        var parser = new CommandLineOptionParser([ShellValue.FromBytes(bytes)], singleDash);

        Assert.True(parser.TryRead(out var item));
        Assert.Equal(CommandLineOptionKind.LongOption, item.Kind);
        Assert.Equal(bytes.AsMemory(prefixLength, 1), item.LongName.Bytes);
        Assert.True(item.HasValue);
        Assert.Equal(bytes.AsMemory(prefixLength + 2), item.Value.Bytes);
        Assert.False(parser.TryRead(out _));
    }

    [Theory]
    [InlineData("--name", false, "name", false)]
    [InlineData("--name=", false, "name", true)]
    [InlineData("--=", false, "", true)]
    [InlineData("-name", true, "name", false)]
    [InlineData("-name=", true, "name", true)]
    public void MissingAndEmptyLongValuesRemainDistinct(string argument, bool singleDash, string name, bool hasValue)
    {
        var parser = new CommandLineOptionParser([ShellValue.FromText(argument)], singleDash);

        Assert.True(parser.TryRead(out var item));
        Assert.Equal(CommandLineOptionKind.LongOption, item.Kind);
        Assert.Equal(ShellValue.FromText(name), item.LongName);
        Assert.Equal(hasValue, item.HasValue);
        Assert.True(item.Value.IsEmpty);
        Assert.False(parser.TryRead(out _));
    }

    [Fact]
    public void PositionalArgumentsDoNotStopOptionsButDoubleDashDoes()
    {
        ShellValue[] arguments =
        [
            ShellValue.Empty, ShellValue.FromText("-"), ShellValue.FromText("-a"),
            ShellValue.FromText("--"), ShellValue.FromText("--"),
            ShellValue.FromBytes(new byte[] { (byte)'-', 0xff })
        ];
        var parser = new CommandLineOptionParser(arguments, false);

        foreach (var index in new[] { 0, 1 })
        {
            Assert.True(parser.TryRead(out var item));
            Assert.Equal(CommandLineOptionKind.Argument, item.Kind);
            Assert.Equal(arguments[index], item.ArgumentValue);
        }
        Assert.True(parser.TryRead(out var option));
        Assert.Equal((byte)'a', option.ShortName);
        foreach (var index in new[] { 4, 5 })
        {
            Assert.True(parser.TryRead(out var item));
            Assert.Equal(CommandLineOptionKind.Argument, item.Kind);
            Assert.Equal(arguments[index], item.ArgumentValue);
        }
        Assert.False(parser.TryRead(out _));
    }

    [Fact]
    public void RawReadDiscardsGroupButDoesNotInterpretDoubleDash()
    {
        ShellValue[] arguments =
        [
            ShellValue.FromText("-abc"), ShellValue.FromText("--"),
            ShellValue.FromText("-d"), ShellValue.FromBytes(new byte[] { 0xff })
        ];
        var parser = new CommandLineOptionParser(arguments, false);

        Assert.True(parser.TryRead(out var first));
        Assert.Equal((byte)'a', first.ShortName);
        Assert.True(parser.TryReadRaw(out var raw));
        Assert.Equal(arguments[1], raw);
        Assert.False(parser.TryReadShortGroupRemainder(out _));
        Assert.True(parser.TryRead(out var next));
        Assert.Equal((byte)'d', next.ShortName);
        Assert.True(parser.TryReadRaw(out var operand));
        Assert.Equal(arguments[3].Bytes, operand.Bytes);
        Assert.False(parser.TryReadRaw(out var missing));
        Assert.True(missing.IsEmpty);
    }

    [Fact]
    public void RemainderConsumptionDoesNotConsumeNextArgument()
    {
        var parser = new CommandLineOptionParser(
            [ShellValue.FromText("-ab"), ShellValue.FromText("-c")], false);

        Assert.True(parser.TryRead(out _));
        Assert.True(parser.TryReadShortGroupRemainder(out var remainder));
        Assert.Equal(ShellValue.FromText("b"), remainder);
        Assert.False(parser.TryReadShortGroupRemainder(out _));
        Assert.True(parser.TryRead(out var item));
        Assert.Equal((byte)'c', item.ShortName);
        Assert.False(parser.TryReadRaw(out _));
        Assert.False(parser.TryRead(out _));
    }

    [Fact]
    public void FindModeKeepsSingleLetterOptionsShort()
    {
        var parser = new CommandLineOptionParser(
            [ShellValue.FromText("-L"), ShellValue.FromText("-name"), ShellValue.FromText("*.txt")], true);

        Assert.True(parser.TryRead(out var mode));
        Assert.Equal(CommandLineOptionKind.ShortOption, mode.Kind);
        Assert.Equal((byte)'L', mode.ShortName);
        Assert.True(parser.TryRead(out var predicate));
        Assert.Equal(CommandLineOptionKind.LongOption, predicate.Kind);
        Assert.Equal(ShellValue.FromText("name"), predicate.LongName);
        Assert.True(parser.TryReadRaw(out var pattern));
        Assert.Equal(ShellValue.FromText("*.txt"), pattern);
        Assert.False(parser.TryRead(out _));
    }
}

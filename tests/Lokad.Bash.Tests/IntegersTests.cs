using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class IntegersTests
{
    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("-0", true, 0)]
    [InlineData("0002", true, 2)]
    [InlineData("2147483647", true, int.MaxValue)]
    [InlineData("2147483648", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("-2147483649", false, 0)]
    [InlineData("+2", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("-", false, 0)]
    [InlineData(" 2", false, 0)]
    [InlineData("2 ", false, 0)]
    [InlineData("2x", false, 0)]
    [InlineData("\u0662", false, 0)]
    [InlineData("2\0", false, 0)]
    public void TryParseExitStatus_PreservesValidationForCharactersAndBytes(
        string input, bool expectedSuccess, int expectedValue)
    {
        Assert.Equal(expectedSuccess, Integers.TryParseExitStatus(input.AsSpan(), out var characters));
        Assert.Equal(expectedSuccess, Integers.TryParseExitStatus(
            System.Text.Encoding.UTF8.GetBytes(input).AsSpan(), out var bytes));
        Assert.Equal(expectedValue, characters);
        Assert.Equal(expectedValue, bytes);
    }

    [Theory]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void TryParseInt_ParsesBoundaries(string input, int expected)
    {
        Assert.True(Integers.TryParseInt(input.AsSpan(), out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("999999999999999999999999999999999999")]
    public void TryParseInt_RejectsOverflow(string input)
    {
        Assert.False(Integers.TryParseInt(input.AsSpan(), out _));
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(2147483648L, "2147483648")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void FormatNumber_Formats64BitRange(long input, string expected)
    {
        Assert.Equal(expected, Integers.FormatNumber(input));
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("+42", 42L)]
    public void TryParseNumber_ParsesCompleteUtf8Boundaries(string input, long expected)
    {
        Assert.True(Integers.TryParseNumber(System.Text.Encoding.ASCII.GetBytes(input), out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("0x10")]
    public void TryParseNumber_RejectsOverflowWhitespaceBasesAndTrailingBytes(string input)
    {
        Assert.False(Integers.TryParseNumber(System.Text.Encoding.ASCII.GetBytes(input), out _));
    }

    [Theory]
    [InlineData("1K", 1024L)]
    [InlineData("-2m", -2097152L)]
    [InlineData(" 3G ", 3221225472L)]
    public void TryParseHumanNumber_AppliesBinarySuffix(string input, long expected)
    {
        Assert.True(Integers.TryParseHumanNumber(System.Text.Encoding.ASCII.GetBytes(input), out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("9223372036854775807K")]
    [InlineData("-9223372036854775808G")]
    public void TryParseHumanNumber_RejectsScaledOverflow(string input)
    {
        Assert.False(Integers.TryParseHumanNumber(System.Text.Encoding.ASCII.GetBytes(input), out _));
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("+42", 42L)]
    public void TryParseNumber_ParsesCompleteCharacterBoundaries(string input, long expected)
    {
        Assert.True(Integers.TryParseNumber(input.AsSpan(), out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("0x10")]
    [InlineData("+-1")]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("1\0")]
    [InlineData("\u0661")]
    public void TryParseNumber_RejectsIncompleteOrNonDecimalTokens(string input)
    {
        Assert.False(Integers.TryParseNumber(input.AsSpan(), out var characters));
        Assert.False(Integers.TryParseNumber(System.Text.Encoding.UTF8.GetBytes(input), out var bytes));
        Assert.Equal(0, characters);
        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData("9007199254740991K", true, 9223372036854774784L)]
    [InlineData("-9007199254740992K", true, long.MinValue)]
    [InlineData("9007199254740992K", false, 0L)]
    [InlineData("-9007199254740993K", false, 0L)]
    public void TryParseHumanNumber_ChecksExactScaledBoundaries(string input, bool expectedSuccess, long expectedValue)
    {
        var success = Integers.TryParseHumanNumber(System.Text.Encoding.ASCII.GetBytes(input), out var value);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedValue, value);
    }
}

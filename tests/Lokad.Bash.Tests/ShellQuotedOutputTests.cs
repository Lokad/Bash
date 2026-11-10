using Lokad.Bash.Formatting;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ShellQuotedOutputTests
{
    [Theory]
    [InlineData("23", "5C23")]
    [InlineData("2323", "5C2323")]
    [InlineData("23612362", "5C23612362")]
    [InlineData("612362", "612362")]
    [InlineData("3A23", "3A23")]
    [InlineData("7E", "5C7E")]
    [InlineData("7E7E", "5C7E7E")]
    [InlineData("7E617E", "5C7E617E")]
    [InlineData("617E62", "617E62")]
    [InlineData("613D7E", "613D5C7E")]
    [InlineData("613A7E", "613A5C7E")]
    [InlineData("61207E62", "615C207E62")]
    [InlineData("615C7E", "615C5C7E")]
    [InlineData("C3A97E", "C3A97E")]
    [InlineData("C3A923", "C3A923")]
    [InlineData("3B237E", "5C3B237E")]
    [InlineData("3D7E3A7E", "3D5C7E3A5C7E")]
    [InlineData("FF7A", "24275C3337377A27")]
    [InlineData("61FF7A", "2427615C3337377A27")]
    [InlineData("F09F9982FF7A", "2427F09F99825C3337377A27")]
    [InlineData("FFC3A9", "24275C333737C3A927")]
    [InlineData("C3A9FF7A", "2427C3A95C3337377A27")]
    [InlineData("FF41FF42", "24275C333737415C3337374227")]
    [InlineData("C341A97A", "24275C333033415C3235317A27")]
    [InlineData("F09F787A", "24275C3336305C323337787A27")]
    [InlineData("EDA0807A", "24275C3335355C3234305C3230307A27")]
    [InlineData("F49080807A", "24275C3336345C3232305C3230305C3230307A27")]
    [InlineData("F8888080807A", "24275C3337305C3231305C3230305C3230305C3230307A27")]
    [InlineData("FC84808080807A", "24275C3337345C3230345C3230305C3230305C3230305C3230307A27")]
    [InlineData("FFF09F9982", "24275C333737F09F998227")]
    [InlineData("FF0A7A", "24275C3337375C6E7A27")]
    [InlineData("FF275C7A", "24275C3337375C275C5C7A27")]
    [InlineData("FF20247A", "24275C33373720247A27")]
    [InlineData("C2A07A", "C2A07A")]
    [InlineData("C2807A", "24275C3330325C3230307A27")]
    [InlineData("C2857A", "24275C3330325C3230357A27")]
    [InlineData("E280A87A", "24275C3334325C3230305C3235307A27")]
    [InlineData("E280A97A", "24275C3334325C3230305C3235317A27")]
    [InlineData("CDB87A", "24275C3331355C3237307A27")]
    [InlineData("C2AD7A", "C2AD7A")]
    [InlineData("E2808B7A", "E2808B7A")]
    [InlineData("F09F99827A", "F09F99827A")]
    [InlineData("C3A97A", "C3A97A")]
    [InlineData("617A", "617A")]
    [InlineData("F48FBFBF7A", "24275C3336345C3231375C3237375C3237377A27")]
    [InlineData("EFBFBF7A", "24275C3335375C3237375C3237377A27")]
    [InlineData("EE80807A", "EE80807A")]
    [InlineData("CC817A", "CC817A")]
    public void QuotedOutput_MatchesNativeByteRendering(string inputHex, string outputHex)
    {
        var input = Convert.FromHexString(inputHex);
        var expected = Convert.FromHexString(outputHex);
        var exact = BoundedByteMemoryBuilder.CreateUnaccounted(0, expected.Length);
        Assert.True(ShellArgumentEscaping.TryAppendQuoted(exact, input, ShellQuoteStyle.Backslash));
        Assert.Equal(expected, exact.ToMemory().ToArray());

        var display = ByteMemoryBuilder.CreateUnaccounted(0);
        Assert.True(ShellArgumentEscaping.TryAppendQuoted(display, input, ShellQuoteStyle.Backslash));
        Assert.Equal(expected, display.ToMemory().ToArray());

        var shortOutput = BoundedByteMemoryBuilder.CreateUnaccounted(0, expected.Length - 1);
        Assert.False(ShellArgumentEscaping.TryAppendQuoted(shortOutput, input, ShellQuoteStyle.Backslash));
        Assert.InRange(shortOutput.Length, 0, expected.Length - 1);
    }

    [Fact]
    public void QuotedOutput_SharesMixedByteRenderingAcrossStyles()
    {
        var input = Convert.FromHexString("FF41C3A9");
        var expected = Convert.FromHexString("24275C33373741C3A927");
        foreach (var style in Enum.GetValues<ShellQuoteStyle>())
        {
            var output = BoundedByteMemoryBuilder.CreateUnaccounted(0, expected.Length);
            Assert.True(ShellArgumentEscaping.TryAppendQuoted(output, input, style));
            Assert.Equal(expected, output.ToMemory().ToArray());
        }
    }

    [Fact]
    public void Printf_EmptyQuotedValue_AppliesPrecisionBeforeFinalOutputBudget()
    {
        var format = string.Concat(Enumerable.Repeat("%1048576s", 63)) + "%1048574s%.1q";
        var result = PrintfFormatting.Evaluate(ShellValue.FromText(format), [], DateTimeOffset.UnixEpoch, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.TextToolBytes));

        Assert.Empty(result.Diagnostics);
        Assert.Equal((64 * 1024 * 1024) - 1, result.Output.Length);
        Assert.Equal((byte)'\'', result.Output.Span[^1]);
    }

    [Fact]
    public void QuotedOutput_RespectsExactAndInsufficientByteBudgets()
    {
        (ShellQuoteStyle Style, string Input, string Expected)[] cases =
        [
            (ShellQuoteStyle.Backslash, "a b", "a\\ b"),
            (ShellQuoteStyle.SingleQuoted, "a'b", "'a'\\''b'"),
            (ShellQuoteStyle.DoubleQuoted, "a\"$`\\!", "\"a\\\"\\$\\`\\\\!\""),
            (ShellQuoteStyle.DoubleQuoted, "\r\n\t\u001b\u0001", "$'\\r\\n\\t\\E\\001'"),
            (ShellQuoteStyle.AssociativeKey, "@", "\"@\""),
            (ShellQuoteStyle.AssociativeKey, "a=b", "a=b"),
            (ShellQuoteStyle.AssociativeKey, "a:~", "\"a:~\""),
            (ShellQuoteStyle.AssignmentValue, "a=~", "'a=~'"),
            (ShellQuoteStyle.DoubleQuoted, "", "\"\"")
        ];
        foreach (var (style, input, expected) in cases)
        {
            var bytes = Utf8Text.Encode(input);
            var length = Utf8Text.Encode(expected).Length;
            var exact = BoundedByteMemoryBuilder.CreateUnaccounted(0, length);
            Assert.True(ShellArgumentEscaping.TryAppendQuoted(exact, bytes.Span, style));
            Assert.Equal(expected, Utf8Text.Decode(exact.ToMemory()));

            var display = ByteMemoryBuilder.CreateUnaccounted(0);
            Assert.True(ShellArgumentEscaping.TryAppendQuoted(display, bytes.Span, style));
            Assert.Equal(expected, Utf8Text.Decode(display.ToMemory()));

            var shortOutput = BoundedByteMemoryBuilder.CreateUnaccounted(0, length - 1);
            Assert.False(ShellArgumentEscaping.TryAppendQuoted(shortOutput, bytes.Span, style));
            Assert.InRange(shortOutput.Length, 0, length - 1);
        }
    }

    [Fact]
    public void IncompleteUtf8_UsesTheSharedOctalQuotingPath()
    {
        foreach (var style in Enum.GetValues<ShellQuoteStyle>())
        {
            var output = BoundedByteMemoryBuilder.CreateUnaccounted(0, 64);
            Assert.True(ShellArgumentEscaping.TryAppendQuoted(output, [0x61, 0xf0, 0x9f], style));
            Assert.Equal("$'a\\360\\237'", Utf8Text.Decode(output.ToMemory()));
        }
    }
}

using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteArithmeticTests
{
    // Linux Bash 5.2.21/C.UTF-8: status, output and diagnostic subject bytes; wording remains separate.
    [Theory]
    [InlineData("x=$'\\377'; let \"i=$x\"; printf 's=%s i=%s\\n' \"$?\" \"${i-unset}\"\n", "733D3120693D756E7365740A", 0, "693DFF")]
    [InlineData("i=0; let 'i=2' $'\\377'; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n", "733D3120693D320A", 0, "FF")]
    [InlineData("i=0; let $'i=2,\\377'; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n", "733D3120693D320A", 0, "693D322CFF")]
    [InlineData("i=0; let $'\\377' \"j=$((i+=1))\"; printf 's=%s i=%s j=%s\\n' \"$?\" \"$i\" \"${j-unset}\"\n", "733D3120693D31206A3D756E7365740A", 0, "FF")]
    [InlineData("x=$'\\377'; printf before; printf '%s' \"$((i=2,$x))\"; printf after\n", "6265666F7265", 1, "693D322CFF")]
    [InlineData("false && printf '%s' \"$(($'\\377'))\"; printf after\n", "6166746572", 0, "")]
    [InlineData("a=(one two); x=$'\\377'; printf before; printf '%s' \"${a[$x]}\"; printf after\n", "6265666F7265", 1, "FF")]
    [InlineData("a=(one two); x=$'\\377'; printf '%s' \"${a[i=2,$x]}\"; printf after\n", "", 1, "693D322CFF")]
    [InlineData("let $'i=2,\\u00e9'; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n", "733D3120693D320A", 0, "693D322CC3A9")]
    [InlineData("let $'i=2,3\\r'; printf 's=%s i=%s\\n' \"$?\" \"$i\"\n", "733D3120693D320A", 0, "693D322C330D")]
    [InlineData("x=$'\\377'; printf '%s' \"$((i=2,2**-1,$x))\"; printf after\n", "", 1, "693D322C322A2A2D312CFF")]
    [InlineData("x=$'\\377'; printf '%s' \"$((i=2,1/0,$x))\"; printf after\n", "", 1, "693D322C312F302CFF")]
    public async Task Evaluation_ArithmeticConsumersRetainBytesAndEffects(
        string script, string outputHex, int exitCode, string errorSubjectHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        var error = result.Host.StandardError.Content;
        if (errorSubjectHex.Length == 0)
            Assert.True(error.IsEmpty);
        else
            Assert.True(error.Span.StartsWith(Convert.FromHexString(errorSubjectHex + "3A20")));
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("C3")]
    [InlineData("C3A9")]
    public void TryEvaluate_RetainsInvalidOperandBytesAfterEarlierWrites(string operandHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append("x=30,"u8).Append(Convert.FromHexString(operandHex).AsSpan());
        var expression = ShellValue.FromBytes(source.ToMemory());
        var x = 0L;

        Assert.False(ArithmeticEvaluation.TryEvaluate(expression, _ => x, Assign, out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(ArithmeticErrorKind.InvalidExpression, error.Kind);
        Assert.Equal(expression, error.Subject);
        Assert.Equal(30, x);
        Assert.Equal(0, ArithmeticEvaluation.Evaluate(expression.Bytes.Span));

        ArithmeticError? Assign(string name, long value)
        {
            Assert.Equal("x", name);
            x = value;
            return null;
        }
    }

    [Theory]
    [InlineData("31FF")]
    [InlineData("31C3")]
    [InlineData("310D")]
    [InlineData("EFBC91")]
    public void TryParseDecimal_RejectsNonDigitsWithoutTextDecoding(string valueHex)
    {
        Assert.False(ArithmeticEvaluation.TryParseDecimal(Convert.FromHexString(valueHex), out var value));
        Assert.Equal(0, value);
    }
}

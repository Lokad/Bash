using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteArithmeticOperandTests
{
    // Linux Bash 5.2.21/C.UTF-8: raw stdin source; status/output and diagnostic byte fragments.
    [Theory]
    [InlineData("((i=2,", ")); printf 's=%s i=%s\\n' \"$?\" \"$i\"\n", "733D3120693D320A", 0, "693D322CFF3A20", "")]
    [InlineData("false && ((", ")); printf after\n", "6166746572", 0, "", "")]
    [InlineData("for ((i=0;0;i=", ")); do printf bad; done; printf after\n", "6166746572", 0, "", "")]
    [InlineData("for ((i=0;i<1;i=", ")); do printf body; done; printf ':s=%s i=%s' \"$?\" \"$i\"\n", "626F64793A733D3120693D30", 0, "693DFF3A20", "")]
    [InlineData("for ((i=0;i=", ";i++)); do printf bad; done; printf 's=%s i=%s' \"$?\" \"$i\"\n", "733D3120693D30", 0, "693DFF3A20", "")]
    [InlineData("for ((i=", ";0;i++)); do printf bad; done; printf 's=%s i=%s' \"$?\" \"${i-unset}\"\n", "733D3120693D756E736574", 0, "693DFF3A20", "")]
    [InlineData("((i=$(printf '%s' '", "'))); printf 's=%s i=%s' \"$?\" \"${i-unset}\"\n", "733D3120693D756E736574", 0, "693DFF3A20", "")]
    [InlineData("f(){ ((i=2,", ")); }; f; printf 's=%s i=%s' \"$?\" \"$i\"\n", "733D3120693D32", 0, "693D322CFF3A20", "")]
    [InlineData("((i=$(printf 'effect\\n' >&2; printf 2)+${missing:-", ")); printf after\n", "", 1, "247B6D697373696E673A2DFF", "6566666563740A")]
    [InlineData("false && [[ ", " ]]; printf after\n", "6166746572", 0, "", "")]
    [InlineData("for ((i=0;1;i=", ")); do printf body; break; done; printf ':s=%s i=%s' \"$?\" \"$i\"\n", "626F64793A733D3020693D30", 0, "", "")]
    [InlineData("((i=\"", "\")); printf 's=%s i=%s' \"$?\" \"${i-unset}\"\n", "733D3120693D756E736574", 0, "693DFF3A20", "")]
    [InlineData("((i=$'", "')); printf 's=%s i=%s' \"$?\" \"${i-unset}\"\n", "733D3120693D756E736574", 0, "693D27FF273A20", "")]
    public async Task Evaluation_RawCommandSourceRetainsBytesAndReachedEffects(
        string prefix, string suffix, string outputHex, int exitCode, string errorFragmentHex, string effectPrefixHex)
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(BuildSource(prefix, suffix), host, TimeSpan.FromSeconds(5));

        AssertResult(result, outputHex, exitCode, errorFragmentHex, effectPrefixHex);
    }

    // Values produced after parsing retain bytes under the existing arithmetic quote rules.
    [Theory]
    [InlineData("x=$'\\377'; ((i=$x)); printf 's=%s i=%s\\n' \"$?\" \"${i-unset}\"\n", "733D3120693D756E7365740A", 0, "693DFF3A20")]
    [InlineData("((i=$'\\377')); printf 's=%s i=%s\\n' \"$?\" \"${i-unset}\"\n", "733D3120693D756E7365740A", 0, "693D27FF273A20")]
    [InlineData("v=$'\\377'; for ((i=0;i<1;i=$v)); do printf body; done; printf ':s=%s i=%s' \"$?\" \"$i\"\n", "626F64793A733D3120693D30", 0, "693DFF3A20")]
    [InlineData("message=$'\\377'; ((i=${missing:?$message})); printf after\n", "", 1, "6D697373696E673A20FF")]
    public async Task Evaluation_PreparedOperandsRetainGeneratedBytes(
        string script, string outputHex, int exitCode, string errorFragmentHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        AssertResult(result, outputHex, exitCode, errorFragmentHex, "");
    }

    [Theory]
    [InlineData("((", "))", nameof(ArithmeticCommand))]
    [InlineData("for ((", ")); do :; done", nameof(ArithmeticForCommand))]
    [InlineData("[[ ", " ]]", nameof(ConditionalCommand))]
    public void Parser_CommandExpressionsRetainRawSource(string prefix, string suffix, string commandKind)
    {
        var command = Assert.Single(Parser.Parse(BuildSource(prefix, suffix)).Commands);

        Assert.Equal(commandKind, command.GetType().Name);
        var expression = command switch
        {
            ArithmeticCommand arithmetic => arithmetic.Expression,
            ArithmeticForCommand loop => loop.Expression,
            ConditionalCommand conditional => conditional.Expression,
            _ => throw new InvalidOperationException("Unexpected command kind.")
        };
        Assert.Equal(new byte[] { 0xff }, expression.Bytes.ToArray());
    }

    [Fact]
    public void LoopClauses_RetainRawUpdateAndCarriageReturn()
    {
        var command = Assert.IsType<ArithmeticForCommand>(
            Assert.Single(Parser.Parse(BuildSource("for ((i=0; \r0; ", " )); do :; done")).Commands));

        command.Split(out var init, out var condition, out var update);

        Assert.Equal("i=0"u8.ToArray(), init.Bytes.ToArray());
        Assert.Equal("\r0"u8.ToArray(), condition.Bytes.ToArray());
        Assert.Equal(new byte[] { 0xff }, update.Bytes.ToArray());
    }

    private static ReadOnlyMemory<byte> BuildSource(string prefix, string suffix)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        return source.ToMemory();
    }

    private static void AssertResult(
        EndToEndHarness.ExecutionResult result,
        string outputHex,
        int exitCode,
        string errorFragmentHex,
        string effectPrefixHex)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        var error = result.Host.StandardError.Content;
        if (errorFragmentHex.Length == 0)
            Assert.True(error.IsEmpty);
        else
            Assert.True(error.Span.IndexOf(Convert.FromHexString(errorFragmentHex)) >= 0);
        Assert.True(error.Span.StartsWith(Convert.FromHexString(effectPrefixHex)));
    }
}

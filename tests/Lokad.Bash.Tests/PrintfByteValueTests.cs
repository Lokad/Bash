using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class PrintfByteValueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VariableNulTruncationAdmitsTheCopyAndRefundsDiscardedOutput(bool enoughMemory)
    {
        const int OutputBytes = 1024 * 1024;
        const int PrefixBytes = OutputBytes - 1;
        var value = System.Text.Encoding.UTF8.GetBytes(new string(' ', PrefixBytes - 1) + "x");
        var governor = new MemoryGovernor((enoughMemory ? 5L : 2L) * OutputBytes);
        using var memory = governor.CreateScope();
        var evaluation = new Evaluation.Evaluation(Parser.Parse($"printf -v saved '%{PrefixBytes}s\\0' x"));
        evaluation.Memory.Budget = memory;

        var step = await evaluation.GetNextStep();
        if (enoughMemory)
        {
            Assert.Null(step);
            Assert.Equal(0, evaluation.LastExitCode);
            Assert.True(evaluation.Variables["saved"].Bytes.Span.SequenceEqual(value));
            Assert.InRange(governor.ReservedBytes, 2L * PrefixBytes, 2L * PrefixBytes + 4096);
            Assert.True(governor.PeakBytes >= OutputBytes + PrefixBytes);
        }
        else
        {
            var diagnostic = Assert.IsType<WriteOperation>(step);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Equal("printf: shared memory budget exhausted\n", Utf8Text.Decode(diagnostic.Content));
            diagnostic.SetResult(DescriptorWriteResult.Open);
            Assert.Null(await evaluation.GetNextStep());
            Assert.Equal(1, evaluation.LastExitCode);
            Assert.False(evaluation.Variables.ContainsKey("saved"));
            Assert.InRange(governor.ReservedBytes, 1, 4096);
            Assert.True(governor.PeakBytes >= OutputBytes);
        }
    }

    [Theory]
    [InlineData("printf $'\\377%s\\251' ok\n", "FF6F6BA9")]
    [InlineData("printf '<%4s><%-4.1s>' $'\\377z' $'\\303\\251'\n", "3C2020FF7A3E3CC32020203E")]
    [InlineData("printf '<%c><%c>' $'\\377z' $'\\uD800'\n", "3CFF3E3CED3E")]
    [InlineData("printf '%b' $'\\377\\\\x41\\\\q\\251'\n", "FF415C71A9")]
    [InlineData("printf 'before%bafter' $'\\377\\\\c\\251' ignored\n", "6265666F7265FF")]
    [InlineData("printf $'\\377\\\\x41\\\\q\\251'\n", "FF415C71A9")]
    [InlineData("printf '%b' $'\\377\\\\U00200000z'\n", "FFF8888080807A")]
    [InlineData("printf '%.1Q' $'\\303\\251z'\n", "24275C33303327")]
    [InlineData("printf $'\\377%s' a b\n", "FF61FF62")]
    [InlineData("printf ok $'\\377'\n", "6F6B")]
    [InlineData("printf '%s' \"$(printf '\\377')\"\n", "FF")]
    [InlineData("printf '%s' $'\\377'\n", "FF")]
    [InlineData("printf '%s' $'\\uD800'\n", "EDA080")]
    [InlineData("printf '%s' $'\\U00200000'\n", "F888808080")]
    [InlineData("printf '%s' $'\\cλ'\n", "0EBB")]
    [InlineData("printf '%s' $'\\c🙂'\n", "109F9982")]
    [InlineData("printf '%q' $'\\377z'\n", "24275C3337377A27")]
    [InlineData("printf '%q' $'a\\377z'\n", "2427615C3337377A27")]
    [InlineData("printf '%q' $'\\U0001f642\\377z'\n", "2427F09F99825C3337377A27")]
    public async Task Evaluation_PreservesFormatAndArgumentBytes(string script, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task InvalidConversion_PreservesTheDiagnosticByte()
    {
        var result = await EndToEndHarness.RunScriptAsync("printf $'%\\377'\n", EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString("7072696E74663A2060FF273A20696E76616C696420666F726D6174206368617261637465720A"),
            result.Host.StandardError.Content.ToArray());
    }
}

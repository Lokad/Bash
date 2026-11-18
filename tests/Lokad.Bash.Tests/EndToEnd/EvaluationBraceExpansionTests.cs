using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationBraceExpansionTests
{
    [Theory]
    [InlineData("{a,{b,c}}{1..2}", "a1 a2 b1 b2 c1 c2")]
    [InlineData("{a,b}\"x\"{1..2}", "ax1 ax2 bx1 bx2")]
    [InlineData("'{1..1000000000}'", "{1..1000000000}")]
    [InlineData("{01..03}", "01 02 03")]
    [InlineData("{-02..01}", "-02 -01 000 001")]
    [InlineData("{-02..0001}", "-002 -001 0000 0001")]
    [InlineData("{+123..01..122}", "0123 0001")]
    [InlineData("{+01..+3}", "1 2 3")]
    [InlineData("{-00..2}", "000 001 002")]
    [InlineData("{001..7..-2}", "001 003 005 007")]
    [InlineData("{5..1..2}", "5 3 1")]
    [InlineData("{3..1..-0}", "3 2 1")]
    [InlineData("{1..3..}", "{1..3..}")]
    [InlineData("{1..3..9223372036854775808}", "{1..3..9223372036854775808}")]
    [InlineData("{1..3..-9223372036854775808}", "{1..3..-9223372036854775808}")]
    [InlineData("{3..1..-9223372036854775808}", "3")]
    [InlineData("{-9223372036854775808..-9223372036854775806}", "-9223372036854775808 -9223372036854775807 -9223372036854775806")]
    [InlineData("{9223372036854775806..9223372036854775807}", "9223372036854775806 9223372036854775807")]
    [InlineData("{09223372036854775806..9223372036854775807}", "-0000000000000000002 -0000000000000000001")]
    [InlineData("{-1..9223372036854775806..9223372036854775807}", "{-1..9223372036854775806..9223372036854775807}")]
    [InlineData("{-9223372036854775808..9223372036854775807..9223372036854775807}", "{-9223372036854775808..9223372036854775807..9223372036854775807}")]
    [InlineData("{2147483646..2147483647}", "2147483646 2147483647")]
    [InlineData("{-2147483647..-2147483648}", "-2147483647 -2147483648")]
    [InlineData("{2147483647..2147483647}", "2147483647")]
    [InlineData("{-2147483648..-2147483648}", "-2147483648")]
    [InlineData("{-2147483648..2147483647..2147483647}", "-2147483648 -1 2147483646")]
    [InlineData("{2147483647..-2147483648..-2147483648}", "2147483647 -1")]
    [InlineData("{a..z..2147483647}", "a")]
    [InlineData("{z..a..-2147483648}", "z")]
    public async Task ExpansionPreservesOrderQuotingAndIntegerEndpoints(string word, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("echo " + word);

        Assert.Equal(expected + "\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("{a..e..-2}", "<a><c><e>")]
    [InlineData("{e..a..2}", "<e><c><a>")]
    [InlineData("{a..1} {@..C} {é..ê}", "<{a..1}><{@..C}><{é..ê}>")]
    [InlineData("{Z..a}", "<Z><[><><]><^><_><`><a>")]
    [InlineData("{a..Z}", "<a><`><_><^><]><><[><Z>")]
    [InlineData("p{Y..a..3}$suffix", "<pYend><p$suffix><p_end>")]
    [InlineData("{,x} tail", "<x><tail>")]
    public async Task CharacterRangesPreserveExactFields(string words, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("suffix=end; printf '<%s>' " + words);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task GeneratedBacktickWithUnquotedSuffixDiagnosesIncompleteSubstitution()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' prefix{Z..a}suffix");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("bad substitution: no closing \"`\" in `suffix\n", result.StdErr);
    }

    [Fact]
    public async Task GeneratedSubstitutionFailureDiscardsItsStreamingCommandUnit()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "printf prior >first\nprintf '%s' p{Z..a}s >body\nprintf tail >after", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("bad substitution: no closing \"`\" in `s\n", host.StandardError.TextContent);
        Assert.Equal("prior", host.ReadFile("/first"));
        Assert.Equal("tail", host.ReadFile("/after"));
        Assert.False(host.FileExists("/body"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task WideRangeRespectsMemoryAllowanceBeforePublishingFields()
    {
        var governor = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync(
            "printf prior\nprintf '<%s>' {2147483648..2148483648}\nprintf ':%s:after' \"$?\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("prior:1:after", host.StandardOutput.TextContent);
        Assert.Equal("shell: shared memory budget exhausted\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task NumericPaddingCanExceedTheFormerStackBuffer()
    {
        var zeros = new string('0', 40);
        var result = await EvaluationTestHelper.RunAsync("echo {" + zeros + "1..2}");

        Assert.Equal(zeros + "1 " + zeros + "2\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RepeatedSingletonExpansionCannotExhaustTheStack()
    {
        var word = string.Concat(Enumerable.Repeat("{1..1}", 300));
        var result = await EvaluationTestHelper.RunAsync("echo " + word);

        Assert.Equal(new string('1', 300) + "\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ExpansionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BraceExpansion.Expand(ShellSource.FromText("{1..1000}"),
                new EvaluationMemory(new MemoryGovernor(TestMemoryBudget.EvaluationBytes)), cancellation.Token));
    }
}

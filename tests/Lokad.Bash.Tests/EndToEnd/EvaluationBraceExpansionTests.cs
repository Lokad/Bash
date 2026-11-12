using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationBraceExpansionTests
{
    [Theory]
    [InlineData("{a,{b,c}}{1..2}", "a1 a2 b1 b2 c1 c2")]
    [InlineData("{a,b}\"x\"{1..2}", "ax1 ax2 bx1 bx2")]
    [InlineData("'{1..1000000000}'", "{1..1000000000}")]
    [InlineData("{01..03}", "01 02 03")]
    [InlineData("{-02..01}", "-02 -01 00 01")]
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
            BraceExpansion.Expand(ShellSource.FromText("{1..1000}"), new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes)), cancellation.Token));
    }
}

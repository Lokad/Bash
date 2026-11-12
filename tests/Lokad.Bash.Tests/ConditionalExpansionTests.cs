using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ConditionalExpansionTests
{
    [Fact]
    public async Task RawInvalidUtf8SourceOperandsRemainByteValues()
    {
        var source = "[[ "u8.ToArray().Concat(new byte[] { 255 }).Concat(" == "u8.ToArray())
            .Concat(new byte[] { 255 }).Concat(" ]]"u8.ToArray()).ToArray();
        var evaluation = new Evaluation.Evaluation(Parser.Parse(source));
        await using var host = new TestHost();
        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("[[ 1/0 -eq 0 ]]", "1")]
    [InlineData("[[ ! 1/0 -eq 0 ]]", "0")]
    [InlineData("[[ 1/0 -eq 0 || x ]]", "0")]
    [InlineData("[[ x && 1/0 -eq 0 ]]", "1")]
    public async Task ArithmeticErrorsRemainConditionalFalseResults(string condition, string status)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(condition + "; printf '%s' \"$?\"", CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(status, host.StandardOutput.TextContent);
        Assert.Contains("division by 0", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("[[ x =~ * ]]", "2", "")]
    [InlineData("[[ ! x =~ * ]]", "0", "")]
    [InlineData("[[ x =~ * || $(printf side >&2) ]]", "1", "side")]
    [InlineData("[[ x =~ * && $(printf side >&2) ]]", "2", "")]
    public async Task RegexErrorStatusParticipatesInConditionalLogic(string condition, string status, string effects)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(condition + "; printf '%s' \"$?\"", CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(status, host.StandardOutput.TextContent);
        Assert.Equal(effects, host.StandardError.TextContent);
    }

    [Fact]
    public async Task LongLogicalChainsUseIterativeEvaluation()
    {
        await using var host = new TestHost();
        var condition = string.Join(" && ", Enumerable.Repeat("x", 10000));
        var result = await host.RunAsync("[[ " + condition + " ]]", CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }
}

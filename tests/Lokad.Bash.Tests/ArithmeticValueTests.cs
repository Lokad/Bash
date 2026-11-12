using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticValueTests
{
    [Theory]
    [InlineData("x=x")]
    [InlineData("x=y; y=x")]
    [InlineData("a[0]='a[0]'; x='a[0]'")]
    public async Task CyclicValueExpressionsReturnAnErrorWithoutEscapingExceptions(string setup)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(setup + "; printf '%s' \"$((x))\"; printf unexpected", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("arithmetic nesting limit exceeded", host.StandardError.TextContent);
        Assert.Single(host.StandardError.TextContent.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("x='$y+1'")]
    [InlineData("x='\"3\"'")]
    [InlineData("x=08")]
    [InlineData("x=0xZZ")]
    [InlineData("x=$'\\377'")]
    public async Task InvalidValueExpressionsStopBeforeLaterEffects(string setup)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(setup + "; printf '%s' \"$((x))\"; printf unexpected", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.NotEmpty(host.StandardError.Content.ToArray());
        Assert.DoesNotContain("unexpected", host.StandardError.TextContent);
    }

    [Fact]
    public async Task EarlierValueAssignmentsSurviveALaterArithmeticFailure()
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse("x='a=1,b=1/0'; printf '%s' \"$((x))\"; later=yes"));
        await using var host = new TestHost();

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(ShellValue.FromText("1"), evaluation.Variables["a"]);
        Assert.False(evaluation.Variables.ContainsKey("later"));
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("division by 0", host.StandardError.TextContent);
    }
}

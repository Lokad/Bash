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
        Assert.Contains("expression recursion level exceeded (error token is \"", host.StandardError.TextContent);
        Assert.Single(host.StandardError.TextContent.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("x=x", "let 'x'", "let: x", "x")]
    [InlineData("x='x+1'", "((x))", "((: x+1", "x+1")]
    [InlineData("a=('a[0]' 5); x='a[0]'", "declare 'a[x]=new'", "a[0]", "a[0]")]
    public async Task RecursionReportsTheStoredExpression(string setup, string command, string subject, string token)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"{setup}\n{command}\nprintf '<%s>' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1>", host.StandardOutput.TextContent);
        Assert.Equal($"line 2: {subject}: expression recursion level exceeded (error token is \"{token}\")\n",
            host.StandardError.TextContent);
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

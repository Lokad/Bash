using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class DescriptorOperandTests
{
    [Theory]
    [InlineData(":", "1")]
    [InlineData("cat", "0")]
    public async Task ValidFilenameFallbackExpandsItsTargetOnce(string command, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"i=0; {command} >&\"out$((i++))\"; printf '%s' \"$i\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.ReadFileBytes("/out0"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/out1"));
    }

    [Theory]
    [InlineData("<&bad")]
    [InlineData("2>&bad")]
    [InlineData("1<&out")]
    [InlineData("3>&'-suffix'")]
    [InlineData("0<&'3-'")]
    [InlineData("0<&\"${fd}\"")]
    public async Task InvalidDescriptorWordsDiagnoseAndAllowContinuation(string redirection)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"fd=3-; printf before; : {redirection}; printf after", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("beforeafter", host.StandardOutput.TextContent);
        Assert.Contains("ambiguous redirect", host.StandardError.TextContent);
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/out"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/bad"));
    }

    [Theory]
    [InlineData("2>&\"out$((i++))\"")]
    [InlineData("<&\"out$((i++))\"")]
    [InlineData("1>&out$((i++))-")]
    public async Task InvalidWordDiagnosticsRetainTheirNativeExpansionEffects(string redirection)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"i=0; : {redirection}; printf '%s' \"$i\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2", host.StandardOutput.TextContent);
        Assert.Contains("out1: ambiguous redirect", host.StandardError.TextContent);
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/out0"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/out1"));
    }

    [Theory]
    [InlineData("3>&$fd")]
    [InlineData("3<&\"$fd\"")]
    [InlineData("3>&'-'")]
    public async Task ExpandedAndQuotedCloseOperandsDoNotRequireNumericParsing(string redirection)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"fd=-; : {redirection}; printf '%s' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("3-")]
    [InlineData("${fd}-")]
    [InlineData("\"$fd\"-")]
    [InlineData("3\\-")]
    public async Task SourceFinalMoveSuffixWritesTheDestinationInsteadOfCreatingADashFilename(string operand)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"exec 3>out; fd=3; printf x 1>&{operand}; exec 3>&-", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("x", host.ReadFile("/out"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/3-"));
    }

    [Theory]
    [InlineData("\"3-\"")]
    [InlineData("'3-'")]
    [InlineData("3'-'")]
    [InlineData("$fd")]
    public async Task QuotedOrExpansionProducedDashWordsRemainFilenameOperands(string operand)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"exec 3>out; fd=3-; printf x 1>&{operand}; exec 3>&-", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("x", host.ReadFile("/3-"));
        Assert.Empty(host.ReadFileBytes("/out"));
    }

    [Fact]
    public async Task MovingAHereInputPreservesBytesAndRestoresTheSourceAfterTheCommand()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("exec 3<<<seed; IFS= read -r value 0<&3-; printf '%s' \"$value\"; : <&3; printf '%s' \"$?\"; exec 3<&-", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("seed0", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task MoveClosesTheSourceWithinTheCommandAndRestoresItAfterward()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("exec 3>out; { printf x; printf y >&3; } 1>&3-; printf z >&3; exec 3>&-", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("xz", host.ReadFile("/out"));
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("Bad file descriptor", host.StandardError.TextContent);
    }

    [Fact]
    public async Task ASourceLeadingCloseDashLeavesFollowingTextAsAnArgument()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("printf '<%s>' 3>&-suffix", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<suffix>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public void CloseSuffixRetainsAdjacencyWithTheNextRedirection()
    {
        var command = Assert.IsType<SimpleCommand>(Assert.Single(Parser.Parse(": 3>&-4>out").Commands));
        Assert.Single(command.Words);
        Assert.Equal(2, command.Redirections.Count);
        Assert.Equal(RedirectionKind.CloseOutput, command.Redirections[0].Kind);
        Assert.Equal(3, command.Redirections[0].FileDescriptor);
        Assert.Equal(RedirectionKind.Output, command.Redirections[1].Kind);
        Assert.Equal(4, command.Redirections[1].FileDescriptor);
    }

    [Fact]
    public async Task OverflowingDescriptorNumbersNeverBecomeFilenames()
    {
        const string Number = "999999999999999999999999999999";
        await using var host = new TestHost();
        var result = await host.RunAsync($"printf before; : >&{Number}; printf after", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("beforeafter", host.StandardOutput.TextContent);
        Assert.Contains("Bad file descriptor", host.StandardError.TextContent);
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/" + Number));
    }

    [Fact]
    public async Task ReusedMoveSyntaxResolvesCurrentBindings()
    {
        var script = Parser.Parse("exec 3>out3; exec 4>out4; fd=$1; printf x 1>&${fd}-; exec 3>&-; exec 4>&-");
        foreach (var number in new[] { "3", "4" })
        {
            await using var host = new TestHost();
            var evaluation = new Evaluation.Evaluation(script);
            evaluation.ReplacePositionalParameters([ShellValue.FromText(number)]);
            var result = await new Engine().RunAsync(evaluation, host);

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.Equal(number == "3" ? "x" : "", host.ReadFile("/out3"));
            Assert.Equal(number == "4" ? "x" : "", host.ReadFile("/out4"));
        }
    }
}

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
    [InlineData("$(f)", "bad", "x", "0", "$(f)")]
    [InlineData("\"$(f)\"", "bad", "x", "0", "$(f)")]
    [InlineData("\"pre`f`$((i++))\"", "bad", "x", "2", "pre`f`1")]
    [InlineData("${none:-$(f)}", "bad", "xx", "0", "bad")]
    [InlineData("out$(( $(f) + i++ ))", "2", "xx", "2", "out3")]
    public async Task InvalidDescriptorDiagnosticsRetainSubstitutionEffects(
        string operand, string captured, string calls, string counter, string reported)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            $"f(){{ printf x >>calls; printf '%s' {captured}; }}; i=0; : 2>error 3>&{operand}; printf '<%s><%s>' \"$?\" \"$i\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<1><{counter}>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(calls, host.ReadFile("/calls"));
        Assert.Equal(reported + ": ambiguous redirect\n", host.ReadFile("/error"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(">&")]
    [InlineData("<&")]
    public async Task AmbiguousDuplicationReportsTheOperand(string redirectionOperator)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("x='1 2'; : 2>error 3" + redirectionOperator + "$x", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("$x: ambiguous redirect\n", host.ReadFile("/error"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("3>&\"$x\"", "3")]
    [InlineData("3>&\"$x\"-", "3")]
    [InlineData("1>&\"$x\"", "\"$x\"")]
    [InlineData("0<&\"\"", "\"\"")]
    public async Task EmptyQuotedDescriptorFieldFails(string redirection, string diagnosticOperand)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("x=; : " + redirection, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(diagnosticOperand + ": Bad file descriptor\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task MoveWithMultipleFieldsClosesAndRestoresTheDestination()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "exec 3>out; x='1 2'; { printf inner >&3; printf '<%s>' \"$?\"; } 2>error 3>&$x-; printf outer >&3",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("3: Bad file descriptor\n", host.ReadFile("/error"));
        Assert.Equal("outer", host.ReadFile("/out"));
        Assert.Equal(0, host.OpenFileCount);
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

    [Theory]
    [InlineData(": 4>&3-", "alias", 1, "3: Bad file descriptor\n")]
    [InlineData(": 4>&3- 3>&4", "alias", 1, "3: Bad file descriptor\n")]
    [InlineData(": 3>&5 4>&3-", "afteralias", 0, "")]
    [InlineData(": 3>&3 4>&3-", "alias", 1, "3: Bad file descriptor\n")]
    [InlineData("exec 4>&5; : 4>&3-", "afteralias", 0, "")]
    [InlineData("exec 3>&3-", "afteralias", 0, "")]
    [InlineData(": 4>&3- 4>&99", "alias", 1, "99: Bad file descriptor\n3: Bad file descriptor\n")]
    [InlineData("( : ) 4>&3-", "afteralias", 0, "")]
    [InlineData("cat /dev/null 4>&3-", "afteralias", 0, "")]
    public async Task MoveRestoresOnlySavedBindings(string command, string file, int writeStatus, string error)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "exec 3>out 5>&3; " + command + "; printf after >&3; printf '%s' \"$?\"; printf alias >&5",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(writeStatus.ToString(System.Globalization.CultureInfo.InvariantCulture), host.StandardOutput.TextContent);
        Assert.Equal(error, host.StandardError.TextContent);
        Assert.Equal(file, host.ReadFile("/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task MoveConsumesTheEnclosingAliasEvenWhenItUsesTheSameHandle()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "exec 3>out 5>&3; { : 4>&3-; printf inside >&3; } 3>&5; printf after >&3",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", host.StandardOutput.TextContent);
        Assert.Equal("3: Bad file descriptor\n", host.StandardError.TextContent);
        Assert.Equal("after", host.ReadFile("/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task MoveConsumesAnInputBindingWhileItsDestinationReadsTheSameBytes()
    {
        await using var host = new TestHost();
        host.AddFile("/in", "first\nsecond\n");
        var result = await host.RunAsync(
            "exec 3<in; { read -r -u4 a; printf '%s;' \"$a\"; } 4<&3-; read -r -u3 b; printf '%s:%s' \"$?\" \"$b\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("first;1:", host.StandardOutput.TextContent);
        Assert.Equal("line 1: read: 3: invalid file descriptor: Bad file descriptor\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
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

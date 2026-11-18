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
    [InlineData("declare absent; empty=; [[ -v empty && ! -v absent ]]")]
    [InlineData("a=([3]=); [[ -v a[-1] && -v a[@] && ! -v a ]]")]
    [InlineData("declare -A a=([k]=); [[ -v a[k] && ! -v a[@] ]]")]
    [InlineData("declare -A a; k=$'\\377'; a[$k]=; [[ -v \"a[$k]\" ]]")]
    [InlineData("declare -A a; a['$v']=; k='$v'; v=other; [[ -v \"a[$k]\" && ! -v \"a['$k']\" ]]")]
    [InlineData("unset target; declare -n ref=target; declare -n unassigned; [[ -R ref && ! -R unassigned && ! -v ref ]]")]
    [InlineData("set -- ''; [[ -v 1 && ! -v 2 ]] && [ -v 0 ] && test -v = -v")]
    public async Task VariablePredicatesPreserveAssignmentPresence(string script)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("[[ yes || -v a[i++] ]]", "0 3")]
    [InlineData("test yes -o -v 'a[i++]'", "0 4")]
    [InlineData("[ '' -a -v 'a[i++]' ]", "1 4")]
    [InlineData("i=0; i=3 test -v 'a[i++]'", "0 4")]
    [InlineData("[[ -v a[$((i++))] ]]", "0 4")]
    public async Task VariablePredicatesEvaluateSelectorsInTheCallerGrammar(string predicate, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"a=([3]=); i=3; {predicate}; printf '%s %s' \"$?\" \"$i\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("test -e marker -a -v 'a[$(printf 3; : >marker)]'", "1")]
    [InlineData("test -v 'a[$(printf 3; : >marker)]' -a -e marker", "0")]
    public async Task TestEvaluatesFileAndVariablePredicatesInArgumentOrder(string predicate, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"a=([3]=); {predicate}; printf '%s' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Equal(string.Empty, host.ReadFile("/marker"));
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("[[ -v 'a[1+]' ]]")]
    [InlineData("test -v 'a[1+]'")]
    public async Task InvalidVariablePredicateArithmeticEndsTheCommandUnit(string predicate)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"a=([3]=); {predicate}; printf tail", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("arithmetic", host.StandardError.TextContent);
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

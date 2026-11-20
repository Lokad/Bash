using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class RegexCaptureTests
{
    [Theory]
    [InlineData(false, 136 * 1024)]
    [InlineData(true, 47 * 1024)]
    public async Task PatternPreparationFitsWithoutUnusedScratch(bool quotedAndClass, int allowance)
    {
        var source = quotedAndClass
            ? "set -- '" + new string('a', 1012) + ".' '[[:alpha:]]'; [[ ! % =~ \"$1\"$2 ]]"
            : "set -- '" + new string('a', 4096) + "'; [[ ! % =~ $1 ]]";
        var evaluation = new Eval(Parser.Parse(source));
        var governor = new MemoryGovernor(allowance);
        await using var host = new TestHost { MemoryBudget = governor };
        using var owner = governor.CreateScope();

        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(host.StandardError.Content.IsEmpty);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(64 * 1024, false)]
    [InlineData(2 * 1024 * 1024, true)]
    public async Task CaptureOwnershipIsAdmittedBeforeReplacingEarlierResults(long limit, bool fits)
    {
        var governor = new MemoryGovernor(limit);
        await using var host = new TestHost { MemoryBudget = governor };
        var pattern = new string('(', 100) + "a+" + new string(')', 100);
        var script = "[[ old =~ (o) ]]; value='" + new string('a', 1024) + "'; [[ $value =~ " + pattern + " ]]; "
            + "printf '%s:%s:<%s>' \"$?\" \"${#BASH_REMATCH[@]}\" \"${BASH_REMATCH[1]}\"";
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(fits ? "0:101:<" + new string('a', 1024) + ">" : "1:2:<o>", host.StandardOutput.TextContent);
        if (fits) Assert.Empty(host.StandardError.Content.ToArray());
        else Assert.Contains("memory", host.StandardError.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("[[ '😀x' =~ (😀)(.) ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"", "<😀x><😀><x>")]
    [InlineData("prefix='\\'; tail='([[:digit:]]+)'; [[ '\\42' =~ \"$prefix\"$tail ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"", "<\\42><42>")]
    public async Task CapturesPreserveSupplementaryValuesAndQuotedPatternBoundaries(string script, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[:unknown:]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[:ascii:]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[:word:]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[.x.]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ '😀' =~ . ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    public async Task UnsupportedOrInvalidMatchingPreservesPriorCaptures(string script)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2:<a a>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("alnum", "a1", "!")]
    [InlineData("alpha", "aZ", "1")]
    [InlineData("blank", " \t", "\n")]
    [InlineData("cntrl", "\r\n", " ")]
    [InlineData("digit", "19", "a")]
    [InlineData("graph", "a!", " ")]
    [InlineData("lower", "az", "A")]
    [InlineData("print", " a!", "\n")]
    [InlineData("punct", "![]", "a")]
    [InlineData("space", " \t\n", "a")]
    [InlineData("upper", "AZ", "a")]
    [InlineData("xdigit", "09aF", "g")]
    public async Task PosixClassNamesUseDeclaredCCharacterMembership(string name, string yes, string no)
    {
        await using var host = new TestHost();
        var script = "yes='" + yes + "'; no='" + no + "'; pattern='^[[:" + name + ":]]+$'; "
            + "[[ $yes =~ $pattern ]]; printf '%s:' \"$?\"; [[ $no =~ $pattern ]]; printf '%s' \"$?\"";
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0:1", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }
}

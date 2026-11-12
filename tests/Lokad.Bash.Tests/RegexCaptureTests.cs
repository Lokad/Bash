using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class RegexCaptureTests
{
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
    [InlineData("[[ abc123 =~ ([a-z]+)([0-9]+) ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"", "<abc123><abc><123>")]
    [InlineData("[[ a =~ (a)(b)? ]]; printf '%s:<%s>' \"${#BASH_REMATCH[@]}\" \"${BASH_REMATCH[2]-unset}\"", "3:<>")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ (y) ]]; declare -p BASH_REMATCH", "declare -a BASH_REMATCH=()\n")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ * ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"", "2:<a a>")]
    [InlineData("[[ abc =~ (a) ]]; [[ x || y =~ (y) ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"", "0:<a a>")]
    [InlineData("[[ ! abc =~ (a) ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"", "1:<a a>")]
    [InlineData("f(){ local BASH_REMATCH=(local); [[ ab =~ (a)(b) ]]; printf 'local:<%s>' \"${BASH_REMATCH[*]}\"; }; f; printf ':global:<%s>' \"${BASH_REMATCH[*]}\"", "local:<local>:global:<ab a b>")]
    [InlineData("readonly BASH_REMATCH; [[ ab =~ (a) ]]; declare -p BASH_REMATCH", "declare -a BASH_REMATCH=([0]=\"a\" [1]=\"a\")\n")]
    [InlineData("export BASH_REMATCH=old; [[ ab =~ (a) ]]; declare -p BASH_REMATCH", "declare -a BASH_REMATCH=([0]=\"a\" [1]=\"a\")\n")]
    [InlineData("declare -A BASH_REMATCH=([key]=value); [[ ab =~ (a)(b) ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"", "<ab><a><b>")]
    [InlineData("declare -n BASH_REMATCH=target; [[ ab =~ (a)(b) ]]; printf '<%s>:<%s>' \"${BASH_REMATCH[*]}\" \"${target-unset}\"", "<ab a b>:<unset>")]
    [InlineData("[[ '😀x' =~ (😀)(.) ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"", "<😀x><😀><x>")]
    public async Task CapturesPreserveValuesAndGlobalBindingRules(string script, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[:unknown:]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
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

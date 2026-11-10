using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class PrintfCountBindingTests
{
    // Bash 5.2.37, LC_ALL=C: count writes update both the caller and later selector reads.
    [Theory]
    [InlineData("i=9; i=0 printf -v 'a[i]' 'abc%n' i; printf '<%s>' \"$i\" \"${!a[@]}\" \"${a[@]}\"", "<3><3><abc>")]
    [InlineData("a=(old keep); declare -n ref='a[i++]'; i=9; i=0 printf 'abc%n%n' i ref; printf '<%s>' \"$i\" \"${a[@]}\"", "abc<4><old><keep><3>")]
    [InlineData("i=9; i=0 printf -v 'a[i]' abc; printf '<%s>' \"$i\" \"${!a[@]}\"", "<9><0>")]
    [InlineData("a=(0 keep); f(){ local -n r='a[2]'; a=0 printf -v 'b[a++]' 'x%n' r; }; f; printf '<%s>' \"${a[1]-missing}\" \"${a[@]}\"", "<keep><1><keep><1>")]
    [InlineData("a=(9 keep); f(){ local -n r='a[2]'; a=0 printf -v 'b[${a[3]:=4}]' 'x%n' r; }; f; printf '<%s>' \"${a[@]}\"", "<9><keep><1><4>")]
    [InlineData("a=(9 keep); a=0 printf -v 'b[${a[3]:=4}]' x; printf '<%s>' \"${a[@]}\"", "<9><keep><4>")]
    public async Task PrefixCountWritesAreVisibleToLaterSelectors(string script, string output)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Recorded byte-stream contracts; no fresh native comparison is implied.
    [Theory]
    [InlineData("i=0; declare -n count='a[i++]'; printf 'ab%ncd%n' count count; printf '<%s><%s><%s>' \"$i\" \"${a[0]}\" \"${a[1]}\"", "616263643C323E3C323E3C343E", "", 0)]
    [InlineData("i=0; declare -n count='a[i++]'; printf 'ab%n' count count; printf '<%s><%s><%s>' \"$i\" \"${a[0]}\" \"${a[1]}\"", "616261623C323E3C323E3C323E", "", 0)]
    [InlineData("declare -A a; declare -n count='a[$(printf selector >&2; printf key)]'; printf 'before%n%dafter' count nope; printf '<%s>' \"${a[key]}\"", "6265666F72653061667465723C363E", "73656C6563746F727072696E74663A206E6F70653A20696E76616C6964206E756D6265720A", 0)]
    [InlineData("declare -A a; declare -n count='a[$(printf selector >&2; printf key)]'; printf '%d%n%s' nope count after; printf '<%s>' \"${a[key]}\"", "3061667465723C313E", "7072696E74663A206E6F70653A20696E76616C6964206E756D6265720A73656C6563746F72", 0)]
    [InlineData("declare -n count='a[1/0]'; printf 'before%n-after' count; printf next", "6265666F7265", "312F303A206469766973696F6E20627920300A", 1)]
    [InlineData("declare -n count='a[-1]'; printf 'before%n-after' count; printf '<%s>' \"$?\"", "6265666F72652D61667465723C303E", "615B2D315D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("readonly a=old; declare -n count=a; printf 'before%n-after' count; printf '<%s>' \"$?\"", "6265666F72652D61667465723C303E", "613A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("arr=(old keep); printf 'abc%n%Z' arr; printf '<%s><%s><%s>' \"$?\" \"${arr[0]}\" \"${arr[1]}\"", "6162633C313E3C333E3C6B6565703E", "7072696E74663A20605A273A20696E76616C696420666F726D6174206368617261637465720A", 0)]
    [InlineData("declare -A a; k=$'\\377'; declare -n count='a[$k]'; printf 'abc%n' count; printf '<%s>' \"${a[$k]}\"", "6162633C333E", "", 0)]
    [InlineData("i=0; declare -n count='a[i++]'; printf -v 'a[i++]' 'abc%n-done' count; printf '<%s><%s><%s>' \"$i\" \"${a[0]}\" \"${a[1]}\"", "3C323E3C333E3C6162632D646F6E653E", "", 0)]
    [InlineData("printf 'abc%n-after' 'a[0]'; printf '<%s>' \"$?\"", "6162633C313E", "7072696E74663A2060615B305D273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("printf 'abc%n-after' $'\\377'; printf '<%s>' \"$?\"", "6162633C313E", "7072696E74663A2060FF273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("declare -A a; declare -n count='a[$(printf selector >&2; printf key)]'; printf 'before%n-after' count 2>&1", "73656C6563746F726265666F72652D6166746572", "", 0)]
    [InlineData("declare -A a; declare -n count='a[$(printf selector >&2; printf key)]'; printf 'before%n%dafter' count nope 2>&1", "73656C6563746F727072696E74663A206E6F70653A20696E76616C6964206E756D6265720A6265666F7265306166746572", "", 1)]
    [InlineData("declare -n count='a[1/0]'; printf 'before%n-after' count 2>&1", "312F303A206469766973696F6E20627920300A6265666F7265", "", 1)]
    [InlineData("readonly a; printf 'before%n-after' a 2>&1", "613A20726561646F6E6C79207661726961626C650A6265666F72652D6166746572", "", 0)]
    public async Task CountWrites_InterleaveWithConversionsAndPreserveBindings(
        string script, string outputHex, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(outputHex, Convert.ToHexString(result.Host.StandardOutput.Content.Span));
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }
}

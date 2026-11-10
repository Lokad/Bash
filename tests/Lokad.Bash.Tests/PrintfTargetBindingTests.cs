using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class PrintfTargetBindingTests
{
    // Bash 5.2.37, LC_ALL=C: global aliases and explicit element writes remain within the prefix.
    [Theory]
    [InlineData("a=9; declare -n r=a; a=0 printf -v 'b[r++]' x; printf '<%s>' \"$a\" \"${!b[@]}\"", "<9><0>")]
    [InlineData("a=9; declare -n r=a; a= printf -v 'b[${r:=4}]' x; printf '<%s>' \"$a\" \"${!b[@]}\"", "<9><4>")]
    [InlineData("a=(old keep); declare -n r=a; a=0 printf -v 'b[${r[2]:=4}]' x; printf '<%s>' \"${a[2]-missing}\" \"${a[@]}\" \"${!b[@]}\"", "<missing><old><keep><4>")]
    [InlineData("a=9; declare -n r=a; f(){ a=0 printf -v 'b[(r++,r++)]' x; }; f; printf '<%s>' \"$a\" \"${!b[@]}\"", "<9><1>")]
    [InlineData("a=(old keep); f(){ local -n r=a; a=0 printf -v 'b[${r[2]:=4}]' x; }; f; printf '<%s>' \"${a[2]-missing}\" \"${a[@]}\" \"${!b[@]}\"", "<missing><old><keep><4>")]
    public async Task SelectorWritesThroughNamerefsStayInTheTemporaryPrefix(string script, string output)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.37, LC_ALL=C: scalar writes through local aliases retain the caller but do not change prefix reads.
    [Theory]
    [InlineData("a=9; f(){ local -n r=a; a=0 printf -v 'b[(r++,r++)]' x; }; f; printf '<%s>' \"$a\" \"${!b[@]}\"", "<1><0>")]
    [InlineData("a=9; f(){ local -n r=a; a= printf -v 'b[${r:=4}+${r:=5}]' x; }; f; printf '<%s>' \"$a\" \"${!b[@]}\"", "<5><9>")]
    [InlineData("a=(old keep); f(){ local -n r=a; a=0 printf -v 'b[r++]' x; }; f; printf '<%s>' \"${a[@]}\" \"${!b[@]}\"", "<1><keep><0>")]
    [InlineData("a=9; f(){ local -n r=a; g(){ a=0 printf -v 'b[r++]' x; }; g; }; f; printf '<%s>' \"$a\" \"${!b[@]}\"", "<1><0>")]
    [InlineData("a=9; f(){ local a=8; local -n r=a; a=0 printf -v 'b[r++]' x; printf '<%s>' \"$a\" \"${!b[@]}\"; }; f; printf '<%s>' \"$a\"", "<1><0><9>")]
    [InlineData("a=9; declare -n hop=a; f(){ local -n r=hop; a=0 printf -v 'b[(r++,r++)]' x; }; f; printf '<%s>' \"$a\" \"${!b[@]}\"", "<1><0>")]
    [InlineData("a=9; f(){ local -n r=a; a=0 printf -v 'b[(r++,r++)]' 'abc%n' a; }; f; printf '<%s>' \"$a\" \"${!b[@]}\"", "<4><3>")]
    public async Task ScalarSelectorWritesThroughLocalNamerefsRetainOnlyTheCaller(string script, string output)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.37, LC_ALL=C: the temporary scalar supplies index zero, not the saved array's maximum.
    [Theory]
    [InlineData("a=([5]=old); a=temp printf -v 'a[-1]' new; printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "<0><5><new><old>", "")]
    [InlineData("a=(old keep); a=temp printf -v 'a[-2]' new; printf '<%s>' \"$?\" \"${a[@]}\"", "<1><old><keep>", "a[-2]: bad array subscript\n")]
    public async Task PrefixNegativeIndicesUseTheActiveBinding(string script, string output, string error)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Recorded byte-stream contracts; no fresh native comparison is implied.
    [Theory]
    [InlineData("i=0; printf -v 'a[i++]' '%s' value; printf '<%s><%s><%s>' \"$i\" \"${!a[@]}\" \"${a[@]}\"", "3C313E3C303E3C76616C75653E", "", 0)]
    [InlineData("declare -A a; k=plain; printf -v 'a[$k]' '%s' value; printf '<%s><%s>' \"${!a[@]}\" \"${a[$k]}\"", "3C706C61696E3E3C76616C75653E", "", 0)]
    [InlineData("declare -A a; k=$'\\377'; printf -v 'a[$k]' '%s' $'\\376'; printf '<%s><%s>' \"${!a[@]}\" \"${a[$k]}\"", "3CFF3E3CFE3E", "", 0)]
    [InlineData("declare -A a; printf -v $'a[\\377]' '%s' value; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", "3CFF3E3C76616C75653E", "", 0)]
    [InlineData("declare -A a; printf $'-va[\\377]' '%s' value; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", "3CFF3E3C76616C75653E", "", 0)]
    [InlineData("a=([5]=old); printf -v 'a[-1]' '%s' new; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", "3C353E3C6E65773E", "", 0)]
    [InlineData("declare -A a=([k]=old); printf -v a '%s' new; printf '<%s><%s><%s>' \"${a[0]}\" \"${a[k]}\" \"${#a[@]}\"", "3C6E65773E3C6F6C643E3C323E", "", 0)]
    [InlineData("i=0; declare -n ref='a[i++]'; printf -v ref '%s' value; printf '<%s><%s><%s><%s>' \"$i\" \"${!a[@]}\" \"${a[@]}\" \"${!ref}\"", "3C313E3C303E3C76616C75653E3C615B692B2B5D3E", "", 0)]
    [InlineData("declare -A a; declare -n ref=a; k=$'\\377'; printf -v 'ref[$k]' '%s' value; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", "3CFF3E3C76616C75653E", "", 0)]
    [InlineData("i=0; j=0; printf -v 'a[i++]' -v 'b[j++]' '%s' value; printf '<%s><%s><%s><%s>' \"$i\" \"$j\" \"${a[0]-missing}\" \"${!b[@]}\"", "3C303E3C313E3C6D697373696E673E3C303E", "", 0)]
    [InlineData("i=0; printf -v 'a[i++]'; printf '<%s><%s><%s>' \"$?\" \"$i\" \"${a[0]-missing}\"", "3C323E3C303E3C6D697373696E673E", "7072696E74663A2075736167653A207072696E7466205B2D76207661725D20666F726D6174205B617267756D656E74735D0A", 0)]
    [InlineData("i=0; printf -v 'a[i++]' '%Z'; printf '<%s><%s><%s><%s>' \"$?\" \"$i\" \"${!a[@]}\" \"${a[@]}\"", "3C313E3C313E3C303E3C3E", "7072696E74663A20605A273A20696E76616C696420666F726D6174206368617261637465720A", 0)]
    [InlineData("i=0; printf -v 'a[i]' 'abc%n' i; printf '<%s><%s><%s>' \"$i\" \"${!a[@]}\" \"${a[@]}\"", "3C333E3C333E3C6162633E", "", 0)]
    [InlineData("declare -A a; printf -v 'a[$(printf selector >&2; printf key)]' '%s' \"$(printf argument >&2; printf value)\"; printf '<%s>' \"${a[key]}\"", "3C76616C75653E", "617267756D656E7473656C6563746F72", 0)]
    [InlineData("i=0; readonly -a a=(old); printf -v 'a[i++]' '%s' value; printf '<%s><%s><%s>' \"$?\" \"$i\" \"${a[0]}\"", "3C313E3C313E3C6F6C643E", "613A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("a=old; declare -rn ref=a; printf -v ref '%s' new; printf '<%s><%s><%s>' \"$?\" \"$a\" \"${!ref}\"", "3C303E3C6E65773E3C613E", "", 0)]
    [InlineData("readonly a=old; declare -n ref=a; printf -v ref '%s' new; printf '<%s><%s>' \"$?\" \"$a\"", "3C313E3C6F6C643E", "613A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("printf -v 'a[-1]' '%s' value; printf '<%s>' \"$?\"", "3C313E", "615B2D315D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("printf -v 'a[]' '%s' value; printf '<%s>' \"$?\"", "3C323E", "7072696E74663A2060615B5D273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("declare -A a; k=; printf -v 'a[$k]' '%s' value; printf '<%s><%s>' \"$?\" \"${#a[@]}\"", "3C313E3C303E", "615B246B5D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("i=0; printf -v 'a[(i++,1/0)]' '%s' value; printf '<%s><%s>' \"$?\" \"$i\"", "", "28692B2B2C312F30293A206469766973696F6E20627920300A", 1)]
    [InlineData("i=outer; i=0 printf -v 'a[i++]' '%s' value; printf '<%s><%s>' \"$i\" \"${!a[@]}\"", "3C313E3C303E", "", 0)]
    [InlineData("a=outer; f(){ local -A a; k=$'\\377'; printf -v 'a[$k]' '%s' value; printf '<%s>' \"${a[$k]}\"; }; f; printf '<%s>' \"$a\"", "3C76616C75653E3C6F757465723E", "", 0)]
    [InlineData("printf -v $'\\377' '%s' value; printf '<%s>' \"$?\"", "3C323E", "7072696E74663A2060FF273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("declare -A a; printf -v 'a[0]' '%s' value; printf '<%s><%s>' \"$?\" \"${a[0]}\"", "3C303E3C76616C75653E", "", 0)]
    [InlineData("declare -A a; printf -v 'a[key]' 'a\\0b'; printf '<%s>' \"${a[key]}\"", "3C613E", "", 0)]
    [InlineData("declare -n ref; printf -v ref '%s' destination; printf '<%s><%s>' \"$ref\" \"${!ref}\"", "3C3E3C64657374696E6174696F6E3E", "", 0)]
    [InlineData("declare -n ref; printf -v ref '%s' 'bad name'; printf '<%s><%s>' \"$?\" \"${!ref}\"", "", "7072696E74663A2060626164206E616D65273A206E6F7420612076616C6964206964656E7469666965720A7265663A20696E76616C696420696E64697265637420657870616E73696F6E0A", 1)]
    [InlineData("declare -n ref; declare -n hop=ref; printf -v hop '%s' destination; printf '<%s><%s>' \"$?\" \"${!ref}\"", "", "7265663A20696E76616C696420696E64697265637420657870616E73696F6E0A", 1)]
    [InlineData("declare -rn ref; printf -v ref '%s' destination; printf '<%s>' \"$?\"", "3C313E", "7265663A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("readonly a=old; printf -v a '%Z'; printf '<%s>' \"$?\"", "3C313E", "7072696E74663A20605A273A20696E76616C696420666F726D6174206368617261637465720A613A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("printf -v 'a[-1]' '%Z'; printf '<%s>' \"$?\"", "3C313E", "7072696E74663A20605A273A20696E76616C696420666F726D6174206368617261637465720A615B2D315D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("i=0; printf -v 'a[i++]' -z format; printf '<%s><%s>' \"$?\" \"$i\"", "3C323E3C303E", "7072696E74663A202D7A3A20696E76616C6964206F7074696F6E0A7072696E74663A2075736167653A207072696E7466205B2D76207661725D20666F726D6174205B617267756D656E74735D0A", 0)]
    [InlineData("i=0; printf -v 'a[]' -v 'a[i++]' format; printf '<%s><%s>' \"$?\" \"$i\"", "3C323E3C303E", "7072696E74663A2060615B5D273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("! printf -v 'a[-1]' value; printf '<%s>' \"$?\"", "3C303E", "615B2D315D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("printf $'-\\377'; printf '<%s>' \"$?\"", "3C323E", "7072696E74663A202DFF3A20696E76616C6964206F7074696F6E0A7072696E74663A2075736167653A207072696E7466205B2D76207661725D20666F726D6174205B617267756D656E74735D0A", 0)]
    [InlineData("printf -v 'a[@]' value; printf '<%s>' \"$?\"", "3C313E", "615B405D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("printf -v 'a[*]' value; printf '<%s>' \"$?\"", "3C313E", "615B2A5D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("declare -A a; printf -v 'a[@]' value; printf '<%s><%s>' \"$?\" \"${a[@]}\"", "3C303E3C76616C75653E", "", 0)]
    [InlineData("printf -v 'a[2147483648]' value; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", "3C323134373438333634383E3C76616C75653E", "", 0)]
    [InlineData("i=0; printf -v 'a[(i++,-1)]' value; printf '<%s><%s>' \"$?\" \"$i\"", "3C313E3C313E", "615B28692B2B2C2D31295D3A20626164206172726179207375627363726970740A", 0)]
    [InlineData("readonly -A a; printf -v 'a[$(printf selector >&2; printf key)]' value; printf '<%s>' \"$?\"", "3C313E", "73656C6563746F72613A20726561646F6E6C79207661726961626C650A", 0)]
    public async Task AssignsSelectedLocationAfterFormatting(
        string script, string outputHex, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }
}

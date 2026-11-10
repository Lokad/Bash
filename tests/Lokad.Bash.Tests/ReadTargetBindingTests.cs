using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ReadTargetBindingTests
{
    // Recorded byte-stream contracts; no fresh native comparison is implied.
    [Theory]
    [InlineData("index=0; read index 'arr[index]' <<< '2 value'; printf '<%s><%s><%s>' \"$index\" \"${arr[0]-missing}\" \"${arr[2]}\"", 0, "3C323E3C6D697373696E673E3C76616C75653E", "")]
    [InlineData("index=2; key='a=b'; declare -A map; read 'arr[index]' 'map[$key]' <<< 'indexed associated'; printf '<%s><%s>' \"${arr[2]}\" \"${map[a=b]}\"", 0, "3C696E64657865643E3C6173736F6369617465643E", "")]
    [InlineData("a=(old keep); i=0; read -r 'a[i++]' <<< value; printf '<%s><%s><%s>' \"$i\" \"${a[0]}\" \"${a[1]}\"", 0, "3C313E3C76616C75653E3C6B6565703E", "")]
    [InlineData("declare -A a; k=key; declare -n ref='a[$k]'; read -r ref <<< value; printf '<%s>' \"${a[key]}\"", 0, "3C76616C75653E", "")]
    [InlineData("declare -A a; read -r $'a[\\377]' <<< $'\\376'; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", 0, "3CFF3E3CFE3E", "")]
    [InlineData("names='first second'; read $names <<< 'one two'; printf '<%s><%s>' \"$first\" \"$second\"", 0, "3C6F6E653E3C74776F3E", "")]
    [InlineData("read '' <<< x; printf '<%s>' \"$?\"", 0, "3C313E", "726561643A2060273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("{ read 1bad later; printf '<%s><%s>' \"$?\" \"${later-unset}\"; read next; printf '<%s>' \"$next\"; } <<< $'one two\\nnext'", 0, "3C313E3C756E7365743E3C6F6E652074776F3E", "726561643A206031626164273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("{ read first 1bad later; printf '<%s><%s><%s>' \"$?\" \"$first\" \"${later-unset}\"; read next; printf '<%s>' \"$next\"; } <<< $'one two three\\nnext'", 0, "3C313E3C6F6E653E3C756E7365743E3C6E6578743E", "726561643A206031626164273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("read 'a[-1]' later <<< 'one two'; printf '<%s><%s>' \"$?\" \"${later-unset}\"", 0, "3C313E3C756E7365743E", "615B2D315D3A20626164206172726179207375627363726970740A")]
    [InlineData("readonly a=old; read a later <<< 'one two'; printf '<%s><%s><%s>' \"$?\" \"$a\" \"${later-unset}\"", 0, "3C313E3C6F6C643E3C756E7365743E", "613A20726561646F6E6C79207661726961626C650A")]
    [InlineData("a=old; declare -rn ref=a; read ref <<< new; printf '<%s><%s>' \"$?\" \"$a\"", 0, "3C303E3C6E65773E", "")]
    [InlineData("i=0; readonly -a a=(old); read 'a[i++]' <<< value; printf '<%s><%s><%s>' \"$?\" \"$i\" \"${a[0]}\"", 0, "3C313E3C313E3C6F6C643E", "613A20726561646F6E6C79207661726961626C650A")]
    [InlineData("i=outer; i=0 read 'a[i++]' <<< value; printf '<%s><%s>' \"$i\" \"${a[0]}\"", 0, "3C6F757465723E3C76616C75653E", "")]
    [InlineData("read first 'a[${missing:?bad}]' <<< 'one two'; printf next", 1, "", "6D697373696E673A206261640A")]
    [InlineData("read first 'a[1/0]' <<< 'one two'; printf next", 1, "", "312F303A206469766973696F6E20627920300A")]
    [InlineData("a=(old keep); read a </dev/null; printf '<%s><%s><%s>' \"$?\" \"${a[0]}\" \"${a[1]}\"", 0, "3C313E3C3E3C6B6565703E", "")]
    [InlineData("a=(old keep); read a <<< new; printf '<%s><%s>' \"${a[0]}\" \"${a[1]}\"", 0, "3C6E65773E3C6B6565703E", "")]
    [InlineData("declare -n ref; read ref <<< target; printf '<%s><%s>' \"$?\" \"${!ref}\"", 0, "3C303E3C7461726765743E", "")]
    [InlineData("{ read 'a[$(read next; printf \"%s\" \"$next\")]'; read tail; printf '<%s><%s>' \"${a[2]}\" \"$tail\"; } <<< $'value\\n2\\ntail'", 0, "3C76616C75653E3C7461696C3E", "")]
    [InlineData("readonly stop=old; read first stop later <<< 'one two three'; printf '<%s><%s><%s>' \"$?\" \"$first\" \"${later-unset}\"", 0, "3C313E3C6F6E653E3C756E7365743E", "73746F703A20726561646F6E6C79207661726961626C650A")]
    [InlineData("readonly a; { read a; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C6E6578743E", "613A20726561646F6E6C79207661726961626C650A")]
    [InlineData("{ read 'a[]'; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C76616C75653E", "726561643A2060615B5D273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("{ read 'a[-1]'; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C6E6578743E", "615B2D315D3A20626164206172726179207375627363726970740A")]
    [InlineData("{ read $'\\377'; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C76616C75653E", "726561643A2060FF273A206E6F7420612076616C6964206964656E7469666965720A")]
    public async Task SelectsTargetsAfterInputAndPriorWrites(
        string script, int exitCode, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }
}

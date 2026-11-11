using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteDeclarationTests
{
    // Bash accepts non-UTF-8 selectors; variable names themselves remain identifiers.
    [Theory]
    [InlineData("r=value", "declare -n r=$'a[\\377]'", "303A3C615BFF5D3E3A3C66697273743E")]
    [InlineData("r=value", "typeset -n r+=$'a[\\377]'", "303A3C76616C7565615BFF5D3E3A3C7365636F6E643E")]
    [InlineData("r=$'a[\\377]'", "declare -n r", "303A3C615BFF5D3E3A3C66697273743E")]
    [InlineData("t=value; declare -n r=t", "declare -n r=$'a[\\377]'", "303A3C615BFF5D3E3A3C66697273743E")]
    [InlineData("r=value", "declare -nrx r=$'a[\\377]'", "303A3C615BFF5D3E3A3C66697273743E")]
    public async Task NonUtf8NamerefTargetsPreserveSelectors(string setup, string declaration, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync("declare -A a=([$'\\377']=first) valuea=([$'\\377']=second); "
            + setup + "; " + declaration + "; printf '%s:<%s>:<%s>' \"$?\" \"${!r}\" \"$r\"", EndToEndHarness.BuildTree([]));

        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("arr[2147483648]=$'\\377'; printf '%s' \"$(printf '%s' \"${arr[2147483648]}\")\"", 0, "FF", "")]
    [InlineData("value=$'\\377'; declare -n ref=value; printf '%s' \"$(ref=$'\\376'; printf '%s' \"$value\")\"; printf '%s' \"$value\"", 0, "FEFF", "")]
    [InlineData("unset X; X=${X:=$'\\377'} true; printf '%s' \"$X\"", 0, "FF", "")]
    [InlineData("i=0; declare x=$((i+=1))$'\\377'; printf '%s:%s' \"$i\" \"$x\"", 0, "313A31FF", "")]
    [InlineData("f(){ local i=0; local x=$((i+=1))$'\\377'; printf '%s:%s' \"$i\" \"$x\"; }; f", 0, "313A31FF", "")]
    [InlineData("x=$'\\377'; x=$'\\376' printf '%s' \"$x\"; printf '%s' \"$x\"", 0, "FFFF", "")]
    [InlineData("message=$'\\377'; : ${missing:?$message}; printf unexpected", 1, "", "6D697373696E673A20FF0A")]
    [InlineData("v=$'\\377'; f(){ local v=$'\\376'; printf '%s' \"$(local v=$'\\375'; printf '%s' \"$v\")\"; printf '%s' \"$v\"; }; f; printf '%s' \"$v\"", 0, "FDFEFF", "")]
    [InlineData("A=outer; A=$'\\377' export A; printf '%s' \"$A\"", 0, "FF", "")]
    [InlineData("A=outer; A=$'\\377' printf -v A '%s' $'\\376'; printf '%s' \"$A\"", 0, "FE", "")]
    [InlineData("A=outer; A=$'\\377' read A <<< $'\\376'; printf '%s' \"$A\"", 0, "6F75746572", "")]
    [InlineData("unused=x eval \"v=\\$'\\\\377'\"; printf '%s' \"$v\"", 0, "FF", "")]
    [InlineData("v=outer; f(){ v=$'\\377'; }; unused=x f; printf '%s' \"$v\"", 0, "FF", "")]
    [InlineData("v=outer; f(){ local v=$'\\377'; }; unused=x f; printf '%s' \"$v\"", 0, "6F75746572", "")]
    [InlineData("A=0; A=1 let 'A=A+1'; printf '%s' \"$A\"", 0, "32", "")]
    [InlineData("unused=x set -- $'\\377' $'\\376'; printf '%s' \"$@\"", 0, "FFFE", "")]
    public async Task ByteValuesPreserveDeclarationAndScopeEffects(string script, int exitCode, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    // Bash 5.2 controls preserve operand bytes and stage effects; option usage lines remain omitted.
    [Theory]
    [InlineData("declare value=$'\\377'; declare value+=$'\\376'; printf '%s' \"$value\"", "FFFE", "")]
    [InlineData("declare -a a=($'\\377' '' $'\\376'); declare -a a+=($'\\375'); printf '<%s>' \"${a[@]}\"", "3CFF3E3C3E3CFE3E3CFD3E", "")]
    [InlineData("declare -A a=([key]=$'\\377'); declare a[key]+=$'\\376'; printf '%s' \"${a[key]}\"", "FFFE", "")]
    [InlineData("value=$'\\377'; f() { value=$'\\376'; local value=$'\\375'; printf '%s' \"$value\"; }; f; printf '%s' \"$value\"", "FDFE", "")]
    [InlineData("export value=$'\\376'; f() { local -n value=$'\\377'; printf '%s:' \"$?\"; printf '%s' \"$value\"; }; f; printf ':%s' \"$value\"", "313AFE3AFE", "6C6F63616C3A2060FF273A20696E76616C6964207661726961626C65206E616D6520666F72206E616D65207265666572656E63650A")]
    [InlineData("x=$'\\377'; f() { local x=$'\\376'; declare -g x+=$'\\375'; printf '%s:' \"$x\"; }; f; printf '%s' \"$x\"", "FE3AFFFD", "")]
    [InlineData("a=($'\\377'); declare -a a=(\"${a[@]}\" $'\\376') b=(\"${a[@]}\"); printf '%s:' \"${a[*]}\"; printf '%s' \"${b[*]}\"", "FF20FE3AFF20FE", "")]
    [InlineData("a=$'\\377'; declare a=$'\\376' b=\"$a\"; printf '%s:%s' \"$a\" \"$b\"", "FE3AFF", "")]
    [InlineData("declare() { printf '<%s>' \"$@\"; }; declare x=$'\\377' y=$'\\376'", "3C783DFF3E3C793DFE3E", "")]
    [InlineData("declare() { printf '<%s>' \"$@\"; }; declare -a a=($'\\377'); printf ':%s' \"${a[0]}\"", "3C2D613E3C613E3AFF", "")]
    [InlineData("'declare' x=$'\\377'; printf '%s' \"$x\"", "FF", "")]
    [InlineData("cmd=declare; $cmd \"x=$'\\377'\"; printf '%s' \"$x\"", "24275C33373727", "")]
    [InlineData("builtin declare x=$'\\377'; printf '%s' \"$x\"", "FF", "")]
    [InlineData("declare probe_x; printf '<%s>:<%s>' \"${!probe_*}\" \"${probe_x-unset}\"; declare probe_x=; printf ':<%s>' \"${probe_x-unset}\"", "3C3E3A3C756E7365743E3A3C3E", "")]
    [InlineData("export value; printf '<%s>' \"${value-unset}\"; export value=$'\\377'; printf '<%s>' \"$value\"", "3C756E7365743E3CFF3E", "")]
    [InlineData("declare $'\\377=value' later=$'\\376'; printf '%s:%s' \"$?\" \"$later\"", "313AFE", "6465636C6172653A2060FF3D76616C7565273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("declare $'x\\377+=\\376' later=ok; printf '%s:%s' \"$?\" \"$later\"", "313A6F6B", "6465636C6172653A206078FF2B3DFE273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("declare $'-\\377' value=yes; printf '%s:%s' \"$?\" \"${value-unset}\"", "323A756E736574", "6465636C6172653A202DFF3A20696E76616C6964206F7074696F6E0A")]
    [InlineData("declare $'-ax\\377' a=($'\\376'); printf '%s:%s' \"$?\" \"${a[0]-unset}\"", "323AFE", "6465636C6172653A202DFF3A20696E76616C6964206F7074696F6E0A")]
    [InlineData("declare $'-\\377A' a=([key]=$'\\376'); printf '%s:%s' \"$?\" \"${a[key]-unset}\"", "323AFE", "6465636C6172653A202DFF3A20696E76616C6964206F7074696F6E0A")]
    [InlineData("declare $'+\\377' value=yes; printf '%s:%s' \"$?\" \"${value-unset}\"", "323A756E736574", "6465636C6172653A202BFF3A20696E76616C6964206F7074696F6E0A")]
    [InlineData("declare probe_x; declare probe_y=; printf '<%s>' \"${!probe_*}\"", "3C70726F62655F793E", "")]
    [InlineData("declare -a probe_a; declare -A probe_m; printf '<%s>' \"${!probe_*}\"", "3C3E", "")]
    [InlineData("declare -a probe_a=(); declare -A probe_m=(); printf '<%s>' \"${!probe_*}\"", "3C70726F62655F612070726F62655F6D3E", "")]
    [InlineData("declare -n probe_r; declare -n probe_s=missing; printf '<%s>' \"${!probe_*}\"", "3C70726F62655F733E", "")]
    [InlineData("export probe_e; readonly probe_r; printf '<%s>' \"${!probe_*}\"", "3C3E", "")]
    public async Task Evaluation_DeclarationsPreserveBytesAndBindingState(
        string script, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("declare value='", "'; printf '%s' \"$value\"", "FF", "")]
    [InlineData("declare '", "=value' later=ok; printf '%s:%s' \"$?\" \"$later\"", "313A6F6B", "6465636C6172653A2060FF3D76616C7565273A206E6F7420612076616C6964206964656E7469666965720A")]
    public async Task Evaluation_DeclarationsPreserveRawSourceBytes(
        string prefix, string suffix, string outputHex, string errorHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }
}

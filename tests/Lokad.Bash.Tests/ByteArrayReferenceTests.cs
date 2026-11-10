using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteArrayReferenceTests
{
    // Linux Bash 5.2.21, C.UTF-8: raw streams match except for Bash's executable/line diagnostic prefix.
    [Theory]
    [InlineData("a[3]=$'\\377'; printf '<%s>' \"${a[3]-missing}\" \"${a[2]-missing}\"\n", "3CFF3E3C6D697373696E673E", 0, "")]
    [InlineData("v=$'\\377'; printf '<%s>' \"${a[2]:=$v}\" \"${a[2]}\" \"${#a[@]}\"\n", "3CFF3E3CFF3E3C313E", 0, "")]
    [InlineData("declare -A a; v=$'\\376'; printf '<%s>' \"${a[k]:=$v}\" \"${a[k]}\" \"${#a[@]}\"\n", "3CFE3E3CFE3E3C313E", 0, "")]
    [InlineData("a[3]=$'\\377'; a[7]=$'\\376'; printf '<%s>' \"${a[-1]-missing}\" \"${a[-5]-missing}\"\n", "3CFE3E3CFF3E", 0, "")]
    [InlineData("i=0; v=$'\\377'; printf '<%s>' \"${a[i++]:=$v}\" \"$i\" \"${a[0]-missing}\" \"${a[1]-missing}\"\n", "3CFF3E3C323E3C6D697373696E673E3CFF3E", 0, "")]
    [InlineData("a=$'\\377'; v=$'\\376'; printf '<%s>' \"${a[2]:=$v}\" \"${a[0]}\" \"${a[2]}\" \"${#a[@]}\"\n", "3CFE3E3CFF3E3CFE3E3C323E", 0, "")]
    [InlineData("declare -n ref=a; v=$'\\377'; printf '<%s>' \"${ref[2]:=$v}\" \"${a[2]-missing}\" \"${#ref[@]}\"\n", "3CFF3E3CFF3E3C313E", 0, "")]
    [InlineData("a=($'\\303' $'\\251'); IFS=; printf '<%s>' \"${a[*]-missing}\"; IFS=$'\\377'; printf '<%s>' \"${a[*]-missing}\" \"${a[@]-missing}\"\n", "3CC3A93E3CC3FFA93E3CC33E3CA93E", 0, "")]
    [InlineData("a=('' ''); IFS=$'\\377'; printf '<%s>' \"${a[*]:-fallback}\"; IFS=; printf '<%s>' \"${a[*]:-fallback}\" \"${a[@]:-fallback}\"\n", "3CFF3E3C66616C6C6261636B3E3C3E3C3E", 0, "")]
    [InlineData("x=abc; printf '<%s>' \"${#x}\"\n", "3C333E", 0, "")]
    [InlineData("x=$'\\u03b1\\U0001f642'; printf '<%s>' \"${#x}\"\n", "3C323E", 0, "")]
    [InlineData("x=$'\\377\\303'; printf '<%s>' \"${#x}\"\n", "3C323E", 0, "")]
    [InlineData("x=$'\\360\\237\\231'; printf '<%s>' \"${#x}\"\n", "3C333E", 0, "")]
    [InlineData("x=$'\\300\\257'; printf '<%s>' \"${#x}\"\n", "3C323E", 0, "")]
    [InlineData("a[3]=$'\\u03b1\\U0001f642\\377'; printf '<%s>' \"${#a[3]}\" \"${#a[@]}\" \"${#a[0]}\"\n", "3C333E3C313E3C303E", 0, "")]
    [InlineData("message=$'\\377'; printf '%s' \"${a[3]:?$message}\"; printf after\n", "", 1, "615B335D3A20FF0A")]
    public async Task Evaluation_ReferencesRetainBytesAndSelectorEffects(
        string script, string outputHex, int exitCode, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }
}

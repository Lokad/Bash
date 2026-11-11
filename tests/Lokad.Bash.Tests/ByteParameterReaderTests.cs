using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteParameterReaderTests
{
    // Linux Bash 5.2.21/C.UTF-8; compare value bytes without decoding them.
    [Theory]
    [InlineData("set -- $'\\377' $'\\u03bb' $'\\U0001f642'; printf '%s:%s:%s\\n' \"${#1}\" \"${#2}\" \"${#3}\"\n", "313A313A310A", "", 0)]
    [InlineData("sh -c 'printf \"%s|%s|%s\" \"$0\" \"${0}\" \"${#0}\"' $'\\377'\n", "FF7CFF7C31", "", 0)]
    [InlineData("set -- a b; IFS=$'\\377'; printf '%s' \"${*}\"\n", "61FF62", "", 0)]
    [InlineData("p=$'\\377'; printf '%s' \"${!p}\"; printf after\n", "", "FF3A20696E76616C6964207661726961626C65206E616D650A", 1)]
    [InlineData("set -- target; unset target; printf '%s|%s' \"${!1:=$'\\377'}\" \"$target\"\n", "FF7CFF", "", 0)]
    [InlineData("p=(target other); IFS=$'\\377'; printf '%s' \"${!p[@]:-fallback}\"; printf after\n", "", "746172676574FF6F746865723A20696E76616C6964207661726961626C65206E616D650A", 1)]
    [InlineData("set -- $'\\360\\237\\377'; printf '%s|%s' \"${1}\" \"${#1}\"\n", "F09FFF7C33", "", 0)]
    [InlineData("value=$'\\377\\u03bb\\U0001f642'; printf '%s' \"${#value}\"\n", "33", "", 0)]
    [InlineData("p=(target); target=$'x\\377z'; printf '%s' \"${!p[@]:1:1}\"", "FF", "", 0)]
    [InlineData("set -- target; target=$'x\\377z'; printf '%s' \"${!@:1:1}\"", "FF", "", 0)]
    [InlineData("p=absent; printf '%s' \"${!p:?$'\\377'}\"; printf after", "", "21703A20FF0A", 1)]
    [InlineData("value=$'\\377'; declare -a value; printf '%s|%s' \"${value[0]}\" \"${#value[0]}\"", "FF7C31", "", 0)]
    [InlineData("value=$'\\377'; declare -A value; printf '%s|%s' \"${value[0]}\" \"${#value[0]}\"", "FF7C31", "", 0)]
    public async Task Evaluation_ParameterValuesRemainBytes(
        string script, string outputHex, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    // Raw stdin source: native status/output and diagnostic fragments; whole-word rendering remains separate.
    [Theory]
    [InlineData("printf '%s' \"${", "}\"; printf after\n", "", "247BFF7D3A2062616420737562737469747574696F6E0A", "", 1)]
    [InlineData("printf '%s' \"${bad", "!}\"; printf after\n", "", "247B626164FF217D3A2062616420737562737469747574696F6E0A", "", 1)]
    [InlineData("printf '%s' \"$(printf effect >&2)${bad", "}$(printf later >&2)\"; printf after\n", "", "247B626164FF7D", "656666656374", 1)]
    [InlineData("printf '%s' \"${a[$(printf '", "' >&2; printf 0)]!}\"; printf after\n", "", "FF", "247B615B", 1)]
    [InlineData("x=keep; printf '<%s>' \"${x:-${", "}}\"; printf after\n", "3C6B6565703E6166746572", "", "", 0)]
    [InlineData("set -- '", "'; printf '%s|%s|%s' \"$1\" \"${1}\" \"${#1}\"\n", "FF7CFF7C31", "", "", 0)]
    [InlineData("unset x; printf '%s|%s' \"${x:=", "}\" \"$x\"\n", "FF7CFF", "", "", 0)]
    [InlineData("target='", "'; p=target; printf '%s|%s' \"${!p}\" \"${#target}\"\n", "FF7C31", "", "", 0)]
    [InlineData("p='", "'; printf '%s' \"${!p}\"; printf after\n", "", "FF3A20696E76616C6964207661726961626C65206E616D650A", "", 1)]
    [InlineData("false && printf '%s' \"${", "}\"; printf after\n", "6166746572", "", "", 0)]
    [InlineData("declare -A a; k=$'\\377'; : \"${a[$k]:=target}\"; target=OK; printf '%s' \"${!a[", "]}\"", "4F4B", "", "", 0)]
    [InlineData("declare -A a; printf '%s' \"${!a[", "]@}\"", "", "", "", 0)]
    public async Task Evaluation_RawParameterSourceRetainsBytesAndLazyEffects(
        string prefix, string suffix, string outputHex, string errorFragmentHex, string errorPrefixHex, int exitCode)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        var error = result.Host.StandardError.Content.Span;
        if (errorFragmentHex.Length == 0)
            Assert.True(error.IsEmpty);
        else
            Assert.True(error.IndexOf(Convert.FromHexString(errorFragmentHex)) >= 0);
        Assert.True(error.StartsWith(Convert.FromHexString(errorPrefixHex)));
    }

    [Theory]
    [InlineData("printf '%s' \"${a[1/0]}$(printf later >&2)\"; printf after\n", "")]
    [InlineData("printf '%s' \"$(printf effect >&2)${a[1/0]}$(printf later >&2)\"; printf after\n", "effect")]
    public async Task Evaluation_FatalSelectorStopsLaterSubstitutions(string script, string effectPrefix)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.StartsWith(effectPrefix + "1/0:", result.StdErr);
        Assert.Contains("division by 0", result.StdErr);
        Assert.False(result.Host.StandardError.Content.Span.EndsWith("later"u8));
    }

    // A name-prefix query retains selector text; it does not select an array element.
    [Theory]
    [InlineData("a=(zero); zero=VALUE; printf '<%s>' \"${!a[0]@}\"; printf after\n", "3C3E6166746572")]
    [InlineData("a=(zero); zero=VALUE; printf '<%s>' \"${!a[0]*}\"; printf after\n", "3C3E6166746572")]
    [InlineData("a=(zero); zero=VALUE; i=0; printf '<%s>|%s' \"${!a[i++]@}\" \"$i\"; printf after\n", "3C303E7C6166746572")]
    public async Task Evaluation_NamePrefixDoesNotEvaluateSelectorText(string script, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.StdErr);
    }
}

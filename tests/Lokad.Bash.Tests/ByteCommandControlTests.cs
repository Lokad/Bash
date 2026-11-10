using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteCommandControlTests
{
    [Theory]
    [InlineData("name=$'\\xff'; eval \"$name(){ printf raw; }\"; command -pv \"$name\"",
        "FF0A", "", 0)]
    [InlineData("name=$'\\xff'; eval \"$name(){ printf raw; }\"; command -pV \"$name\"",
        "FF20697320612066756E6374696F6E0AFF202829200A7B200A202020207072696E7466207261770A7D0A", "", 0)]
    [InlineData("builtin -- $'\\xc3' 2>&1; printf '[%s]' \"$?\"",
        "6275696C74696E3A20C33A206E6F742061207368656C6C206275696C74696E0A5B315D", "", 0)]
    [InlineData("command $'-\\xff' printf",
        "", "636F6D6D616E643A202DFF3A20696E76616C6964206F7074696F6E0A636F6D6D616E643A2075736167653A20636F6D6D616E64205B2D7056765D20636F6D6D616E64205B617267202E2E2E5D0A", 2)]
    [InlineData("exec $'-c\\xfe' sh; printf '[%s]' \"$?\"",
        "5B325D", "657865633A202DFE3A20696E76616C6964206F7074696F6E0A657865633A2075736167653A2065786563205B2D636C5D205B2D61206E616D655D205B636F6D6D616E64205B617267756D656E74202E2E2E5D5D205B7265646972656374696F6E202E2E2E5D0A", 0)]
    [InlineData("exec $'-a\\xff\\xc3' sh -c 'printf \"%s\" \"$0\"'",
        "FFC3", "", 0)]
    [InlineData("exec -a $'\\xff\\xc3' sh -c 'printf \"%s\" \"$0\"'",
        "FFC3", "", 0)]
    [InlineData("export MARKER=outer; exec $'-ca\\xff\\xc3' sh -c 'printf \"%s:<%s>\" \"$0\" \"${MARKER-unset}\"'",
        "FFC33A3C756E7365743E", "", 0)]
    [InlineData("x=0; command $'-p\\xff' \"$((x++))\" 2>&1; printf '[%s:%s]' \"$?\" \"$x\"",
        "636F6D6D616E643A202DFF3A20696E76616C6964206F7074696F6E0A636F6D6D616E643A2075736167653A20636F6D6D616E64205B2D7056765D20636F6D6D616E64205B617267202E2E2E5D0A5B323A315D", "", 0)]
    [InlineData("MARKER=$'\\xfe' exec -a $'\\xff' sh -c 'printf \"%s:%s\" \"$0\" \"$MARKER\"'",
        "FF3AFE", "", 0)]
    // Virtual exec of evaluator builtins is a platform contract, not native process execution.
    [InlineData("exec -a $'\\xff\\xc3' eval 'printf \"%s\" \"$0\"'",
        "FFC3", "", 0)]
    [InlineData("printf '%s\\n' 'printf \"%s\" \"$0\"' > /script; exec -a $'\\xff' source /script",
        "FF", "", 0)]
    [InlineData("exec $'-cla\\xff\\xc3' eval 'printf \"%s\" \"$0\"'",
        "2DFFC3", "", 0)]
    public async Task PreservesByteOperandsAndDeferredMessages(
        string script, string outputHex, string errorHex, int expectedExitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }
}

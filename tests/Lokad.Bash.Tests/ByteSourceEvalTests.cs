using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteSourceEvalTests
{
    // Bash 5.2.21/UTF-8; the expansion-error fixture omits only the source location prefix.
    [Theory]
    [InlineData("eval \"printf '%s' '\"$'\\377'\"'\"", 0, "FF", "")]
    [InlineData("set -- \"printf '%s'\" \"'\" $'\\377' \"'\"; eval \"$@\"", 0, "20FF20", "")]
    [InlineData("args=(\"printf '%s'\" \"'\" $'\\377' \"'\"); eval \"${args[@]}\"", 0, "20FF20", "")]
    [InlineData("set -- \"printf '%s' '\" '' $'\\377' \"'\"; eval \"$@\"", 0, "2020FF20", "")]
    [InlineData("IFS=:; value=\"printf:'%s':'raw'\"; eval $value", 0, "726177", "")]
    [InlineData("v=$'\\377'; eval \"printf '%s' '\"\"$v\"\"'\"", 0, "FF", "")]
    [InlineData("i=0; eval \"printf '%s' '$((i++))'\" '\"$((i++))\"'; printf ':%s' \"$i\"", 0, "30313A32", "")]
    [InlineData("x=wrong; value=$'\\377'; eval \"printf '%s' '\\$x' '\"\"$value\"\"'\"", 0, "2478FF", "")]
    [InlineData("x=outer; x=inner eval \"value='\"$'\\377'\"'; printf '%s' \\\"\\$x\\\"\"; printf '%s:%s' \"$x\" \"$value\"", 0, "696E6E65726F757465723AFF", "")]
    [InlineData("f(){ eval \"printf '%s' '\"$'\\377'\"'; return 7\"; printf wrong; }; f; printf ':%s' \"$?\"", 0, "FF3A37", "")]
    [InlineData("eval \"printf wrong\" \"${MISSING:?missing}\" \"$(printf later >&2)\"; printf after", 1, "", "4D495353494E473A206D697373696E670A")]
    [InlineData("value=$'\\377\\r'; eval \"printf '%s' '\"\"$value\"\"'\"", 0, "FF0D", "")]
    [InlineData("false && eval \"printf '%s' '\"$'\\377'\"'\"; printf '%s' \"$?\"", 0, "31", "")]
    [InlineData("set --; eval \"$@\"; printf '%s' \"$?\"", 0, "30", "")]
    [InlineData("set -- '' ''; eval \"$@\"; printf '%s' \"$?\"", 0, "30", "")]
    public async Task EvalPreservesSourceBytesFieldsAndStagedEffects(
        string script, int exitCode, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task EvalDefinesAFunctionFromRawSource()
    {
        const string Script = "saved=\"probe_f(){ printf '%s' '\"$'\\377'\"'; }\"; eval \"$saved\" && probe_f";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { 0xff }, result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task EvalExpandsArgumentPathnamesBeforeParsingTheResult()
    {
        var tree = EndToEndHarness.BuildTree([("/printf", "")]);
        var result = await EndToEndHarness.RunScriptAsync("cd /; eval \"printf '%s' '\" /print? \"'\"", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(" /printf ", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("source")]
    [InlineData(".")]
    public async Task SourceParsesFileBytesWithoutChangingParameterOwnership(string builtin)
    {
        var tree = EndToEndHarness.BuildTree([]);
        var host = EndToEndHarness.WithMemoryBudget(tree, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var script = "printf \"printf '%%s' '\\377' \\\"\\$1\\\"; value=\\\"\\$1\\\"\" > /script; "
            + $"set -- outer; {builtin} /script $'\\376'; printf ':%s:%s' \"$1\" \"$value\"";
        var result = await EndToEndHarness.RunScriptAsync(script, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("FFFE3A6F757465723AFE"), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }
}

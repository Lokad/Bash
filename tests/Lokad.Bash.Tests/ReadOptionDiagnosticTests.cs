using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ReadOptionDiagnosticTests
{
    [Theory]
    [InlineData("-ra", "a")]
    [InlineData("-sep", "p")]
    [InlineData("-i", "i")]
    public async Task MissingReadOptionOperandsLeaveInputUntouched(string options, string flag)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "{ read " + options + "; printf '<%s>' \"$?\"; read next; printf '<%s>' \"$next\"; } <<< data",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2><data>", result.StdOut);
        Assert.Equal($"read: -{flag}: option requires an argument\n"
            + "read: usage: read [-ers] [-a array] [-d delim] [-i text] [-n count] [-N count] [-p prompt] [-u fd] [name ...]\n",
            result.StdErr);
    }

    [Theory]
    [InlineData("-sreprompt")]
    [InlineData("-p --help -s")]
    [InlineData("-i -- -e")]
    [InlineData("-s -e -p '' -i seed")]
    public async Task ReadAcceptsNonInteractiveOptionsWithoutWritingAPrompt(string options)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "{ read " + options + " first 2>diagnostic; printf '<%s><%s>' \"$?\" \"$first\";"
            + " IFS= read -r second; printf '<%s><%s>' \"$?\" \"$second\"; } <<< $'one two\\nnext'",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0><one two><0><next>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Empty(result.Host.ReadFileBytes("/diagnostic"));
    }

    // Recorded error-byte/status/effect contracts; usage lists only supported options.
    [Theory]
    [InlineData("read -z value <<< data; printf '<%s><%s>' \"$?\" \"${value-missing}\"", "3C323E3C6D697373696E673E", "7A")]
    [InlineData("read $'-\\377' value <<< data; printf '<%s>' \"$?\"", "3C323E", "FF")]
    [InlineData("read -rrz value <<< data; printf '<%s>' \"$?\"", "3C323E", "7A")]
    [InlineData("read $'-r\\377z' value <<< data; printf '<%s>' \"$?\"", "3C323E", "FF")]
    [InlineData("{ read -z first; printf '<%s>' \"$?\"; read next; printf '<%s>' \"$next\"; } <<< data", "3C323E3C646174613E", "7A")]
    [InlineData("x=outer; x=temp read -z value <<< data; printf '<%s><%s>' \"$?\" \"$x\"", "3C323E3C6F757465723E", "7A")]
    [InlineData("read -z \"${side:=value}\" <<< data; printf '<%s><%s>' \"$?\" \"$side\"", "3C323E3C76616C75653E", "7A")]
    [InlineData("x=${side:=rhs} read -z value <<< data; printf '<%s><%s><%s>' \"$?\" \"$side\" \"${x-missing}\"", "3C323E3C7268733E3C6D697373696E673E", "7A")]
    [InlineData("read --unknown value <<< data; printf '<%s>' \"$?\"", "3C323E", "2D")]
    [InlineData("read -r -z value <<< data; printf '<%s>' \"$?\"", "3C323E", "7A")]
    [InlineData("! read -z value <<< data; printf '<%s>' \"$?\"", "3C303E", "7A")]
    public async Task InvalidOptionsRetainTheirDiagnosticBytesStatusAndEffects(
        string script, string outputHex, string optionHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        byte[] diagnostic = [.. "read: -"u8, .. Convert.FromHexString(optionHex),
            .. ": invalid option\nread: usage: read [-ers] [-a array] [-d delim] [-i text] [-n count] [-N count] [-p prompt] [-u fd] [name ...]\n"u8];

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(diagnostic, result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("read -- -z <<< data; printf '<%s>' \"$?\"", "3C313E", "726561643A20602D7A273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("read - <<< data; printf '<%s>' \"$?\"", "3C313E", "726561643A20602D273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("read +r <<< data; printf '<%s>' \"$?\"", "3C313E", "726561643A20602B72273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("read -rr -- value <<< 'a\\b'; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C615C623E", "")]
    [InlineData("read value -z <<< 'one two'; printf '<%s><%s>' \"$?\" \"$value\"", "3C313E3C6F6E653E", "726561643A20602D7A273A206E6F7420612076616C6964206964656E7469666965720A")]
    public async Task SupportedOptionsAndOperandBoundariesRetainNativeBehavior(
        string script, string outputHex, string diagnosticHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(diagnosticHex), result.Host.StandardError.Content.ToArray());
    }
}

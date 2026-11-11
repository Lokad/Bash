using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteAssignmentTargetTests
{
    // Bash 5.2.21/C.UTF-8 timing and value bytes; numeric diagnostic wording remains a separate gate.
    [Theory]
    [InlineData("i=0; a[ 1 + 1 ]=$'\\377\\u03bb'; printf '%s' \"${a[2]}\"\n", "FFCEBB", "", 0)]
    [InlineData("i=0; a[i++]=$'\\377'; printf '%s|%s' \"${a[0]}\" \"$i\"\n", "FF7C31", "", 0)]
    [InlineData("i=0; a[i++]=$((i+=10))$'\\377'; printf '%s|%s' \"$i\" \"${a[10]}\"\n", "31317C3130FF", "", 0)]
    [InlineData("index=$'\\377'; a[$index]=$(printf rhs >&2); printf after\n", "", "726873FF3A20696E76616C69642061726974686D657469632065787072657373696F6E0A", 1)]
    [InlineData("a[$(printf selector >&2)]=\"${missing:?$'\\377'}\"; printf after\n", "", "6D697373696E673A20FF0A", 1)]
    [InlineData("a=old; readonly a; declare -n r=a; r[0]=$(printf rhs >&2); printf after\n", "", "726873723A20726561646F6E6C79207661726961626C650A", 1)]
    public async Task Evaluation_TargetSelectionPreservesByteValuesAndEffects(
        string script, string outputHex, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("false && a[", "]=$(printf forbidden >&2); printf done\n", "646F6E65", "", 0)]
    [InlineData("a[", "]=$(printf rhs >&2); printf after\n", "", "726873FF3A20696E76616C69642061726974686D657469632065787072657373696F6E0A", 1)]
    [InlineData("a[", "]=${missing:?error}; printf after\n", "", "6D697373696E673A206572726F720A", 1)]
    [InlineData("a[0]=x; printf '%s' \"${a[", "]}\"; printf after\n", "", "FF3A20696E76616C69642061726974686D657469632065787072657373696F6E0A", 1)]
    [InlineData("a[0]=x; ref='a[", "]'; printf '%s' \"${!ref}\"; printf after\n", "", "FF3A20696E76616C69642061726974686D657469632065787072657373696F6E0A", 1)]
    [InlineData("x=set; printf '%s' \"${x:-${a[", "]}}\"; printf done\n", "736574646F6E65", "", 0)]
    [InlineData("a[ 1 + 1 ]='", "'; printf '%s' \"${a[2]}\"\n", "FF", "", 0)]
    [InlineData("declare -A a; false && a['", "']=value; printf done\n", "646F6E65", "", 0)]
    public async Task Evaluation_TargetSourceIsDecodedOnlyByItsReachedConsumer(
        string prefix, string suffix, string outputHex, string errorHex, int exitCode)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public void Parse_TargetRetainsSelectorBytesAndCarriageReturn()
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append("ar\\\nr[1\r + '"u8).Append(0xff).Append("']\\\n=value"u8);

        var command = Assert.IsType<SimpleCommand>(Assert.Single(Parser.Parse(source.ToMemory()).Commands));
        var target = Assert.Single(command.Assignments).Target;
        Assert.Equal(Convert.FromHexString("6172725B310D202B2027FF275D"), target.Bytes.ToArray());
    }
}

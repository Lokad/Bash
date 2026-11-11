using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteCompoundArrayTests
{
    // Bash 5.2.21/C.UTF-8 values, statuses and expansion order.
    [Theory]
    [InlineData("a=($'\\377'); i='['; a+=([$i]=$'\\376'); printf '%s' \"${a[1]}\"", "FF5B5B5D3DFE", "", 0)]
    [InlineData("IFS=:; src=($'\\377' $'\\376'); a=([0]=${missing:-${src[@]}}); printf '%s' \"${a[0]}\"", "FF20FE", "", 0)]
    [InlineData("IFS=:; src=($'\\377' $'\\376'); a=([0]=${missing:-\"${src[@]}\"}); printf '%s' \"${a[0]}\"", "FF3AFE", "", 0)]
    [InlineData("a=([2]=$'\\377' [5]='' tail); printf '<%s>' \"${a[@]}\" \"${!a[@]}\"\n", "3CFF3E3C3E3C7461696C3E3C323E3C353E3C363E", "", 0)]
    [InlineData("v=$'\\303'; v+=$'\\251'; printf '%s' \"$v\"\n", "C3A9", "", 0)]
    [InlineData("a=([3]=$'\\377'); a+=([3]+=$'\\376' $'\\375'); printf '<%s>' \"${a[@]}\" \"${!a[@]}\"\n", "3CFFFE3E3CFD3E3C333E3C343E", "", 0)]
    [InlineData("declare -A a; a=([x]=$'\\377'); a+=([x]+=$'\\376'); printf '%s' \"${a[x]}\"\n", "FFFE", "", 0)]
    [InlineData("declare -A a; a=(k $'\\377' z $'\\376'); printf '%s|%s' \"${a[k]}\" \"${a[z]}\"\n", "FF7CFE", "", 0)]
    [InlineData("src=($'\\377' '' $'\\376'); a=(\"${src[@]}\"); printf '<%s>' \"${a[@]}\"\n", "3CFF3E3C3E3CFE3E", "", 0)]
    [InlineData("v=$'\\303'; f(){ printf '%s' \"$v\"; }; v+=$'\\251' f; printf '%s' \"$v\"\n", "C3A9C3", "", 0)]
    [InlineData("a=old; a=([0]=$(printf rhs >&2; printf '\\377') \"${missing:?$'\\377'}\" $(printf forbidden >&2)); printf after\n", "", "7268736D697373696E673A20FF0A", 1)]
    [InlineData("i=$'\\377'; a=([0]=$'\\376' [$i]=$(printf rhs >&2)); printf after\n", "", "726873FF3A20696E76616C69642061726974686D657469632065787072657373696F6E0A", 1)]
    [InlineData("IFS=$'\\377'; src=(x y); a=([0]=\"${src[@]}\"); printf '%s' \"${a[0]}\"\n", "78FF79", "", 0)]
    [InlineData("i='['; a=([$i]=$'\\377'); printf '%s' \"${a[0]}\"\n", "5B5B5D3DFF", "", 0)]
    public async Task Evaluation_CompoundItemsAndAppendsRetainByteValues(
        string script, string outputHex, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("a=([2]='", "' [5]='' tail); printf '<%s>' \"${a[@]}\"\n", "3CFF3E3C3E3C7461696C3E", "", 0)]
    [InlineData("a=('", "' '' last); printf '<%s>' \"${a[@]}\"\n", "3CFF3E3C3E3C6C6173743E", "", 0)]
    [InlineData("false && a=([", "]=$(printf forbidden >&2)); printf done\n", "646F6E65", "", 0)]
    [InlineData("a=([0]=$(printf first >&2) [", "]=$(printf rhs >&2) $(printf later >&2)); printf after\n", "", "66697273747268736C61746572FF3A20696E76616C69642061726974686D657469632065787072657373696F6E0A", 1)]
    [InlineData("a[", "]=($(printf forbidden >&2)); printf after\n", "", "615BFF5D3A2063616E6E6F742061737369676E206C69737420746F206172726179206D656D6265720A", 1)]
    [InlineData("a=([", "]=${missing:?boom}); printf after\n", "", "6D697373696E673A20626F6F6D0A", 1)]
    public async Task Evaluation_CompoundSourcePreservesBytesAndEffectOrder(
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

    // Bash-only EXIT probes observe these completed writes after each fatal stage.
    [Theory]
    [InlineData("a=(old); i=$'\\377'; a=([0]=$'\\376' [$i]=bad)\n", new[] { "FE" }, "FF3A20696E76616C69642061726974686D657469632065787072657373696F6E0A")]
    [InlineData("a=($'\\377'); a=(first \"${a[2]:=$'\\376'}\" \"${missing:?boom}\")\n", new[] { "FF", "", "FE" }, "6D697373696E673A20626F6F6D0A")]
    public async Task Evaluation_CompoundFailureRetainsCompletedByteWrites(
        string script, string[] expectedArrayHex, string errorHex)
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(script));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(1, evaluation.LastExitCode);
        Assert.Equal(expectedArrayHex.Select(hex => ShellValue.FromBytes(Convert.FromHexString(hex))),
            evaluation.IndexedArrays["a"]);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.Equal(Convert.FromHexString(errorHex), host.StandardError.Content.ToArray());
    }

    [Fact]
    public void Parse_CompoundSelectorRetainsBytesAndCarriageReturn()
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append("a=([1\r + '"u8).Append(0xff).Append("']=value)"u8);

        var command = Assert.IsType<SimpleCommand>(Assert.Single(Parser.Parse(source.ToMemory()).Commands));
        var items = Assert.Single(command.Assignments).ArrayValues;
        Assert.NotNull(items);
        var element = Assert.Single(items).Element;
        Assert.NotNull(element);
        Assert.Equal(Convert.FromHexString("310D202B2027FF27"), element.Subscript.Bytes.ToArray());
    }
}

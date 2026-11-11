using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteCommandSelectionTests
{
    // Bash 5.2.21/C.UTF-8 with pipefail; diagnostics omit only the executable/file/line prefix.
    [Theory]
    [InlineData("cmd=(printf '<%s>' $'\\377' '' $'\\376'); \"${cmd[@]}\"\n", "3CFF3E3C3E3CFE3E", "", 0)]
    [InlineData("args=(-- printf '%s|%s' $'\\377' $'\\376'); command \"${args[@]}\"; printf after\n", "FF7CFE6166746572", "", 0)]
    [InlineData("args=(-- printf '%s|%s' $'\\377' $'\\376'); builtin \"${args[@]}\"; printf after\n", "FF7CFE6166746572", "", 0)]
    [InlineData("args=(-- printf '%s|%s' $'\\377' $'\\376'); exec \"${args[@]}\"; printf after\n", "FF7CFE", "", 0)]
    [InlineData("IFS=$'\\377'; cmd=$'printf\\377%s\\377\\376'; $cmd\n", "FE", "", 0)]
    [InlineData("$(printf 'printf %%s \\377'); printf after\n", "FF6166746572", "", 0)]
    [InlineData("v=$'\\377['; printf '%s' $v\n", "FF5B", "", 0)]
    [InlineData("empty=(); v=old; v=$'\\377' \"${empty[@]}\"; printf '%s' \"$v\"\n", "FF", "", 0)]
    [InlineData("x=0; args=(printf '%s' $'\\377'); command \"$(printf -- --)\" \"${args[@]}\" \"$((x++))\"; printf ':%s' \"$x\"\n", "FF303A31", "", 0)]
    [InlineData("command ${missing:?$'\\377'} \"$(printf forbidden >&2)\"; printf after\n", "", "6D697373696E673A20FF0A", 1)]
    [InlineData("args=(printf '%s' $'\\303' $'\\251'); builtin \"${args[@]}\"\n", "C3A9", "", 0)]
    [InlineData("args=(command builtin -- printf '%s' $'\\377'); \"${args[@]}\"\n", "FF", "", 0)]
    public async Task Evaluation_SelectedFieldsPreserveBytesAndEffects(
        string script, string outputHex, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("cmd=(printf '%s' '", "'); \"${cmd[@]}\"\n", "FF")]
    [InlineData("$(printf 'printf %%s ", "'); printf after\n", "FF6166746572")]
    [InlineData("empty=(); \"${empty[@]}\" printf '%s' '", "'\n", "FF")]
    [InlineData("v='", "['; printf '%s' $v\n", "FF5B")]
    public async Task Evaluation_SelectionRetainsRawSourceFields(string prefix, string suffix, string outputHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void GlobPreflight_PreservesQuotingAcrossByteSegments(bool prefixQuoted, bool suffixQuoted, bool expected)
    {
        var prefixMask = prefixQuoted ? (byte)0 : GlobMask.Active;
        var suffixMask = suffixQuoted ? (byte)0 : GlobMask.Active;
        var mask = new GlobMask(new byte[] { prefixMask, prefixMask, suffixMask }).AsSpan();
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromBytes(new byte[] { 0xff, (byte)'[' }), prefixQuoted),
            new ExpandedSegment(ShellValue.FromText("]"), suffixQuoted)]);

        Assert.Equal(expected, GlobPattern.RequiresExpansion(word));
        Assert.Equal(expected, GlobPattern.RequiresExpansion("ÿ[]".AsSpan(), in mask));
    }

    [Fact]
    public void GlobPreflight_QuotedSlashStillSeparatesPathComponents()
    {
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromBytes(new byte[] { 0xff, (byte)'[' }), isGlobQuoted: false),
            new ExpandedSegment(ShellValue.FromText("/"), isGlobQuoted: true),
            new ExpandedSegment(ShellValue.FromText("]"), isGlobQuoted: false)]);
        var mask = new GlobMask(new byte[] { GlobMask.Active, GlobMask.Active, 0, GlobMask.Active }).AsSpan();

        Assert.False(GlobPattern.RequiresExpansion(word));
        Assert.False(GlobPattern.RequiresExpansion("ÿ[/]".AsSpan(), in mask));
    }
}

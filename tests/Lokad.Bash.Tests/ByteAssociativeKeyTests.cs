using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteAssociativeKeyTests
{
    // Recorded byte-stream contracts; ordering has a separate platform assertion.
    [Theory]
    [InlineData("declare -A a=([$'\\377']=4); k=$'\\377'; printf '<%s><%s>' \"$((a[$k]+=3))\" \"${a[$k]}\"", "3C373E3C373E", "")]
    [InlineData("declare -A a; k=$'\\377'; printf '<%s><%s><%s>' \"${a[$k]:=$'\\376'}\" \"${a[$k]}\" \"${a[$'\\376']-missing}\"", "3CFE3E3CFE3E3C6D697373696E673E", "")]
    [InlineData("declare -A a; a[$(printf selector >&2; printf '\\377')]=$(printf rhs >&2; printf value); printf '<%s>' \"${a[$'\\377']}\"", "3C76616C75653E", "72687373656C6563746F72")]
    [InlineData("declare -A a=([$'\\377']=$'\\376'); a+=([$'\\377']+=x); printf '<%s><%s>' \"${a[$'\\377']}\" \"${!a[@]}\"", "3CFE783E3CFF3E", "")]
    [InlineData("declare -A a=([$'\\377']=first [$'\\376']=second [$'\\303']=third [é]=fourth); printf '<%s>' \"${#a[@]}\" \"${a[$'\\377']}\" \"${a[$'\\376']}\" \"${a[$'\\303']}\" \"${a[é]}\"", "3C343E3C66697273743E3C7365636F6E643E3C74686972643E3C666F757274683E", "")]
    [InlineData("declare -A a=([$'\\377']=$'\\376'); declare -p a; snapshot=$(declare -p a); unset a; eval \"$snapshot\"; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", "6465636C617265202D4120613D285B24275C333737275D3D24275C3337362720290A3CFF3E3CFE3E", "")]
    public async Task References_PreserveKeyBytesAcrossConsumers(string script, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("declare -A a; a['", "']=value; printf '<%s>' \"${!a[@]}\"", "3CFF3E")]
    [InlineData("declare -A a; declare 'a[", "]=value'; printf '%s' \"${a[@]}\"", "76616C7565")]
    public async Task RawSourceKeys_ReachTheSameStorage(string prefix, string suffix, string outputHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Fact]
    public void KeyEnumeration_UsesUnsignedByteOrderIncludingMalformedUtf8()
    {
        // A platform ordering contract, not Bash's hash-table enumeration order.
        var orderedKeys = new[] { "C3", "C3A9", "EE8080", "F0908080", "FE", "FF" }
            .Select(hex => ShellValue.FromBytes(Convert.FromHexString(hex))).ToArray();
        var variables = Variables.CreateEmpty();
        foreach (var key in orderedKeys.Reverse())
            Assert.True(variables.TrySetArrayElement("a", key, key, ArrayKind.Associative));

        var array = variables.AssociativeArrays["a"];
        Assert.Equal(orderedKeys, array.PresentKeys);
        Assert.Equal(orderedKeys, array.PresentValues);
        Assert.Equal(orderedKeys, array.Clone().CaptureValues().Keys);
    }

    [Fact]
    public void KeyRendering_PreservesCarriageReturn()
    {
        // LF-only platform input: CR remains data even where native Bash trims it.
        var variables = Variables.CreateEmpty();
        var key = ShellValue.FromBytes(new byte[] { 0xff, 0x0d });
        Assert.True(variables.TrySetArrayElement("a", key, ShellValue.FromText("value"), ArrayKind.Associative));
        var expected = Utf8Text.Encode("declare -A a=([$'\\377\\r']=\"value\" )\n");
        var complete = ByteMemoryBuilder.CreateUnaccounted(0);

        Assert.True(variables.TryAppendVariable("a", complete, VariableDisplayStyle.Declaration));
        Assert.Equal(expected.ToArray(), complete.ToMemory().ToArray());
        Assert.Equal(key, Assert.Single(variables.AssociativeArrays["a"].PresentKeys));
    }
}

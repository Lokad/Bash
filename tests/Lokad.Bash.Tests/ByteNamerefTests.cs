using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteNamerefTests
{
    // Recorded byte contracts; target inspection never evaluates its stored selector.
    [Theory]
    [InlineData("declare -A a=([$'\\377']=2); declare -n ref=$'a[\\377]'; printf '<%s><%s>' \"$((ref+=3))\" \"${a[$'\\377']}\"", "3C353E3C353E")]
    [InlineData("declare -A a=([$'\\377']=outer); f(){ local -n r=$'a[\\377]'; local r=inner; tmp=x printf -v r new; printf '<%s>/<%s>/<%s>' \"$?\" \"$r\" \"${a[$'\\377']}\"; }; f", "3C303E2F3C6E65773E2F3C6F757465723E")]
    [InlineData("declare -A a=([$'\\377']=outer); f(){ local -n r=$'a[\\377]' s=$'a[\\377]'; local r=inner; declare +n r=new; printf '<%s>/<%s>' \"$r\" \"$s\"; }; f", "3C6E65773E2F3C696E6E65723E")]
    [InlineData("declare -A a=([$'\\377']=outer); f(){ local -n r=$'a[\\377]'; local r=inner; declare +n r; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${a[$'\\377']}\"", "3C303E2F3C615BFF5D3E3C6F757465723E")]
    [InlineData("a=(outer); f(){ local -n r='a[0]'; local r=$'\\377'; printf '<%s>/<%s>/<%s>' \"$?\" \"$r\" \"${a[0]}\"; }; f", "3C303E2F3CFF3E2F3C6F757465723E")]
    [InlineData("declare -A a=([$'\\377']=outer); f(){ local -n r=$'a[\\377]'; local r=inner; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${a[$'\\377']}\"", "3C303E2F3C696E6E65723E3C6F757465723E")]
    [InlineData("declare -A a=([$'\\377']=outer); f(){ local -n r=$'a[\\377]'; declare +n r=inner; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${a[$'\\377']}\"", "3C303E2F3C696E6E65723E3C6F757465723E")]
    [InlineData("declare -A a=([$'\\377']=outer); f(){ local -n r=$'a[\\377]'; local r=inner; (r=child; printf '<%s>' \"$r\"); printf '<%s>' \"$r\"; }; f; printf '<%s>' \"${a[$'\\377']}\"", "3C6368696C643E3C696E6E65723E3C6F757465723E")]
    // Bash 5.2 crashes on this default; retain the ordinary selected-element write contract.
    [InlineData("declare -A a=(); declare -n r=$'a[\\377]'; : ${r:=new}; printf '<%s>' \"${a[$'\\377']}\"", "3C6E65773E")]
    [InlineData("declare -A a=([$'\\377']=old [keep]=sibling); declare -n r=$'a[\\377]' s=r; unset s; printf '<%s>/<%s>/<%s>' \"${!s}\" \"${a[$'\\377']-unset}\" \"${a[keep]}\"", "3C615BFF5D3E2F3C756E7365743E2F3C7369626C696E673E")]
    [InlineData("declare -A a=([$'\\377']=old); declare -n ref=$'a[\\377]'; printf '<%s><%s><%s>' \"$ref\" \"${!ref}\" \"${#ref}\"", "3C6F6C643E3C615BFF5D3E3C303E")]
    [InlineData("declare -A a=([$'\\377']=old); declare -n ref=$'a[\\377]'; ref=new; printf '<%s>' \"${a[$'\\377']}\"", "3C6E65773E")]
    [InlineData("declare -A a=([$'\\377']=old); declare -n ref=$'a[\\377]' hop=ref; printf '<%s><%s>' \"$hop\" \"${!hop}\"", "3C6F6C643E3C615BFF5D3E")]
    [InlineData("declare -A a=([$'\\377']=old); declare -n ref=$'a[\\377]'; f(){ local -n ref='other'; ref=inner; }; f; printf '<%s><%s><%s>' \"$ref\" \"${!ref}\" \"$other\"", "3C6F6C643E3C615BFF5D3E3C696E6E65723E")]
    [InlineData("declare -A a=([$'\\377']=old); declare -n ref; ref=$'a[\\377]'; printf '<%s><%s>' \"$ref\" \"${!ref}\"", "3C6F6C643E3C615BFF5D3E")]
    [InlineData("declare -A a=([$'\\377']=outer); declare -n ref=$'a[\\377]'; f(){ local -A a=([$'\\377']=local); declare -g ref=new; printf '<%s>' \"${a[$'\\377']}\"; }; f; printf '<%s>' \"${a[$'\\377']}\"", "3C6E65773E3C6F757465723E")]
    [InlineData("i=0; declare -n ref='a[i++]'; printf '<%s><%s><%s>' \"${!ref}\" \"${#ref}\" \"$i\"", "3C615B692B2B5D3E3C303E3C303E")]
    [InlineData("declare -A a=([key]=old [keep]=sibling); declare -n ref='a[key]'; printf '<%s><%s><%s>' \"${ref[@]}\" \"${ref[0]}\" \"${#ref}\"", "3C3E3C303E3C3E")]
    [InlineData("declare -n ref=$'a[\\377]'; declare +n ref; printf '<%s>' \"$ref\"; declare -p ref", "3C615BFF5D3E6465636C617265202D2D207265663D2427615B5C3337375D270A")]
    [InlineData("declare -n ref=$'a[\\377]'; declare -p ref", "6465636C617265202D6E207265663D2427615B5C3337375D270A")]
    public async Task RetainsTargetBytesAcrossConsumers(string script, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("export")]
    [InlineData("readonly")]
    public async Task AttributeDiagnosticsRetainElementTargetBytes(string builtin)
    {
        var script = "declare -A a=(); declare -n r=$'a[\\377]'; " + builtin + " r";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        var expected = ByteMemoryBuilder.CreateUnaccounted(0).Append(ShellValue.FromText(builtin).Bytes)
            .Append(": `a["u8).Append(0xff).Append("]': not a valid identifier\n"u8).ToMemory();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(expected.ToArray(), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public void CloneAndRestoreKeepTypedElementTargetsOutOfWholeVariableLookup()
    {
        var variables = Variables.CreateEmpty();
        var target = ShellValue.FromBytes(new byte[] { (byte)'a', (byte)'[', 0xff, (byte)']' });
        Assert.True(variables.TrySetNameref("ref", target));
        var clone = variables.Clone();
        Assert.True(clone.TrySetNameref("ref", ShellValue.FromText("other")));
        Assert.Equal(target, variables.Namerefs["ref"]);

        clone.CopyBindingFrom(variables, "ref");
        Assert.Equal(target, clone.Namerefs["ref"]);
        Assert.True(clone.TryResolveBinding("ref", selectOwner: null, out var binding));
        Assert.Equal(VariableReferenceKind.Element, binding.Reference.Kind);
        Assert.Equal("a", binding.Reference.Name);
        Assert.Equal(new byte[] { 0xff }, binding.Reference.Subscript.Bytes.ToArray());
        Assert.False(clone.TryGetScalar("ref", out _));
        Assert.Equal(string.Empty, clone.ResolveNamerefName("ref"));
    }
}

using System.Runtime.InteropServices;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteStoredValueTests
{
    // Bash 5.2.21, LC_ALL=C.UTF-8; compare bytes without a text-decoding test adapter.
    [Theory]
    [InlineData("x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("a=($'\\377' $'\\303' $'\\251'); printf '<%s>' \"${a[@]}\"\n", "3CFF3E3CC33E3CA93E")]
    [InlineData("a[3]=$'\\377'; printf '<%s>' \"${a[3]}\"\n", "3CFF3E")]
    [InlineData("a=($'\\377'); printf '<%s>' \"$a\" \"${a[0]}\"", "3CFF3E3CFF3E")]
    [InlineData("declare -A a; a=(0 $'\\377'); printf '<%s>' \"$a\" \"${a[0]}\"", "3CFF3E3CFF3E")]
    [InlineData("a=([1]=keep); printf '<%s>' \"${a:=$'\\377'}\" \"${a[0]}\" \"${a[1]}\"", "3CFF3E3CFF3E3C6B6565703E")]
    [InlineData("x=$'\\376'; f(){ declare x=$'\\377'; printf '<%s>' \"$x\"; }; f; printf '<%s>' \"$x\"", "3CFF3E3CFE3E")]
    [InlineData("f(){ printf '<%s>' \"$X\"; }; X=($'\\377' $'\\376') f", "3C28FF20FE293E")]
    [InlineData("x=$'\\376'; f(){ local x; local x=$'\\377'; printf '<%s>' \"$x\"; }; f; printf '<%s>' \"$x\"", "3CFF3E3CFE3E")]
    [InlineData("declare -A a; a[k]=$'\\377'; printf '<%s>' \"${a[k]}\"\n", "3CFF3E")]
    [InlineData("printf '<%s>' \"${missing:-$'\\377'}\"\n", "3CFF3E")]
    [InlineData("printf '<%s>' \"${x:=$'\\377'}\" \"$x\"\n", "3CFF3E3CFF3E")]
    [InlineData("x=$'\\377'; printf '<%s>' \"${x:+$'\\376'}\"\n", "3CFE3E")]
    [InlineData("x=$'\\377'; y=\"${x:-no}\"; printf '<%s>' \"$y\"\n", "3CFF3E")]
    [InlineData("export x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("readonly x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("declare x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("x=$'\\376'; f(){ local x=$'\\377'; printf '<%s>' \"$x\"; }; f; printf '<%s>' \"$x\"\n", "3CFF3E3CFE3E")]
    [InlineData("i=0; declare x=$((i++)); printf '<%s:%s>' \"$i\" \"$x\"\n", "3C313A303E")]
    [InlineData("set -- $'\\377' $'\\376'; printf '<%s>' \"$1\" \"$2\"\n", "3CFF3E3CFE3E")]
    [InlineData("set -- $'\\376'; f(){ printf '<%s>' \"$1\"; }; f $'\\377'; printf '<%s>' \"$1\"\n", "3CFF3E3CFE3E")]
    [InlineData("set -- $'\\377' $'\\376'; shift; printf '<%s>' \"$1\"\n", "3CFE3E")]
    [InlineData("for x in $'\\377' $'\\376'; do printf '<%s>' \"$x\"; done\n", "3CFF3E3CFE3E")]
    [InlineData("x=$'\\377'; ( x=$'\\376'; printf '<%s>' \"$x\" ); printf '<%s>' \"$x\"\n", "3CFE3E3CFF3E")]
    [InlineData("x=$(printf '\\xff\\n\\n'); printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("printf -v x '\\xff\\0tail'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("printf -v 'a[3]' '\\xff'; printf '<%s>' \"${a[3]}\"\n", "3CFF3E")]
    [InlineData("IFS=$'\\377'; x=$'a\\377b'; printf '<%s>' $x\n", "3C613E3C623E")]
    [InlineData("IFS=$'\\377'; set -- a b; printf '<%s>' \"$*\"\n", "3C61FF623E")]
    [InlineData("IFS=$'\\377'; set -- a b; printf '<%s>' $*\n", "3C613E3C623E")]
    [InlineData("IFS=é; x=$'a\\303\\251b'; printf '<%s>' $x\n", "3C613E3C623E")]
    [InlineData("IFS=$'\\377'; x=é; printf '<%s>' $x\n", "3CC3A93E")]
    [InlineData("IFS=$'\\377'; set -- '' b ''; printf '<%s>' \"$*\"\n", "3CFF62FF3E")]
    [InlineData("IFS=; set -- $'\\303' $'\\251'; printf '<%s>' \"$*\"\n", "3CC3A93E")]
    [InlineData("HOME=$'/\\377'; printf '<%s>' ~\n", "3C2FFF3E")]
    [InlineData("IFS= read -r x <<< $'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("IFS= read x <<< $'\\\\\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("IFS= read -r x <<< $'\\377\\r'; printf '<%s>' \"$x\"\n", "3CFF0D3E")]
    [InlineData("IFS=$'\\377' read -r a b <<< $'a\\377b'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C623E")]
    [InlineData("IFS=é read -r a b <<< $'a\\303\\251b'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C623E")]
    [InlineData("IFS=é read -r a b <<< $'a\\303b'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C623E")]
    [InlineData("IFS=$'\\377' read a b <<< $'a\\\\\\377b\\377c'; printf '<%s>' \"$a\" \"$b\"\n", "3C61FF623E3C633E")]
    [InlineData("IFS=$'\\377' read a b <<< $'a\\377b\\377c'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C62FF633E")]
    [InlineData("IFS= read x <<'EOF'\na\\\nÿ\nEOF\nprintf '<%s>' \"$x\"\n", "3C61C3BF3E")]
    [InlineData("cat <<< $'\\377'\n", "FF0A")]
    [InlineData("cat <<< $'\\377\\n'\n", "FF0A0A")]
    [InlineData("x=$'\\377'; cat <<EOF\n$x\nEOF\n", "FF0A")]
    [InlineData("getopts a opt -a; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3C613E")]
    [InlineData("getopts é opt -é; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3CC33E")]
    [InlineData("set -- -é; getopts é opt; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3CC33E")]
    [InlineData("getopts $'\\200' opt $'-\\200'; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3C803E")]
    [InlineData("set -- $'-\\200'; getopts $'\\200' opt; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3C803E")]
    [InlineData("p=18446744073709551617; ((p)); printf '%s' \"$?\"; let p; printf '%s' \"$?\"; "
        + "printf '%s' \"$((p))\"; for ((i=p;i<2;i++)); do printf '%s' \"$i\"; done", "30303131")]
    [InlineData("x=$'\\376'; f(){ y=$x; x=$'\\375'; }; x=$'\\377' f; printf '<%s>' \"$x\" \"$y\"", "3CFE3E3CFF3E")]
    [InlineData("a=($'\\376' $'\\375'); a=temp printf -v a '\\xff\\0tail'; printf '<%s>' \"${a[@]}\"", "3CFF3E3CFD3E")]
    [InlineData("a=($'\\376' $'\\375'); f(){ declare -n ref=a; a=temp printf -v ref '\\xff'; }; f; printf '<%s>' \"${a[@]}\"", "3CFF3E3CFD3E")]
    [InlineData("x=$'\\376'; declare -n ref=x; x=temp printf -v ref '\\xff'; printf '<%s>' \"$x\"", "3CFE3E")]
    [InlineData("x=$'\\376'; f(){ y=temp eval 'export x'; }; x=$'\\377' f; printf '<%s>' \"$x\"", "3CFF3E")]
    [InlineData("x=$'\\376'; f(){ y=temp eval 'readonly x'; }; x=$'\\377' f; printf '<%s>' \"$x\"", "3CFF3E")]
    [InlineData("opt=($'\\376' $'\\375'); opt=temp getopts $'\\200' opt $'-\\200'; printf '<%s>' \"${opt[@]}\"", "3C803E3CFD3E")]
    [InlineData("a=(8 $'\\377'); a=1 let 'a=a+1' 'a=a+1'; printf '<%s>' \"${a[@]}\"", "3C333E3CFF3E")]
    [InlineData("set -- $'\\377' $'\\376'; printf '<%s>' \"${x:-\"$@\"}\"", "3CFF3E3CFE3E")]
    [InlineData("set -- $'\\377' $'\\376'; printf '<%s>' \"${x:=\"$@\"}\" \"$x\"", "3CFF20FE3E3CFF20FE3E")]
    [InlineData("set -- $'\\377' $'\\376'; IFS=:; printf '<%s>' ${x:-\"$*\"}", "3CFF3AFE3E")]
    [InlineData("set -- $'\\377' $'\\376'; x=pre${value:-\"$@\"}post; printf '<%s>' \"$x\"", "3C707265FF20FE706F73743E")]
    [InlineData("set -- '' $'\\377' ''; x=${value:-${inner:-$@}}; printf '<%s>' \"$x\"", "3C20FF203E")]
    [InlineData("set -- $'\\377' $'\\376'; cat <<EOF\n${value:-\"$@\"}\ntail\nEOF\n", "FF20FE0A7461696C0A")]
    public async Task Evaluation_PreservesStoredBytes(string script, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task Source_RetainsByteArgumentsAndRestoresCallerPositionals()
    {
        var tree = EndToEndHarness.BuildTree([("/script", "printf '<%s>' \"$1\"")]);
        var result = await EndToEndHarness.RunScriptAsync(
            "set -- $'\\376'; source /script $'\\377'; printf '<%s>' \"$1\"", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("3CFF3E3CFE3E"), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData(false, "", "")]
    [InlineData(false, "a", "61")]
    [InlineData(true, "", "")]
    [InlineData(true, "\\xff", "FF")]
    public async Task Printf_TruncatedAssignmentsDoNotRetainDiscardedOutput(bool arrayTarget, string prefix, string expectedHex)
    {
        var target = arrayTarget ? "values[0]" : "value";
        var evaluation = new Evaluation.Evaluation(Parser.Parse(
            $"printf -v '{target}' '{prefix}\\0%524288s' ''"));

        Assert.Null(await evaluation.GetNextStep());
        Assert.Equal(0, evaluation.LastExitCode);
        var stored = arrayTarget ? evaluation.IndexedArrays["values"][0] : evaluation.Variables["value"];
        Assert.Equal(Convert.FromHexString(expectedHex), stored.Bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(stored.Bytes, out var storage));
        Assert.NotNull(storage.Array);
        Assert.True(storage.Array.Length <= 16, $"Retained {storage.Array.Length} bytes for a truncated value.");
    }

    [Theory]
    [InlineData("5C000A", "", "next")]
    [InlineData("5C000A", "-r", "next")]
    [InlineData("5C005C0A", "", "")]
    [InlineData("5C005C0A", "-r", "next")]
    [InlineData("5C5C000A", "", "next")]
    [InlineData("5C0A", "", "")]
    [InlineData("5C0A", "-r", "next")]
    public async Task Read_ContinuationUsesTheUnfilteredPhysicalLine(string firstLineHex, string options, string expectedNext)
    {
        // Compare line consumption with Bash, not its escaped-NUL truncation/sentinel quirks.
        var input = Convert.FromHexString(firstLineHex + "6E6578740A");
        var result = await EndToEndHarness.RunScriptAsync(
            $"IFS= read {options} first; IFS= read {options} next; printf '%s' \"$next\"",
            EndToEndHarness.BuildTree([]), input);

        Assert.Equal(expectedNext, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void AssociativeAssignment_RejectsEmptyKeysWithoutReplacingExistingValues()
    {
        var variables = Variables.CreateEmpty();
        var original = ShellValue.FromText("original");
        Assert.True(variables.TrySetScalar("value", original));

        Assert.False(variables.TryAssignArray("value",
            [ShellValue.Empty, ShellValue.Empty], ArrayKind.Associative));

        Assert.True(variables.TryGetScalar("value", out var retained));
        Assert.Equal(original, retained);
        Assert.Empty(variables.AssociativeArrays);
    }

    [Fact]
    public async Task Invocation_ExportsByteValuesWithoutDecoding()
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse("X=$'\\377' inspect"));
        var operation = Assert.IsType<RunCommand>(await evaluation.GetNextStep());
        var command = operation.Command;
        var value = Assert.Single(command.Environment, variable => variable.Name == "X").Value;

        Assert.Equal(new byte[] { 0xff }, value.Bytes.ToArray());
        operation.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await evaluation.GetNextStep());
        Assert.False(evaluation.Variables.ContainsKey("X"));
    }

    [Fact]
    public void Clone_DetachesBindingsWhileRetainingImmutableByteStorage()
    {
        var value = ShellValue.FromBytes(new byte[] { 0xff, 0xc3 });
        var variables = Variables.CreateInitial(
            new Dictionary<BindingName, ShellValue> { ["x"] = value }, [value]);
        Assert.True(variables.TrySetArrayElement("a", ShellValue.FromText("3"), value, ArrayKind.Indexed));
        variables.SetExported("x", true);

        var clone = variables.Clone();
        Assert.True(clone.TryGetScalar("x", out var scalar));
        Assert.True(value.Bytes.Equals(scalar.Bytes));
        Assert.True(value.Bytes.Equals(clone.PositionalParameters[0].Bytes));
        Assert.True(value.Bytes.Equals(Assert.Single(clone.BuildExportedEnvironment()).Value.Bytes));
        Assert.True(IndexedArrayIndex.TryCreate(3, out var index));
        Assert.True(clone.IndexedArrays["a"].TryGet(index, out var arrayValue));
        Assert.True(value.Bytes.Equals(arrayValue.Bytes));

        Assert.True(clone.TrySetScalar("x", ShellValue.Empty));
        Assert.True(variables.TryGetScalar("x", out var original));
        Assert.Equal(value, original);
    }
}

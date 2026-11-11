using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class DeclarationInspectionTestsEndToEnd
{
    // Git Bash 5.2.37. Only the native script path/line prefix is removed from diagnostics.
    [Theory]
    [InlineData("x='two words'; declare -p x", "declare -- x=\"two words\"\n", "")]
    [InlineData("x=value; declare -p missing x; printf 'status=<%s>\\n' \"$?\"", "declare -- x=\"value\"\nstatus=<1>\n", "declare: missing: not found\n")]
    [InlineData("declare x; xempty=''; declare -p x xempty", "declare -- x\ndeclare -- xempty=\"\"\n", "")]
    [InlineData("target=value; declare -n ref=target empty; declare -p ref empty", "declare -n ref=\"target\"\ndeclare -n empty\n", "")]
    [InlineData("declare -n first=second second=first; declare -p first second", "declare -n first=\"second\"\ndeclare -n second=\"first\"\n", "")]
    [InlineData("declare -xrx x=value; declare -p x; declare -prx missing; printf 'status=<%s>\\n' \"$?\"", "declare -rx x=\"value\"\nstatus=<1>\n", "declare: missing: not found\n")]
    [InlineData("x=value; declare -prx x; declare -p x", "declare -- x=\"value\"\ndeclare -- x=\"value\"\n", "")]
    [InlineData("x=old; declare -p x=new; printf 'status=<%s> value=<%s>\\n' \"$?\" \"$x\"", "status=<1> value=<old>\n", "declare: x=new: not found\n")]
    [InlineData("declare -pa x=(one two); declare -p x", "declare -a x=([0]=\"one\" [1]=\"two\")\ndeclare -a x=([0]=\"one\" [1]=\"two\")\n", "")]
    [InlineData("declare -a unset; empty=(); values=([2]=two [9]='nine words'); declare -p unset empty values", "declare -a unset\ndeclare -a empty=()\ndeclare -a values=([2]=\"two\" [9]=\"nine words\")\n", "")]
    [InlineData("declare -A unset empty=() values=([key]='two words'); declare -p unset empty values", "declare -A unset\ndeclare -A empty=()\ndeclare -A values=([key]=\"two words\" )\n", "")]
    [InlineData("arr=(zero one); declare -p 'arr[1]'; printf 'status=<%s>\\n' \"$?\"", "status=<1>\n", "declare: arr[1]: not found\n")]
    [InlineData("x=global; f() { local x=local; declare -gp x; local -p x; }; f; declare -p x", "declare -- x=\"local\"\ndeclare -- x=\"local\"\ndeclare -- x=\"global\"\n", "")]
    [InlineData("x=global; f() { local -p x; printf 'status=<%s>\\n' \"$?\"; }; f", "status=<1>\n", "local: x: not found\n")]
    [InlineData("x=old; export -p x=new; declare -p x", "declare -x x=\"new\"\n", "")]
    [InlineData("x=old; readonly -p x; declare -p x", "declare -r x=\"old\"\n", "")]
    [InlineData("x='a\"b$c`d\\e!f'; declare -p x", "declare -- x=\"a\\\"b\\$c\\`d\\\\e!f\"\n", "")]
    [InlineData("printf -v x 'a\\nb\\rc\\td\\033e\\001'; declare -p x", "declare -- x=$'a\\nb\\rc\\td\\Ee\\001'\n", "")]
    [InlineData("x=global; f() { local x=local; declare -gp x; declare -p x; }; f", "declare -- x=\"local\"\ndeclare -- x=\"local\"\n", "")]
    [InlineData("f() { local x=old; local -a x; declare -p x; }; f", "declare -a x=()\n", "")]
    [InlineData("x=old; declare -a x; declare -p x", "declare -a x=([0]=\"old\")\n", "")]
    [InlineData("declare -a x; x+=(); declare -p x", "declare -a x=()\n", "")]
    [InlineData("f() { local x=old; local -p x=new; printf 'status=<%s> value=<%s>\\n' \"$?\" \"$x\"; }; f", "status=<1> value=<old>\n", "local: x=new: not found\n")]
    [InlineData("x=old; readonly -p x=new; declare -p x", "declare -r x=\"new\"\n", "")]
    [InlineData("declare x; declare -a x; declare -p x; f(){ local y; local -a y; declare -p y; }; f", "declare -a x\ndeclare -a y\n", "")]
    [InlineData("declare -a x; declare -a x; declare -p x; x=(); declare -a x; declare -p x", "declare -a x\ndeclare -a x=()\n", "")]
    [InlineData("declare -A x; x+=(); declare -p x", "declare -A x=()\n", "")]
    [InlineData("x=old; f(){ local -a x; declare -p x;}; f; declare -p x", "declare -a x\ndeclare -- x=\"old\"\n", "")]
    [InlineData("declare -a x; f(){ x+=(); }; f; declare -p x; declare -A y; (y=()); declare -p y", "declare -a x=()\ndeclare -A y\n", "")]
    [InlineData("declare -A x=(['!']=\"x\"); declare -p x; x=(['@']=\"x\"); declare -p x; x=(['a=b']=\"x\"); declare -p x", "declare -A x=([\"!\"]=\"x\" )\ndeclare -A x=([\"@\"]=\"x\" )\ndeclare -A x=([a=b]=\"x\" )\n", "")]
    [InlineData("x=old; declare -p x+=new; printf 'status=<%s> value=<%s>\\n' \"$?\" \"$x\"", "status=<1> value=<old>\n", "declare: x+=new: not found\n")]
    [InlineData("x=old; x=temp declare -p x; declare -p x", "declare -x x=\"temp\"\ndeclare -- x=\"old\"\n", "")]
    [InlineData("i=0; x=old; declare -p x=$((i++)); printf 'status=<%s> i=<%s> value=<%s>\\n' \"$?\" \"$i\" \"$x\"", "status=<1> i=<1> value=<old>\n", "declare: x=0: not found\n")]
    [InlineData("declare -A x=(['a@b']=value); declare -p x; declare -A x=(['%']=value); declare -p x; declare -A x=(['a%']=value); declare -p x; declare -A x=(['+']=value); declare -p x; declare -A x=(['=']=value); declare -p x; declare -A x=([':']=value); declare -p x; declare -A x=([',']=value); declare -p x; declare -A x=(['.']=value); declare -p x; declare -A x=(['/']=value); declare -p x; declare -A x=(['-']=value); declare -p x; declare -A x=(['_']=value); declare -p x; declare -A x=(['~']=value); declare -p x; declare -A x=(['a~b']=value); declare -p x; declare -A x=(['#']=value); declare -p x; declare -A x=(['a#b']=value); declare -p x; declare -A x=(['a!b']=value); declare -p x", "declare -A x=([a@b]=\"value\" )\ndeclare -A x=([%]=\"value\" )\ndeclare -A x=([a%]=\"value\" )\ndeclare -A x=([+]=\"value\" )\ndeclare -A x=([=]=\"value\" )\ndeclare -A x=([:]=\"value\" )\ndeclare -A x=([,]=\"value\" )\ndeclare -A x=([.]=\"value\" )\ndeclare -A x=([/]=\"value\" )\ndeclare -A x=([-]=\"value\" )\ndeclare -A x=([_]=\"value\" )\ndeclare -A x=([\"~\"]=\"value\" )\ndeclare -A x=([a~b]=\"value\" )\ndeclare -A x=([\"#\"]=\"value\" )\ndeclare -A x=([a#b]=\"value\" )\ndeclare -A x=([\"a!b\"]=\"value\" )\n", "")]
    public async Task Evaluation_NamedDeclarations_InspectBindingsWithoutScalarWrites(
        string script, string expectedOutput, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // K: preserve ordinal associative order and the existing UTF-8 quoting policy, not native hash/locale order.
    [Theory]
    [InlineData("x='élève 😀'; declare -p x", "declare -- x=\"élève 😀\"\n")]
    [InlineData("declare -A x=([key]=plain ['a b']='two words' ['d$']='quote\"'); declare -p x", "declare -A x=([\"a b\"]=\"two words\" [\"d\\$\"]=\"quote\\\"\" [key]=\"plain\" )\n")]
    public async Task Evaluation_NamedDeclarations_RetainDeterministicRendering(string script, string expectedOutput)
    {
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_FailedCompoundElement_RetainsAssignedEmptyArray()
    {
        // Native prints the same binding; its bad-subscript subject is "[-1]=value" instead of "x[-1]".
        var result = await EndToEndHarness.RunScriptAsync("declare -a x; x=([-1]=value); declare -p x",
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -a x=()\n", result.StdOut);
        Assert.Equal("x[-1]: bad array subscript\n", result.StdErr);
    }

    [Theory]
    [InlineData(ArrayKind.Indexed)]
    [InlineData(ArrayKind.Associative)]
    public void UnsetArray_OnlyRemovesAssignmentIdentityWithItsContainer(ArrayKind kind)
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TryDeclareArray("x", kind, ScalarArrayPromotion.RetainValue));
        Assert.True(variables.TryUnset("x", UnsetFlags.ScalarOnly));
        var output = ByteMemoryBuilder.CreateUnaccounted(32);
        Assert.True(variables.TryAppendVariable("x", output, VariableDisplayStyle.Declaration));
        Assert.Equal(kind == ArrayKind.Indexed ? "declare -a x\n" : "declare -A x\n", Utf8Text.Decode(output.ToMemory()));

        Assert.True(variables.TryUnset("x", UnsetFlags.ArrayOnly));
        Assert.False(variables.HasBinding("x"));
        Assert.True(variables.TrySetScalar("x", ShellValue.Empty));
        Assert.True(variables.TryUnset("x", UnsetFlags.ArrayOnly));
        output = ByteMemoryBuilder.CreateUnaccounted(32);
        Assert.True(variables.TryAppendVariable("x", output, VariableDisplayStyle.Declaration));
        Assert.Equal("declare -- x=\"\"\n", Utf8Text.Decode(output.ToMemory()));
    }
}

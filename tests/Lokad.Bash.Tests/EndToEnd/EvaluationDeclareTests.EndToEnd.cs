using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationDeclareTestsEndToEnd
{

    [Fact]
    public async Task Evaluation_FunctionNamedDeclare_ReceivesExpandedElementAssignment()
    {
        var script = @"declare() { printf '%s:<%s>\n' ""$#"" ""$1""; }; key='a b'; value='two words'; declare map[""$key""]=""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1:<map[a b]=two words>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_SameNameAssignmentPrefixDoesNotEscapeCommandScope()
    {
        var script = @"value=outer; value=temporary declare value; printf '<%s>\n' ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_SameNamePrefixContainsDeclarationWrites()
    {
        var script = @"scalar=outer; scalar=temporary declare scalar=declared; array=outer; array=temporary declare -a array; reference=outer; reference=temporary declare -n reference=target; target=changed; printf '<%s>:<%s>:<%s>\n' ""$scalar"" ""$array"" ""$reference""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<outer>:<outer>:<outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_SameNameAssignmentPrefixInitializesFunctionLocal()
    {
        var script = @"value=outer; f() { value=temporary declare value; printf 'inside=<%s>\n' ""$value""; }; f; printf 'outside=<%s>\n' ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inside=<temporary>\noutside=<outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_UninitializedNamerefAcquiresTarget()
    {
        var script = @"declare -n ref; ref=target; assignment=$?; target=value; printf '%s:<%s>\n' ""$assignment"" ""$ref""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0:<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareCompoundAssignment_ExpandsBeforeCreatingFunctionLocal()
    {
        var script = @"arr=(outer); f() { declare -a arr=(""${arr[@]}"" inner); printf '<%s>:<%s>\n' ""${arr[0]-missing}"" ""${arr[1]-missing}""; }; f";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<outer>:<inner>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LocalCompoundAssignment_CreatesIndexedFunctionLocal()
    {
        var script = @"arr=(outer); f() { local arr=(one two); printf 'inside=<%s>:<%s>\n' ""${arr[0]}"" ""${arr[1]}""; }; f; printf 'outside=<%s>\n' ""${arr[0]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inside=<one>:<two>\noutside=<outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LocalWithoutAssignment_RemainsUnset()
    {
        var script = @"value=outer; f() { local value; printf '<%s>\n' ""x${value+set}:${value-default}x""; }; f";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<x:defaultx>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local 'arr[arr]'=inside")]
    [InlineData("local -a 'arr[arr]'=inside")]
    public async Task Evaluation_LocalElementAssignment_ResolvesSubscriptAfterCreatingLocal(string declaration)
    {
        var script = $@"arr=2; f() {{ {declaration}; printf '<%s>:<%s>\n' ""${{arr[0]-missing}}"" ""${{arr[2]-missing}}""; }}; f; printf '<%s>\n' ""$arr""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<inside>:<missing>\n<2>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LocalBuiltin_AcceptsArrayAndNamerefOptions()
    {
        var script = @"target=value; f() { local -a arr=(one two); status_a=$?; local -A map=([key]=entry); status_A=$?; local -n reference=target; status_n=$?; printf '%s:%s:%s:<%s>:<%s>:<%s>\n' ""$status_a"" ""$status_A"" ""$status_n"" ""${arr[1]}"" ""${map[key]}"" ""$reference""; }; f; printf '<%s>:<%s>\n' ""${arr[0]-unset}"" ""${map[key]-unset}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0:0:0:<two>:<entry>:<value>\n<unset>:<unset>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionNamedDeclare_ReceivesCompoundTargetName()
    {
        var script = @"declare() { printf '%s:<%s>:<%s>\n' ""$#"" ""$1"" ""$2""; }; key='a b'; value='two words'; declare -A map=([""$key""]=""$value"")";
        var result = await EvaluationTestHelper.RunAsync(script);

        // Compound assignment precedes dispatch; the function receives its target name.
        Assert.Equal("2:<-A>:<map>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionNamedDeclare_ReceivesTargetNameForExpandedArray()
    {
        var script = @"declare() { printf '%s:<%s>\n' ""$#"" ""$2""; }; source=(one two); declare -a target=(""${source[@]}"")";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("2:<target>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_DeclareBuiltin_AssignsVariable()
    {
        var script = @"declare foo=bar; echo $foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare")]
    [InlineData("typeset")]
    public async Task Evaluation_DeclarationBuiltin_WithoutAssignmentRemainsUnset(string builtin)
    {
        var script = $@"{builtin} foo; echo ${{foo-unset}}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_AppliesExportAttribute()
    {
        var script = @"declare -x foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_RejectsInvalidName()
    {
        var script = @"declare 1bad";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("declare: `1bad': not a valid identifier\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_CreatesArrayDeclarations()
    {
        var script = @"declare -a arr; declare -A map; arr[0]=one; map[key]=value; echo ${arr[0]} ${map[key]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_IgnoresIndexedArraySubscript()
    {
        var script = @"declare -a arr[1]; arr[1]=one; echo ${arr[1]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_IgnoresAssociativeArraySubscript()
    {
        var script = @"declare -A map[key]; map[key]=value; echo ${map[key]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_NamerefExpandsTarget()
    {
        var script = @"declare -n ref=target; target=value; echo $ref";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_NamerefAssignsThrough()
    {
        var script = @"declare -n ref=target; ref=value; echo $target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -n a=b; declare -n b=a; unset a 2>/dev/null; status=$?; a=value; printf '%s:%s:%s' \"$status\" \"$a\" \"$b\"", "0:value:value")]
    [InlineData("declare -n a=b; declare -n b=a; unset b 2>/dev/null; status=$?; b=value; printf '%s:%s:%s' \"$status\" \"$a\" \"$b\"", "0:value:value")]
    [InlineData("declare -n a=b; declare -n b=c; declare -n c=a; unset b 2>/dev/null; status=$?; b=value; printf '%s:%s:%s:%s' \"$status\" \"$a\" \"$b\" \"$c\"", "0:value:value:value")]
    [InlineData("declare -n a=b; declare -n b=a; guard=temp unset a 2>/dev/null; status=$?; a=value; printf '%s:%s:%s' \"$status\" \"$a\" \"$b\"", "0:value:value")]
    public async Task Evaluation_UnsetNamerefCycle_RestoresUsableBindings(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("unset ref")]
    [InlineData("target=temp unset ref")]
    [InlineData("target=temp eval 'unset ref'")]
    [InlineData("f() { unset ref; }; target=temp f")]
    public async Task Evaluation_UnsetNameref_PreservesMapping(string command)
    {
        var script = $"target=outer; declare -n ref=target; {command}; target=value; echo $ref";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

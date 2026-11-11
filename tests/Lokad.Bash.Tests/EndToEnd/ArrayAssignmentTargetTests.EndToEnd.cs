using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArrayAssignmentTargetTestsEndToEnd
{
    [Theory]
    [InlineData("arr[-1]=x", "arr[-1]: bad array subscript\n", true)]
    [InlineData("arr[1/0]=x", "1/0: division by 0\n", false)]
    [InlineData("arr[$((1/0))]=x", "1/0: division by 0\n", true)]
    [InlineData("arr[0]=$((1/0))", "1/0: division by 0\n", true)]
    [InlineData("readonly arr; arr[0]=x", "arr: readonly variable\n", true)]
    [InlineData("readonly arr; arr=x", "arr: readonly variable\n", true)]
    [InlineData("declare -A arr; key=; arr[$key]=x", "arr[$key]: bad array subscript\n", true)]
    [InlineData("arr[${missing:?bad}]=x", "missing: bad\n", false)]
    [InlineData("readonly arr; : ${arr:=x}", "arr: readonly variable\n", false)]
    public async Task Evaluation_AssignmentFailures_RespectEvalAndSourceBoundaries(
        string assignment, string expectedError, bool recoverable)
    {
        var content = assignment + "; printf inside";
        var tree = EndToEndHarness.BuildTree([("/assignment", content)]);
        foreach (var invocation in new[] { $"eval '{content}'", "source /assignment" })
        {
            var result = await EndToEndHarness.RunScriptAsync(
                invocation + "; printf 'after:%s' \"$?\"", tree, ReadOnlyMemory<byte>.Empty);

            Assert.Equal(recoverable ? "after:1" : string.Empty, result.StdOut);
            Assert.Equal(expectedError, result.StdErr);
            Assert.Equal(recoverable ? 0 : 1, result.ExitCode);
        }
    }

    [Fact]
    public async Task Evaluation_AssociativeSubscript_JoinsAggregateExpansionFields()
    {
        var script = @"set -- a b; declare -A map; map[""$@""]=value; printf '<%s>:<%s>\n' ""${map[a b]-missing}"" ""${map[a]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<value>:<missing>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssociativeSubscript_PreservesEscapedDollarInDoubleQuotes()
    {
        var script = @"x=wrong; declare -A map; map[""\$x""]=value; printf '<%s>:<%s>:<%s>\n' ""${!map[@]}"" ""${map[\$x]-missing}"" ""\$x""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<$x>:<value>:<$x>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ElementAssignment_IgnoresBracketsInsideParameterExpansion()
    {
        var script = @"unset missing; declare -A map; map[${missing:-[}]=value; key='['; printf '<%s>\n' ""${map[$key]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("arr[1/0]=bad")]
    public async Task Evaluation_IndexedAssignment_RejectsInvalidArithmeticSubscript(string assignment)
    {
        var script = $@"arr=(keep); {assignment}; status=$?; printf '%s:<%s>:%s\n' ""$status"" ""${{arr[0]}}"" ""${{#arr[@]}}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("1/0: division by 0\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArrayElementSubscripts_ExpandByExistingArrayKind()
    {
        var script = @"index=2; key='a b'; arr[2]=old; arr[index]=new; declare -A map; map[""$key""]=value; map[@]=literal; printf '<%s>:<%s>:<%s>:<%s>\n' ""${arr[index]}"" ""${map[$key]}"" ""${map[""a b""]}"" ""${map[""@""]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<new>:<value>:<value>:<literal>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_IndexedArray_NegativeAssignmentUsesScalarElementZero()
    {
        var script = @"x=old; x[-1]=new; printf '%s:<%s>:<%s>\n' ""$?"" ""$x"" ""${x[0]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0:<new>:<new>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_EmptyExpandedIndexedSubscriptUsesZero()
    {
        var script = @"index=; arr[$index]=value; printf '%s:<%s>\n' ""$?"" ""${arr[0]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0:<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_ExistingAssociativeArrayAcceptsNumericKey()
    {
        var script = @"declare -A map; map[1]=one; printf '<%s>\n' ""${map[1]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<one>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_PlainAssignment_PreservesDeclaredArrayKinds()
    {
        var script = @"arr[2]=two; arr=zero; declare -A map; map[key]=value; map=numeric; printf '<%s><%s><%s><%s>\n' ""${arr[0]}"" ""${arr[2]}"" ""${map[0]}"" ""${map[key]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<zero><two><numeric><value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash 5.2.37 output/status; diagnostics use the engine's subject/message format.
    [Theory]
    [InlineData("arr[1 + 2]=three; printf '<%s>\\n' \"${arr[3]}\"", "<three>\n", "", 0)]
    [InlineData("arr[\"1\"]=value; printf '<%s>\\n' \"${arr[1]}\"", "<value>\n", "", 0)]
    [InlineData("arr['1']=value; printf after", "", "'1': invalid arithmetic expression\n", 1)]
    [InlineData("arr[$'\\x31']=value; printf after", "", "'1': invalid arithmetic expression\n", 1)]
    [InlineData("arr=(one two); arr[~0]=value; printf '<%s>\\n' \"${arr[1]}\"", "<value>\n", "", 0)]
    [InlineData("printf '<%s>\\n' arr[1 + 2]=three", "<arr[1>\n<+>\n<2]=three>\n", "", 0)]
    [InlineData("key=literal; printf '<%s>\\n' map[\"$key\"]=value", "<map[literal]=value>\n", "", 0)]
    [InlineData("arr=(old); readonly arr; arr[1]=new; printf after", "", "arr: readonly variable\n", 1)]
    [InlineData("arr=(old); arr[-2]=new; printf after", "", "arr[-2]: bad array subscript\n", 1)]
    [InlineData("arr[]=new; printf after", "", "arr[]: bad array subscript\n", 1)]
    [InlineData("declare -A map; key=; map[$key]=new; printf after", "", "map[$key]: bad array subscript\n", 1)]
    [InlineData("HOME=/home/test; declare -A map; map[~]=value; printf '<%s>\\n' \"${!map[@]}\"", "</home/test>\n", "", 0)]
    [InlineData("i=0; arr[$(printf 2; printf selector >&2)]=$(printf value; printf rhs >&2); printf '<%s>\\n' \"${arr[2]}\"", "<value>\n", "rhsselector", 0)]
    [InlineData("arr=(old); readonly arr; arr[$(printf 1; printf selector >&2)]=$(printf value; printf rhs >&2); printf after", "", "rhsselectorarr: readonly variable\n", 1)]
    [InlineData("arr=(old); arr[-2]=$(printf value; printf rhs >&2); printf after", "", "rhsarr[-2]: bad array subscript\n", 1)]
    [InlineData("arr[2]=old; declare -n ref=arr; ref[2]=new; printf '<%s>:<%s>\\n' \"${arr[2]}\" \"${ref[2]}\"", "<new>:<new>\n", "", 0)]
    [InlineData("x=wrong; declare -A map; map['$x']=value; printf '<%s>\\n' \"${map[\\$x]}\"", "<value>\n", "", 0)]
    [InlineData("declare -A map; map[\"a=b]\"]=value; printf '<%s>\\n' \"${map['a=b]']}\"", "<value>\n", "", 0)]
    [InlineData("ar\\\nr[1 \\\n+ 1]\\\n=two; printf '<%s>\\n' \"${arr[2]}\"", "<two>\n", "", 0)]
    [InlineData("HOME=/home/test; declare -A map; map[\"~\"]=value; printf '<%s>\\n' \"${!map[@]}\"", "<~>\n", "", 0)]
    [InlineData("i=0; arr[i++]=$((i+=10)); printf '<%s>:<%s>:<%s>\\n' \"$i\" \"${arr[10]-missing}\" \"${arr[0]-missing}\"", "<11>:<10>:<missing>\n", "", 0)]
    [InlineData("i=0; declare -A map; map[$((i++))]=$((i+=10)); printf '<%s>:<%s>:<%s>\\n' \"$i\" \"${map[10]-missing}\" \"${map[0]-missing}\"", "<11>:<10>:<missing>\n", "", 0)]
    public async Task Evaluation_ArrayAssignmentTarget_PreservesContextAndEffects(
        string script,
        string expectedOutput,
        string expectedError,
        int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }
}

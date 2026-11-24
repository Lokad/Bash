using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArrayAssignmentTargetTestsEndToEnd
{
    [Theory]
    [InlineData("arr[-1]=x", "line 1: arr[-1]: bad array subscript\n", true)]
    [InlineData("arr[1/0]=x", "line 1: 1/0: division by 0 (error token is \"0\")\n", false)]
    [InlineData("arr[$((1/0))]=x", "line 1: 1/0: division by 0 (error token is \"0\")\n", true)]
    [InlineData("arr[0]=$((1/0))", "line 1: 1/0: division by 0 (error token is \"0\")\n", true)]
    [InlineData("readonly arr; arr[0]=x", "line 1: arr: readonly variable\n", true)]
    [InlineData("readonly arr; arr=x", "line 1: arr: readonly variable\n", true)]
    [InlineData("declare -A arr; key=; arr[$key]=x", "line 1: arr[$key]: bad array subscript\n", true)]
    [InlineData("arr[${missing:?bad}]=x", "line 1: missing: bad\n", false)]
    [InlineData("readonly arr; : ${arr:=x}", "line 1: arr: readonly variable\n", true)]
    public async Task Evaluation_AssignmentFailures_RespectEvalAndSourceBoundaries(
        string assignment, string expectedError, bool recoverable)
    {
        var content = assignment + "; printf inside";
        var tree = EndToEndHarness.BuildTree([("/assignment", content)]);
        foreach (var (invocation, path) in new[] { ($"eval '{content}'", ""), ("source /assignment", "/assignment: ") })
        {
            var result = await EndToEndHarness.RunScriptAsync(
                invocation + "; printf 'after:%s' \"$?\"", tree, ReadOnlyMemory<byte>.Empty);

            Assert.Equal(recoverable ? "after:1" : string.Empty, result.StdOut);
            Assert.Equal(path + expectedError, result.StdErr);
            Assert.Equal(recoverable ? 0 : 1, result.ExitCode);
        }
    }

    [Theory]
    [InlineData("a[\\\n-3\n]=no", "<after:1>", "line 3: a[-3\n]: bad array subscript\n", null)]
    [InlineData("2>log declare 'a[-3]=first\nlast'", "<after:1>", "", "line 1: a[-3]: bad array subscript\n")]
    [InlineData("a[$(printf forbidden >&2)]='value' true", "<after:0>",
        "line 1: `a[$(printf forbidden 1>&2)]': not a valid identifier\n", null)]
    public async Task RejectedArrayTargetsRetainTheirSourceLineAndSpelling(
        string body, string output, string error, string? redirectedError)
    {
        var script = body + "\nprintf '<after:%s>' \"$?\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            if (redirectedError is not null)
                Assert.Equal(ShellValue.FromText(redirectedError).Bytes.ToArray(), host.ReadFileBytes("/log"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("values=(\n new\n) other=(\n more\n)", "line 5: values: readonly variable\n")]
    [InlineData("first=\"one\ntwo\" \\\nvalues=(\n new\n)", "line 4: values: readonly variable\n")]
    [InlineData("2>\"first\nsecond\" values=(\n new\n)", "line 4: values: readonly variable\n")]
    [InlineData("values=(\n new\n) |& cat", "line 5: values: readonly variable\n")]
    [InlineData("declare -n ref=values\ndeclare -a ref=(\n new\n)", "line 4: ref: readonly variable\n")]
    public async Task AssignmentDiagnosticUsesTheFirstCommandElement(string body, string diagnostic)
    {
        var script = "printf 'before\\n'\nvalues=(old); readonly values\n" + body
            + "\nprintf '<%s>:<%s>\\n' \"$?\" \"${values[0]}\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal("before\n<1>:<old>\n", host.StandardOutput.TextContent);
            Assert.Equal(diagnostic, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("arr[\n0]=value", "<0:value>", "")]
    [InlineData("declare arr[ 0 ]=value", "<1:>", "line 1: declare: `arr[': not a valid identifier\nline 1: declare: `0': not a valid identifier\nline 1: declare: `]=value': not a valid identifier\n")]
    [InlineData("declare -a arr[\n0]=value", "<127:>", "line 1: declare: `arr[': not a valid identifier\nline 2: 0]=value: command not found\n")]
    [InlineData("typeset arr[\nprintf '<next>'; ]=value", "<next><127:>", "line 1: typeset: `arr[': not a valid identifier\nline 2: ]=value: command not found\n")]
    [InlineData("declare arr[\\\n0]=value", "<0:value>", "")]
    [InlineData("declare arr[\"\n0\"]=value", "<0:value>", "")]
    [InlineData("declare arr[$(cat <<EOF\n0\nEOF\n)]=value", "<0:value>", "")]
    [InlineData("declare -a arr=([\n0]=value)", "<0:value>", "")]
    public Task DeclarationSelectorsRespectTheLexicalWord(string body, string output, string error) =>
        AssertDeclarationAsync(body + "\nprintf '<%s:%s>' \"$?\" \"${arr[0]}\"\n", output, error);

    [Theory]
    [InlineData("declare arr[]; printf '<%s>' \"$?\"; declare -p arr 2>/dev/null || :",
        "<1>", "line 1: declare: `arr[]': not a valid identifier\n")]
    [InlineData("declare -a arr=(seed); command declare arr[\"\"]; printf '<%s:%s>' \"$?\" \"${arr[0]}\"",
        "<1:seed>", "line 1: declare: `arr[]': not a valid identifier\n")]
    [InlineData("f() { local arr[$empty]; printf '<%s>' \"$?\"; }; f",
        "<1>", "line 1: local: `arr[]': not a valid identifier\n")]
    [InlineData("declare -A arr; declare arr[\"]\"\n]=item; printf '<%s>' \"${arr[\"]\"]}\"",
        "<>", "line 1: declare: `arr[]': not a valid identifier\nline 2: ]=item: command not found\n")]
    [InlineData("declare -a arr=(seed); declare arr[]=new; printf '<%s:%s>' \"$?\" \"${arr[0]}\"",
        "<1:seed>", "line 1: arr[]: bad array subscript\n")]
    public Task DeclarationWithEmptySelectorUsesTheReachedOperandKind(string body, string output, string error) =>
        AssertDeclarationAsync(body + "\n", output, error);

    [Theory]
    [InlineData("i=0; declare 'arr[i++]'; printf '<%s:%s>\\n' \"$?\" \"$i\"; declare -p arr",
        "<0:0>\ndeclare -a arr\n", "")]
    [InlineData("readonly arr=seed; typeset arr[3]; declare -p arr",
        "declare -ar arr=([0]=\"seed\")\n", "")]
    [InlineData("declare -A arr=([key]=seed); builtin declare arr[0]; declare -p arr",
        "declare -A arr=([key]=\"seed\" )\n", "")]
    [InlineData("arr=seed; declare -n r=arr; command declare -r r[4]; declare -p r arr",
        "declare -n r=\"arr\"\ndeclare -ar arr=([0]=\"seed\")\n", "")]
    [InlineData("declare -n r=arr; declare r[0] arr[1]; declare -p r arr",
        "declare -a r\ndeclare -a arr\n", "")]
    [InlineData("declare -n r=arr; declare +n r[0]; declare -p r; declare -p arr 2>/dev/null || :",
        "declare -- r=\"arr\"\n", "")]
    [InlineData("arr=seed; f() { local arr=inner; declare -g arr[0]; declare -p arr; }; f; declare -p arr",
        "declare -- arr=\"inner\"\ndeclare -a arr=([0]=\"seed\")\n", "")]
    [InlineData("arr=seed; declare -n r='arr[0]'; declare -g r[1]; printf '<%s>\\n' \"$?\"; declare -p r arr",
        "<1>\ndeclare -n r=\"arr[0]\"\ndeclare -- arr=\"seed\"\n",
        "line 1: declare: `arr[0][1]': not a valid identifier\n")]
    [InlineData("declare -n r=s s=r; declare r[0]; declare -p r s",
        "declare -a r\ndeclare -n s=\"r\"\n", "line 1: warning: r: circular name reference\n")]
    [InlineData("arr=seed; declare -an arr[0]; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -- arr=\"seed\"\n", "line 1: declare: arr[0]: reference variable cannot be an array\n")]
    public Task NameOnlyDeclarationSelectorsPublishTheSelectedArray(string body, string output, string error) =>
        AssertDeclarationAsync(body + "\n", output, error);

    [Theory]
    [InlineData("arr=old; declare -n arr[2]=target; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -- arr=\"old\"\n", "line 1: declare: arr[2]: reference variable cannot be an array\n")]
    [InlineData("i=0; typeset -n 'arr[i++]=target'; printf '<%s:%s>' \"$?\" \"$i\"",
        "<1:0>", "line 1: typeset: arr[i++]: reference variable cannot be an array\n")]
    [InlineData("i=0; declare -an arr[$((i++))]=target; printf '<%s:%s>' \"$?\" \"$i\"",
        "<1:1>", "line 1: declare: arr[0]: reference variable cannot be an array\n")]
    [InlineData("f() { local arr=old; local -n arr[0]+=target; printf '<%s>\\n' \"$?\"; declare -p arr; }; f",
        "<1>\ndeclare -- arr=\"old\"\n", "line 1: local: arr[0]: reference variable cannot be an array\n")]
    public Task NamerefElementInitializersRejectTheSelectedName(string body, string output, string error) =>
        AssertDeclarationAsync(body + "\n", output, error);

    [Theory]
    [InlineData("declare -n arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -a arr=()\n", "line 1: arr[]: bad array subscript\n")]
    [InlineData("declare arr; declare -n arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -a arr\n", "line 1: arr[]: bad array subscript\n")]
    [InlineData("declare -i arr=3; declare -nx arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -ax arr=([0]=\"3\")\n", "line 1: arr[]: bad array subscript\n")]
    [InlineData("dest=old; declare -n arr=dest; declare -nA arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr dest",
        "<1>\ndeclare -A arr=([0]=\"dest\" )\ndeclare -- dest=\"old\"\n", "line 1: arr[]: bad array subscript\n")]
    [InlineData("arr=(old); declare -nA arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -a arr=([0]=\"old\")\n", "line 1: declare: arr: reference variable cannot be an array\n")]
    [InlineData("arr=outer; f() { local arr=old; local -nr arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr; }; f; declare -p arr",
        "<1>\ndeclare -a arr=()\ndeclare -- arr=\"outer\"\n", "line 1: local: arr: reference variable cannot be an array\n")]
    [InlineData("arr=outer; f() { local arr=inner; declare -gn arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr; }; f; declare -p arr",
        "<1>\ndeclare -- arr=\"inner\"\ndeclare -a arr=([0]=\"outer\")\n", "line 1: arr[]: bad array subscript\n")]
    [InlineData("arr=old; arr=prefix declare -n arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -- arr=\"old\"\n", "line 1: arr[]: bad array subscript\n")]
    [InlineData("declare -Ai arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr",
        "<1>\ndeclare -Ai arr=()\n", "line 1: arr[]: bad array subscript\n")]
    [InlineData("dest=old; declare -n arr=dest; declare arr[]=target; printf '<%s>\\n' \"$?\"; declare -p arr dest",
        "<1>\ndeclare -a arr=()\ndeclare -- dest=\"old\"\n", "line 1: warning: arr: removing nameref attribute\nline 1: arr[]: bad array subscript\n")]
    public Task EmptyDeclarationAssignmentsPublishBeforeRejectingTheSelector(string body, string output, string error) =>
        AssertDeclarationAsync(body + "\n", output, error);

    private static async Task AssertDeclarationAsync(string script, string output, string error)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
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
        Assert.Equal("line 1: 1/0: division by 0 (error token is \"0\")\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("arr[1/0]=bad", "[1:1]declare -a arr=([7]=\"old\")\n", 4)]
    [InlineData("arr=([1/0]=bad [2]=later)", "[1:1]declare -a arr=()\n", 4)]
    [InlineData("arr=([1]=early [1/0]=bad [2]=later)", "[1:1]declare -a arr=([1]=\"early\")\n", 4)]
    [InlineData("arr+=([8]=early [1/0]=bad [9]=later)", "[1:1]declare -a arr=([7]=\"old\" [8]=\"early\")\n", 4)]
    [InlineData("f", "[1:7 0]declare -a arr=([7]=\"old\")\n", 2)]
    public async Task InvalidNumericIndexPublishesStatusInTheReachedScopeAndKeepsCompletedWrites(
        string assignment, string expectedOutput, int line)
    {
        var script = "declare -a arr=([7]=old)\nf() { local -a PIPESTATUS=(4 5); arr[1/0]=bad; }\n(exit 7) | true\n" + assignment
            + "; printf skipped\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -p arr";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var exitCode = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, exitCode);
            Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
            Assert.Equal($"line {line}: 1/0: division by 0 (error token is \"0\")\n", host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
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
    [InlineData("arr['1']=value; printf after", "", "line 1: '1': syntax error: operand expected (error token is \"'1'\")\n", 1)]
    [InlineData("arr[$'\\x31']=value; printf after", "", "line 1: '1': syntax error: operand expected (error token is \"'1'\")\n", 1)]
    [InlineData("arr=(one two); arr[~0]=value; printf '<%s>\\n' \"${arr[1]}\"", "<value>\n", "", 0)]
    [InlineData("printf '<%s>\\n' arr[1 + 2]=three", "<arr[1>\n<+>\n<2]=three>\n", "", 0)]
    [InlineData("key=literal; printf '<%s>\\n' map[\"$key\"]=value", "<map[literal]=value>\n", "", 0)]
    [InlineData("arr=(old); readonly arr; arr[1]=new; printf after", "", "line 1: arr: readonly variable\n", 1)]
    [InlineData("arr=(old); arr[-2]=new; printf after", "", "line 1: arr[-2]: bad array subscript\n", 1)]
    [InlineData("arr[]=new; printf after", "", "line 1: arr[]: bad array subscript\n", 1)]
    [InlineData("declare -A map; key=; map[$key]=new; printf after", "", "line 1: map[$key]: bad array subscript\n", 1)]
    [InlineData("HOME=/home/test; declare -A map; map[~]=value; printf '<%s>\\n' \"${!map[@]}\"", "</home/test>\n", "", 0)]
    [InlineData("i=0; arr[$(printf 2; printf selector >&2)]=$(printf value; printf rhs >&2); printf '<%s>\\n' \"${arr[2]}\"", "<value>\n", "rhsselector", 0)]
    [InlineData("arr=(old); readonly arr; arr[$(printf 1; printf selector >&2)]=$(printf value; printf rhs >&2); printf after", "", "rhsselectorline 1: arr: readonly variable\n", 1)]
    [InlineData("arr=(old); arr[-2]=$(printf value; printf rhs >&2); printf after", "", "rhsline 1: arr[-2]: bad array subscript\n", 1)]
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

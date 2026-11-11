using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ArithmeticArrayTestsEndToEnd
{
    [Fact]
    public async Task CircularElementWrite_PreservesReadonlyBindings()
    {
        // Unlike Bash, converting a cycle must not remove readonly protection.
        var result = await EvaluationTestHelper.RunAsync(
            "declare -nr r=s; declare -n s=r; ((r[0]=7)); printf '%s\\n' \"$?\"; declare -p r s");

        Assert.Equal("1\ndeclare -nr r=\"s\"\ndeclare -n s=\"r\"\n", result.StdOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("readonly variable", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let 'a[1]=3'")]
    [InlineData("printf -v 'b[(a[1]=3)]' value")]
    public async Task PrefixElementWrites_PreserveTheCallersArrayKind(string command)
    {
        // Platform contract: Bash 5.2 converts this map to an indexed array and can expose corrupt bytes.
        var result = await EvaluationTestHelper.RunAsync("declare -A a=([key]=10); a=7 " + command
            + "; printf '<%s>/<%s>' \"${a[key]}\" \"${a[1]}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<10>/<3>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task WideArithmeticExpression_PreservesSelectorAndWriteOrder()
    {
        var result = await EvaluationTestHelper.RunAsync(BuildScript(800));

        Assert.Equal(0, result.ExitCode);

        static string BuildScript(int count)
        {
            var terms = new string[count];
            terms[0] = "values[$key]";
            for (var index = 1; index < count; index++)
                terms[index] = "(counter+=1, values[$key])";

            return "declare -A values=([key]=1); key=key; counter=0; result=$((counter=0, "
                   + string.Join('+', terms)
                   + ")); test \"$result\" -eq "
                   + count
                   + " && test \"$counter\" -eq "
                   + (count - 1);
        }
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_PrefixNamedAssignmentPersists()
    {
        var script = @"x=1; x=5 let 'x++'; printf '%s\n' ""$x""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("6\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_PrefixArrayAssignmentCommitsOnlyArithmeticElement()
    {
        var script = @"arr=(1 2); arr=(5 2) let 'arr[1]=9'; printf '<%s>:<%s>\n' ""${arr[0]}"" ""${arr[1]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<1>:<9>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticSubscripts_ExpandBeforeArithmeticSideEffects()
    {
        var script = """
            declare -A map=([0]=1 [1]=2)
            key=0
            result=$((map[$(printf '%s' "$key")] + (key=1, map[$(printf '%s' "$key")])))
            printf '<%s>:<%s>\n' "$result" "$key"
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<2>:<1>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticSubscripts_SelectConditionalBranchValue()
    {
        var script = """
            declare -A map=([selector]=0 [same]=7)
            result=$((map[$(printf selector)] ? map[$(printf same)] : map[$(printf same)]))
            printf '<%s>\n' "$result"
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<7>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticAssignment_PreservesEarlierWritesThroughNamerefCycle()
    {
        var script = """
            stable=before
            declare -n first=second
            declare -n second=first
            ((stable=after, first=9))
            status=$?
            printf '<%s>:<%s>\n' "$status" "$stable"
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<0>:<0>\n", result.StdOut);
        Assert.Equal("warning: first: circular name reference\nwarning: first: circular name reference\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticAssignment_ResolvesNestedArraySubscript()
    {
        var script = """
            indexes=(2)
            values=([2]=41)
            result=$((values[indexes[0]] + 1))
            printf '<%s>\n' "$result"
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<42>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticAssignment_PreservesSparseHighIndex()
    {
        var script = """
            values[1000000000]=41
            ((values[1000000000]++))
            printf '<%s>:<%s>\n' "${#values[@]}" "${values[1000000000]}"
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<1>:<42>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=(10 20 30 40); i=0; ((a[i++]=7)); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:1:7 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((a[i++]+=7)); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:1:17 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((a[i++]++)); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:1:11 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((++a[i++])); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:1:11 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((a[i++]=(i+=1,7))); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:2:10 7 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((a[i++]+=(i+=1,7))); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:2:17 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((a[0]+=(a[0]=7))); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:0:17 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((1 || a[i++]++)); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:0:10 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((a[a[2]/10]+=2)); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:0:10 20 30 42\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((i=1, a[i++]/=0)); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "1:2:10 20 30 40\n", 0, "division by 0")]
    [InlineData("a=(10 20 30 40); i=0; ((a[i++]+1)); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "0:1:10 20 30 40\n", 0, "")]
    [InlineData("a=(10 20 30 40); i=0; ((a[(i=1, 1/0)])); status=$?; printf '%s:%s:%s\\n' \"$status\" \"$i\" \"${a[*]}\"", "", 1, "division by 0")]
    [InlineData("a=(10 20); i=0; printf '%s:%s:%s\\n' \"$((a[i++]+=7))\" \"$i\" \"${a[*]}\"", "17:1:17 20\n", 0, "")]
    [InlineData("a=(10 20); i=5; i=0 let 'a[i++]+=7'; printf '%s:%s:%s\\n' \"$?\" \"$i\" \"${a[*]}\"", "0:1:17 20\n", 0, "")]
    [InlineData("a=(0); for ((a[0]=0;a[0]<3;a[0]++)); do printf '%s' \"${a[0]}\"; done; printf ':%s\\n' \"${a[0]}\"", "012:3\n", 0, "")]
    [InlineData("a=(0 1); b=(10 20); printf '%s:%s\\n' \"${b[a[1]++]}\" \"${a[*]}\"", "20:0 2\n", 0, "")]
    [InlineData("declare -A a=([key]=10); ((a[key]+=7)); printf '%s:%s\\n' \"$?\" \"${a[key]}\"", "0:17\n", 0, "")]
    [InlineData("declare -A a=([two\\ words]=10); ((a[\"two words\"]+=7)); printf '%s:%s\\n' \"$?\" \"${a[\"two words\"]}\"", "0:17\n", 0, "")]
    [InlineData("declare -A a=([key]=10); ((a[\"$(printf key)\"]+=7)); printf '%s:%s\\n' \"$?\" \"${a[key]}\"", "0:17\n", 0, "")]
    [InlineData("a=(10 20); declare -n ref=a; ((ref[1]+=7)); printf '%s:%s\\n' \"$?\" \"${a[*]}\"", "0:10 27\n", 0, "")]
    [InlineData("a=(10 20); ((a+=7)); printf '%s:%s\\n' \"$?\" \"${a[*]}\"", "0:17 20\n", 0, "")]
    [InlineData("a=(10 20); ((a[-1]+=7)); printf '%s:%s\\n' \"$?\" \"${a[*]}\"", "0:10 27\n", 0, "")]
    [InlineData("a=10; ((a[-1]+=7)); printf '%s:%s\\n' \"$?\" \"$a\"", "0:7\n", 0, "bad array subscript")]
    [InlineData("a=(10 20); readonly a; i=0; ((a[0]+=(i=7))); printf '%s:%s:%s\\n' \"$?\" \"$i\" \"${a[*]}\"", "1:7:10 20\n", 0, "readonly variable")]
    [InlineData("a=(10); i=0; ((i=1,a[)); printf '%s:%s\\n' \"$?\" \"$i\"", "1:1\n", 0, "bad array subscript")]
    [InlineData("a=(10); ((a[-2])); printf '%s\\n' \"$?\"", "1\n", 0, "bad array subscript")]
    [InlineData("a=(10); ((1 || a[-2])); printf '%s\\n' \"$?\"", "0\n", 0, "")]
    [InlineData("a=10; i=-1; ((a[i++]+=7)); printf '%s:%s:%s\\n' \"$?\" \"$i\" \"$a\"", "0:1:7\n", 0, "bad array subscript")]
    [InlineData("a=10; i=-1; ((a[i++]++)); printf '%s:%s:%s\\n' \"$?\" \"$i\" \"$a\"", "1:1:1\n", 0, "bad array subscript")]
    [InlineData("a=10; i=0; ((a[-1]+=(a[2]=20,7))); printf '%s:%s\\n' \"$?\" \"${a[*]}\"", "0:10 7\n", 0, "bad array subscript")]
    [InlineData("a=(10 20); ((a[-1]+=(a[2]=30,7))); printf '%s:%s\\n' \"$?\" \"${a[*]}\"", "0:10 27 30\n", 0, "")]
    [InlineData("a=(10); ((a[-2]=7)); printf '%s:%s\\n' \"$?\" \"${a[*]}\"", "0:10\n", 0, "bad array subscript")]
    [InlineData("a=(10); printf '%s\\n' \"$((a[-2]))\"; printf done", "0\ndone", 0, "bad array subscript")]
    [InlineData("a=(10 20); declare -n ref='a[1]'; ((ref+=7)); printf '%s:%s\\n' \"$?\" \"${a[*]}\"", "0:10 27\n", 0, "")]
    [InlineData("declare -A a=([key]=10); ((a[\"$(ignored=\"]\"; printf key)\"]+=7)); printf '%s:%s\\n' \"$?\" \"${a[key]}\"", "0:17\n", 0, "")]
    [InlineData("arr=(3 5); i=1; printf '<%s>\\n' \"$((arr[i] + 2))\"; ((arr[i] += 3)); printf '<%s>\\n' \"${arr[1]}\"", "<7>\n<8>\n", 0, "")]
    [InlineData("declare -A map=([key]=7); key=key; printf '<%s>\\n' \"$((map[$key] + 1))\"; let 'map[$key] += 2'; printf '<%s>\\n' \"${map[key]}\"", "<8>\n<9>\n", 0, "")]
    [InlineData("declare -A map=([']']=7); key=']'; printf '<%s>\\n' \"$((map[$key] + 1))\"", "<8>\n", 0, "")]
    [InlineData("arr=(10 20); i=0; printf '<%s>:<%s>:<%s>:<%s>\\n' \"$((i=1, arr[i] += 2))\" \"$i\" \"${arr[0]}\" \"${arr[1]}\"", "<22>:<1>:<10>:<22>\n", 0, "")]
    [InlineData("arr=(10 20); i=0; first=$((arr[i++] += 2)); printf '<%s>:<%s>:<%s>:<%s>\\n' \"$first\" \"$i\" \"${arr[0]}\" \"${arr[1]}\"", "<12>:<1>:<12>:<20>\n", 0, "")]
    [InlineData("arr=(10 20); i=0; second=$((arr[i] = (i=1, 99))); printf '<%s>:<%s>:<%s>:<%s>\\n' \"$second\" \"$i\" \"${arr[0]}\" \"${arr[1]}\"", "<99>:<1>:<10>:<99>\n", 0, "")]
    [InlineData("declare -A map=([key]=7); let 'map[$(printf key)] += 2'; ((map[$(printf key)] += 3)); for ((i=0; i<$(printf 2); i++)); do :; done; printf '<%s>:<%s>\\n' \"${map[key]}\" \"$i\"", "<12>:<2>\n", 0, "")]
    [InlineData("declare -A map=([0]=10 [1]=20); i=0; declare -n ref='map[$(printf %s \"$i\")]'; result=$((i=1, ref += 2)); printf '<%s>:<%s>:<%s>:<%s>\\n' \"$result\" \"$i\" \"${map[0]}\" \"${map[1]}\"", "<22>:<1>:<10>:<22>\n", 0, "")]
    [InlineData("a=(10 20); i=0; printf '<%s>:<%s>\\n' \"$((i=1,a[$i]))\" \"$i\"", "<10>:<1>\n", 0, "")]
    [InlineData("declare -A a=([0]=10 [1]=20); i=0; printf '<%s>:<%s>\\n' \"$((i=1,a[$i]))\" \"$i\"", "<10>:<1>\n", 0, "")]
    [InlineData("a=(10 20); i=0; printf '<%s>:<%s>\\n' \"$((i=1,a[$(printf %s \"$i\")]))\" \"$i\"", "<10>:<1>\n", 0, "")]
    [InlineData("declare -A a=([0]=10 [1]=20); i=0; printf '<%s>:<%s>\\n' \"$((i=1,a[$(printf %s \"$i\")]))\" \"$i\"", "<10>:<1>\n", 0, "")]
    [InlineData("declare -A a=([']']=7); key=']'; ((a[$key]+=2)); printf '<%s>:<%s>\\n' \"$?\" \"${a[$key]}\"", "<0>:<9>\n", 0, "")]
    [InlineData("declare -A a=(['$value']=7); value=wrong; key='$value'; printf '<%s>\\n' \"$((a[$key]))\"", "<7>\n", 0, "")]
    [InlineData("declare -A a=([key]=7 ['$key']=9); key=key; printf '<%s>\\n' \"$((a['$key']))\"", "<9>\n", 0, "")]
    [InlineData("declare -A a=(['$value']=7); value=wrong; key='$value'; printf '<%s>\\n' \"$((a[\"$key\"]))\"", "<7>\n", 0, "")]
    [InlineData("declare -A a=(['$value']=7); value=wrong; printf '<%s>\\n' \"$((a[\\$value]))\"", "<7>\n", 0, "")]
    [InlineData("declare -A a=([']']=7); printf '<%s>\\n' \"$((a[\\]]))\"", "<7>\n", 0, "")]
    [InlineData("declare -A a=(['map[$key]']=7); key=wrong; printf '<%s>\\n' \"$((a[\"map[\\$key]\"]))\"", "<7>\n", 0, "")]
    [InlineData("a=(1); ((1 || a[$(printf 'effect\\n' >&2; printf 0)])); printf '<%s>\\n' \"$?\"", "<0>\n", 0, "effect\n")]
    [InlineData("declare -A a=([key]=7); key=key; expr='a[$key]'; printf '<%s>\\n' \"$(($expr))\"", "<7>\n", 0, "")]
    [InlineData("declare -A a=(['$value']=7); key='$value'; value=wrong; let 'a[$key]+=2'; printf '<%s>\\n' \"${a[$key]}\"", "<9>\n", 0, "")]
    [InlineData("declare -A a=([']']=7); key=']'; printf '<%s>:<%s>\\n' \"$((a[$key]++ + a[$key]))\" \"${a[$key]}\"", "<15>:<8>\n", 0, "")]
    [InlineData("a=(10 20); printf '<%s>\\n' \"$((\"a[0]\"))\"", "<10>\n", 0, "")]
    [InlineData("a=(10 20); i=0; printf '<%s>\\n' \"$((\"a[$i]\"))\"", "<10>\n", 0, "")]
    [InlineData("declare -A a=([']']=7); key=']'; printf '<%s>\\n' \"$((\"a[$key]\"))\"", "<7>\n", 0, "")]
    [InlineData("declare -A a=(['$value']=7); value=wrong; key='$value'; printf '<%s>\\n' \"$((\"a[$key]\"))\"", "<7>\n", 0, "")]
    [InlineData("declare -A a=([']']=7); key=']'; ((\"a[$key]\"+=2)); printf '<%s>:<%s>\\n' \"$?\" \"${a[$key]}\"", "<0>:<9>\n", 0, "")]
    [InlineData("declare -A a=([']']=7); key=']'; printf '<%s>\\n' \"$((\"a\"[$key]))\"", "<7>\n", 0, "")]
    [InlineData("a=(10 20); i=0; ((\"a[$i]\"+=2)); printf '<%s>:<%s>\\n' \"$?\" \"${a[0]}\"", "<0>:<12>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((a[b[0]]))\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); i=0; printf '<%s>\\n' \"$((a[b[$i]]))\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); declare -A b=([key]=1); printf '<%s>\\n' \"$((a[b[key]]))\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); declare -A b=([key]=1); i=key; printf '<%s>\\n' \"$((a[b[i]]))\"", "<10>\n", 0, "")]
    [InlineData("a=(10 20); declare -A b=([key]=1); i=key; printf '<%s>\\n' \"$((a[b[\"$i\"]]))\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); declare -A b=([key]=1); i=key; ((a[b[$i]]+=2)); printf '<%s>:<%s>\\n' \"$?\" \"${a[*]}\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); i='b[0]'; b=(1); printf '<%s>\\n' \"$((a[$i]))\"", "", 1, "arithmetic")]
    [InlineData("declare -A a=([']']=7); key=']'; expression='a[$key]'; printf '<%s>\\n' \"$(($expression))\"", "<7>\n", 0, "")]
    [InlineData("a=(10 20); i=0; printf '<%s>:<%s>\\n' \"$((i=1,a[\"$i\"]))\" \"$i\"", "<10>:<1>\n", 0, "")]
    [InlineData("declare -A map=([']']=1); a=(10 20); key=']'; printf '<%s>\\n' \"$((a[map[$key]]))\"", "", 1, "arithmetic")]
    [InlineData("declare -A map=(['$value']=1); a=(10 20); value=wrong; key='$value'; printf '<%s>\\n' \"$((a[map[$key]]))\"", "", 1, "arithmetic")]
    [InlineData("declare -A map=([0]=0 [1]=1); a=(10 20); i=0; printf '<%s>:<%s>\\n' \"$((i=1,a[map[$i]]))\" \"$i\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20 30); x='1+1'; printf '<%s>\\n' \"$((a[$x]))\"", "<30>\n", 0, "")]
    [InlineData("a=(10 20 30); x='1*2'; printf '<%s>\\n' \"$((a[$x]))\"", "<30>\n", 0, "")]
    [InlineData("a=(10 20 30); x='1?2:0'; printf '<%s>\\n' \"$((a[$x]))\"", "<30>\n", 0, "")]
    [InlineData("a=(10 20 30); x='(1,2)'; printf '<%s>\\n' \"$((a[$x]))\"", "<30>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); i=0; printf '<%s>\\n' \"$((a[b[0]+$i]))\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((a[b[0]+$(printf 0)]))\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((a[\"b[0]\"]))\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((a[b[\"0\"]]))\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((\"a[b[0]]\"))\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((a['b[0]']))\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((a[b\\[0\\]]))\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); b=(1); printf '<%s>\\n' \"$((a[$'b[0]']))\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); b=(1); i=0; let 'a[b[$i]]+=2'; printf '<%s>:<%s>\\n' \"$?\" \"${a[*]}\"", "<0>:<10 22>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); i=0; printf '<%s>\\n' \"${a[b[$i]]}\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); i=0; a[b[$i]]=9; printf '<%s>\\n' \"${a[*]}\"", "<10 9>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); i=0; x='b[0]'; printf '<%s>\\n' \"$((a[(i=1,$x)]))\"; printf '<%s>\\n' \"$i\"", "", 1, "arithmetic")]
    [InlineData("a=(10 20); x='b[0]'; printf '<%s>\\n' \"$((1 || a[$x]))\"", "<1>\n", 0, "")]
    [InlineData("declare -A a=(['b[0]']=7); x='b[0]'; printf '<%s>\\n' \"$((a[$x]))\"", "<7>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); i=0; declare -n ref='a[b[$i]]'; printf '<%s>\\n' \"$ref\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); b=(1); i=0; x='a[b[$i]]'; printf '<%s>\\n' \"$(($x))\"", "<20>\n", 0, "")]
    [InlineData("a=(10 20); i=0; declare -n r='a[i++]'; let 'r+=3'; printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<0>/<2>/<10 13>", 0, "")]
    [InlineData("a=(10 20); i=0; declare -n r='a[i++]'; ((r+=3)); printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<0>/<2>/<10 13>", 0, "")]
    [InlineData("a=(10 20); i=0; declare -n r='a[i++]'; ((r=3)); printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<0>/<1>/<3 20>", 0, "")]
    [InlineData("a=(10 20); i=0; declare -n r='a[i++]'; printf '<%s>/<%s>/<%s>' \"$((r++))\" \"$i\" \"${a[*]}\"", "<10>/<2>/<10 11>", 0, "")]
    [InlineData("a=(10 20); i=0; f(){ local -n r='a[i++]'; printf -v 'b[(r++,r++)]' x; printf '<%s>/<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\" \"${!b[@]}\"; }; f", "<0>/<4>/<10 11 1>/<0>", 0, "")]
    [InlineData("a=(10 20); i=0; f(){ local -n r='a[i++]'; a=7 let 'r+=3'; printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"; }; f", "<0>/<2>/<10 10>", 0, "")]
    [InlineData("a=(10 20); i=0; declare -n r='a[i++]'; a=7 let 'r=3'; printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<0>/<1>/<3 20>", 0, "")]
    [InlineData("a=(10 20); b=(1 0); i=0; read -r 'a[b[i++]]' <<<'new'; printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<0>/<1>/<10 new>", 0, "")]
    [InlineData("a=(10 20); b=(1 0); i=0; unset 'a[b[i++]]'; printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<0>/<1>/<10>", 0, "")]
    [InlineData("a=(10 20); i=0; ((a[i++]=12foo)); printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<1>/<0>/<10 20>", 0, "invalid arithmetic expression")]
    [InlineData("a=(10 20); i=0; ((a[i++]+=12foo)); printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<1>/<1>/<10 20>", 0, "invalid arithmetic expression")]
    [InlineData("a=(10 20); i=0; ((++a[i++]@)); printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<1>/<0>/<10 20>", 0, "invalid arithmetic expression")]
    [InlineData("a=(10 20); i=0; declare -n r='a[i++]'; readonly a; ((r+=3)); printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\"", "<1>/<2>/<10 20>", 0, "readonly variable")]
    [InlineData("a=(4); k=''; ((a[$k]=5)); printf '%s' \"${a[*]}\"", "4", 0, "not a valid identifier")]
    [InlineData("a=(4); k=''; ((a[$k]+=5)); printf '%s' \"${a[*]}\"", "4", 0, "bad array subscript")]
    [InlineData("a=(4); k=''; ((a[$k])); printf '%s' \"$?\"", "1", 0, "bad array subscript")]
    [InlineData("a=(4); k=@; printf '%s' \"$((a[$k]))\"; printf done", "0done", 0, "bad array subscript")]
    [InlineData("a=(4); k='*'; ((a[$k]=5)); printf '%s' \"${a[*]}\"", "4", 0, "bad array subscript")]
    [InlineData("a=(4); ((a[$(true)]=5)); printf '%s' \"${a[*]}\"", "4", 0, "not a valid identifier")]
    [InlineData("a=(4); k=''; let 'a[$k]=5'; printf '%s' \"${a[*]}\"", "5", 0, "")]
    [InlineData("a=(4); k=''; a[$k]=5; printf '%s' \"${a[*]}\"", "5", 0, "")]
    [InlineData("declare -A a=(['*']=2 ['@']=3); k=@; printf '%s' \"$((a[$k]))\"", "3", 0, "")]
    [InlineData("a=(10 20); declare -n b=a r='b[1]'; ((r=3)); printf '<%s>/<%s>' \"${a[*]}\" \"${b[*]}\"", "<10 20>/<3>", 0, "removing nameref attribute")]
    [InlineData("a=(10 20); declare -n b=a r='b[1]'; ((r+=3)); printf '<%s>/<%s>' \"${a[*]}\" \"${b[*]}\"", "<10 20>/<23>", 0, "removing nameref attribute")]
    [InlineData("a=(10 20); declare -n b=a r='b[1]'; printf '%s' \"$((r=3))\"; printf '<%s>/<%s>' \"${a[*]}\" \"${b[*]}\"", "3<10 20>/<3>", 0, "removing nameref attribute")]
    [InlineData("a=(10 20); declare -n r=a; a=3 let 'r[1]=4'; printf '%s' \"${a[*]}\"", "10 20", 0, "")]
    [InlineData("a=(10 20); declare -n r=a; a=3 let 'r[1]+=4'; printf '%s' \"${a[*]}\"", "10 20", 0, "")]
    [InlineData("a=(10 20); declare -n r='a[1]'; a=3 let 'r=4'; printf '%s' \"${a[*]}\"", "10 4", 0, "")]
    [InlineData("declare -n r; ((r=7)); printf '%s' \"$?\"", "0", 0, "not a valid identifier")]
    [InlineData("declare -n r; printf '%s' \"$((r=7))\"", "7", 0, "not a valid identifier")]
    [InlineData("declare -n r; let 'r=7'; printf '%s' \"$?\"", "0", 0, "not a valid identifier")]
    [InlineData("declare -n r; for ((r=7;0;)); do :; done; printf '%s' \"$?\"", "0", 0, "not a valid identifier")]
    [InlineData("declare -n r; x=0; ((r=7,x=5)); printf '%s' \"$x\"", "5", 0, "not a valid identifier")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; let 'r=4'; printf '<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\"; }; f", "<0>/<10 4>/<10 4>", 0, "")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; ((r=4)); printf '<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\"; }; f", "<0>/<10 4>/<10 4>", 0, "")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; printf '%s' \"$((r=4))\"; printf '<%s>/<%s>' \"${a[*]}\" \"${b[*]}\"; }; f", "4<10 4>/<10 4>", 0, "")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; let 'r+=4'; printf '<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\"; }; f", "<0>/<10 24>/<10 24>", 0, "")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; let 'r++'; printf '<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\"; }; f", "<0>/<10 21>/<10 21>", 0, "")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; tmp=x let 'r=4'; printf '<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\"; }; f", "<0>/<10 4>/<10 4>", 0, "")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; a=3 let 'r=4'; }; f; printf '<%s>/<%s>/<%s>' \"${a[*]}\" \"${b[*]}\" \"${!b}\"", "<10 20>/<10 20>/<a>", 0, "")]
    [InlineData("a=(10 20); f(){ local -n b=a r='b[1]'; let 'r=4'; printf '<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\"; }; f", "<0>/<10 4>/<10 4>", 0, "")]
    [InlineData("declare -nr r; f(){ local -n s=r; let 's=7'; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${r-unset}\"", "<0>/<7><unset>", 0, "")]
    [InlineData("declare -nr r; f(){ local -n s=r; tmp=x let 's=7'; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${r-unset}\"", "<0>/<7><unset>", 0, "")]
    [InlineData("declare -n r; printf '%s' \"$((r[0]=7))\"; printf done", "7done", 0, "not a valid identifier")]
    [InlineData("declare -n r; x=0; let 'r[x++]+=7'; printf '<%s>/<%s>' \"$?\" \"$x\"", "<0>/<1>", 0, "not a valid identifier")]
    [InlineData("declare -n r=s s=r; printf '%s' \"$((r[0]=7))\"; printf done", "7done", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; x=0; ((r[x++]+=7)); printf '<%s>/<%s>' \"$?\" \"$x\"", "<0>/<1>", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; printf '%s' \"$((r[0]))\"; printf done", "0done", 0, "circular name reference")]
    [InlineData("a=(10 20); declare -n r='a[1]'; printf '%s' \"$((r[0]=7))\"; printf ':%s' \"${a[*]}\"", "7:10 20", 0, "not a valid identifier")]
    [InlineData("a=(10 20); readonly a; f(){ local -n r='a[1]'; let 'r=7'; printf '<%s>/<%s>' \"$?\" \"${a[*]}\"; }; f", "<1>/<10 20>", 0, "readonly variable")]
    [InlineData("a=(10 20); readonly a; f(){ local -n r=a; let 'r=7'; printf '<%s>/<%s>' \"$?\" \"${a[*]}\"; }; f", "<1>/<10 20>", 0, "readonly variable")]
    [InlineData("a=(10 20); declare -n r='a[1]'; r=3 let 'r[1]=4'; printf '<%s>/<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\" \"${r[*]}\"", "<0>/<10 20>/<>/<>", 0, "")]
    [InlineData("a=(10 20); f(){ local -n r='a[1]'; r=3 let 'r[1]=4'; printf '<%s>/<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\" \"${r[*]}\"; }; f; printf 'after:%s' \"${a[*]}\"", "<0>/<10 20>/<>/<>after:10 20", 0, "")]
    [InlineData("a=(10 20); declare -n b=a r='b[1]'; r=3 let 'r[1]=4'; printf '<%s>/<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\" \"${r[*]}\"", "<0>/<10 20>/<10 20>/<>", 0, "")]
    [InlineData("a=(10 20); declare -n b=a; f(){ local -n r='b[1]'; r=3 let 'r[1]=4'; printf '<%s>/<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"${b[*]}\" \"${r[*]}\"; }; f; printf 'after:%s' \"${a[*]}\"", "<0>/<10 20>/<10 20>/<>after:10 20", 0, "")]
    [InlineData("declare -n r=s s=r; ((r[0]=7)); printf '<%s>/<%s>/<%s>' \"$?\" \"${r[0]}\" \"${s[0]}\"", "<0>/<7>/<7>", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; ((r[0]+=7)); printf '<%s>/<%s>/<%s>' \"$?\" \"${r[0]}\" \"${s[0]}\"", "<0>/<7>/<7>", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; printf '%s' \"$((r[0]=7))\"; declare -p r s", "7declare -a r=([0]=\"7\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; let 'r[0]=7'; declare -p r s", "declare -a r=([0]=\"7\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; x=0; ((r[x++]=7)); printf '<%s>/<%s>/<%s>/<%s>' \"$?\" \"$x\" \"${r[0]}\" \"${s[0]}\"", "<0>/<1>/<7>/<7>", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; ((r[0]++)); printf 's:%s\\n' \"$?\"; declare -p r s", "s:1\ndeclare -a r=([0]=\"1\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; x=0; ((r[x++]+=(r[3]=4,7))); printf 'x:%s\\n' \"$x\"; declare -p r s", "x:1\ndeclare -a r=([0]=\"7\" [3]=\"4\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; tmp=5 let 'r[2]=7'; declare -p r s", "declare -a r=([2]=\"7\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; r=$((r[2]=7)); declare -p r s", "declare -a r=([0]=\"7\" [2]=\"7\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r q=r; ((q[2]=7)); declare -p r s q", "declare -n r=\"s\"\ndeclare -n s=\"r\"\ndeclare -a q=([2]=\"7\")\n", 0, "")]
    [InlineData("declare -n r=s s=r; f(){ local -n q=r; ((q[2]=7)); }; f; declare -p r s q", "declare -n r=\"s\"\ndeclare -n s=\"r\"\ndeclare -a q=([2]=\"7\")\n", 0, "")]
    [InlineData("f(){ local -n r=s s=r; ((r[2]=7)); }; f; declare -p r", "declare -a r=([2]=\"7\")\n", 0, "circular name reference")]
    [InlineData("q=(4 5); declare -n r=s s=r; f(){ local -n q=r; ((q[2]=7)); declare -p q; }; f; declare -p q", "declare -n q=\"r\"\ndeclare -a q=([2]=\"7\")\n", 0, "")]
    [InlineData("declare -n r=s s=r; f(){ local -n r=s; ((r[2]=7)); declare -p r s; }; f; declare -p r s", "declare -n r=\"s\"\ndeclare -n s=\"r\"\ndeclare -a r=([0]=\"s\" [2]=\"7\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; f(){ local -n q=r; ((q[0]=7,q[0]+=8)); }; f; declare -p q", "declare -a q=([0]=\"8\")\n", 0, "")]
    [InlineData("declare -n r=s s=r; ((r[(r[0]=3,1)]=4)); declare -p r s", "declare -a r=([0]=\"3\" [1]=\"4\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; ((r[(r[0]=3,1)]+=4)); declare -p r s", "declare -a r=([0]=\"3\" [1]=\"4\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; i=0; ((r[(i++,r[3]=3,1)]++)); printf '%s' \"$i\"; declare -p r s", "1declare -a r=([1]=\"1\" [3]=\"3\")\ndeclare -n s=\"r\"\n", 0, "circular name reference")]
    [InlineData("declare -n r=s s=r; ((r[0]+= (s[2]=9,7))); declare -p r s", "declare -n r=\"s\"\ndeclare -a s=([0]=\"7\" [2]=\"9\")\n", 0, "circular name reference")]
    public async Task ArithmeticArrays_PreserveSelectedEffectsAndConsumerStatus(
        string script, string expectedOutput, int expectedStatus, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedStatus, result.ExitCode);
        if (diagnostic.Length == 0)
            Assert.Empty(result.StdErr);
        else
            Assert.Contains(diagnostic, result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArithmeticArrays_BoundNestedSelectorEvaluation(bool exceedsLimit)
    {
        // The numeric budget includes the outer expression itself.
        var depth = ShellSourceCursor.MaximumNestedExpansionDepth - (exceedsLimit ? 0 : 1);
        var operand = string.Concat(Enumerable.Repeat("a[", depth)) + "0" + new string(']', depth);
        var result = await EvaluationTestHelper.RunAsync("a=(0); printf '%s' \"$(( " + operand + " ))\"; printf done");

        Assert.Equal(exceedsLimit ? 1 : 0, result.ExitCode);
        Assert.Equal(exceedsLimit ? "" : "0done", result.StdOut);
        if (exceedsLimit)
            Assert.Contains("arithmetic nesting limit exceeded", result.StdErr, StringComparison.Ordinal);
        else
            Assert.Empty(result.StdErr);
    }
}

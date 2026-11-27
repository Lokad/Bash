using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class DeclarationOperandTestsEndToEnd
{
    [Theory]
    [InlineData("name='typeset -i'; $name")]
    [InlineData("name=declare; builtin \"$name\" -i")]
    public async Task ResolvedDeclarationKeepsItsExpandedOptionsAndEvaluatesTheOperandOnce(string declaration)
    {
        var result = await EvaluationTestHelper.RunAsync("count=0; " + declaration
            + " 'value=++count+2'; printf '<%s:%s:%s>' \"$value\" \"${value@a}\" \"$count\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<3:i:1>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("declare item={1..3}; printf '<%s:%s>' \"$item\" \"$_\"", "<3:item=3>")]
    [InlineData("item=seed; typeset item+={a,\"$item\"}; printf '<%s:%s>' \"$item\" \"$_\"", "<seedaseed:item+=seed>")]
    [InlineData("export item={left,\"b c\"}; declare -p item", "declare -x item=\"b c\"\n")]
    [InlineData("f(){ local item={left,}; printf '<%s:%s>' \"$item\" \"$_\"; }; f", "<:item=>")]
    [InlineData("declare(){ printf '<%s>' \"$@\"; }; value='a b'; declare item={$value,tail}",
        "<item=a><b><item=tail>")]
    [InlineData("i=0; declare item[i++]={a,b}; printf '<%s:%s>' \"$i\" \"$_\"; declare -p item",
        "<2:item[i++]=b>declare -a item=([0]=\"a\" [1]=\"b\")\n")]
    public async Task BraceGeneratedDeclarationOperandsUseOrdinaryArgumentExpansion(string script, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task BraceGeneratedAssignmentsExpandTheirCommonPrefixForEachOperand()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            declare item="$(printf x >> log; printf start)"{a,b}
            printf '<%s:%s>' "$item" "$_"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<startb:item=startb>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal("xx", result.Host.ReadFile("/log"));
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task BraceGeneratedReadonlyAssignmentsKeepTheFirstPublishedValue()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            declare -r item={red,blue}
            printf '<%s:%s:%s>' "$?" "$item" "$_"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:red:item=blue>", result.StdOut);
        Assert.Equal("line 1: declare: item: readonly variable\n", result.StdErr);
    }

    [Fact]
    public async Task BraceGeneratedDeclarationOperandsExpandPathnamesAsCompleteArguments()
    {
        await using var host = EvaluationTestHelper.BuildDefaultTree();
        host.AddFile("/item=matched", "data");
        var result = await EvaluationTestHelper.RunAsync("""
            declare item=*
            printf '<%s>' "$item"
            declare item={left,*}
            printf '<%s:%s>' "$item" "$_"
            """, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<*><matched:item=matched>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal("data", host.ReadFile("/item=matched"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("local", "-ga", "declare -- x=\"local\"\ndeclare -a x=([0]=\"one\" [1]=\"two\")\n")]
    [InlineData("local", "\"-ga\"", "declare -a x=([0]=\"one\" [1]=\"two\")\ndeclare -a x=([0]=\"old\")\n")]
    [InlineData("local", "\"$flags\"", "declare -a x=([0]=\"one\" [1]=\"two\")\ndeclare -a x=([0]=\"old\")\n")]
    [InlineData("declare", "\"$flags\"", "declare -a x=([0]=\"one\" [1]=\"two\")\ndeclare -a x=([0]=\"old\")\n")]
    [InlineData("local", "-g\"$tail\"", "declare -- x=\"local\"\ndeclare -ail x=([0]=\"0\" [1]=\"0\")\n")]
    [InlineData("local", "\"$assoc\"", "declare -a x=([0]=\"one\" [1]=\"two\")\ndeclare -A x=([0]=\"old\" )\n")]
    public async Task ParsedCompoundInitializerUsesSourceOptionsBeforeDispatch(
        string builtin, string options, string output)
    {
        var result = await EvaluationTestHelper.RunAsync("x=old; f(){ local x=local flags=-ga tail=a assoc=-gA; "
            + builtin + " " + options + " x=(one two); declare -p x; }; f; declare -p x");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task ExpandedGlobalFlagDoesNotRepeatTheParsedInitializer()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            x=old
            f() {
              local x=local flags=-ga
              local "$flags" x=("$(printf hit >> count; printf element)" tail)
              printf '<%s><%s><%s>' "$?" "${x[*]}" "$(<count)"
            }
            f
            declare -p x
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0><element tail><hit>declare -a x=([0]=\"old\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("""declare -a 'items=([2]=one [5]="two words")'; printf '<%s>' "${items[2]}" "${items[5]}" """, "<one><two words>")]
    [InlineData("""arg='items=(one "two words")'; command declare -a "$arg"; printf '<%s>' "${items[@]}" """, "<one><two words>")]
    [InlineData("""items=(old); declare 'items+=(one "two words")'; printf '<%s>' "${items[@]}" """, "<old><one><two words>")]
    [InlineData("""readonly -A 'items=(left one right "two words")'; printf '<%s>' "${items[left]}" "${items[right]}" """, "<one><two words>")]
    [InlineData("""items=(outer); f(){ local -I 'items=(one two)'; printf 'local<%s>' "${items[@]}"; }; f; printf 'outer<%s>' "${items[@]}" """, "local<one>local<two>outer<outer>")]
    [InlineData("""items=(old); declare -n ref=items; declare 'ref=()'; printf '<%s>' "${#items[@]}" """, "<0>")]
    public async Task ExpandedCompoundInitializerFollowsTheSelectedArray(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("declare -Ar 'items=()'")]
    [InlineData("declare -Ar 'items=( # comment\n)'")]
    [InlineData("declare -Ar items=( # comment\n)")]
    public async Task EmptyCompoundDeclarationReplacesEntriesAndPublishesReadonly(string declaration)
    {
        var result = await EvaluationTestHelper.RunAsync("declare -A items=([key]=value)\n"
            + declaration + "\ndeclare -p items");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -Ar items=()\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task ExpandedCompoundInitializerRunsAfterOuterArgumentsAndRedirection()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            unset mark
            declare -a 'items=("${mark:=late}" "$(printf item; printf reached >&2)")' "earlier=$mark" 2>log
            printf '<%s>' "$earlier" "$mark" "${items[@]}"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<><late><late><item>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal("reached", result.Host.ReadFile("/log"));
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task MalformedExpandedInitializerDiscardsItsCommandUnit()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            items=(keep)
            declare -a 'items=(one ; two)' 'other=(unreached)'; printf unreached
            printf '<%s>' "$?" "${items[@]}" "${other[0]-missing}"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1><keep><missing>", result.StdOut);
        Assert.Contains("syntax error", result.StdErr);
    }

    [Theory]
    [InlineData("source fixture; printf '<%s>' \"$?\"", "loaded<0>")]
    [InlineData("set -e; if source fixture; then printf yes; fi", "loadedyes")]
    public async Task SourceResumesAfterMalformedExpandedInitializer(string script, string expected)
    {
        await using var host = EvaluationTestHelper.BuildDefaultTree();
        host.AddFile("/fixture", "declare -a 'items=(one ; two)'; printf unreached\nprintf loaded\n");
        var result = await EvaluationTestHelper.RunAsync(script, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Contains("syntax error", result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ErrexitMakesMalformedExpandedInitializerASyntaxExitEvenWhenTested()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            set -e
            if declare -a 'items=(one ; two)'; then printf yes; else printf no; fi
            printf unreached
            """);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("syntax error", result.StdErr);
    }

    [Theory]
    [InlineData("""declare -ar 'items=("${items@a}")'; printf '<%s>' "${items[@]}" """, "<ar>")]
    [InlineData("""declare -ax 'items=("${items@a}")'; printf '<%s>' "${items[@]}" """, "<ax>")]
    [InlineData("""declare -A items=([first]=before); declare -Ar 'items+=([second]="${items@a}")'; printf '<%s>' "${items[first]}" "${items[second]}" """, "<before><Ar>")]
    [InlineData("""declare -air 'items=(1 2+3)'; printf '<%s>' "${items[@]}" """, "<1><5>")]
    public async Task ExpandedCompoundInitializerSeesDeclarationAttributes(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("-a", "")]
    [InlineData("-A", "")]
    [InlineData("-a", "declare -a ")]
    [InlineData("-A", "declare -A ")]
    public async Task EmptyCompoundAppendsInitializeDeclaredArrays(string kind, string command)
    {
        var result = await EvaluationTestHelper.RunAsync("declare " + kind + " probe_a; " + command
            + "probe_a+=(); printf '<%s>' \"${!probe_*}\"");

        Assert.Equal("<probe_a>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -n r; declare -a r=(\"${r:=a}\" tail); printf '<%s>' \"${a[@]}\"", "<a><tail>")]
    [InlineData("f(){ local -n r; local -a r=(\"${r:=a}\" tail); printf '<%s>' \"${a[@]}\"; }; f", "<a><tail>")]
    [InlineData("declare -n r; declare -A r=([key]=\"${r:=a}\"); printf '<%s>' \"${a[key]}\"", "<a>")]
    [InlineData("declare -A a; declare -n r; v='two words'; declare r=(\"${r:=a}\" $v); printf '<%s><%s>' \"${a[a]}\" \"${a[words]}\"", "<two><>")]
    [InlineData("declare -A a; declare -n r; i=0; declare r=(\"${r:=a}\" \"$((i++))\"); printf 'i=%s key=%s val=%s' \"$i\" \"${!a[*]}\" \"${a[*]}\"", "i=1 key=a val=0")]
    [InlineData("declare -A a; declare -n r; v='two words'; declare r=([key]=\"${r:=a}\" [other]=$v); printf '<%s><%s>' \"${a[key]}\" \"${a[other]}\"", "<a><two words>")]
    [InlineData("declare -A a; declare -n r; declare r=(\"${r:=a}\" [key]=value); printf '<%s>' \"${a[a]}\"", "<[key]=value>")]
    public async Task Declaration_CompoundAssignmentsUseTargetsInitializedByTheirRhs(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -a values=([[ value ]] tail); printf '<%s>\\n' \"${values[@]}\"", "<[[ value ]]>\n<tail>\n")]
    [InlineData("f() { local values=([one two] tail); printf '<%s>\\n' \"${values[@]}\"; }; f", "<[one two]>\n<tail>\n")]
    [InlineData("declare -A values=([one two] value); printf '<%s>\\n' \"${values['[one two]']}\"", "<value>\n")]
    public async Task Declaration_CompoundBracketWordsPreserveGroupingAndExpansion(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareElementSubscripts_ExpandByDeclaredKind()
    {
        var script = @"index=2; key='a b'; declare -a symbolic[index]=symbolic; declare -a arithmetic[1+1]=arithmetic; declare -A map[""$key""]=associated; printf '<%s>:<%s>:<%s>\n' ""${symbolic[2]}"" ""${arithmetic[2]}"" ""${map[a b]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<symbolic>:<arithmetic>:<associated>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareAssociativeArray_AcceptsInlineCompoundAssignment()
    {
        var script = @"declare -A map=([a]=1 [b]=2); set -- ""${!map[@]}""; printf '<%s>:<%s> count=<%s> keys=%s:<%s>:<%s>\n' ""${map[a]}"" ""${map[b]}"" ""${#map[@]}"" ""$#"" ""$1"" ""$2""";
        var result = await EvaluationTestHelper.RunAsync(script);

        // Key order follows unsigned byte order, not Bash's hash order.
        Assert.Equal("<1>:<2> count=<2> keys=2:<a>:<b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareCompoundSubscripts_ExpandByArrayKind()
    {
        var script = @"key='a b'; index=2; declare -A map=([""$key""]=associated); declare -a arr=([index]=indexed); printf '<%s>:<%s>\n' ""${map[a b]}"" ""${arr[2]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<associated>:<indexed>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareCompoundSubscript_IgnoresBracketInsideParameterExpansion()
    {
        var script = "key=']'; declare -A map=([${missing:-]}]=value); printf '<%s>\\n' \"${map[$key]}\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareElementAssignment_PreservesEqualsInAssociativeKey()
    {
        var script = @"key='a=b'; declare -A map dynamic; declare map[""$key""]=value; argument='dynamic[a=b]=expanded'; declare ""$argument""; printf '%s:<%s>:<%s>\n' ""$?"" ""${map[a=b]-missing}"" ""${dynamic[a=b]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0:<value>:<expanded>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_AssociativeSubscripts_PreserveBracesAndCompoundTilde()
    {
        var script = @"HOME=/home/test; declare -A direct; direct[{a,b}]=brace; declare -A compound=([{c,d}]=compound [~]=home); direct_key='{a,b}'; compound_key='{c,d}'; printf '<%s>:<%s>:<%s>\n' ""${direct[$direct_key]}"" ""${compound[$compound_key]}"" ""${compound[""~""]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<brace>:<compound>:<home>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareCompoundAssignment_PersistsPastPrefixScope()
    {
        var script = @"PREFIX=temporary declare -A map=([a]=value); printf '<%s>:<%s>\n' ""${PREFIX-unset}"" ""${map[a]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<unset>:<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_DeclareCompoundAssignment_RestoresFunctionScope()
    {
        var script = @"value=outer; f() { typeset -A value=([key]=inside); printf '<%s>\n' ""${value[key]}""; }; f; printf '<%s>:<%s>\n' ""${value}"" ""${value[key]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<inside>\n<outer>:<outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_ParameterAssignDefault_AssignsAssociativeAtKey()
    {
        var script = @"declare -A map=(); printf 'not reached\n' ""${map[@]:=value}""; status=$?; printf '%s:<%s>:<%s>\n' ""$status"" ""${#map[@]}"" ""${map[""@""]-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("not reached\n0:<1>:<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_CompoundAssignment_CannotChangeExistingArrayKind()
    {
        var script = @"arr[0]=old; declare -A arr=([key]=bad); printf '%s:<%s>\n' ""$?"" ""${arr[0]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: arr: cannot convert indexed to associative array\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_Assignment_AssociativeCompoundMixedItemsFollowLeadingPairForm()
    {
        var script = @"declare -A map=(foo bar [x]=y); key='[x]=y'; printf '<%s>:<%s>:<%s>\n' ""${map[foo]-missing}"" ""${map[$key]-missing}"" ""${#map[@]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<bar>:<>:<2>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_Assignment_AppendOperatorCoversDeclarationsAndNamerefs()
    {
        var script =
            "value=a; declare value+=b; "
            + "arr=(one); declare -a arr+=(two); "
            + "declare -A map=([key]=old); declare -A map+=([key]=new [other]=value); "
            + "declare -n ref=value; ref+=c; "
            + "printf '<%s>:<%s>:<%s>:<%s>:<%s>\\n' "
            + "\"$value\" \"${arr[0]}\" \"${arr[1]}\" \"${map[key]}\" \"${map[other]}\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<abc>:<one>:<two>:<new>:<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("item=(zero one); i=0; declare -n link='item[i++]'\ndeclare -A link\nprintf '<%s:%s:%s:%s>' \"$?\" \"$i\" \"${item[0]}\" \"${item[1]}\"",
        "<1:0:zero:one>", "line 2: declare: item: cannot convert indexed to associative array\n")]
    [InlineData("item=(zero one); i=0; declare -n link=item\ndeclare -A link\nprintf '<%s:%s:%s:%s>' \"$?\" \"$i\" \"${item[0]}\" \"${item[1]}\"",
        "<1:0:zero:one>", "line 2: declare: link: cannot convert indexed to associative array\n")]
    [InlineData("item=(zero one); i=0; declare -n hop='item[i++]' link=hop\ndeclare -A link=([k]=$(printf rhs >&2; printf fresh))\nprintf '<%s:%s:%s:%s>' \"$?\" \"$i\" \"${item[0]}\" \"${item[1]}\"",
        "<1:0:zero:one>", "rhsline 2: item: cannot convert indexed to associative array\n")]
    [InlineData("item=(zero one); i=0; declare -n link=item\ndeclare -A link=([k]=$(printf rhs >&2; printf fresh))\nprintf '<%s:%s:%s:%s>' \"$?\" \"$i\" \"${item[0]}\" \"${item[1]}\"",
        "<1:0:zero:one>", "rhsline 2: link: cannot convert indexed to associative array\n")]
    [InlineData("declare -A item=([k]=value); declare -n link='item[k]'\neval 'declare -A link=([k]=$(printf rhs >&2; printf fresh))'\nprintf '<%s:%s>' \"$?\" \"${item[k]}\"",
        "<1:value>", "rhsline 2: eval: `item[k]': not a valid identifier\n")]
    [InlineData("readonly item=old; i=0; declare -n link='item[i++]'\ndeclare -A link=([k]=$(printf rhs >&2; printf fresh))\nprintf '<%s:%s>' \"$?\" \"$i\"; declare -p item",
        "<1:0>declare -Ar item=([0]=\"old\" )\n", "rhsline 2: `item[i++]': not a valid identifier\n")]
    [InlineData("item=(zero one); readonly item; i=0; declare -n link='item[i++]'\ndeclare -A link=([k]=$(printf rhs >&2; printf fresh))\nprintf '<%s:%s:%s:%s>' \"$?\" \"$i\" \"${item[0]}\" \"${item[1]}\"",
        "<1:0:zero:one>", "rhsline 2: item: cannot convert indexed to associative array\n")]
    [InlineData("item=(zero one); i=0; declare -n link='item[i++]'\nf(){\ndeclare -gA link=([k]=$(printf rhs >&2; printf fresh))\nprintf '<%s:%s:%s:%s>' \"$?\" \"$i\" \"${item[0]}\" \"${item[1]}\"\n}; f",
        "<1:0:zero:one>", "rhsline 3: f: item: cannot convert indexed to associative array\nline 3: declare: item: cannot convert indexed to associative array\n")]
    public async Task ArrayDeclarationDiagnosticsNameTheReachedBinding(string script, string stdout, string stderr)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(stdout, result.StdOut);
        Assert.Equal(stderr, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("i=0; item=(old); declare -n alias='item[i++]'\ndeclare -ai alias=([2]=$(printf rhs >&2; printf skipped))\nprintf '<%s:%s>' \"$?\" \"$i\"; item[1]=2+3; printf '<%s:%s:%s>' \"${item[0]}\" \"${item[1]}\" \"${item[2]-unset}\"",
        "<1:0><old:5:unset>", "rhsline 2: `item[i++]': not a valid identifier\n")]
    [InlineData("i=0; item=(old); declare -n alias='item[i++]'\neval 'declare -au alias=([2]=$(printf rhs >&2; printf skipped))'\nprintf '<%s:%s>' \"$?\" \"$i\"; item[1]=MiXeD; printf '<%s:%s>' \"${item[0]}\" \"${item[1]}\"",
        "<1:0><old:MIXED>", "rhsline 2: eval: `item[i++]': not a valid identifier\n")]
    [InlineData("declare -Ar item=([k]=old); declare -n alias='item[k]'\ndeclare -Ac alias=([k]=$(printf rhs >&2; printf fresh))\nprintf '<%s>' \"$?\"; declare -p item",
        "<1>declare -Arc item=([k]=\"old\" )\n", "rhsline 2: `item[k]': not a valid identifier\n")]
    [InlineData("declare -Ar item=([k]=old); declare -n alias=item\ndeclare -Ai alias=([0]=$(printf rhs >&2; printf fresh))\nprintf '<%s>' \"$?\"; declare -p item",
        "<1>declare -Air item=([k]=\"old\" )\n", "rhsline 2: alias: readonly variable\n")]
    [InlineData("i=0; unset item\ndeclare -ai item[i++]=([2]=$(printf rhs >&2; printf fresh))\nprintf '<%s:%s>' \"$?\" \"$i\"; declare -p item",
        "<1:0>declare -ai item\n", "rhsline 2: item[i++]: cannot assign list to array member\n")]
    [InlineData("item=(old); readonly item; declare -n alias=item\ndeclare -Ai alias=([0]=$(printf rhs >&2; printf fresh))\nprintf '<%s>' \"$?\"; declare -p item",
        "<1>declare -ar item=([0]=\"old\")\n", "rhsline 2: alias: cannot convert indexed to associative array\n")]
    [InlineData("item=(old); readonly item; declare -n alias=item\nf(){\ndeclare -gAi alias=([0]=$(printf rhs >&2; printf fresh))\nprintf '<%s>' \"$?\"; declare -p item\n}; f",
        "<1>declare -ar item=([0]=\"old\")\n", "rhsline 3: f: alias: cannot convert indexed to associative array\nline 3: declare: alias: cannot convert indexed to associative array\n")]
    [InlineData("i=0; item=(old); declare -n alias='item[i++]'\ndeclare -arx alias=([2]=$(printf rhs >&2; printf fresh))\nprintf '<%s:%s>' \"$?\" \"$i\"; declare -p item",
        "<1:0>declare -a item=([0]=\"old\")\n", "rhsline 2: `item[i++]': not a valid identifier\n")]
    public async Task RejectedCompoundDeclarationPreservesSourceAttributes(string script, string stdout, string stderr)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(stdout, result.StdOut);
        Assert.Equal(stderr, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("i=1; item=([0]=zero [1]=one [4]=four); declare -n link='item[i++]'\ndeclare link\nprintf '<%s:%s:%s>' \"$i\" \"${#item[@]}\" \"${item[4]-hidden}\"; item[9]=tail; declare -p item",
        "<2:0:hidden>declare -a item=([0]=\"zero\" [1]= [4]=\"four\" [9]=\"tail\")\n", "")]
    [InlineData("declare -A item=([9]=old [k]=one); declare -n link='item[${key:=k}]'\ndeclare link\nprintf '<%s:%s:%s>' \"$key\" \"${#item[@]}\" \"${item[9]-hidden}\"; item[9]=tail; declare -p item",
        "<k:0:hidden>declare -A item=([9]=\"tail\" [k]= )\n", "")]
    [InlineData("i=1; item=(zero one); declare -n link='item[i++]'\neval 'declare link=($(printf \"rhs%s\" \"$i\" >&2; printf fresh))'\nprintf '<%s:%s:%s>' \"$?\" \"$i\" \"${#item[@]}\"; item[9]=tail; declare -p item",
        "<1:2:0>declare -a item=([0]=\"zero\" [1]= [9]=\"tail\")\n", "rhs1line 2: eval: `item[i++]': not a valid identifier\n")]
    [InlineData("i=1; declare -ar item=(zero one); declare -n link='item[i++]'\ndeclare link\nprintf '<%s:%s:%s>' \"$i\" \"${#item[@]}\" \"${item[0]-hidden}\"; declare -p item",
        "<2:0:hidden>declare -ar item\n", "")]
    [InlineData("i=1; declare -n link='item[i++]'\ndeclare -i link\nprintf '<%s:%s>' \"$i\" \"${#item[@]}\"; item[0]=2+3; declare -p item",
        "<1:0>declare -ai item=([0]=\"5\")\n", "")]
    [InlineData("i=1; item=(zero one); declare -n link='item[i++]'\ni=5 declare link\nprintf '<%s:%s>' \"$i\" \"${#item[@]}\"; item[9]=tail; declare -p item",
        "<6:0>declare -a item=([0]=\"zero\" [1]=\"one\" [5]= [9]=\"tail\")\n", "")]
    [InlineData("item=(zero one); declare -n link='item[1]'\ndeclare link\n(item[9]=tail; declare -p item); declare -p item",
        "declare -a item=([0]=\"zero\" [1]= [9]=\"tail\")\ndeclare -a item\n", "")]
    [InlineData("readonly item=old; i=1; declare -n link='item[i++]'\ndeclare +r link\nprintf '<%s:%s>' \"$?\" \"$i\"; declare -p item",
        "<1:1>declare -r item=\"old\"\n", "line 2: declare: item: readonly variable\n")]
    [InlineData("i=1; declare -n link='item[i++]'\nexport link=(new)\nprintf '<%s:%s>' \"$?\" \"$i\"; declare -p item",
        "<1:1>declare -a item\n", "line 2: `item[i++]': not a valid identifier\n")]
    public async Task ImplicitDeclarationThroughElementNamerefPreservesOtherCells(string script, string stdout, string stderr)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(stdout, result.StdOut);
        Assert.Equal(stderr, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ValuelessElementNamerefKeepsTemporaryScalarAttributesLocal()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "i=0; item=(one two); declare -n link='item[i++]'\nitem=temporary declare -x link\n"
            + "item=temporary declare -r link\nprintf '<%s:%s>' \"$?\" \"$i\"; declare -p item");

        Assert.Equal("<0:0>declare -a item=([0]=\"one\" [1]=\"two\")\n", result.StdOut);
        Assert.Equal("", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("v='a b'; export x=$v; printf '<%s>\\n' \"$x\"", "<a b>\n", "", 0)]
    [InlineData("v='a b'; \"export\" x=$v; printf '<%s>\\n' \"$x\"", "<a>\n", "", 0)]
    [InlineData("v='a b'; ex\"port\" x=$v; printf '<%s>\\n' \"$x\"", "<a>\n", "", 0)]
    [InlineData("v='a b'; ex\\port x=$v; printf '<%s>\\n' \"$x\"", "<a>\n", "", 0)]
    [InlineData("v='a b'; ex\\\nport x=$v; printf '<%s>\\n' \"$x\"", "<a b>\n", "", 0)]
    [InlineData("v='a b'; export \"x\"=$v; printf '<%s>\\n' \"$x\"", "<a>\n", "", 0)]
    [InlineData("v='a b'; export x\"=\"=$v; printf '<%s>\\n' \"$x\"", "<=a>\n", "", 0)]
    [InlineData("v='a b'; export \"x=$v\"; printf '<%s>\\n' \"$x\"", "<a b>\n", "", 0)]
    [InlineData("name=x; v='a b'; export $name=$v; printf '<%s>\\n' \"$x\"", "<a>\n", "", 0)]
    [InlineData("v='x y'; export $v; printf '<%s>:<%s>\\n' \"${x+set}\" \"${y+set}\"", "<>:<>\n", "", 0)]
    [InlineData("v='a b'; export 1x=$v; printf '<%s>:<%s>\\n' \"$?\" \"${b+set}\"", "<1>:<>\n", "line 1: export: `1x=a': not a valid identifier\n", 0)]
    [InlineData("v=old; export v=new x=$v; printf '<%s>:<%s>\\n' \"$v\" \"$x\"", "<new>:<old>\n", "", 0)]
    [InlineData("v='a b'; cmd=export; $cmd x=$v; printf '<%s>\\n' \"$x\"", "<a>\n", "", 0)]
    [InlineData("v='a b'; empty=; $empty export x=$v; printf '<%s>\\n' \"$x\"", "<a>\n", "", 0)]
    [InlineData("export() { printf '<%s>\\n' \"$@\"; }; v='a b'; export x=$v", "<x=a b>\n", "", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; v='a b'; declare -a arr=(\"$v\" \"$(printf z; printf effect >&2)\")", "<-a>\n<arr>\n", "effect", 0)]
    [InlineData("declare -a arr=([2]=\"two words\" tail); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<2>\n<3>\n<two words>\n<tail>\n", "", 0)]
    [InlineData("x=old; readonly x; declare x=$(printf new; printf rhs >&2); printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<1>:<old>\n", "rhsline 1: declare: x: readonly variable\n", 0)]
    [InlineData("arr=(old); readonly arr; declare -a arr=($(printf new; printf rhs >&2)); printf '<%s>:<%s>\\n' \"$?\" \"${arr[0]}\"", "", "rhsline 1: arr: readonly variable\n", 1)]
    [InlineData("local a=(one); printf '%s:<%s>' \"$?\" \"${a[*]}\"", "1:<one>", "line 1: local: can only be used in a function\n", 0)]
    [InlineData("local(){ printf 'called:%s/' \"$1\"; }; local a=(one); printf '<%s>' \"${a[*]}\"", "called:a/<one>", "", 0)]
    [InlineData("readonly a=old; f(){ local a=($(printf rhs >&2; printf new)) b=ok; printf '%s:%s:%s/' \"$?\" \"$a\" \"$b\"; }; f; printf end", "1:old:ok/end", "rhsline 1: f: a: readonly variable\nline 1: local: a: readonly variable\n", 0)]
    [InlineData("readonly a=old; f(){ local -a a=(new) b=(two); printf '%s:%s:%s/' \"$?\" \"$a\" \"$b\"; }; f; printf end", "1:old:two/end", "line 1: f: a: readonly variable\nline 1: local: a: readonly variable\n", 0)]
    [InlineData("readonly a=old; f(){ local -n r=a; local r=(new); printf '%s:%s/' \"$?\" \"$a\"; }; f; printf end", "1:old/end", "line 1: f: a: readonly variable\nline 1: local: a: readonly variable\n", 0)]
    [InlineData("f(){ local -A a=([key]=old); local -a a=(new); printf '%s:%s/' \"$?\" \"${a[key]}\"; }; f; printf end", "1:old/end", "line 1: a: cannot convert associative to indexed array\nline 1: local: a: cannot convert associative to indexed array\n", 0)]
    [InlineData("f(){ local -r a=old; local a=(new); printf inside; }; f; printf outside", "", "line 1: a: readonly variable\n", 1)]
    [InlineData("arr=(old); declare -A arr; printf '<%s>:<%s>\\n' \"$?\" \"${arr[0]}\"", "<1>:<old>\n", "line 1: declare: arr: cannot convert indexed to associative array\n", 0)]
    [InlineData("arr=(old); declare -A arr=([a]=$(printf new; printf rhs >&2)); printf after", "", "rhsline 1: arr: cannot convert indexed to associative array\n", 1)]
    [InlineData("declare -Z x=$(printf new; printf rhs >&2); printf '<%s>\\n' \"$?\"", "<2>\n", "rhsline 1: declare: -Z: invalid option\n" + EvaluationTestHelper.DeclareUsage, 0)]
    [InlineData("declare +- -i x=2+3; printf '%s/%s' \"$?\" \"${x-unset}\"", "2/unset", "line 1: declare: +-: invalid option\n" + EvaluationTestHelper.DeclareUsage, 0)]
    [InlineData("declare +- x=new 2>&-; printf '%s/%s' \"$?\" \"${x-unset}\"", "2/unset", "", 0)]
    [InlineData("x=outer; f(){ local +- -i x=2+3; printf '%s/%s' \"$?\" \"$x\"; }; f", "2/outer", "line 1: local: +-: invalid option\nlocal: usage: local [option] name[=value] ...\n", 0)]
    [InlineData("declare -Z arr=($(printf new; printf rhs >&2)); printf '<%s>\\n' \"$?\"", "<2>\n", "rhsline 1: declare: -Z: invalid option\n" + EvaluationTestHelper.DeclareUsage, 0)]
    [InlineData("v=old; declare v+=new; printf '<%s>\\n' \"$v\"", "<oldnew>\n", "", 0)]
    [InlineData("arr[3]=old; declare -a arr+=(new); printf '<%s>\\n' \"${!arr[@]}\" \"${arr[@]}\"", "<3>\n<4>\n<old>\n<new>\n", "", 0)]
    [InlineData("arr=(outer); f() { local -a arr=(\"${arr[0]-missing}\" new); printf 'inside=<%s>\\n' \"${arr[@]}\"; }; f; printf 'outside=<%s>\\n' \"${arr[@]}\"", "inside=<outer>\ninside=<new>\noutside=<outer>\n", "", 0)]
    [InlineData("arr=(outer); f() { declare -a arr=(\"${arr[0]-missing}\" new); printf 'inside=<%s>\\n' \"${arr[@]}\"; }; f; printf 'outside=<%s>\\n' \"${arr[@]}\"", "inside=<outer>\ninside=<new>\noutside=<outer>\n", "", 0)]
    [InlineData("x=outer; f() { local x=\"$x\"; printf '<%s>\\n' \"$x\"; }; f; printf '<%s>\\n' \"$x\"", "<outer>\n<outer>\n", "", 0)]
    [InlineData("x=outer; f() { local x=inner y=$x; printf '<%s>:<%s>\\n' \"$x\" \"$y\"; }; f", "<inner>:<outer>\n", "", 0)]
    [InlineData("v=outer; f() { local v+=new; printf '<%s>\\n' \"$v\"; }; f; printf '<%s>\\n' \"$v\"", "<new>\n<outer>\n", "", 0)]
    [InlineData("arr=(outer); f() { local -a arr+=(new); printf '<%s>\\n' \"${arr[@]}\"; }; f; printf '<%s>\\n' \"${arr[@]}\"", "<new>\n<outer>\n", "", 0)]
    [InlineData("export arr=(one two); printf '<%s>\\n' \"${arr[@]}\"", "<one>\n<two>\n", "", 0)]
    [InlineData("readonly -a arr=(one two); printf '<%s>\\n' \"${arr[@]}\"", "<one>\n<two>\n", "", 0)]
    [InlineData("i=0; declare -a arr[i++]; printf '<%s>:<%s>\\n' \"$i\" \"${#arr[@]}\"", "<0>:<0>\n", "", 0)]
    [InlineData("i=0; declare -a arr[i++]=$((i+=10)); printf '<%s>\\n' \"$i\" \"${!arr[@]}\" \"${arr[@]}\"", "<11>\n<10>\n<10>\n", "", 0)]
    [InlineData("declare() { printf 'arg=<%s>\\n' \"$@\"; printf 'during=<%s>\\n' \"${arr[@]}\"; }; arr=(old); declare -a arr=(new tail); printf 'after=<%s>\\n' \"${arr[@]}\"", "arg=<-a>\narg=<arr>\nduring=<new>\nduring=<tail>\nafter=<new>\nafter=<tail>\n", "", 0)]
    [InlineData("arr=(old); declare -Z arr=(new tail); printf 'status=<%s> value=<%s>:<%s>\\n' \"$?\" \"${arr[0]}\" \"${arr[1]}\"", "status=<2> value=<new>:<tail>\n", "line 1: declare: -Z: invalid option\n" + EvaluationTestHelper.DeclareUsage, 0)]
    [InlineData("arr=(old); declare arr=(new) x=${arr[0]}; printf '<%s>:<%s>\\n' \"$x\" \"${arr[0]}\"", "<new>:<new>\n", "", 0)]
    [InlineData("x=old; declare x=new arr=(\"$x\"); printf '<%s>:<%s>\\n' \"$x\" \"${arr[0]}\"", "<new>:<old>\n", "", 0)]
    [InlineData("arr=(old); declare arr=(new) copy=(\"${arr[0]}\"); printf '<%s>\\n' \"${copy[0]}\"", "<new>\n", "", 0)]
    [InlineData("declare -A map=([a]=one); printf '<%s>:<%s>\\n' \"${map[a]}\" \"${map[0]-missing}\"", "<one>:<missing>\n", "", 0)]
    [InlineData("f() { local -a arr=(one); local -a arr+=(two); printf '<%s>\\n' \"${arr[@]}\"; }; f", "<one>\n<two>\n", "", 0)]
    [InlineData("arr=(outer); f() { local -a arr=(); printf '<%s>\\n' \"${#arr[@]}\"; }; f; printf '<%s>\\n' \"${arr[0]}\"", "<0>\n<outer>\n", "", 0)]
    [InlineData("arr=(old); declare -n ref=arr; declare ref[0]+=new; printf '<%s>\\n' \"${arr[0]}\"", "<old>\n", "line 1: warning: ref: removing nameref attribute\n", 0)]
    [InlineData("unset x; export x; printf '<%s>\\n' \"${x+set}\"", "<>\n", "", 0)]
    [InlineData("unset x; readonly x; printf '<%s>\\n' \"${x+set}\"", "<>\n", "", 0)]
    [InlineData("unset x; declare x; printf '<%s>\\n' \"${x+set}\"", "<>\n", "", 0)]
    [InlineData("v='a b'; export -f x=$v; printf '<%s>\\n' \"$?\"", "<1>\n", "line 1: export: x=a b: not a function\n", 0)]
    [InlineData("HOME=/home/test; declare -a arr=(~); declare -A map=([~]=~); printf '<%s>:<%s>\\n' \"${arr[0]}\" \"${map[/home/test]}\"", "</home/test>:<>\n", "", 0)]
    [InlineData("i=0; declare x=$((i++)); printf '<%s>:<%s>\\n' \"$i\" \"$x\"", "<1>:<0>\n", "", 0)]
    [InlineData("declare x=$(printf value; printf effect >&2); printf '<%s>\\n' \"$x\"", "<value>\n", "effect", 0)]
    [InlineData("arr=(old); declare -n ref=arr; declare ref[0]+=new; printf '<%s>:<%s>\\n' \"${arr[0]}\" \"${ref[0]}\"", "<old>:<new>\n", "line 1: warning: ref: removing nameref attribute\n", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"${arr[@]}\"; }; arr=(outer); f() { declare arr=(inner); }; f; printf 'after=<%s>\\n' \"${arr[@]}\"", "<inner>\nafter=<outer>\n", "", 0)]
    [InlineData("arr=(old); declare -n ref=arr; declare ref[0]; printf '<%s>:<%s>\\n' \"${arr[0]}\" \"${ref[0]-unset}\"", "<old>:<old>\n", "", 0)]
    [InlineData("arr=(old); declare -n ref=arr; declare ref=(new); printf '<%s>:<%s>\\n' \"${arr[0]}\" \"${ref[0]}\"", "<new>:<new>\n", "", 0)]
    [InlineData("arr=(old); export arr[0]=new; printf '<%s>:<%s>\\n' \"$?\" \"${arr[0]}\"", "<1>:<old>\n", "line 1: export: `arr[0]': not a valid identifier\n", 0)]
    [InlineData("x=old; declare -a x; printf '<%s>\\n' \"${x[@]}\"", "<old>\n", "", 0)]
    [InlineData("declare -Z -A map=([key]=value); printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${!map[@]}\" \"${map[key]}\"", "<2>:<key>:<value>\n", "line 1: declare: -Z: invalid option\n" + EvaluationTestHelper.DeclareUsage, 0)]
    [InlineData("arr=(old); f() { local -a arr; printf '<%s>:<%s>\\n' \"${arr+set}\" \"${#arr[@]}\"; }; f; printf '<%s>\\n' \"${arr[0]}\"", "<>:<0>\n<old>\n", "", 0)]
    // Identifier quoting and arithmetic-token suffixes retain the engine's established diagnostic format.
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; i=0; declare arr[$((i++))]=$i; printf 'i=<%s>\\n' \"$i\"", "<arr[0]=1>\ni=<1>\n", "", 0)]
    [InlineData("i=0; declare arr[$((i++))]=$i; printf '<%s>:<%s>\\n' \"$i\" \"${arr[0]}\"", "<1>:<1>\n", "", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; i=0; declare arr[i++]=$i; printf 'i=<%s>\\n' \"$i\"", "<arr[i++]=0>\ni=<0>\n", "", 0)]
    [InlineData("key=1; declare -A map; declare map[\"$key\"]=$((key=2)); printf '<%s>:<%s>\\n' \"${map[1]-missing}\" \"${map[2]-missing}\"", "<2>:<missing>\n", "", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; declare arr[$(printf key; printf target >&2)]=$(printf value; printf rhs >&2)", "<arr[key]=value>\n", "targetrhs", 0)]
    [InlineData("declare -A map; declare map[$(printf key; printf target >&2)]=$(printf value; printf rhs >&2); printf '<%s>\\n' \"${map[key]}\"", "<value>\n", "targetrhs", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; key='$(printf hidden >&2)'; declare map[\"$key\"]=value", "<map[$(printf hidden >&2)]=value>\n", "", 0)]
    [InlineData("declare -A map; key='$(printf hidden >&2)'; declare map[\"$key\"]=value; printf '<%s>\\n' \"${!map[@]}\"", "<>\n", "hiddenline 1: map[$(printf hidden >&2)]: bad array subscript\n", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; key=expanded; declare map['$key']=value", "<map[$key]=value>\n", "", 0)]
    [InlineData("declare -A map; key=expanded; declare map['$key']=value; printf '<%s>\\n' \"${!map[@]}\"", "<expanded>\n", "", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; key='a b'; declare map[\"$key\"]+='two words'", "<map[a b]+=two words>\n", "", 0)]
    [InlineData("declare -A map; key='a]b'; declare map[\"$key\"]=value; printf '<%s>\\n' \"${!map[@]}\"", "<>\n", "line 1: declare: `map[a]b]=value': not a valid identifier\n", 0)]
    [InlineData("declare() { printf '<%s>\\n' \"$@\"; }; i=0; declare arr[$((i++))]=$i next=$i", "<arr[0]=1>\n<next=1>\n", "", 0)]
    [InlineData("declare -A map; key=; declare map[$key]=value later=yes; printf '<%s>:<%s>\\n' \"$?\" \"${later-unset}\"", "<1>:<yes>\n", "line 1: map[]: bad array subscript\n", 0)]
    [InlineData("arr=(old); declare arr[-2]=new later=yes; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${arr[0]}\" \"${later-unset}\"", "<1>:<old>:<yes>\n", "line 1: arr[-2]: bad array subscript\n", 0)]
    [InlineData("declare arr[]=new later=yes; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${arr[0]-unset}\" \"${later-unset}\"", "<1>:<unset>:<yes>\n", "line 1: arr[]: bad array subscript\n", 0)]
    [InlineData("declare arr[@]=new later=yes; printf '<%s>:<%s>\\n' \"$?\" \"${later-unset}\"", "<1>:<yes>\n", "line 1: arr[@]: bad array subscript\n", 0)]
    [InlineData("declare 'arr[1/0]=new' later=yes; printf '<%s>:<%s>\\n' \"$?\" \"${later-unset}\"", "", "line 1: 1/0: division by 0 (error token is \"0\")\n", 1)]
    [InlineData("arr=old; declare arr[-1]+=new; printf '<%s>:<%s>\\n' \"$?\" \"${arr[0]}\"", "<0>:<oldnew>\n", "", 0)]
    [InlineData("arr=(old); readonly arr; declare arr[-2]=$(printf rhs >&2; printf new) later=yes; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${arr[0]}\" \"${later-unset}\"", "<1>:<old>:<yes>\n", "rhsline 1: declare: arr: readonly variable\n", 0)]
    [InlineData("f() { local -A map; key=; local map[$key]=value later=yes; printf '<%s>:<%s>\\n' \"$?\" \"${later-unset}\"; }; f", "<1>:<yes>\n", "line 1: map[]: bad array subscript\n", 0)]
    [InlineData("arr=(old); typeset arr[-2]=new later=yes; printf '<%s>:<%s>\\n' \"$?\" \"${later-unset}\"", "<1>:<yes>\n", "line 1: arr[-2]: bad array subscript\n", 0)]
    [InlineData("declare() { printf called; }; declare arr[$((1/0))]=$(printf rhs >&2; printf value); printf after", "", "line 1: 1/0: division by 0 (error token is \"0\")\n", 1)]
    [InlineData("v='a b'; export exported=$v; readonly fixed=$v; printf '<%s>:<%s>\\n' \"$exported\" \"$fixed\"", "<a b>:<a b>\n", "", 0)]
    [InlineData("seed=old; arr=(zero); seed=new export arr=(\"$seed\" tail); printf '<%s>:<%s>:<%s>\\n' \"${arr[0]}\" \"${arr[1]}\" \"$seed\"", "<old>:<tail>:<old>\n", "", 0)]
    [InlineData("declare -A map; key='$other'; other=actual; declare map[\"$key\"]=value; printf '<%s>\\n' \"${!map[@]}\"", "<actual>\n", "", 0)]
    [InlineData("declare value; printf '<%s>:<%s>\\n' \"${value+x}\" \"${value-unset}\"", "<>:<unset>\n", "", 0)]
    [InlineData("declare -a values; printf '<%s>:<%s>\\n' \"${#values[@]}\" \"${values[0]-unset}\"", "<0>:<unset>\n", "", 0)]
    [InlineData("declare -A values=([key]='a b'); printf '<%s>:<%s>\\n' \"${#values[@]}\" \"${values[key]}\"", "<1>:<a b>\n", "", 0)]
    public async Task Evaluation_DeclarationOperands_PreserveSyntaxAndStaging(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("\"declare\" -a arr=(one two); printf after")]
    [InlineData("command declare -a arr=(one two); printf after")]
    [InlineData("builtin declare -a arr=(one two); printf after")]
    public void Parse_QuotedOrWrappedCompoundDeclaration_IsRejected(string script)
    {
        Assert.Throws<Lokad.Bash.Syntax.SyntaxException>(() => Lokad.Bash.Syntax.Parser.Parse(script));
    }

    [Fact]
    public async Task Evaluation_ExportedNameFields_RemainUnsetAndKeepSeparateAttributes()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(
            Lokad.Bash.Syntax.Parser.Parse("v='x y'; export $v"));
        var host = EndToEndHarness.WithMemoryBudget(EvaluationTestHelper.BuildDefaultTree(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.False(evaluation.Variables.ContainsKey("x"));
        Assert.False(evaluation.Variables.ContainsKey("y"));
        Assert.Contains("x", evaluation.ExportedVariables);
        Assert.Contains("y", evaluation.ExportedVariables);
        Assert.DoesNotContain("x y", evaluation.ExportedVariables);
        Assert.True(host.StandardError.Content.IsEmpty);
    }
}

using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class DeclarationAttributeTestsEndToEnd
{
    [Theory]
    [InlineData("item=old; declare -rn link=item", "export link+=more",
        "<0:link+=more>declare -x item=\"oldmore\"\ndeclare -nr link=\"item\"\n", "", 0)]
    [InlineData("item=(old); declare -rn link='item[i++]'", "export link=new",
        "<0:link=new>declare -a item=([0]=\"new\")\ndeclare -nr link=\"item[i++]\"\n",
        "line 2: export: `item[i++]': not a valid identifier\n", 1)]
    [InlineData("declare -ar item=(old); declare -rn link='item[i++]'", "readonly link=new",
        "<1:link=new>declare -ar item=([0]=\"old\")\ndeclare -nr link=\"item[i++]\"\n",
        "line 2: item: readonly variable\nline 2: readonly: `item[i++]': not a valid identifier\n", 1)]
    [InlineData("declare -rn link", "export link=new",
        "<1:link=new>declare -nr link\n",
        "line 2: link: readonly variable\nline 2: export: `': not a valid identifier\n", 0)]
    public async Task NamedAttributeAssignmentsCheckTheReachedNamerefBinding(
        string setup, string command, string expectedOutput, string expectedError, int expectedIndex)
    {
        var result = await EvaluationTestHelper.RunAsync("i=0; " + setup + "\n" + command
            + "\nprintf '<%s:%s>' \"$?\" \"$_\"; declare -p item link 2>/dev/null; printf '<%s>' \"$i\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{expectedOutput}<{expectedIndex}>", result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Theory]
    [InlineData("i", "i=1", "[0]=\"zero\" [1]=\"oldmore\"")]
    [InlineData("-1", "", "[0]=\"zeromore\" [1]=\"old\"")]
    public async Task NamedAttributeElementWritesUseTheSavedArrayUnderAPrefix(string index, string prefix, string savedArray)
    {
        var result = await EvaluationTestHelper.RunAsync($"""
            item=(zero old); i=0; declare -rn link='item[{index}]'
            item=scoped {prefix} command export link+=more
            printf '<%s:%s:%s>' "$?" "$i" "$_"
            declare -p item link
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<0:0:link+=more>declare -a item=({savedArray})\ndeclare -nr link=\"item[{index}]\"\n",
            result.StdOut);
        Assert.Equal($"line 2: export: `item[{index}]': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Theory]
    [InlineData("export -a x; declare -p x", "declare -x x\n", "")]
    [InlineData("x=old; readonly -A x; declare -p x", "declare -r x=\"old\"\n", "")]
    [InlineData("x=(one two); export -A x; declare -p x", "declare -ax x=([0]=\"one\" [1]=\"two\")\n", "")]
    [InlineData("declare -n x=target; target=old; readonly -a x; declare -p x target",
        "declare -n x=\"target\"\ndeclare -r target=\"old\"\n", "")]
    [InlineData("declare -n x; export -A x; printf '<%s>' \"$?\"; declare -p x",
        "<0>declare -n x\n", "line 1: export: `': not a valid identifier\n")]
    [InlineData("export -aA x=edited; declare -p x", "declare -ax x=([0]=\"edited\")\n", "")]
    [InlineData("x=(old spare); export -A x=edited later=next; printf '<%s>' \"$?\"; declare -p x later",
        "<1>declare -ax x=([0]=\"old\" [1]=\"spare\")\ndeclare -Ax later=([0]=\"next\" )\n",
        "line 1: export: x: cannot convert indexed to associative array\n")]
    [InlineData("readonly x=old; export -a x=edited; printf '<%s>' \"$?\"; declare -p x",
        "<1>declare -rx x=\"old\"\n", "line 1: export: x: readonly variable\n")]
    [InlineData("x=outer; f(){ local x=inner; export -a x+=tail; declare -p x; }; f; declare -p x",
        "declare -ax x=([0]=\"innertail\")\ndeclare -ax x=([0]=\"outer\")\n", "")]
    [InlineData("x=outer; f(){ local x=inner; export -n -A x=edited; declare -p x; }; f; declare -p x",
        "declare -- x=\"inner\"\ndeclare -Ax x=([0]=\"edited\" )\n", "")]
    [InlineData("x=outer; f(){ local x=inner; readonly -A x=([key]=item); declare -p x; }; f; declare -p x",
        "declare -Ar x=([key]=\"item\" )\ndeclare -- x=\"outer\"\n", "")]
    [InlineData("x=outer; f(){ local x=inner; readonly -A 'x=([key]=item)'; declare -p x; }; f; declare -p x",
        "declare -r x=\"inner\"\ndeclare -Ar x=([key]=\"item\" )\n", "")]
    [InlineData("x=old; x=prefix export -a x=edited; declare -p x",
        "declare -ax x=([0]=\"prefix\")\n", "")]
    [InlineData("x=old; x=prefix readonly -A x=edited; declare -p x",
        "declare -Arx x=([0]=\"edited\" )\n", "line 1: x: readonly variable\n")]
    [InlineData("x=old; x=prefix readonly -n -a x=edited; declare -p x",
        "declare -a x=([0]=\"edited\")\n", "")]
    [InlineData("x=outer; f(){ local -A x=([0]=old [key]=other); export -a x=edited; declare -p x; }; f; declare -p x",
        "declare -ax x=([0]=\"edited\")\ndeclare -ax x=([0]=\"outer\")\n", "")]
    [InlineData("x=outer; f(){ local -A x=([0]=old [key]=other); export -a x+=tail; declare -p x; }; f; declare -p x",
        "declare -ax x=([0]=\"oldtail\")\ndeclare -ax x=([0]=\"outer\")\n", "")]
    [InlineData("declare -ax ix=(one); declare -Ax ax=([key]=two); export -a; export -A; export -aA",
        "declare -ax ix=([0]=\"one\")\ndeclare -Ax ax=([key]=\"two\" )\n", "")]
    public async Task ArrayOptionsSelectAssignmentKindsAndKeepNamedAttributes(
        string script, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
    }

    [Theory]
    [InlineData("v=MiXeD; declare -l v; printf '<%s>' \"$v\"; v+=TaIL; declare -p v", "<MiXeD>declare -l v=\"mixedtail\"\n")]
    [InlineData("declare -c a='...mIXeD tEXT' b='tWO wORDS'; declare -p a b", "declare -c a=\"...mixed text\"\ndeclare -c b=\"Two words\"\n")]
    [InlineData("declare -lu v=MiXeD; declare -p v", "declare -- v=\"MiXeD\"\n")]
    [InlineData("declare -u v=one; declare -n r=v; r=MiXeD; declare -p r v", "declare -n r=\"v\"\ndeclare -u v=\"MIXED\"\n")]
    [InlineData("declare -uA a; printf '<%s>' \"${a[Key]:=MiXeD}\"; declare -p a", "<MIXED>declare -Au a=([Key]=\"MIXED\" )\n")]
    [InlineData("declare -l v=MiXeD; f(){ local -I v; v=NeXt; declare -p v; }; f; declare -p v", "declare -l v=\"next\"\ndeclare -l v=\"mixed\"\n")]
    [InlineData("declare --help -A a=([Key]=MiXeD) >&-; declare -p a", "declare -Al a=([Key]=\"mixed\" )\n")]
    [InlineData("declare -ua v; read -ra v <<< 'MiXeD Text'; declare -p v", "declare -au v=([0]=\"MIXED\" [1]=\"TEXT\")\n")]
    [InlineData("declare -la v=(OLD); mapfile -t -O 2 v <<< MiXeD; declare -p v", "declare -al v=([0]=\"old\" [2]=\"mixed\")\n")]
    [InlineData("declare -u v=école; declare -p v", "declare -u v=\"ÉCOLE\"\n")]
    [InlineData("f(){ :; }; g(){ :; }; declare -fu f; declare -Fu", "declare -fu f\n")]
    [InlineData("opt=-u; declare \"$opt\" -a a=(MiXeD); declare -p a", "declare -au a=([0]=\"MiXeD\")\n")]
    public async Task DeclarationCaseAttributesApplyWhenTheSelectedValueIsWritten(string script, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("readonly -n r=v\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -p r",
        "[0:0]declare -- r=\"v\"\n", "")]
    [InlineData("v=old; declare -n r=v\nreadonly -n r=new\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -p r v",
        "[0:0]declare -n r=\"v\"\ndeclare -- v=\"new\"\n", "")]
    [InlineData("readonly r=old\nreadonly -n r=new new=ok\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -p r new",
        "[1:1]declare -r r=\"old\"\ndeclare -- new=\"ok\"\n", "line 2: r: readonly variable\n")]
    [InlineData("readonly -an r=(first second)\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -p r",
        "[0:0]declare -a r=([0]=\"first\" [1]=\"second\")\n", "")]
    [InlineData("f(){ :; }; readonly -f f\nreadonly -fn f\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -pF f",
        "[0:0]declare -fr f\n", "")]
    [InlineData("readonly -na missing\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -p missing; :",
        "[0:0]", "line 2: declare: missing: not found\n")]
    [InlineData("a=(old); declare -n r='a[0]'\nreadonly -n r=new\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"; declare -p r a",
        "[0:0]declare -n r=\"a[0]\"\ndeclare -a a=([0]=\"new\")\n", "")]
    public async Task ReadonlyDashNAllowsAssignmentsAndPreservesAttributes(string script, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r", "declare -r", "")]
    [InlineData("r=s s", "declare -r", "")]
    [InlineData("r=s s", "declare -gr", "")]
    [InlineData("r", "declare -rx", "")]
    [InlineData("r", "readonly", "line 1: readonly: `': not a valid identifier\n")]
    [InlineData("r=s s", "readonly", "line 1: readonly: `': not a valid identifier\n")]
    [InlineData("r", "export", "line 1: export: `': not a valid identifier\n")]
    [InlineData("r", "export -n", "")]
    public async Task UnsetNamerefAttributesDoNotAffectUnrelatedBindings(string references, string declaration, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n " + references + "; " + declaration + " r later=kept; status=$?; "
            + "declare -n probe; probe=target; target=value; printf '%s/<%s>/<%s>' \"$status\" \"$probe\" \"$later\"");

        Assert.Equal("0/<value>/<kept>", result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local", "=")]
    [InlineData("local", "+=")]
    [InlineData("declare", "=")]
    [InlineData("declare", "+=")]
    public async Task ReadonlyLocalElementDiagnosticsKeepTheOriginalNamerefName(string command, string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); i=1; f(){ local -n r=a; " + command + " -r 'r[i++]" + assignment
            + "new' later=kept; printf '%s/%s/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\" \"$later\"; }; "
            + "f; printf '/<%s>' \"${a[*]}\"");

        Assert.Equal("0/2/<>/<kept>/<old tail>", result.StdOut);
        Assert.Equal("line 1: r: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExistingReadonlyLocalElementDiagnosticsNameTheReferent()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); readonly a; f(){ local -n r=a; local 'r[1]=new'; "
            + "printf '%s/<%s>' \"$?\" \"${a[*]}\"; }; f");

        Assert.Equal("1/<old tail>", result.StdOut);
        Assert.Equal("line 1: local: a: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(false, "=")]
    [InlineData(false, "+=")]
    [InlineData(true, "=")]
    [InlineData(true, "+=")]
    public async Task ReadonlyElementInitializersKeepValuesAndEvaluateSelectorsOnce(bool local, string assignment)
    {
        var script = (local ? "local a" : "a") + "=(old tail); i=1; " + (local ? "local" : "declare")
            + " -r 'a[i++]" + assignment + "new' later=kept; printf '%s/%s/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\" \"$later\"";
        if (local) script = "f(){ " + script + "; }; f";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0/2/<old tail>/<kept>", result.StdOut);
        Assert.Equal("line 1: a: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("=")]
    [InlineData("+=")]
    public async Task ReadonlyAssociativeElementInitializersKeepValues(string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -A a=([key]=old [0]=zero); declare -r 'a[key]" + assignment
            + "new'; printf '%s/<%s>/<%s>' \"$?\" \"${a[key]}\" \"${a[0]}\"");

        Assert.Equal("0/<old>/<zero>", result.StdOut);
        Assert.Equal("line 1: a: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RejectedReadonlyElementInitializerStillCreatesTheIndexedArray()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -r 'a[1]=new'; declare -A a; printf '%s/<%s>' \"$?\" \"${a[*]}\"");

        Assert.Equal("1/<>", result.StdOut);
        Assert.Equal("line 1: a: readonly variable\nline 1: declare: a: cannot convert indexed to associative array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("r=a", "<after>/<unset>")]
    [InlineData("r=s s=a", "<after>/<after>")]
    public async Task ExplicitElementNamerefRemovalWritesBeforeDetachingTheLastReference(string references, string finalReferences)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); i=1; declare -n " + references
            + "; declare +n 'r[i++]+=new'; printf '%s/%s/<%s>/<%s>;' \"$?\" \"$i\" \"${a[*]}\" \"$r\"; "
            + "r=after; printf '<%s>/<%s>' \"$r\" \"${s-unset}\"");

        Assert.Equal("0/2/<old tailnew>/<a>;" + finalReferences, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("=", "new")]
    [InlineData("+=", "oldnew")]
    public async Task ExplicitAssociativeElementNamerefRemovalKeepsTheReferentArray(string assignment, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -A a=([key]=old [other]=tail); declare -n r=a; declare +n 'r[key]" + assignment
            + "new'; printf '%s/<%s>/<%s>/<%s>' \"$?\" \"${a[key]}\" \"${a[other]}\" \"$r\"");

        Assert.Equal("0/<" + expected + ">/<tail>/<a>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExplicitElementReadonlyDiagnosticUsesTheOriginalNamerefName()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); declare -n r=s s=a; declare -r +n 'r[1]=new'; "
            + "printf '%s/<%s>/<%s>/<%s>' \"$?\" \"${a[*]}\" \"$r\" \"$s\"");

        Assert.Equal("0/<old tail>/<a>/<a>", result.StdOut);
        Assert.Equal("line 1: r: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DirectElementWritesOutlivePrefixesWithoutPromotingTheirReadonlyAttribute()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); a=tmp declare -r 'a[1]=new'; a[0]=after; printf '%s/<%s>' \"$?\" \"${a[*]}\"");

        Assert.Equal("0/<after new>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExplicitNamerefElementWritesStayWithinTheReferentsPrefix()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); declare -n r=a; a=tmp declare +n 'r[1]+=new'; "
            + "printf '%s/<%s>/<%s>' \"$?\" \"${a[*]}\" \"$r\"");

        Assert.Equal("0/<old tail>/<a>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a[i++]=new", "zero new two")]
    [InlineData("'a[i++]+=new'", "zero onenew two")]
    public async Task NamerefRemovalPreservesExplicitIndexedSelectors(string operand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(zero one two); i=1; declare +n " + operand
            + "; printf '%s:%s:<%s>' \"$?\" \"$i\" \"${a[*]}\"");

        Assert.Equal("0:2:<" + expected + ">", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a[key]=new", "new")]
    [InlineData("'a[key]+=new'", "oldnew")]
    public async Task NamerefRemovalPreservesExplicitAssociativeSelectors(string operand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -A a=([0]=zero [key]=old [tail]=kept); declare +n " + operand
            + "; printf '%s:<%s>:<%s>:<%s>' \"$?\" \"${a[0]}\" \"${a[key]}\" \"${a[tail]}\"");

        Assert.Equal("0:<zero>:<" + expected + ">:<kept>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "+n", "=", 1, "new two")]
    [InlineData("", "+n", "+=", 1, "onenew two")]
    [InlineData("a=temporary ", "+n", "=", 1, "new two")]
    [InlineData("a=temporary ", "+n", "+=", 1, "onenew two")]
    [InlineData("a=temporary ", "-g +n", "+=", 1, "onenew two")]
    [InlineData("a=temporary i=1 ", "+n", "+=", 2, "one twonew")]
    [InlineData("a=temporary i=1 ", "-g +n", "+=", 2, "one twonew")]
    public async Task NamerefRemovalWritesTheSelectedArrayElementOnce(
        string prefix, string flags, string assignment, int index, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(one two); i=0; declare -n r='a[i++]'; " + prefix + "declare " + flags + " r" + assignment
            + "new; printf '%s:<%s>:<%s>' \"$i\" \"${a[*]}\" \"$r\"");

        Assert.Equal(index + ":<" + expected + ">:<a[i++]>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("+n")]
    [InlineData("-g +n")]
    public async Task NamerefElementRemovalPreservesThePrefixedAssociativeArray(string flags)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -A a=([key]=old [other]=kept); declare -n r='a[key]'; a=temporary declare "
            + flags + " r+=new; printf '%s/<%s>/<%s>/<%s>' \"$?\" \"${a[key]}\" \"${a[other]}\" \"$r\"");

        Assert.Equal("0/<oldnew>/<kept>/<a[key]>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "old tailnew")]
    [InlineData("a=temporary ", "oldnew tail")]
    public async Task NamerefElementRemovalUsesActiveNegativeIndexBounds(string prefix, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); declare -n r='a[-1]'; " + prefix
            + "declare +n r+=new; printf '<%s>/<%s>' \"${a[*]}\" \"$r\"");

        Assert.Equal("<" + expected + ">/<a[-1]>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(false, "=")]
    [InlineData(false, "+=")]
    [InlineData(true, "=")]
    [InlineData(true, "+=")]
    public async Task NamerefElementRemovalDistinguishesExistingAndNewReadonlyAttributes(bool alreadyReadonly, string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); i=0; declare -n r='a[i++]'; " + (alreadyReadonly ? "readonly a; declare +n" : "declare -r +n")
            + " r" + assignment + "new later=kept; printf '%s/%s/<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[*]}\" \"$r\" \"$later\"");

        Assert.Equal((alreadyReadonly ? "1/0" : "0/1") + "/<old tail>/<a[i++]>/<kept>", result.StdOut);
        Assert.Equal("line 1: " + (alreadyReadonly ? "declare: " : "") + "a: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamerefElementRemovalProtectsReadonlyAssociativeValues(bool alreadyReadonly)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -A a=([key]=old); declare -n r='a[key]'; "
            + (alreadyReadonly ? "readonly a; declare +n" : "declare -r +n")
            + " r=new; printf '%s/<%s>/<%s>' \"$?\" \"${a[key]}\" \"$r\"");

        Assert.Equal((alreadyReadonly ? "1" : "0") + "/<old>/<a[key]>", result.StdOut);
        Assert.Equal("line 1: " + (alreadyReadonly ? "declare: " : "") + "a: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NamerefElementRemovalDoesNotPromoteTemporaryReadonlyAttributes()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old); declare -n r='a[0]'; a=temporary declare -r +n r=new; "
            + "printf '<%s>/' \"${a[0]}\"; a[0]=after; printf '<%s>/<%s>' \"${a[0]}\" \"$r\"");

        Assert.Equal("<new>/<after>/<a[0]>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NamerefRemovalWritesThroughAChainToAnAssociativeElement()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -A a=([key]=old); declare -n r=s s='a[key]'; declare +n r=new; "
            + "printf '<%s>/<%s>/<%s>' \"${a[key]}\" \"$r\" \"$s\"");

        Assert.Equal("<new>/<a[key]>/<a[key]>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NamerefRemovalAppliesReadonlyToTheArrayWithoutEvaluatingItsSelector()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "i=0; a=(old); declare -n r='a[i++]'; declare -r +n r; "
            + "printf 'i=%s;' \"$i\"; r=detached; a[0]=fail; printf should-not-run");

        Assert.Equal("i=0;", result.StdOut);
        Assert.Equal("line 1: a: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    // Bash 5.2: scalar -an converts the reference itself; compound preparation happens first.
    [Theory]
    [InlineData("declare -n r=s s; declare -an r", "0:<s>/<>", "")]
    [InlineData("declare -nr r=s; declare -n s; declare -an r=value", "1:<>/<>", "line 1: declare: r: readonly variable\n")]
    [InlineData("declare -nr r=s; declare -n s; declare -an r", "1:<>/<>", "line 1: declare: r: readonly variable\n")]
    [InlineData("declare -n r=s s; declare -an r=(one two)", "1:<one two>/<>", "line 1: declare: r: reference variable cannot be an array\n")]
    [InlineData("declare -nr r=s; declare -n s; declare -an r=(one two)", "1:<one two>/<>", "line 1: declare: r: reference variable cannot be an array\n")]
    [InlineData("declare -n r=s s; declare -a +n r=value", "0:<value>/<>", "")]
    public async Task ArrayAndNamerefFlagsRetainTheirDeclarationOrder(string declaration, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync(declaration + "; printf '%s:<%s>/<%s>' \"$?\" \"${r[*]}\" \"${s[*]}\"");

        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -n r=s s=t; declare +n r", "0:<t>/<t>/<old>", "")]
    [InlineData("declare -n r=s s=t; declare +n r=value", "0:<t>/<t>/<value>", "")]
    [InlineData("declare -n r=s s=t; declare +n r+=value", "0:<t>/<t>/<oldvalue>", "")]
    [InlineData("declare -nr r=s; declare -n s=t; declare +n r=value", "0:<t>/<t>/<value>", "")]
    [InlineData("declare -n r=s; declare -nr s=t; declare +n r=value", "1:<old>/<old>/<old>", "line 1: declare: r: readonly variable\n")]
    [InlineData("declare -n r=s s=t t=u; declare +n r", "0:<u>/<u>/<u>", "")]
    [InlineData("declare -n r=s s; declare +n r; s=value; value=final", "0:<final>/<final>/<old>", "")]
    public async Task NonlocalNamerefRemovalSelectsTheLastReference(string declaration, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync("t=old; " + declaration + "; printf '%s:<%s>/<%s>/<%s>' \"$?\" \"$r\" \"$s\" \"$t\"");

        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("-r +n", "")]
    [InlineData("-r +n", "=value")]
    [InlineData("-ar +n", "=value")]
    [InlineData("-r +n", "=(one two)")]
    public async Task NamerefRemovalAppliesReadonlyToTheFormerReferent(string flags, string assignment)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "t=old; declare -n r=s s=t; declare " + flags + " r" + assignment
            + "; s=detached; printf '<%s>/' \"$r\"; t=fail; printf should-not-run");

        Assert.Equal("<detached>/", result.StdOut);
        Assert.Equal("line 1: t: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("+n")]
    [InlineData("-rx +n")]
    [InlineData("-grx +n")]
    public async Task NamerefRemovalLeavesAnIndirectUnsetReferenceIntact(string flags)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -n r=s s; declare " + flags
            + " r; s=t; t=value; printf '<%s>/<%s>' \"$r\" \"$s\"");

        Assert.Equal("<value>/<value>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Explicit kinds replace the original nameref; an implicit compound declaration follows the chain.
    [Theory]
    [InlineData("declare -n r=s s; declare -a r=(one two)", "<one two>/<>")]
    [InlineData("declare -n r=s s; declare -A r=([key]=value)", "<value>/<>")]
    [InlineData("declare -n r=s s; declare -a r; r=changed", "<changed>/<>")]
    [InlineData("declare -n r=s s; declare -A r=value", "<value>/<>")]
    [InlineData("declare -n r=s s=t t; declare -a r+=(one two)", "<one two>/<>")]
    [InlineData("declare -nr s; declare -n r=s; declare -a r=(one two)", "<one two>/<>")]
    [InlineData("declare -nr r=s; declare -n s; declare -a r=value", "<value>/<>")]
    [InlineData("declare -n r=s s; export -a r=(one two)", "<one two>/<>")]
    [InlineData("declare -n r=s s; readonly -A r=([key]=value)", "<value>/<>")]
    [InlineData("declare -n r=s s; declare -a r=(\"${s:=t}\" tail)", "<t tail>/<t tail>")]
    public async Task ExplicitArrayDeclarationsReplaceChainsEndingInUnsetNamerefs(string declaration, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(declaration + "; printf '<%s>/<%s>' \"${r[*]}\" \"${s[*]}\"");

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare", "n", "(one two)", "one two")]
    [InlineData("typeset", "an", "(one two)", "one two")]
    [InlineData("declare", "An", "([key]=one)", "one")]
    [InlineData("local", "n", "(one two)", "one two")]
    public async Task CompoundNamerefRejectionRetainsThePreparedArray(
        string command, string options, string operand, string expected)
    {
        var script = command + " -" + options + " r=" + operand + "; printf '%s:<%s>' \"$?\" \"${r[*]}\"";
        if (command == "local")
            script = "f(){ " + script + "; }; f";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1:<" + expected + ">", result.StdOut);
        Assert.Equal("line 1: " + command + ": r: reference variable cannot be an array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -n r=a; declare -n r=(one two); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<one two>:<one two>", "", 0)]
    [InlineData("declare -n r=a; declare -an r=(one two); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<one two>:<a>", "", 0)]
    [InlineData("declare -n r=a; declare -An r=([key]=one); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<one>:<a>", "", 0)]
    [InlineData("declare -n r=a; declare -an r=value; printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<>:<value>", "", 0)]
    [InlineData("f(){ local -n r=a; local -an r=(one two); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"; }; f", "1:<one two>:<>", "line 1: local: r: reference variable cannot be an array\n", 0)]
    [InlineData("f(){ local -n r=a; local -an r=value; printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"; }; f", "1:<>:<>", "line 1: local: r: reference variable cannot be an array\n", 0)]
    [InlineData("f(){ local -an r; printf '%s:<%s>' \"$?\" \"${r[*]}\"; }; f", "1:<>", "line 1: local: r: reference variable cannot be an array\n", 0)]
    [InlineData("declare -n r; declare -a r=(one two); printf '%s:<%s>' \"${#r[@]}\" \"${r[*]}\"", "2:<one two>", "", 0)]
    [InlineData("declare -n r; declare -A r=([key]=value); printf '%s:<%s>' \"${#r[@]}\" \"${r[key]}\"", "1:<value>", "", 0)]
    [InlineData("declare -n r; declare -a r+=(one two); printf '%s:<%s>' \"${#r[@]}\" \"${r[*]}\"", "2:<one two>", "", 0)]
    [InlineData("declare -n r; declare -A r+=([key]=value); printf '%s:<%s>' \"${#r[@]}\" \"${r[key]}\"", "1:<value>", "", 0)]
    [InlineData("declare -n r; declare r=(one two); printf '%s:<%s>' \"${#r[@]}\" \"${r[*]}\"", "2:<one two>", "warning: r: removing nameref attribute\n", 0)]
    [InlineData("export x=old; export +n x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<1>:<old>\n", "line 1: export: `+n': not a valid identifier\n", 0)]
    [InlineData("export x=old; export +p x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<1>:<old>\n", "line 1: export: `+p': not a valid identifier\n", 0)]
    [InlineData("readonly x=old; declare +r x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<1>:<old>\n", "line 1: declare: x: readonly variable\n", 0)]
    [InlineData("declare +r x=old; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<old>\n", "", 0)]
    [InlineData("declare -r +r x=old; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<old>\n", "", 0)]
    [InlineData("x=(old); declare +a x; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]}\"", "<1>:<old>\n", "line 1: declare: x: cannot destroy array variables in this way\n", 0)]
    [InlineData("declare +a x=old; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<old>\n", "", 0)]
    [InlineData("target=value; ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("target=value; declare -n ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<0>:<>\n", "", 0)]
    [InlineData("target=value; declare -n ref; ref=target; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("ref=''; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<1>:<set>\n", "line 1: declare: `': invalid variable name for name reference\n", 0)]
    [InlineData("declare -n ref=''; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<1>:<>\n", "line 1: declare: `': not a valid identifier\n", 0)]
    [InlineData("ref='bad name'; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<1>:<bad name>\n", "line 1: declare: `bad name': invalid variable name for name reference\n", 0)]
    [InlineData("declare -n ref='bad name'; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<1>:<>\n", "line 1: declare: `bad name': invalid variable name for name reference\n", 0)]
    [InlineData("ref=(target); declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref[0]}\"", "<1>:<target>\n", "line 1: declare: ref: reference variable cannot be an array\n", 0)]
    [InlineData("readonly target=value; ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("readonly target=value; declare -n ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("readonly first=one; second=two; declare -n ref=first; declare -n ref=second; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<two>\n", "", 0)]
    [InlineData("target=value; declare -n ref=target; declare +n ref; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"$ref\" \"$target\"", "<0>:<target>:<value>\n", "", 0)]
    [InlineData("declare -n ref; declare +n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<0>:<>\n", "", 0)]
    [InlineData("target=value; declare -n ref=target; declare +n ref=new; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"$ref\" \"$target\"", "<0>:<target>:<new>\n", "", 0)]
    [InlineData("first=one; firsttail=two; declare -n ref=first; declare -n ref+=tail; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<two>\n", "", 0)]
    [InlineData("target=value; ref=target; f() { local -n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"; }; f; printf '<%s>\\n' \"$ref\"", "<0>:<>\n<target>\n", "", 0)]
    [InlineData("target=value; f() { local ref=target; local -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"; }; f", "<0>:<value>\n", "", 0)]
    [InlineData("x=old; declare -A x; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${x[0]}\" \"${#x[@]}\"", "<0>:<old>:<1>\n", "", 0)]
    [InlineData("x=old; declare -A x+=new; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${x[0]}\" \"${#x[@]}\"", "<0>:<oldnew>:<1>\n", "", 0)]
    [InlineData("x=old; declare -A x[key]=new; printf '<%s>:<%s>:<%s>:<%s>\\n' \"$?\" \"${x[0]}\" \"${x[key]}\" \"${#x[@]}\"", "<0>:<old>:<new>:<2>\n", "", 0)]
    [InlineData("x=old; declare -A x+=([key]=new); printf '<%s>:<%s>:<%s>:<%s>\\n' \"$?\" \"${x[0]-unset}\" \"${x[key]}\" \"${#x[@]}\"", "<0>:<old>:<new>:<2>\n", "", 0)]
    [InlineData("x=old; declare -A x=([key]=new); printf '<%s>:<%s>:<%s>:<%s>\\n' \"$?\" \"${x[0]-unset}\" \"${x[key]}\" \"${#x[@]}\"", "<0>:<unset>:<new>:<1>\n", "", 0)]
    [InlineData("readonly x=old; declare -A x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<old>\n", "", 0)]
    [InlineData("declare +Z x=value; printf '<%s>:<%s>\\n' \"$?\" \"${x+set}\"", "<2>:<>\n", "line 1: declare: +Z: invalid option\n" + EvaluationTestHelper.DeclareUsage, 0)]
    [InlineData("export x=outer; x=temp export -n x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<outer>\n", "", 0)]
    [InlineData("export x=outer; x=temp declare +x x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<outer>\n", "", 0)]
    [InlineData("export x=outer; f() { export -n x; }; x=temp f; printf '<%s>\\n' \"$x\"", "<outer>\n", "", 0)]
    [InlineData("ref=outer; f() { local -n ref='bad name'; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"; }; f", "<1>:<set>\n", "line 1: local: `bad name': invalid variable name for name reference\n", 0)]
    [InlineData("declare -n ref; declare -a ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<0>:<>\n", "", 0)]
    [InlineData("target=value; declare -n ref=target; declare -a ref; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"$ref\" \"$target\"", "<0>:<value>:<value>\n", "", 0)]
    [InlineData("target=value; declare -an ref=target; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<0>:<set>\n", "", 0)]
    [InlineData("target=value; ref=target; declare +n -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<target>\n", "", 0)]
    [InlineData("declare -A x=([key]=old); declare +a x; printf '<%s>:<%s>\\n' \"$?\" \"${x[key]}\"", "<0>:<old>\n", "", 0)]
    [InlineData("x=global; f() { local x=old; declare -A x; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]}\"; }; f; printf '<%s>\\n' \"$x\"", "<0>:<>\n<global>\n", "", 0)]
    [InlineData("x=outer; f() { local -r x=inner; declare -A x; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]}\"; }; f; printf '<%s>\\n' \"$x\"", "<0>:<>\n<outer>\n", "", 0)]
    [InlineData("f() { local x=old; declare -A x+=([key]=new); printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${x[0]-unset}\" \"${x[key]}\"; }; f", "<0>:<unset>:<new>\n", "", 0)]
    [InlineData("f() { local x=old; declare -A x+=new; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]-unset}\"; }; f", "<0>:<new>\n", "", 0)]
    [InlineData("target=value; declare -nr ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<1>:<value>\n", "line 1: declare: ref: readonly variable\n", 0)]
    [InlineData("declare -r value=changed; status=$?; printf '<%s>:<%s>\\n' \"$status\" \"${value-unset}\"", "<0>:<changed>\n", "", 0)]
    public async Task Evaluation_DeclarationAttributes_PreserveValuesAndScope(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("export x=old; export -n x", false, "old", false)]
    [InlineData("export x=old; declare +x x", false, "old", false)]
    [InlineData("export x; export -n x", false, null, false)]
    [InlineData("export -n x", false, null, false)]
    [InlineData("declare +x x", false, null, false)]
    [InlineData("export x=old; export -n x=new", false, "new", false)]
    [InlineData("declare -x +x x=one", false, "one", false)]
    [InlineData("declare +x -x x=two", false, "two", false)]
    [InlineData("export x=old; readonly x; export -n x", false, "old", true)]
    [InlineData("declare -r +r x=old", false, "old", false)]
    [InlineData("export x=outer; x=temp export -n x", true, "outer", false)]
    [InlineData("export x=outer; x=temp declare +x x", true, "outer", false)]
    [InlineData("export x=outer; f() { export -n x; }; f", false, "outer", false)]
    [InlineData("export x=outer; f() { local +x x=inner; }; f", true, "outer", false)]
    [InlineData("export x=outer; f() { export -n x; }; x=temp f", true, "outer", false)]
    [InlineData("export x=value; declare -n ref=x; export -n ref", false, "value", false)]
    [InlineData("export x=value; declare -n ref=x; declare +x ref", false, "value", false)]
    public async Task Evaluation_ExportAttributeChanges_RetainValuesAndReadonlyState(
        string script, bool expectedExport, string? expectedValue, bool expectedReadonly)
    {
        var evaluation = await RunForStateAsync(script);

        Assert.Equal(expectedValue != null, evaluation.Variables.TryGetValue("x", out var value));
        Assert.Equal(expectedValue == null ? ShellValue.Empty : ShellValue.FromText(expectedValue), value);
        Assert.Equal(expectedExport, evaluation.ExportedVariables.Contains("x"));
        Assert.Equal(expectedReadonly, evaluation.ReadonlyVariables.Contains("x"));
    }

    [Fact]
    public async Task Evaluation_NamerefAttributes_BelongToTheReferenceBinding()
    {
        var evaluation = await RunForStateAsync("target=value; ref=target; declare -nrx ref");

        Assert.Equal(ShellValue.FromText("target"), evaluation.Namerefs["ref"]);
        Assert.False(evaluation.Variables.ContainsKey("ref"));
        Assert.Equal(ShellValue.FromText("value"), evaluation.Variables["target"]);
        Assert.Contains("ref", evaluation.ReadonlyVariables);
        Assert.Contains("ref", evaluation.ExportedVariables);
        Assert.DoesNotContain("target", evaluation.ReadonlyVariables);
        Assert.DoesNotContain("target", evaluation.ExportedVariables);
    }

    private static async Task<ShellEvaluation> RunForStateAsync(string script)
    {
        var evaluation = new ShellEvaluation(Parser.Parse(script));
        var host = EndToEndHarness.WithMemoryBudget(EvaluationTestHelper.BuildDefaultTree(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
        return evaluation;
    }
}

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
    [InlineData("r", "declare -r", "")]
    [InlineData("r=s s", "declare -r", "")]
    [InlineData("r=s s", "declare -gr", "")]
    [InlineData("r", "declare -rx", "")]
    [InlineData("r", "readonly", "readonly: `': not a valid identifier\n")]
    [InlineData("r=s s", "readonly", "readonly: `': not a valid identifier\n")]
    [InlineData("r", "export", "export: `': not a valid identifier\n")]
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
        Assert.Equal("r: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExistingReadonlyLocalElementDiagnosticsNameTheReferent()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(old tail); readonly a; f(){ local -n r=a; local 'r[1]=new'; "
            + "printf '%s/<%s>' \"$?\" \"${a[*]}\"; }; f");

        Assert.Equal("1/<old tail>", result.StdOut);
        Assert.Equal("local: a: readonly variable\n", result.StdErr);
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
        Assert.Equal("a: readonly variable\n", result.StdErr);
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
        Assert.Equal("a: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RejectedReadonlyElementInitializerStillCreatesTheIndexedArray()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -r 'a[1]=new'; declare -A a; printf '%s/<%s>' \"$?\" \"${a[*]}\"");

        Assert.Equal("1/<>", result.StdOut);
        Assert.Equal("a: readonly variable\ndeclare: a: cannot convert indexed to associative array\n", result.StdErr);
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
        Assert.Equal("r: readonly variable\n", result.StdErr);
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
        Assert.Equal((alreadyReadonly ? "declare: " : "") + "a: readonly variable\n", result.StdErr);
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
        Assert.Equal((alreadyReadonly ? "declare: " : "") + "a: readonly variable\n", result.StdErr);
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
        Assert.Equal("a: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    // Bash 5.2: scalar -an converts the reference itself; compound preparation happens first.
    [Theory]
    [InlineData("declare -n r=s s; declare -an r", "0:<s>/<>", "")]
    [InlineData("declare -nr r=s; declare -n s; declare -an r=value", "1:<>/<>", "declare: r: readonly variable\n")]
    [InlineData("declare -nr r=s; declare -n s; declare -an r", "1:<>/<>", "declare: r: readonly variable\n")]
    [InlineData("declare -n r=s s; declare -an r=(one two)", "1:<one two>/<>", "declare: r: reference variable cannot be an array\n")]
    [InlineData("declare -nr r=s; declare -n s; declare -an r=(one two)", "1:<one two>/<>", "declare: r: reference variable cannot be an array\n")]
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
    [InlineData("declare -n r=s; declare -nr s=t; declare +n r=value", "1:<old>/<old>/<old>", "declare: r: readonly variable\n")]
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
        Assert.Equal("t: readonly variable\n", result.StdErr);
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
        Assert.Equal(command + ": r: reference variable cannot be an array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -n r=a; declare -n r=(one two); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<one two>:<one two>", "", 0)]
    [InlineData("declare -n r=a; declare -an r=(one two); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<one two>:<a>", "", 0)]
    [InlineData("declare -n r=a; declare -An r=([key]=one); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<one>:<a>", "", 0)]
    [InlineData("declare -n r=a; declare -an r=value; printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"", "0:<>:<value>", "", 0)]
    [InlineData("f(){ local -n r=a; local -an r=(one two); printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"; }; f", "1:<one two>:<>", "local: r: reference variable cannot be an array\n", 0)]
    [InlineData("f(){ local -n r=a; local -an r=value; printf '%s:<%s>:<%s>' \"$?\" \"${a[*]}\" \"${r[*]}\"; }; f", "1:<>:<>", "local: r: reference variable cannot be an array\n", 0)]
    [InlineData("f(){ local -an r; printf '%s:<%s>' \"$?\" \"${r[*]}\"; }; f", "1:<>", "local: r: reference variable cannot be an array\n", 0)]
    [InlineData("declare -n r; declare -a r=(one two); printf '%s:<%s>' \"${#r[@]}\" \"${r[*]}\"", "2:<one two>", "", 0)]
    [InlineData("declare -n r; declare -A r=([key]=value); printf '%s:<%s>' \"${#r[@]}\" \"${r[key]}\"", "1:<value>", "", 0)]
    [InlineData("declare -n r; declare -a r+=(one two); printf '%s:<%s>' \"${#r[@]}\" \"${r[*]}\"", "2:<one two>", "", 0)]
    [InlineData("declare -n r; declare -A r+=([key]=value); printf '%s:<%s>' \"${#r[@]}\" \"${r[key]}\"", "1:<value>", "", 0)]
    [InlineData("declare -n r; declare r=(one two); printf '%s:<%s>' \"${#r[@]}\" \"${r[*]}\"", "2:<one two>", "warning: r: removing nameref attribute\n", 0)]
    [InlineData("export x=old; export +n x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<1>:<old>\n", "export: `+n': not a valid identifier\n", 0)]
    [InlineData("export x=old; export +p x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<1>:<old>\n", "export: `+p': not a valid identifier\n", 0)]
    [InlineData("readonly x=old; declare +r x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<1>:<old>\n", "declare: x: readonly variable\n", 0)]
    [InlineData("declare +r x=old; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<old>\n", "", 0)]
    [InlineData("declare -r +r x=old; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<old>\n", "", 0)]
    [InlineData("x=(old); declare +a x; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]}\"", "<1>:<old>\n", "declare: x: cannot destroy array variables in this way\n", 0)]
    [InlineData("declare +a x=old; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<old>\n", "", 0)]
    [InlineData("target=value; ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("target=value; declare -n ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<0>:<>\n", "", 0)]
    [InlineData("target=value; declare -n ref; ref=target; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<value>\n", "", 0)]
    [InlineData("ref=''; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<1>:<set>\n", "declare: `': invalid variable name for name reference\n", 0)]
    [InlineData("declare -n ref=''; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<1>:<>\n", "declare: `': not a valid identifier\n", 0)]
    [InlineData("ref='bad name'; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<1>:<bad name>\n", "declare: `bad name': invalid variable name for name reference\n", 0)]
    [InlineData("declare -n ref='bad name'; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<1>:<>\n", "declare: `bad name': invalid variable name for name reference\n", 0)]
    [InlineData("ref=(target); declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref[0]}\"", "<1>:<target>\n", "declare: ref: reference variable cannot be an array\n", 0)]
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
    [InlineData("declare +Z x=value; printf '<%s>:<%s>\\n' \"$?\" \"${x+set}\"", "<2>:<>\n", "declare: +Z: invalid option\n", 0)]
    [InlineData("export x=outer; x=temp export -n x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<outer>\n", "", 0)]
    [InlineData("export x=outer; x=temp declare +x x; printf '<%s>:<%s>\\n' \"$?\" \"$x\"", "<0>:<outer>\n", "", 0)]
    [InlineData("export x=outer; f() { export -n x; }; x=temp f; printf '<%s>\\n' \"$x\"", "<outer>\n", "", 0)]
    [InlineData("ref=outer; f() { local -n ref='bad name'; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"; }; f", "<1>:<set>\n", "local: `bad name': invalid variable name for name reference\n", 0)]
    [InlineData("declare -n ref; declare -a ref; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<0>:<>\n", "", 0)]
    [InlineData("target=value; declare -n ref=target; declare -a ref; printf '<%s>:<%s>:<%s>\\n' \"$?\" \"$ref\" \"$target\"", "<0>:<value>:<value>\n", "", 0)]
    [InlineData("target=value; declare -an ref=target; printf '<%s>:<%s>\\n' \"$?\" \"${ref+set}\"", "<0>:<set>\n", "", 0)]
    [InlineData("target=value; ref=target; declare +n -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<0>:<target>\n", "", 0)]
    [InlineData("declare -A x=([key]=old); declare +a x; printf '<%s>:<%s>\\n' \"$?\" \"${x[key]}\"", "<0>:<old>\n", "", 0)]
    [InlineData("x=global; f() { local x=old; declare -A x; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]}\"; }; f; printf '<%s>\\n' \"$x\"", "<0>:<>\n<global>\n", "", 0)]
    [InlineData("x=outer; f() { local -r x=inner; declare -A x; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]}\"; }; f; printf '<%s>\\n' \"$x\"", "<0>:<>\n<outer>\n", "", 0)]
    [InlineData("f() { local x=old; declare -A x+=([key]=new); printf '<%s>:<%s>:<%s>\\n' \"$?\" \"${x[0]-unset}\" \"${x[key]}\"; }; f", "<0>:<unset>:<new>\n", "", 0)]
    [InlineData("f() { local x=old; declare -A x+=new; printf '<%s>:<%s>\\n' \"$?\" \"${x[0]-unset}\"; }; f", "<0>:<new>\n", "", 0)]
    [InlineData("target=value; declare -nr ref=target; declare -n ref; printf '<%s>:<%s>\\n' \"$?\" \"$ref\"", "<1>:<value>\n", "declare: ref: readonly variable\n", 0)]
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

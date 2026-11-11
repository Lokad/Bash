using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ElementNamerefTestsEndToEnd
{
    [Theory]
    [InlineData("")]
    [InlineData("tmp=x ")]
    public async Task BuiltinElementOutputsRejectUnsetLocalNamerefs(string prefix)
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(
            "declare -n r; f(){ local -n s=r; " + prefix + "printf -v 's[1]' hello; }; f"));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(1, evaluation.LastExitCode);
        Assert.Empty(evaluation.IndexedArrays);
        Assert.Empty(evaluation.AssociativeArrays);
        Assert.False(evaluation.Variables.ContainsKey(BindingName.Empty));
        Assert.Equal(ShellValue.Empty, Assert.Single(evaluation.Namerefs).Value);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.NotEmpty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("declare -n r; f(){ local -n s=r; tmp=x printf -v s hello; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${r-unset}\"", "<0>/<hello><unset>", 0, "")]
    [InlineData("declare -n r; f(){ local -n s=r; printf -v s hello; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${r-unset}\"", "<0>/<hello><unset>", 0, "")]
    [InlineData("declare -n r; f(){ local -n s=r; tmp=x let 's=7'; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${r-unset}\"", "<0>/<7><unset>", 0, "")]
    [InlineData("declare -n r; f(){ local -n s=r; tmp=x getopts a s -a; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${r-unset}\"", "<0>/<a><unset>", 0, "")]
    [InlineData("a=(); declare -n b=a r='b[0]'; T=${r:=new} true; printf '<%s>/<%s>' \"${a[0]}\" \"${b[0]}\"", "<>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(); readonly a; declare -n b=a r='b[0]'; T=${r:=new} true; printf '<%s>/<%s>' \"${a[0]}\" \"${b[0]}\"", "<>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(); i=0; declare -n b=a r='b[i++]'; T=${r:=new} true; printf '<%s>/<%s>/<%s>/<%s>' \"${a[0]}\" \"${b[0]}\" \"${b[1]}\" \"$i\"", "<>/<>/<new>/<2>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(zero one); declare -n b=a r='b[-1]'; r=new; printf '<%s>/<%s>' \"${a[*]}\" \"${b[*]}\"", "<zero one>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(); declare -n b=a r='b[-1]'; r+=new; printf '<%s>/<%s>' \"${a[*]}\" \"${b[*]}\"", "<>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(zero one); declare -n b=a r='b[-2]'; r=new; printf unreachable", "", 1, "b[-2]: bad array subscript")]
    [InlineData("a=(); a[-1]=new; printf unreachable", "", 1, "a[-1]: bad array subscript")]
    [InlineData("f(){ local -n r s; tmp=x printf -v r hello; printf '<%s>/<%s>/<%s>/<%s>' \"$?\" \"${r-unset}\" \"${s-unset}\" \"${!r}\"; }; f", "<0>/<unset>/<unset>/<hello>", 0, "")]
    [InlineData("f(){ local -n r s=r; tmp=x printf -v s hello; printf '<%s>/<%s>/<%s>' \"$?\" \"${r-unset}\" \"${s-unset}\"; printf '%s' \"${!r}\"; }; f", "<0>/<unset>/<unset>", 1, "r: invalid indirect expansion")]
    [InlineData("f(){ local -n r s=r; printf -v s hello; printf '<%s>' \"$?\"; printf '%s' \"${!r}\"; }; f", "<0>", 1, "r: invalid indirect expansion")]
    [InlineData("f(){ local -n r s=r; tmp=x let 's=7'; printf '<%s>' \"$?\"; printf '%s' \"${!r}\"; }; f", "<0>", 1, "r: invalid indirect expansion")]
    [InlineData("a=(outer); f(){ local -n r='a[0]'; local r='two words'; tmp=x printf -v r new; printf '<%s>/<%s>/<%s>' \"$?\" \"$r\" \"${a[0]}\"; }; f", "<0>/<new>/<outer>", 0, "")]
    [InlineData("a=(outer); f(){ local -n r='a[0]'; local r=2; tmp=x let 'r+=3'; printf '<%s>/<%s>/<%s>' \"$?\" \"$r\" \"${a[0]}\"; }; f", "<0>/<5>/<outer>", 0, "")]
    [InlineData("a=(old); declare -n b=a r='b[0]'; r=new; printf '<%s>/<%s>' \"${a[0]}\" \"${b[0]}\"", "<old>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(old); declare -n b=a r='b[0]'; r+=new; printf '<%s>/<%s>' \"${a[0]}\" \"${b[0]}\"", "<old>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(); declare -n b=a r='b[0]'; : ${r:=new}; printf '<%s>/<%s>' \"${a[0]}\" \"${b[0]}\"", "<>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(old); declare -n b=a r='b[0]'; declare r=new; printf '<%s>/<%s>' \"${a[0]}\" \"${b[0]}\"", "<old>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(old); readonly a; declare -n b=a r='b[0]'; r=new; printf '<%s>/<%s>' \"${a[0]}\" \"${b[0]}\"", "<old>/<new>", 0, "warning: b: removing nameref attribute")]
    [InlineData("a=(old); declare -nr b=a; declare -n r='b[0]'; r=new; printf fail", "", 1, "b: readonly variable")]
    [InlineData("a=(old); declare -n b=a r='b[0]'; printf '<%s>' \"$r\"; b[0]=new; printf '<%s>/<%s>' \"${a[0]}\" \"$r\"", "<old><new>/<new>", 0, "")]
    [InlineData("a=(old); declare -n r='a[0]'; export r", "", 0, "export: `a[0]': not a valid identifier")]
    [InlineData("a=(old); declare -n r='a[0]'; readonly r", "", 0, "readonly: `a[0]': not a valid identifier")]
    [InlineData("a=(old); declare -n r='a[0]'; unset -n r; printf '<%s>/<%s>' \"${r-unset}\" \"${a[0]-unset}\"", "<unset>/<old>", 0, "")]
    [InlineData("a=(old); declare -n r='a[0]'; unset -v r; printf '<%s>/<%s>' \"${!r}\" \"${a[0]-unset}\"", "<a[0]>/<unset>", 0, "")]
    [InlineData("a=(old); declare -n r='a[0]'; unset -f r; printf '<%s>/<%s>' \"$r\" \"${a[0]-unset}\"", "<old>/<old>", 0, "")]
    [InlineData("a=(old); declare -n r='a[0]'; r(){ :; }; unset -nf r; command -v r; printf '<%s>/<%s>/<%s>' \"$?\" \"$r\" \"${a[0]}\"", "<1>/<old>/<old>", 0, "")]
    [InlineData("a=(old); declare -n r='a[0]'; unset -nv r; printf '<%s>/<%s>' \"${r-unset}\" \"${a[0]-unset}\"", "<unset>/<old>", 0, "")]
    [InlineData("a=(old); declare -n r='a[0]'; unset -fv r; printf '<%s>/<%s>' \"$?\" \"${a[0]}\"", "<1>/<old>", 0, "cannot simultaneously unset")]
    [InlineData("a=(old); declare -n r='a[0]'; unset -z r; printf '<%s>/<%s>' \"$?\" \"${a[0]}\"", "<2>/<old>", 0, "invalid option")]
    [InlineData("a=(old); declare -n r='a[0]'; unset -- r; printf '<%s>/<%s>' \"${!r}\" \"${a[0]-unset}\"", "<a[0]>/<unset>", 0, "")]
    [InlineData("a=(old); f(){ local -n r='a[0]'; local r='two words'; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${a[0]}\"", "<0>/<two words><old>", 0, "")]
    [InlineData("a=(old); f(){ local -n r='a[0]'; local r=42; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${a[0]}\"", "<0>/<42><old>", 0, "")]
    [InlineData("a=(old); f(){ local -n r='a[0]'; local r=''; printf '<%s>/<%s>' \"$?\" \"$r\"; }; f; printf '<%s>' \"${a[0]}\"", "<0>/<><old>", 0, "")]
    [InlineData("a=(old); f(){ local -n r='a[0]'; tmp=x printf -v r new; printf '<%s>/<%s>' \"$?\" \"${a[0]}\"; }; f", "<0>/<new>", 0, "")]
    [InlineData("a=(old); f(){ local -n r='a[0]'; tmp=x let 'r=1'; printf '<%s>/<%s>' \"$?\" \"${a[0]}\"; }; f", "<0>/<1>", 0, "")]
    [InlineData("a=('' one two); i=0; declare -n r='a[i++]'; : ${r:=new}; printf '<%s>/<%s>/<%s>/<%s>' \"${a[0]}\" \"${a[1]}\" \"${a[2]}\" \"$i\"", "<>/<new>/<two>/<2>", 0, "")]
    [InlineData("a=(); declare -n r='a[0]'; : ${r:=new}; printf '<%s>/<%s>' \"$r\" \"${a[0]-unset}\"", "<new>/<new>", 0, "")]
    [InlineData("a=(); declare -n r='a[0]'; T=${r:=new} true; printf '<%s>' \"${a[0]-unset}\"", "<new>", 0, "")]
    [InlineData("a=(); declare -n r='a[0]'; T=temp eval ': ${r:=new}'; printf '<%s>' \"${a[0]-unset}\"", "<new>", 0, "")]
    [InlineData("a=(); declare -n r='a[0]'; a=temp T=${r:=new} true; printf '<%s>' \"${a[0]-unset}\"", "<unset>", 0, "")]
    [InlineData("a=(zero one two); i=1; declare -n r='a[i++]' s=r; unset s; printf '<%s>/<%s>/<%s>/<%s>' \"${!s}\" \"${a[1]-unset}\" \"${a[2]}\" \"$i\"", "<a[i++]>/<unset>/<two>/<2>", 0, "")]
    [InlineData("declare -n r='a[i++]'; i=0; unset r; printf '<%s>/<%s>' \"${!r}\" \"$i\"", "<a[i++]>/<0>", 0, "")]
    [InlineData("a=(zero one two); declare -n r='a[-1]'; unset r; printf '<%s>/<%s>/<%s>' \"${!r}\" \"${a[-1]}\" \"${#a[@]}\"", "<a[-1]>/<one>/<2>", 0, "")]
    [InlineData("x=old; declare -n r='x[0]'; unset r; printf '<%s>/<%s>' \"${!r}\" \"${x-unset}\"", "<x[0]>/<unset>", 0, "")]
    [InlineData("arr=(zero one); declare -n ref='arr[1]'; printf 'before=<%s>\\n' \"$ref\"; ref=temporary true; printf 'scoped=<%s>\\n' \"${arr[1]}\"; ref=changed; printf 'after=<%s>:<%s>\\n' \"$ref\" \"${arr[1]}\"", "before=<one>\nscoped=<one>\nafter=<changed>:<changed>\n", 0, "")]
    [InlineData("array=(a hello); declare -n element_ref='array[1]'; declare -n chained_ref=element_ref; scalar=hello; declare -n scalar_ref=scalar; printf '<%s>:<%s>:<%s>:<%s>\\n' \"${#element_ref}\" \"${#chained_ref}\" \"${#array[1]}\" \"${#scalar_ref}\"", "<0>:<0>:<5>:<5>\n", 0, "")]
    [InlineData("a=(zero one two); i=0; declare -n r='a[i++]' s=r; printf '<%s><%s><%s>' \"$s\" \"${s}\" \"$i\"", "<zero><one><2>", 0, "")]
    [InlineData("a=(zero one two); i=0; declare -n r='a[i++]'; r+=X; printf '<%s><%s><%s>' \"${a[0]}\" \"${a[1]}\" \"$i\"", "<zeroX><one><1>", 0, "")]
    [InlineData("a=(old other); declare -n r='a[$(printf 1)]'; printf '<%s>' \"$r\"", "<other>", 0, "")]
    [InlineData("a=(outer); declare -n r='a[0]'; f(){ local -a a=(inner); printf '<%s>' \"$r\"; r=changed; printf '<%s>' \"${a[0]}\"; }; f; printf '<%s>' \"$r\"", "<inner><changed><outer>", 0, "")]
    [InlineData("a=(outer); declare -n r='a[0]'; (r=inner; printf '<%s>' \"$r\"); printf '<%s>' \"$r\"", "<inner><outer>", 0, "")]
    [InlineData("i=0; declare -n r='a[i++]'; r=temp eval 'printf \"<%s>\" \"$r\"'; printf '<%s><%s>' \"${!r}\" \"$i\"", "<temp><a[i++]><0>", 0, "")]
    public async Task ElementNamerefs_PreserveReadsWritesAndLength(
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

    // Intentional Bash divergence: element aliases cannot bypass readonly protection.
    [Theory]
    [InlineData("a=(old); i=0; readonly a; declare -n r='a[i++]'; unset r; printf '<%s>/<%s>/<%s>' \"$?\" \"$i\" \"${a[0]}\"", "<1>/<0>/<old>")]
    [InlineData("declare -A a=([key]=old); readonly a; declare -n r='a[key]'; unset r; printf '<%s>/<%s>' \"$?\" \"${a[key]}\"", "<1>/<old>")]
    public async Task ReadonlyElementUnsetsRejectBeforeSelectorEffects(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal("unset: a: cannot unset: readonly variable\n", result.StdErr);
    }
}

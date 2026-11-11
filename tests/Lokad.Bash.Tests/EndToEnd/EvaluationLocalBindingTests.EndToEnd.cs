using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationLocalBindingTestsEndToEnd
{
    [Theory]
    [InlineData("f(){ declare -a a; a[0]=one; declare -a a[bad]=two; printf '%s/%s' \"$?\" \"${a[0]}\"; }; f", "0/two")]
    [InlineData("f(){ declare -n r=x; x=one; declare r=two; printf '%s/%s' \"$r\" \"$x\"; }; f", "two/two")]
    [InlineData("f(){ declare -n r=x; x=one; local r=two; printf '%s/%s' \"$r\" \"$x\"; }; f", "two/two")]
    [InlineData("f(){ declare -a a; a[0]=first; a[1]=second; declare -a a[0]=new; printf '<%s>' \"${a[@]}\"; }; f", "<new><second>")]
    [InlineData("f(){ declare -a a; a[0]=first; a[1]=second; local a=new; printf '<%s>' \"${a[@]}\"; }; f", "<new><second>")]
    [InlineData("f(){ declare -A a; a[key]=first; a[other]=second; declare -A a[key]=new; printf '%s/%s' \"${a[key]}\" \"${a[other]}\"; }; f", "new/second")]
    [InlineData("f(){ declare -A a; a[key]=first; declare a=new; printf '%s/%s' \"${a[0]}\" \"${a[key]}\"; }; f", "new/first")]
    [InlineData("f(){ local r=x; declare -n r; x=hello; printf '%s' \"$r\"; }; f", "hello")]
    [InlineData("f(){ local a=first; declare -a a[1]=second; printf '<%s>' \"${a[@]}\"; }; f", "<second>")]
    [InlineData("a=outer; f(){ local a=first; declare a[1]; printf '<%s>/' \"${a[@]}\"; }; f; printf '%s' \"$a\"", "<>/outer")]
    [InlineData("f(){ local a=first; T=tmp eval 'declare a[1]'; printf '<%s>' \"${a[@]}\"; }; f", "<>")]
    [InlineData("f(){ local a=first; declare a[bad]; printf '%s/<%s>' \"$?\" \"${a[@]}\"; }; f", "0/<>")]
    [InlineData("f(){ local a=first; declare -a a[bad]; printf '%s/<%s>' \"$?\" \"${a[@]}\"; }; f", "0/<>")]
    [InlineData("f(){ local a=first; declare -n r=a; declare r[1]; printf '<%s>' \"${a[@]}\"; }; f", "<>")]
    [InlineData("f(){ declare -a a; a[0]=first; a[1]=second; declare a[2]; printf '<%s>' \"${a[@]}\"; }; f", "<first><second>")]
    [InlineData("f(){ declare -A a; a[key]=value; declare a[unused]; printf '<%s>' \"${a[key]}\"; }; f", "<value>")]
    [InlineData("a=outer; f(){ local a=first; declare 'a[@]'; printf '%s/<%s>/' \"$?\" \"${a[@]}\"; }; f; printf '%s' \"$a\"", "0/<>/outer")]
    [InlineData("f(){ local a=first; declare 'a[*]'; printf '%s/<%s>' \"$?\" \"${a[@]}\"; }; f", "0/<>")]
    [InlineData("f(){ local a=first; declare -a 'a[@]'; printf '%s/<%s>' \"$?\" \"${a[@]}\"; }; f", "0/<>")]
    [InlineData("f(){ local a=first; T=tmp eval 'declare \"a[*]\"'; printf '%s/<%s>' \"$?\" \"${a[@]}\"; }; f", "0/<>")]
    [InlineData("f(){ declare -a a; a[0]=first; a[1]=second; declare 'a[@]'; printf '%s/' \"$?\"; printf '<%s>' \"${a[@]}\"; }; f", "0/<first><second>")]
    [InlineData("f(){ declare -A a; a[key]=value; declare 'a[*]'; printf '%s/<%s>' \"$?\" \"${a[key]}\"; }; f", "0/<value>")]
    [InlineData("f(){ local a=first; declare -n r=a; declare 'r[@]'; printf '%s/<%s>' \"$?\" \"${a[@]}\"; }; f", "0/<>")]
    [InlineData("x=outer; f(){ x=before; declare x=inner; }; f; printf '%s' \"$x\"", "before")]
    [InlineData("x=outer; f(){ x=before; local x=inner; local x=again; }; f; printf '%s' \"$x\"", "before")]
    [InlineData("x=outer; f(){ x=before; local x=inner; g; printf '%s' \"$x\"; }; g(){ x=changed; local x=deep; }; f; printf '/%s' \"$x\"", "changed/before")]
    [InlineData("x=outer; f(){ x=prefix local x=inner; printf '%s' \"$x\"; }; f; printf '/%s' \"$x\"", "inner/outer")]
    [InlineData("x=outer; f(){ export x=before; local x=inner; }; f; sh -c 'printf %s \"$x\"'", "before")]
    [InlineData("x=outer; declare -n r=x; f(){ local r=inner; printf '%s/%s' \"$r\" \"$x\"; }; f; printf '/%s/%s' \"$r\" \"$x\"", "inner/outer/outer/outer")]
    [InlineData("a=(outer); f(){ a[0]=before; declare -a a[0]=inner; }; f; printf '%s' \"${a[0]}\"", "before")]
    [InlineData("x=outer; f(){ x=temp eval 'local x=inner'; }; f; printf '%s' \"$x\"", "outer")]
    [InlineData("x=outer; f(){ x=temp eval 'declare x=inner'; }; f; printf '%s' \"$x\"", "outer")]
    [InlineData("x=outer; f(){ local x=local; x=temp eval 'local x=inner'; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "local/outer")]
    [InlineData("x=outer; f(){ x=temp eval \"eval 'local x=inner'\"; }; f; printf '%s' \"$x\"", "outer")]
    [InlineData("f(){ declare -a a; a[0]=one; a[1]=two; (declare -a a; printf '<%s>' \"${a[@]}\"); }; f", "<one><two>")]
    [InlineData("f(){ declare -a a; a[0]=one; a[1]=two; out=$(declare -a a; printf '<%s>' \"${a[@]}\"); printf '%s' \"$out\"; }; f", "<one><two>")]
    [InlineData("f(){ declare -a a; a[0]=one; a[1]=two; { declare -a a; printf '<%s>' \"${a[@]}\"; } | cat; }; f", "<one><two>")]
    [InlineData("x=outer; f(){ local x=one; (local x=two; printf '%s/' \"$x\"); printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "two/one/outer")]
    [InlineData("x=outer; f(){ local x=one; (local x; printf '%s/' \"$x\"); }; f; printf '%s' \"$x\"", "one/outer")]
    [InlineData("x=outer; f(){ declare -n r=x; declare r=inner; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "inner/outer")]
    [InlineData("x=outer; f(){ declare -n r=x; local r=inner; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "inner/outer")]
    [InlineData("x=outer; f(){ declare -n r=x; declare -n s=r; declare s=inner; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "inner/outer")]
    [InlineData("x=outer; f(){ declare -n r=x; declare r; printf '<%s>/' \"${x-unset}\"; }; f; printf '%s' \"$x\"", "<unset>/outer")]
    [InlineData("a=(one two); f(){ declare -n r=a; declare r=three; printf '<%s>' \"${a[@]}\"; }; f; printf '/<%s>' \"${a[@]}\"", "<three>/<one>/<two>")]
    [InlineData("f(){ declare -a a; a[0]=one; a[1]=two; declare -n r=a; declare r=three; printf '<%s>' \"${a[@]}\"; }; f", "<three><two>")]
    [InlineData("x=outer; f(){ declare -n r=x; (declare r=inner; printf '%s/' \"$x\"); printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "inner/outer/outer")]
    [InlineData("x=outer; f(){ declare -n r=x; T=1 declare r=inner; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "inner/outer")]
    [InlineData("f(){ local x; printf '<%s>' \"${x-unset}\"; }; f", "<unset>")]
    [InlineData("x=outer; f(){ x=temp eval 'local x=inner'; x=after; }; f; printf '%s' \"$x\"", "after")]
    [InlineData("x=outer; f(){ x=temp eval 'declare x=inner'; x=after; }; f; printf '%s' \"$x\"", "after")]
    [InlineData("x=outer; f(){ x=temp eval 'local x=inner'; unset x; }; f; printf '%s' \"${x-unset}\"", "unset")]
    [InlineData("x=outer; f(){ x=temp eval 'local x; printf \"<%s>/\" \"${x-unset}\"'; x=after; }; f; printf '%s' \"$x\"", "<temp>/after")]
    [InlineData("x=outer; f(){ T=temp eval 'local x=inner; x=next'; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "next/outer")]
    [InlineData("x=outer; f(){ x=temp eval 'local y=inner; local x=inner'; printf '%s/%s/' \"$x\" \"$y\"; x=after; }; f; printf '%s/%s' \"$x\" \"${y-unset}\"", "outer/inner/after/unset")]
    [InlineData("x=outer; f(){ x=temp eval 'T=other eval \"local x=inner\"'; x=after; }; f; printf '%s' \"$x\"", "after")]
    [InlineData("x=outer; f(){ T=temp eval \"Y=other eval 'local x=inner; x=next'\"; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "next/outer")]
    [InlineData("x=outer; f(){ local x=local; x=temp eval 'local x=inner'; x=after; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "after/outer")]
    [InlineData("x=outer; f(){ local x=local; T=temp eval 'x=next'; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "next/outer")]
    [InlineData("x=outer; f(){ T=temp eval 'x=before; local x=inner'; }; f; printf '%s' \"$x\"", "before")]
    [InlineData("x=outer; f(){ x=temp eval 'T=other eval \"local y=inner\"'; printf '%s/' \"$y\"; x=after; }; f; printf '%s/%s' \"$x\" \"${y-unset}\"", "inner/after/unset")]
    [InlineData("f(){ declare -a a; a[0]=one; a[1]=two; declare -n r=a; declare r[1]=new; printf '<%s>' \"${a[@]}\"; }; f", "<one><new>")]
    [InlineData("f(){ declare -A a; a[key]=one; a[other]=two; declare -n r=a; declare r[key]=new; printf '%s/%s/%s' \"${a[key]}\" \"${a[other]}\" \"${a[0]}\"; }; f", "new/two/")]
    [InlineData("a=(one two); f(){ declare -n r=a; declare r[1]=new; printf '<%s>' \"${a[@]}\"; }; f; printf '/<%s>' \"${a[@]}\"", "<new>/<one>/<two>")]
    [InlineData("f(){ declare -a a; a[0]=one; a[1]=two; declare -n r=a; declare -n s=r; declare s[1]=new; printf '<%s>' \"${a[@]}\"; }; f", "<one><new>")]
    [InlineData("x=outer; f(){ x=temp local x; printf '%s/' \"${x-unset}\"; }; f; printf '%s' \"$x\"", "temp/outer")]
    [InlineData("x=outer; f(){ x=temp declare x; printf '%s/' \"${x-unset}\"; }; f; printf '%s' \"$x\"", "temp/outer")]
    [InlineData("x=outer; f(){ x=temp local x=new; sh -c 'printf \"%s/\" \"${x-unset}\"'; }; f; printf '%s' \"$x\"", "new/outer")]
    [InlineData("x=outer; f(){ x=temp declare x=new; sh -c 'printf \"%s/\" \"${x-unset}\"'; }; f; printf '%s' \"$x\"", "new/outer")]
    [InlineData("x=outer; f(){ x=temp local x; sh -c 'printf \"%s/\" \"${x-unset}\"'; }; f; printf '%s' \"$x\"", "temp/outer")]
    [InlineData("x=outer; f(){ T=temp local x; printf '%s/' \"${x-unset}\"; sh -c 'printf \"%s/\" \"${x-unset}\"'; }; f; printf '%s' \"$x\"", "unset/unset/outer")]
    [InlineData("x=outer; f(){ T=temp eval 'x=prefix local x'; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "prefix/outer")]
    [InlineData("f(){ declare -n r='a[1]'; declare r=new; printf '%s/%s' \"$r\" \"${a[1]}\"; }; f", "new/")]
    [InlineData("f(){ declare -a a; a[0]=one; a[1]=two; declare -n r='a[1]'; local r=new; printf '%s/%s/%s' \"$r\" \"${a[0]}\" \"${a[1]}\"; }; f", "new/one/two")]
    [InlineData("f(){ declare -A a; a[key]=one; a[other]=two; declare -n r='a[key]'; declare r=new; printf '%s/%s/%s' \"$r\" \"${a[key]}\" \"${a[other]}\"; }; f", "new/one/two")]
    [InlineData("a=(one two); f(){ declare -n r='a[1]'; declare r=new; printf '%s/' \"$r\"; }; f; printf '<%s>' \"${a[@]}\"", "new/<one><two>")]
    [InlineData("f(){ declare -n r='a[1]'; declare -n s=r; declare s=new; printf '%s/%s/%s' \"$r\" \"$s\" \"${a[1]}\"; }; f", "new/new/")]
    [InlineData("f(){ declare -n r='a[1]'; declare r[0]=new; printf '%s/%s' \"${r[0]}\" \"${a[1]}\"; }; f", "new/")]
    [InlineData("f(){ declare -n r='a[1]'; declare -A r[key]=new; printf '%s/%s' \"${r[key]}\" \"${a[1]}\"; }; f", "new/")]
    [InlineData("f(){ declare -n r='a[1]'; declare r=outer; (declare r=inner; printf '%s/' \"$r\"); printf '%s' \"$r\"; }; f", "inner/outer")]
    [InlineData("f(){ declare -n r='a[1]'; T=temp eval 'declare r=inner'; printf '%s' \"$r\"; }; f", "inner")]
    [InlineData("f(){ declare -n r; declare r=target; printf '%s/' \"$?\"; target=hello; printf '%s' \"$r\"; }; f", "0/hello")]
    [InlineData("f(){ declare -n r; local r=target; printf '%s/' \"$?\"; target=hello; printf '%s' \"$r\"; }; f", "0/hello")]
    [InlineData("f(){ declare -n r; declare r; printf '%s' \"$?\"; }; f", "0")]
    [InlineData("f(){ declare -n r; local r; printf '%s' \"$?\"; }; f", "0")]
    [InlineData("f(){ declare -n r; declare -n s=r; declare s=target; target=hello; printf '%s/%s' \"$r\" \"$s\"; }; f", "/hello")]
    [InlineData("f(){ declare -n r; T=temp eval 'local r=target'; target=hello; printf '%s' \"$r\"; }; f", "hello")]
    [InlineData("x=outer; f(){ x=temp declare -n x; printf '%s/' \"$x\"; x=after; }; f; printf '%s' \"$x\"", "outer/after")]
    [InlineData("x=outer; f(){ x=temp declare -n x; unset x; }; f; printf '%s' \"${x-unset}\"", "unset")]
    [InlineData("x=outer; f(){ local x=local; x=temp declare -n x; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "local/outer")]
    [InlineData("x=outer; f(){ T=temp eval 'x=inner declare -n x'; x=after; }; f; printf '%s' \"$x\"", "after")]
    [InlineData("x=outer; f(){ x=temp declare -n x x=target; target=hello; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "hello/outer")]
    [InlineData("x=outer; f(){ x=temp declare -n x=target x; target=hello; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"", "hello/outer")]
    public async Task LocalDeclarations_PreserveBindingsAndRestoreDeclarationTimeState(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("local x=inner")]
    [InlineData("declare x=inner")]
    public async Task FailedLocalDeclaration_DoesNotRestoreEarlierCallerAttributes(string declaration)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "x=outer; f(){ readonly x=before; " + declaration + "; }; f; x=after; printf '%s' \"$x\"");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(declaration.Split(' ')[0] + ": x: readonly variable\nx: readonly variable\n", result.StdErr);
    }

    [Theory]
    [InlineData("x=outer; f(){ x=before; declare -n x='bad target'; printf '%s' \"$?\"; }; f; printf '/%s' \"$x\"", "1/before", "declare: `bad target': invalid variable name for name reference\n")]
    [InlineData("f(){ declare -n r; local r=; printf '%s/' \"$?\"; local r=target; target=hello; printf '%s' \"$r\"; }; f", "1/hello", "local: `': invalid variable name for name reference\n")]
    [InlineData("x=outer; f(){ x='bad target' declare -n x; printf '%s/' \"$?\"; x=after; }; f; printf '%s' \"$x\"", "1/after", "declare: `bad target': invalid variable name for name reference\n")]
    [InlineData("f(){ declare -a a; a[0]=one; declare -A a; printf '%s/%s' \"$?\" \"${a[0]}\"; }; f", "1/one", "declare: a: cannot convert indexed to associative array\n")]
    [InlineData("f(){ declare -a a; a[0]=first; declare 'a[@]=next'; printf '%s/<%s>' \"$?\" \"${a[@]}\"; }; f", "1/<first>", "a[@]: bad array subscript\n")]
    public async Task FailedDeclarations_PreserveValues(string script, string expected, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("local x=inner")]
    [InlineData("declare x=inner")]
    public async Task PrefixedSource_CannotReplaceTheFunctionsRestorationSnapshot(string declaration)
    {
        var tree = EndToEndHarness.BuildTree([("/script", declaration)]);
        var result = await EndToEndHarness.RunScriptAsync(
            "x=outer; f(){ x=temp source /script; }; f; printf '%s' \"$x\"", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("outer", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("local x=inner", "x=temp", "after", "after")]
    [InlineData("declare x=inner", "x=temp", "after", "after")]
    [InlineData("local x=inner", "T=temp", "after", "outer")]
    public async Task PrefixedSource_PreservesTheLifetimeOfEachDeclaredBinding(
        string declaration, string prefix, string inside, string after)
    {
        var tree = EndToEndHarness.BuildTree([("/script", declaration)]);
        var result = await EndToEndHarness.RunScriptAsync(
            "x=outer; f(){ " + prefix + " source /script; x=after; printf '%s/' \"$x\"; }; f; printf '%s' \"$x\"",
            tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(inside + "/" + after, result.StdOut);
        Assert.Empty(result.StdErr);
    }
}

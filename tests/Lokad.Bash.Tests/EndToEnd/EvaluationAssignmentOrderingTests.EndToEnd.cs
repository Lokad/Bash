using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationAssignmentOrderingTestsEndToEnd
{
    [Theory]
    [InlineData("unset X; X=${X:=bar} true; echo \"${X-unset}\"", "bar\n")]
    [InlineData("unset X; X=a Y=${X:=bar} true; echo \"${X-unset}\"", "unset\n")]
    [InlineData("unset X; Y=${X:=bar} X=b true; echo \"${X-unset}\"", "bar\n")]
    [InlineData("unset X; X=a Y=$((X=3)) true; echo \"${X-unset}\"", "3\n")]
    [InlineData("x=1; x=3 A=$((x=3)) true; printf '%s' \"$x\"", "3")]
    [InlineData("unset x; x=3 A=$((x=3)) true; printf '%s' \"${x-unset}\"", "3")]
    [InlineData("x=1; x=3 A=$((x=4)) true; sh -c 'printf \"<%s>\" \"${x-unset}\"'", "<unset>")]
    [InlineData("export x=1; x=3 A=$((x=4)) true; sh -c 'printf \"<%s>\" \"${x-unset}\"'", "<4>")]
    [InlineData("x=1; x=3 A=$(printf '%s' \"$((x=4))\") true; printf '%s' \"$x\"", "1")]
    [InlineData("x=1; declare -n r=x; x=3 A=$((r=3)) true; printf '%s' \"$x\"", "1")]
    [InlineData("x=1; declare -n r=x; A=$((r=4)) true; printf '%s' \"$x\"", "4")]
    [InlineData("x=1; declare -n r=x; r=3 A=$((x=4)) true; printf '%s' \"$x\"", "4")]
    [InlineData("x=1; declare -n r=x; f(){ printf '%s/%s' \"$x\" \"$A\"; }; x=3 A=$((r=4)) f; printf '/%s' \"$x\"", "4/4/1")]
    [InlineData("unset x; declare -n r=x; f(){ printf '%s/%s' \"$x\" \"$A\"; }; x= A=${r:=4} f; printf '/%s' \"${x-unset}\"", "4/4/unset")]
    [InlineData("result=before; TEMP=value printf -v result '%s' after; printf '%s' \"$result\"", "after")]
    [InlineData("TEMP=value cd /dir; printf '%s/%s' \"$PWD\" \"$OLDPWD\"", "/dir//")]
    [InlineData("value=$(printf 'x\\r\\n\\n'); printf '<%s>\\n' \"$value\"", "<x\r>\n")]
    [InlineData("declare -n r; r=target sh -c 'printf \"%s\" \"${r-unset}\"'; printf '/%s' \"${r-unset}\"", "target/unset")]
    [InlineData("declare -n r; declare -n s=r; s=target sh -c 'printf \"%s/%s\" \"${r-unset}\" \"${s-unset}\"'", "unset/target")]
    [InlineData("declare -n r; r=target eval 'printf \"%s\" \"${r-unset}\"'; printf '/%s' \"${r-unset}\"", "target/unset")]
    [InlineData("declare -n r; r=first r=second sh -c 'printf \"%s\" \"${r-unset}\"'; printf '/%s' \"${r-unset}\"", "second/unset")]
    [InlineData("declare -n r; r= sh -c 'printf \"<%s>\" \"${r-unset}\"'", "<>")]
    [InlineData("declare -n r; r=temp X=$r sh -c 'printf \"%s/%s\" \"$r\" \"$X\"'", "temp/temp")]
    [InlineData("f(){ declare -n r; r=target sh -c 'printf \"%s\" \"${r-unset}\"'; printf '/%s' \"${r-unset}\"; }; f", "target/unset")]
    public async Task PrefixAndSubstitutionEffects_MatchBashScriptFiles(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("a=(one two); X=$((a=3)) true; printf '%s/<%s>/<%s>' \"$a\" \"${a[0]}\" \"${a[1]}\"", "3/<3>/<two>")]
    [InlineData("a=(1 kept); X=$((a+=2)) true; printf '%s/%s' \"$a\" \"${a[1]}\"", "3/kept")]
    [InlineData("declare -A a; a=(0 1 key kept); X=$((a+=2)) sh -c 'printf \"%s/\" \"$X\"'; printf '%s/%s' \"$a\" \"${a[key]}\"", "3/3/kept")]
    [InlineData("a=(1 kept); a=3 X=$((a=3)) true; printf '%s/%s' \"$a\" \"${a[1]}\"", "3/kept")]
    [InlineData("a=(1 kept); declare -n r=a; X=$((r=4)) true; printf '%s/%s/%s' \"$r\" \"$a\" \"${a[1]}\"", "4/4/kept")]
    [InlineData("a=(1 kept); declare -n r=a; a=3 X=$((r=4)) true; printf '%s/%s' \"$a\" \"${a[1]}\"", "1/kept")]
    [InlineData("a=('' kept); X=${a:=new} true; printf '%s/%s' \"$a\" \"${a[1]}\"", "new/kept")]
    [InlineData("a[1000000]=kept; X=${a:=zero} true; printf '%s/%s' \"$a\" \"${a[1000000]}\"", "zero/kept")]
    [InlineData("a=(1 kept); X=tmp : <<< \"$((a=4))\"; printf '%s/%s' \"$a\" \"${a[1]}\"", "4/kept")]
    [InlineData("a=(1 kept); X=$((a=3)) Y=${a:=wrong} sh -c 'printf \"%s/%s/\" \"$X\" \"$Y\"'; printf '%s/%s' \"$a\" \"${a[1]}\"", "3/3/3/kept")]
    [InlineData("a=(1 kept); X=$((a=3)) sh -c 'printf \"<%s>/\" \"${a-unset}\"'; printf '%s/%s' \"$a\" \"${a[1]}\"", "<unset>/3/kept")]
    public async Task ExpansionWrites_PreserveCallerArraysAndTheirDefaultElements(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("A=() sh -c 'printf \"<%s>\" \"$A\"'", "<()>")]
    [InlineData("A=('') sh -c 'printf \"<%s>\" \"$A\"'", "<()>")]
    [InlineData("A=('' '') sh -c 'printf \"<%s>\" \"$A\"'", "<( )>")]
    [InlineData("A=('a b' '' c) sh -c 'printf \"<%s>\" \"$A\"'", "<(a b  c)>")]
    [InlineData("set -- a b; A=(\"$@\") sh -c 'printf \"<%s>\" \"$A\"'", "<(a b)>")]
    [InlineData("arr=(first second); A=(\"${arr[@]}\") sh -c 'printf \"<%s>\" \"$A\"'", "<(first second)>")]
    [InlineData("set -- a b; A=(head pre\"$@\"post tail) sh -c 'printf \"<%s>\" \"$A\"'", "<(head prea bpost tail)>")]
    [InlineData("set -- '' b; A=($@) sh -c 'printf \"<%s>\" \"$A\"'", "<( b)>")]
    [InlineData("set -- a ''; A=(\"$@\") sh -c 'printf \"<%s>\" \"$A\"'", "<(a )>")]
    [InlineData("set --; A=(head \"$@\" tail) sh -c 'printf \"<%s>\" \"$A\"'", "<(head  tail)>")]
    [InlineData("set --; A=(head $@ tail) sh -c 'printf \"<%s>\" \"$A\"'", "<(head  tail)>")]
    [InlineData("set -- a b; IFS=:; A=(\"$@\" \"$*\") sh -c 'printf \"<%s>\" \"$A\"'", "<(a b a:b)>")]
    [InlineData("set -- a b; IFS=; A=($@ $*) sh -c 'printf \"<%s>\" \"$A\"'", "<(a b ab)>")]
    [InlineData("set -- a b; IFS=:; A=(pre\"${N:-$@}\"post) sh -c 'printf \"<%s>\" \"$A\"'", "<(prea bpost)>")]
    [InlineData("set -- '' b; IFS=:; A=(\"${N:-$@}\") sh -c 'printf \"<%s>\" \"$A\"'", "<( b)>")]
    [InlineData("set -- a b; N=set; A=(\"${N:+$@}\") sh -c 'printf \"<%s>\" \"$A\"'", "<(a b)>")]
    [InlineData("set -- a b; IFS=:; A=(\"${N:=$@}\") sh -c 'printf \"<%s>\" \"$A\"'; printf '/<%s>' \"$N\"", "<(a b)>/<a b>")]
    [InlineData("v='a  b'; A=($v * {a,b}) sh -c 'printf \"<%s>\" \"$A\"'", "<(a  b * {a,b})>")]
    [InlineData("HOME=/home; A=(~ ~/dir ~+ ~-) sh -c 'printf \"<%s>\" \"$A\"'", "<(~ ~/dir ~+ ~-)>")]
    [InlineData("HOME=/home; A=('~' \"~/dir\" ~/$((1 + 2))) sh -c 'printf \"<%s>\" \"$A\"'", "<(~ ~/dir ~/3)>")]
    [InlineData("HOME=/home; A=(~ ${N:-~}) sh -c 'printf \"<%s>\" \"$A\"'", "<(~ /home)>")]
    [InlineData("HOME=/home; A=(${N:=~}) sh -c 'printf \"<%s>\" \"$A\"'; printf '/%s' \"$N\"", "<(/home)>//home")]
    public async Task CompoundPrefixes_PreserveParenthesesAndItemSeparators(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("A=$(printf assignment >&2) printf '' > \"$(printf redirect >&2; printf /dev/null)\"", "assignmentredirect")]
    [InlineData("A=$(printf assignment >&2) cat > \"$(printf redirect >&2; printf /dev/null)\"", "assignmentredirect")]
    public async Task CommandPrefixesExpandBeforeRedirections(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expected, result.StdErr);
    }

    [Theory]
    [InlineData("X=$(printf assignment >&2; printf value) >$(printf redirect >&2; printf /dev/null); printf '%s' \"$X\"", "value", "assignmentredirect")]
    [InlineData("X=$(printf side >&2; printf value) 2>&1; printf '%s' \"$X\"", "value", "side")]
    [InlineData("X=$(printf side >&2; printf value) 2>/dev/null; printf '%s' \"$X\"", "value", "side")]
    [InlineData("X=old; X=new >$(printf '%s' \"$X\" >&2; printf /dev/null); printf '%s' \"$X\"", "new", "new")]
    public async Task AssignmentOnlyCommands_ExpandBeforeTheirRedirections(string script, string expected, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Fact]
    public async Task AssignmentOnlyCommands_KeepAssignmentsAfterRedirectionFailure()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "X=value < /missing-input; printf '%s/%s' \"$?\" \"$X\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1/value", result.StdOut);
    }

    [Theory]
    [InlineData("X=${MISSING:?assignment-failed} true || true; echo after", "assignment-failed")]
    [InlineData("true > ${MISSING:?redirection-failed} || true; echo after", "redirection-failed")]
    [InlineData("! echo ${MISSING:?word-failed}; echo after", "word-failed")]
    [InlineData("printf '%s' \"$((1 + ${MISSING:?arithmetic-failed}))\"; printf after", "arithmetic-failed")]
    [InlineData("X=$((1 + ${MISSING:?arithmetic-failed})) true; printf after", "arithmetic-failed")]
    [InlineData("true > \"$((1 + ${MISSING:?arithmetic-failed}))\"; printf after", "arithmetic-failed")]
    public async Task FatalExpansion_CannotBeSuppressedByOrListOrNegation(string script, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("MISSING: " + diagnostic + "\n", result.StdErr);
    }

    [Theory]
    [InlineData("echo ${OUTER:?${INNER:?first}}; printf later", "494E4E45523A2066697273740A")]
    [InlineData("message=$'\\377'; echo ${MISSING:?$message}; printf later", "4D495353494E473A20FF0A")]
    [InlineData("echo ${MISSING:?$(printf '\\377')}; printf later", "4D495353494E473A20FF0A")]
    [InlineData("echo $((1 + ${MISSING:?$(printf '\\377')})); printf later", "4D495353494E473A20FF0A")]
    [InlineData("set -- first second; echo ${MISSING:?${NESTED:-$(printf '\\377')}$@}", "4D495353494E473A20FF6669727374207365636F6E640A")]
    public async Task FatalExpansion_PreservesTheFirstDiagnosticBytes(string script, string diagnosticHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(diagnosticHex), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task ArithmeticExpansion_AllowsCommandSubstitutionToMakeProgress()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '%s' \"$((1 + $(printf 2)))\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("3", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("printf changed > /victim")]
    [InlineData("printf changed > /victim | cat")]
    [InlineData("(printf changed > /victim | cat)")]
    [InlineData("{ printf changed > /victim | cat; }")]
    public async Task FatalForItemExpansion_DoesNotRunAnyBodyOrWriteFiles(string body)
    {
        var tree = EndToEndHarness.BuildTree([("/victim", "original")]);
        var result = await EndToEndHarness.RunScriptAsync(
            "for i in ok ${MISSING:?bad}; do " + body + "; done", tree);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("MISSING: bad\n", result.StdErr);
        Assert.Equal("original", await EndToEndHarness.ReadFileAsync(result.Host, "/victim"));
    }

    [Theory]
    [InlineData("printf '%s' \"${MISSING:?}\"; printf after", "MISSING: parameter null or not set\n")]
    [InlineData("printf '%s' \"${MISSING?}\"; printf after", "MISSING: parameter not set\n")]
    [InlineData("printf '%s' \"${MISSING:?$EMPTY}\"; printf after", "MISSING: \n")]
    [InlineData("printf '%s' \"${MISSING:?$(printf '')}\"; printf after", "MISSING: \n")]
    [InlineData("printf '%s' \"${OUTER:?${INNER:?}}\"; printf after", "INNER: parameter null or not set\n")]
    [InlineData("for i in ok ${MISSING:?}; do printf after; done", "MISSING: parameter null or not set\n")]
    public async Task FatalExpansion_DistinguishesOmittedAndEmptyMessages(string script, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Theory]
    [InlineData("set -- first second; printf '%s' \"${MISSING:?$@}\"", "MISSING: first second\n")]
    [InlineData("set -- first second; printf '%s' \"${MISSING:?\"$@\"}\"", "MISSING: first second\n")]
    [InlineData("arr=(one two); printf '%s' \"${MISSING:?${arr[@]}}\"", "MISSING: one two\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?$@}\"", "MISSING: second\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?\"$@\"}\"", "MISSING:  second\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?prefix$@suffix}\"", "MISSING: prefix secondsuffix\n")]
    [InlineData("set -- first second; IFS=:; printf '%s' \"${MISSING:?$*}\"", "MISSING: first second\n")]
    [InlineData("set -- first second; IFS=:; printf '%s' \"${MISSING:?\"$*\"}\"", "MISSING: first:second\n")]
    [InlineData("arr=(one two); IFS=:; printf '%s' \"${MISSING:?${arr[*]}}\"", "MISSING: one two\n")]
    [InlineData("set -- first second; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "MISSING: first second\n")]
    [InlineData("set --; printf '%s' \"${MISSING:?$@}\"", "MISSING: \n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?$@}\"", "MISSING: a b c\n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?\"$@\"}\"", "MISSING: a:b c\n")]
    [InlineData("set -- '' second; IFS=:; printf '%s' \"${MISSING:?$@}\"", "MISSING:  second\n")]
    [InlineData("set -- first ''; IFS=:; printf '%s' \"${MISSING:?$@}\"", "MISSING: first\n")]
    [InlineData("set -- first ''; IFS=:; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "MISSING: first \n")]
    [InlineData("message='  first   second '; printf '%s' \"${MISSING:?$message}\"", "MISSING: first second\n")]
    [InlineData("message='  first   second '; printf '%s' \"${MISSING:?\"$message\"}\"", "MISSING:   first   second \n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?${NESTED:-\"$@\"}}\"", "MISSING: a:b c\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "MISSING: second\n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "MISSING: a:b c\n")]
    [InlineData("set -- ' a:b ' ' c:d '; IFS=': '; printf '%s' \"${MISSING:?pre${NESTED:-$@}post}\"", "MISSING: pre a:b   c:d post\n")]
    [InlineData("set -- ' a:b ' ' c:d '; IFS=' :'; printf '%s' \"${MISSING:?pre${NESTED:-$@}post}\"", "MISSING: pre a b c d post\n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?${NESTED:=\"$@\"}}\"", "MISSING: a b c\n")]
    public async Task FatalExpansion_JoinsEveryDiagnosticField(string script, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Theory]
    [InlineData("unset IFS; message='a:b'; : \"${MISSING:?${IFS:=:}$message}\"", "MISSING:  a b\n")]
    [InlineData("unset IFS; message='a:b'; : \"${MISSING:?$message${IFS:=:}}\"", "MISSING: a b\n")]
    [InlineData("unset IFS; message='a:b'; : \"${MISSING:?${IFS:=:}\"$message\"}\"", "MISSING:  a:b\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${IFS:=:}$@}\"", "MISSING:  a b c d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?$@${IFS:=:}}\"", "MISSING: a b c d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${IFS:=:}\"$*\"}\"", "MISSING:  a:b:c:d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?\"$*\"${IFS:=:}}\"", "MISSING: a:b c:d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${IFS:=:}${NESTED:-$@}}\"", "MISSING:  a:b c:d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${NESTED:-$@}${IFS:=:}}\"", "MISSING: a b c d\n")]
    [InlineData("IFS=; set -- a:b c:d; : \"${MISSING:?${IFS:=:}$@}\"", "MISSING:  a b c d\n")]
    public async Task FatalExpansion_UsesUpdatedIfsWithoutLosingQuotedFields(string script, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Theory]
    [InlineData("unset IFS; message='a:b'; printf '<%s>' ${IFS:=:}$message", "<><a><b>")]
    [InlineData("unset IFS; set -- a:b c:d; printf '<%s>' $@${IFS:=:}", "<a><b c><d>")]
    [InlineData("unset IFS; set -- a:b c:d; printf '<%s>' ${IFS:=:}$@", "<><a><b><c><d>")]
    [InlineData("unset IFS; set -- a:b c:d; printf '<%s>' \"${IFS:=:}$*\"", "<:a:b:c:d>")]
    [InlineData("unset IFS; set -- a:b c:d; printf '<%s>' \"$*${IFS:=:}\"", "<a:b c:d:>")]
    public async Task WordExpansion_JoinsWithCurrentIfsAndSplitsWithFinalIfs(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }
}

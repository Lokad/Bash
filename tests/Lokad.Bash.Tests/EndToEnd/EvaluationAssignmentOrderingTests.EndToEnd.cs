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
        Assert.Equal("line 1: MISSING: " + diagnostic + "\n", result.StdErr);
    }

    [Theory]
    [InlineData("echo ${OUTER:?${INNER:?first}}; printf later", "6C696E6520313A20494E4E45523A2066697273740A")]
    [InlineData("message=$'\\377'; echo ${MISSING:?$message}; printf later", "6C696E6520313A204D495353494E473A20FF0A")]
    [InlineData("echo ${MISSING:?$(printf '\\377')}; printf later", "6C696E6520313A204D495353494E473A20FF0A")]
    [InlineData("echo $((1 + ${MISSING:?$(printf '\\377')})); printf later", "6C696E6520313A204D495353494E473A20FF0A")]
    [InlineData("set -- first second; echo ${MISSING:?${NESTED:-$(printf '\\377')}$@}", "6C696E6520313A204D495353494E473A20FF6669727374207365636F6E640A")]
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
        Assert.Equal("line 1: MISSING: bad\n", result.StdErr);
        Assert.Equal("original", await EndToEndHarness.ReadFileAsync(result.Host, "/victim"));
    }

    [Theory]
    [InlineData("printf '%s' \"${MISSING:?}\"; printf after", "line 1: MISSING: parameter null or not set\n")]
    [InlineData("printf '%s' \"${MISSING?}\"; printf after", "line 1: MISSING: parameter not set\n")]
    [InlineData("printf '%s' \"${MISSING:?$EMPTY}\"; printf after", "line 1: MISSING: \n")]
    [InlineData("printf '%s' \"${MISSING:?$(printf '')}\"; printf after", "line 1: MISSING: \n")]
    [InlineData("printf '%s' \"${OUTER:?${INNER:?}}\"; printf after", "line 1: INNER: parameter null or not set\n")]
    [InlineData("for i in ok ${MISSING:?}; do printf after; done", "line 1: MISSING: parameter null or not set\n")]
    public async Task FatalExpansion_DistinguishesOmittedAndEmptyMessages(string script, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Theory]
    [InlineData("set -- first second; printf '%s' \"${MISSING:?$@}\"", "line 1: MISSING: first second\n")]
    [InlineData("set -- first second; printf '%s' \"${MISSING:?\"$@\"}\"", "line 1: MISSING: first second\n")]
    [InlineData("arr=(one two); printf '%s' \"${MISSING:?${arr[@]}}\"", "line 1: MISSING: one two\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?$@}\"", "line 1: MISSING: second\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?\"$@\"}\"", "line 1: MISSING:  second\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?prefix$@suffix}\"", "line 1: MISSING: prefix secondsuffix\n")]
    [InlineData("set -- first second; IFS=:; printf '%s' \"${MISSING:?$*}\"", "line 1: MISSING: first second\n")]
    [InlineData("set -- first second; IFS=:; printf '%s' \"${MISSING:?\"$*\"}\"", "line 1: MISSING: first:second\n")]
    [InlineData("arr=(one two); IFS=:; printf '%s' \"${MISSING:?${arr[*]}}\"", "line 1: MISSING: one two\n")]
    [InlineData("set -- first second; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "line 1: MISSING: first second\n")]
    [InlineData("set --; printf '%s' \"${MISSING:?$@}\"", "line 1: MISSING: \n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?$@}\"", "line 1: MISSING: a b c\n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?\"$@\"}\"", "line 1: MISSING: a:b c\n")]
    [InlineData("set -- '' second; IFS=:; printf '%s' \"${MISSING:?$@}\"", "line 1: MISSING:  second\n")]
    [InlineData("set -- first ''; IFS=:; printf '%s' \"${MISSING:?$@}\"", "line 1: MISSING: first\n")]
    [InlineData("set -- first ''; IFS=:; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "line 1: MISSING: first \n")]
    [InlineData("message='  first   second '; printf '%s' \"${MISSING:?$message}\"", "line 1: MISSING: first second\n")]
    [InlineData("message='  first   second '; printf '%s' \"${MISSING:?\"$message\"}\"", "line 1: MISSING:   first   second \n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?${NESTED:-\"$@\"}}\"", "line 1: MISSING: a:b c\n")]
    [InlineData("set -- '' second; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "line 1: MISSING: second\n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?${NESTED:-$@}}\"", "line 1: MISSING: a:b c\n")]
    [InlineData("set -- ' a:b ' ' c:d '; IFS=': '; printf '%s' \"${MISSING:?pre${NESTED:-$@}post}\"", "line 1: MISSING: pre a:b   c:d post\n")]
    [InlineData("set -- ' a:b ' ' c:d '; IFS=' :'; printf '%s' \"${MISSING:?pre${NESTED:-$@}post}\"", "line 1: MISSING: pre a b c d post\n")]
    [InlineData("set -- a:b c; IFS=:; printf '%s' \"${MISSING:?${NESTED:=\"$@\"}}\"", "line 1: MISSING: a b c\n")]
    public async Task FatalExpansion_JoinsEveryDiagnosticField(string script, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
    }

    [Theory]
    [InlineData("unset IFS; message='a:b'; : \"${MISSING:?${IFS:=:}$message}\"", "line 1: MISSING:  a b\n")]
    [InlineData("unset IFS; message='a:b'; : \"${MISSING:?$message${IFS:=:}}\"", "line 1: MISSING: a b\n")]
    [InlineData("unset IFS; message='a:b'; : \"${MISSING:?${IFS:=:}\"$message\"}\"", "line 1: MISSING:  a:b\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${IFS:=:}$@}\"", "line 1: MISSING:  a b c d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?$@${IFS:=:}}\"", "line 1: MISSING: a b c d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${IFS:=:}\"$*\"}\"", "line 1: MISSING:  a:b:c:d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?\"$*\"${IFS:=:}}\"", "line 1: MISSING: a:b c:d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${IFS:=:}${NESTED:-$@}}\"", "line 1: MISSING:  a:b c:d\n")]
    [InlineData("unset IFS; set -- a:b c:d; : \"${MISSING:?${NESTED:-$@}${IFS:=:}}\"", "line 1: MISSING: a b c d\n")]
    [InlineData("IFS=; set -- a:b c:d; : \"${MISSING:?${IFS:=:}$@}\"", "line 1: MISSING:  a b c d\n")]
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

    [Theory]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' $*${IFS:=,}; printf '|<%s>' \"$IFS\"", "<a b><c,d,>|<,>")]
    [InlineData("IFS=; set --; printf '<%s>' ${*}${IFS:=,}", "<,>")]
    [InlineData("IFS=; set -- a b ''; printf '<%s>' $*${IFS:=$'\\t\\n '}", "<a><b><\t\n>")]
    [InlineData("IFS=; set -- 'a b' c,d; data='x, y z'; printf '<%s>' $*${IFS:=,}$data", "<a b><c,d,x,><y><z>")]
    [InlineData("unset IFS; set -- 'a b' c,d; data='p q,'; printf '<%s>' $data${IFS:=}$*", "<p><q,a b><c,d>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' $*${IFS:=,}\"$@\"", "<a b><c,d,a b,c,d>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' $*${IFS:=,}$@", "<a b><c,d,a><b,c,d>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' $* ${IFS:=,}", "<a b><c,d><>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' \"$*${IFS:=,}\"", "<a bc,d,>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' ${IFS:=,}$*", "<><a b><c><d>")]
    public async Task WordExpansion_PositionalStarWithEmptyIfsSplitsOnSpaces(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=; set -- zero 'a b' c,d ''; printf '<%s>' ${*:2:2}${IFS:=,}", "<a b><c,d,>")]
    [InlineData("IFS=; set -- ''; printf '<%s>' ${*:1:1}${IFS:=,}", "<,>")]
    [InlineData("IFS=; set -- ''; printf '<%s>' x${*:1:0}${IFS:=,}", "<x>")]
    [InlineData("IFS=; set -- 'a b' c,d ''; printf '<%s>' ${*#?}${IFS:=,}", "< b><,d><,>")]
    [InlineData("IFS=; set -- 'a b' c,d ''; printf '<%s>' ${*//,/Z}${IFS:=,}", "<a b><cZd><,>")]
    [InlineData("IFS=; set --; printf '<%s>' x${*//,/Z}${IFS:=,}", "<x>")]
    [InlineData("IFS=; set -- 'a b' c,d ''; printf '<%s>' ${*^^}${IFS:=,}", "<A B><C,D><,>")]
    [InlineData("IFS=; set -- 'a b' c,d ''; printf '<%s>' ${*@A}${IFS:=,}", "<set><--><'a b'><'c,d'><'',>")]
    [InlineData("IFS=; set -- ''; printf '<%s>' ${*@a}${IFS:=,}", "<,>")]
    [InlineData("IFS=; set --; printf '<%s>' ${*:+skip}${IFS:=,}", "<,>")]
    [InlineData("IFS=; set -- 'a b' c,d ''; printf '<%s>' ${*:-fallback}${IFS:=,}", "<a b><c,d><,>")]
    [InlineData("IFS=; set --; printf '<%s>' ${*:-fallback}${IFS:=,}", "<fallback>")]
    public async Task WordExpansion_PositionalStarOperatorSelectsSpaceSplitting(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=; set -- east west; printf '<%s>' $@${IFS:=,}; printf '|<%s>' \"$IFS\"", "<east west>|<,>")]
    [InlineData("IFS=; set -- 'a b' c,d; data='x y'; printf '<%s>' ${@}${IFS:=' '}$data", "<a b><c,d><x><y>")]
    [InlineData("unset IFS; set -- 'a b' c,d; data='x y'; printf '<%s>' $@${IFS:=}$data", "<a><b><c,dx y>")]
    [InlineData("IFS=; set -- '' ''; printf '<%s>' $@${IFS:=,}", "< >")]
    [InlineData("IFS=; a=('a b' '' c,d); printf '<%s>' ${a[@]}${IFS:=,}", "<a b  c><d>")]
    [InlineData("IFS=; a=('skip me' '' c,d); printf '<%s>' ${a[*]:1:2}${IFS:=,}", "< c><d>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' $@${IFS:=,},tail", "<a b c><d><,tail>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' $@ ${IFS:=,}", "<a b><c,d><>")]
    [InlineData("IFS=; set -- 'a b' c,d; printf '<%s>' ${IFS:=,}$@", "<><a b><c><d>")]
    public async Task WordExpansion_UnquotedAggregateRetainsJoinAcrossIfsAssignment(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=; set -- east west; printf '<%s>' $@,${IFS:=,}tail", "<east west><><tail>")]
    [InlineData("unset IFS; set -- east west; printf '<%s>' $@:${IFS:=:}tail", "<east west><><tail>")]
    [InlineData("IFS=1; set -- east west; printf '<%s>' pre$@2$((IFS=2))2tail", "<preeast1west><><2tail>")]
    [InlineData("IFS=12; set -- east west; printf '<%s>' pre$@2$((IFS=2))2tail", "<preeast1west2><2tail>")]
    [InlineData("IFS=; printf '<%s>' pre,${IFS:=,}tail", "<pre><><tail>")]
    [InlineData("IFS=; value=kept; set -- east west; printf '<%s>' ${value:-$@},${IFS:=,}tail", "<kept><><tail>")]
    [InlineData("IFS=; set -- east west; printf '<%s>' $@\\,${IFS:=,}tail", "<east west,><tail>")]
    [InlineData("IFS=; a=(east west); printf '<%s>' ${a[*]},${IFS:=,}tail", "<east west><><tail>")]
    [InlineData("IFS=; set -- east west; printf '<%s>' $@2$((IFS=1))2$((IFS=2))2tail", "<east west><1><><2tail>")]
    public async Task WordExpansion_ProtectsSourceBytesUsingTheIfsAtTheirPosition(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=; set -- 'a b' '' c,d; printf '<%s>' \"$@${IFS:=,}\"", "<a b  c,d,>")]
    [InlineData("IFS=; set -- 'a b' '' c,d; printf '<%s>' \"$@\"${IFS:=,}", "<a b  c,d>")]
    [InlineData("IFS=; a=('a b' '' c,d); printf '<%s>' \"${a[@]}${IFS:=,}\"", "<a b  c,d,>")]
    [InlineData("IFS=1; set -- 'a b' c2d; printf '<%s>' \"$@$((IFS=2))$@\"", "<a b1c2d><a b><c2d>")]
    [InlineData("IFS=1; set -- east west; printf '<%s>' \"prefix2\"\"$@$((IFS=2))\"suffix", "<prefix2east1westsuffix>")]
    [InlineData("IFS=2; set -- east west; printf '<%s>' \"p${#@}q$@r\"", "<p><qeast><westr>")]
    [InlineData("IFS=2; set -- east west; printf '<%s>' \"p$((2))q\"", "<p2q>")]
    [InlineData("IFS=; set -- 'a b' '' c,d; printf '<%s>' \"$@\" ${IFS:=,}", "<a b><><c,d><>")]
    [InlineData("unset IFS; v='left right '; set -- east west; printf '<%s>' $v\"$@${IFS:=}\"", "<left><right><east><west>")]
    [InlineData("IFS=; set -- 'a b' '' c,d; printf '<%s>' \"$@${IFS:=,}\"\"$@\"", "<a b  c,d,a b><><c,d>")]
    public async Task WordExpansion_ResolvesQuotedAggregateBeforeSplittingTheWord(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-\"$@\"}${IFS:=,}", "<a b><c,d><,>")]
    [InlineData("unset IFS v; set -- 'a b' c,d ''; printf '<%s>' ${v:-\"$@\"}${IFS:=,}", "<a b c,d >")]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' \"${v:-$@}\"${IFS:=,}", "<a b c,d >")]
    [InlineData("IFS=; ok=1; a=('a b' c,d ''); printf '<%s>' ${ok:+\"${a[@]}\"}${IFS:=,}", "<a b><c,d><,>")]
    [InlineData("IFS=; unset v w; set -- '' '' ''; printf '<%s>' p${v:-${w:-\"$@\"}}${IFS:=,}q", "<p><><,q>")]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; r='t u,v'; printf '<%s>' ${v:-\"$@\"}${IFS:=,}$r", "<a b><c,d><,t><u,v>")]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-\"$@\"${IFS:=,}}", "<a b c,d >")]
    [InlineData("IFS=,; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-\"$@\"}${IFS:=,}", "<a b><c,d><>")]
    [InlineData("IFS=; unset v; set --; set -- ${v:-\"$@\"}; printf '%s' \"$#\"", "0")]
    public async Task WordExpansion_RetainsQuotedAggregateJoinFromSelectedOperand(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-$@}${IFS:=,}", "<a b c><d >")]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-$@${IFS:=,}}", "<a b c d >")]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-$*}${IFS:=,}", "<a b><c,d,>")]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-$*${IFS:=,}}", "<a b><c><d><>")]
    [InlineData("IFS=; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-$@}", "<a b><c,d>")]
    [InlineData("IFS=; unset v; set -- '' '' ''; set -- ${v:-$@}; printf '%s' \"$#\"", "0")]
    [InlineData("IFS=; unset v; set --; set -- ${v:-$*}; printf '%s' \"$#\"", "0")]
    [InlineData("IFS=,; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-$@\"$@\"}", "<a b><c,d><a b><c,d><>")]
    [InlineData("IFS=', '; unset v; set -- 'a b' c,d ''; printf '<%s>' ${v:-$@}", "<a b><c,d><>")]
    [InlineData("IFS=12; unset v; set -- 'a b' c2d ''; printf '<%s>' ${v:-$@$((IFS=2))}", "<a b1c2d1>")]
    [InlineData("IFS=12; unset v; a=('a b' c2d ''); printf '<%s>' ${v:-${a[@]}$((IFS=2))}", "<a b c2d >")]
    [InlineData("IFS=; unset v; a=('a b' c,d ''); printf '<%s>' ${v:-${a[*]}}${IFS:=,}", "<a b c><d >")]
    public async Task WordExpansion_PreservesUnquotedAggregateJoinFromSelectedOperand(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=; v='a b'; w='c d'; printf '<%s>' $v${IFS:=' '}$w", "<a b><c><d>")]
    [InlineData("unset IFS; v='a b'; printf '<%s>' $v${IFS:=' '}", "<a><b>")]
    public async Task WordExpansion_ProtectsSpacesFromAnExpansionWithEmptyIfs(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }
}

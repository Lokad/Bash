using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ExternalRedirectionScopeTests
{
    // Bash 5.2 -s: omit diagnostic source locations; enable pipefail explicitly in its control.
    [Theory]
    [InlineData("unset HDS_VALUE HDS_READ; hds_fn(){ printf 'fn:%s\\n' \"${HDS_VALUE-unset}\"; }\ncat <<HDS_END\n${HDS_VALUE:=set}\nHDS_END\nprintf 'status=%s value=%s read=%s' \"$?\" \"${HDS_VALUE-unset}\" \"${HDS_READ-unset}\"", "set\nstatus=0 value=unset read=unset", "", 0)]
    [InlineData("unset HDS_MISSING; hds_fn(){ printf wrong; }\ncat <<HDS_END\n${HDS_MISSING:?stop}\nHDS_END\nprintf 'after:%s' \"$?\"", "after:1", "HDS_MISSING: stop\n", 0)]
    [InlineData("HDS_COUNT=0; unset HDS_VALUE\ncat <<HDS_END\n$((HDS_COUNT+=1)):${HDS_VALUE:=set}:$(printf seen >&2; printf captured)\nHDS_END\nprintf 'after:%s:%s:%s' \"$?\" \"$HDS_COUNT\" \"${HDS_VALUE-unset}\"", "1:set:captured\nafter:0:0:unset", "seen", 0)]
    [InlineData("HDS_COUNT=0; unset HDS_MISSING\ncat <<HDS_END\n$((HDS_COUNT+=1))${HDS_MISSING:?stop}$(printf wrong >&2)\nHDS_END\nprintf 'after:%s:%s' \"$?\" \"$HDS_COUNT\"", "after:1:0", "HDS_MISSING: stop\n", 0)]
    [InlineData("unset HDS_MISSING\n! cat <<HDS_END\n${HDS_MISSING:?stop}\nHDS_END\nprintf 'after:%s' \"$?\"", "after:0", "HDS_MISSING: stop\n", 0)]
    [InlineData("unset HDS_MISSING\ncat <<HDS_END || printf recovered\n${HDS_MISSING:?stop}\nHDS_END\nprintf 'after:%s' \"$?\"", "recoveredafter:0", "HDS_MISSING: stop\n", 0)]
    [InlineData("unset HDS_MISSING\nif cat <<HDS_END\n${HDS_MISSING:?stop}\nHDS_END\nthen printf wrong; else printf recovered; fi\nprintf after", "recoveredafter", "HDS_MISSING: stop\n", 0)]
    [InlineData("unset HDS_MISSING\nHDS_CAPTURE=$(cat <<HDS_END\n${HDS_MISSING:?stop}\nHDS_END\nprintf after)\nprintf 'status=%s capture=%s' \"$?\" \"$HDS_CAPTURE\"", "status=0 capture=after", "HDS_MISSING: stop\n", 0)]
    [InlineData("unset HDS_MISSING\ncat <<'HDS_END'\n${HDS_MISSING:?stop}\nHDS_END\nprintf 'after:%s' \"$?\"", "${HDS_MISSING:?stop}\nafter:0", "", 0)]
    [InlineData("unset HDS_MISSING; cat <<< \"${HDS_MISSING:?stop}\"; printf 'after:%s' \"$?\"", "after:1", "HDS_MISSING: stop\n", 0)]
    [InlineData("unset HDS_VALUE\ncat <<HDS_FIRST <<HDS_SECOND\n${HDS_VALUE:=first}:$(printf first >&2)\nHDS_FIRST\n${HDS_VALUE-unset}:$(printf second >&2)\nHDS_SECOND\nprintf 'after:%s:%s' \"$?\" \"${HDS_VALUE-unset}\"", "first:\nafter:0:unset", "firstsecond", 0)]
    [InlineData("unset HDS_VALUE; cat < \"${HDS_VALUE:=/dev/null}\"; printf 'after:%s:%s' \"$?\" \"${HDS_VALUE-unset}\"", "after:0:unset", "", 0)]
    [InlineData("unset HDS_MISSING; cat < \"${HDS_MISSING:?stop}\"; printf 'after:%s' \"$?\"", "after:1", "HDS_MISSING: stop\n", 0)]
    [InlineData("unset HDS_ARG; HDS_COUNT=0; cat ${HDS_ARG:=/dev/null} <<HDS_END\n$((HDS_COUNT+=1))\nHDS_END\nprintf 'after:%s:%s:%s' \"$?\" \"$HDS_ARG\" \"$HDS_COUNT\"", "after:0:/dev/null:0", "", 0)]
    [InlineData("unset HDE_VALUE; export HDE_VALUE; sh -c 'printf \"child:%s\" \"$HDE_VALUE\"' <<< \"${HDE_VALUE:=redirected}\"; printf ':caller:%s' \"${HDE_VALUE-unset}\"", "child::caller:unset", "", 0)]
    [InlineData("unset HDE_VALUE; export HDE_VALUE; HDE_VALUE=prefix sh -c 'printf \"child:%s\" \"$HDE_VALUE\"' <<< \"${HDE_VALUE:=redirected}\"; printf ':caller:%s' \"${HDE_VALUE-unset}\"", "child:prefix:caller:unset", "", 0)]
    [InlineData("unset HDE_VALUE; export HDE_VALUE; HDE_PREFIX=prefix sh -c 'printf \"child:%s:%s\" \"$HDE_PREFIX\" \"$HDE_VALUE\"' <<< \"${HDE_VALUE:=redirected}\"; printf ':caller:%s:%s' \"${HDE_PREFIX-unset}\" \"${HDE_VALUE-unset}\"", "child:prefix::caller:unset:unset", "", 0)]
    [InlineData("unset HDE_VALUE; declare -n HDE_REF=HDE_VALUE; cat <<< \"${HDE_REF:=redirected}\"; printf 'caller:%s:%s' \"${HDE_VALUE-unset}\" \"${!HDE_REF}\"", "redirected\ncaller:unset:HDE_VALUE", "", 0)]
    [InlineData("unset HDE_MISSING; cat <<< \"$(cat <<< \"${HDE_MISSING:?stop}\"; printf continued)\"; printf 'after:%s' \"$?\"", "continued\nafter:0", "HDE_MISSING: stop\n", 0)]
    [InlineData("unset HDE_MISSING; exec cat <<< \"${HDE_MISSING:?stop}\"; printf after", "", "HDE_MISSING: stop\n", 1)]
    [InlineData("HDE_COUNT=0; unset HDE_VALUE; $(printf 'name' >&2; printf cat) <<< \"$((HDE_COUNT+=1)):${HDE_VALUE:=set}\"; printf 'after:%s:%s' \"$HDE_COUNT\" \"${HDE_VALUE-unset}\"", "1:set\nafter:0:unset", "name", 0)]
    [InlineData("HDE_COUNT=0; HDE_PREFIX=$((HDE_COUNT+=10)) cat \"${HDE_ARG:=/dev/null}\" <<< \"$((HDE_COUNT+=100))\"; printf 'after:%s:%s' \"$HDE_COUNT\" \"$HDE_ARG\"", "after:10:/dev/null", "", 0)]
    [InlineData("unset HDE_VALUE; cat(){ printf 'function:%s' \"${HDE_VALUE-unset}\"; }; cat <<< \"${HDE_VALUE:=set}\"; printf ':after:%s' \"${HDE_VALUE-unset}\"", "function:set:after:set", "", 0)]
    [InlineData("unset HDE_VALUE; cat(){ printf wrong; }; command cat <<< \"${HDE_VALUE:=set}\"; printf 'after:%s' \"${HDE_VALUE-unset}\"", "set\nafter:unset", "", 0)]
    [InlineData("HDE_VALUE=outer; hde_fn(){ local HDE_VALUE=inner; cat <<< \"${HDE_MISSING:?stop}\"; printf 'inside:%s:%s' \"$?\" \"$HDE_VALUE\"; }; hde_fn; printf ':outside:%s' \"$HDE_VALUE\"", "inside:1:inner:outside:outer", "HDE_MISSING: stop\n", 0)]
    [InlineData("unset HDE_VALUE HDE_LINE; read -r HDE_LINE <<< \"${HDE_VALUE:=set}\"; printf '%s:%s' \"$HDE_VALUE\" \"$HDE_LINE\"", "set:set", "", 0)]
    [InlineData("unset HDE_MISSING; cat <<< \"${HDE_MISSING:?stop}\" | cat; printf 'after:%s' \"$?\"", "after:1", "HDE_MISSING: stop\n", 0)]
    [InlineData("HDE_VALUE=; export HDE_VALUE; sh -c 'printf \"child:%s\" \"$HDE_VALUE\"' <<< \"${HDE_VALUE:=redirected}\"; printf ':caller:%s' \"$HDE_VALUE\"", "child::caller:", "", 0)]
    [InlineData("unset HDE_VALUE; exec 3<<<seed; cat <&3 <<< \"${HDE_VALUE:=set}\"; read -r HDE_LINE <&3; printf 'after:%s:%s' \"$HDE_LINE\" \"${HDE_VALUE-unset}\"", "set\nafter:seed:unset", "", 0)]
    [InlineData("HDE_ITEMS=(old); cat <<< \"${HDE_ITEMS[1]:=set}\"; printf 'caller:%s:%s' \"${HDE_ITEMS[0]}\" \"${HDE_ITEMS[1]-unset}\"", "set\ncaller:old:unset", "", 0)]
    public async Task ExternalRedirectionsIsolateEffectsAndFailureAfterCallerPreparation(
        string script, string output, string error, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task HostBackedBuiltinKeepsCallerRedirectionEffects()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "unset HDE_VALUE; apply_patch <<< \"${HDE_VALUE:=set}\" >/dev/null 2>/dev/null; printf '%s' \"${HDE_VALUE-unset}\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("set", result.StdOut);
        Assert.Equal("", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

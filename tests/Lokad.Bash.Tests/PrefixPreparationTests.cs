using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class PrefixPreparationTests
{
    // Bash 5.2.21 -s; omit only source-location prefixes from diagnostics.
    [Theory]
    [InlineData("unset HDS_VALUE; HDS_PREFIX=${HDS_VALUE:=set} cat <<HDS_END\n${HDS_VALUE-unset}:$(printf redir >&2)\nHDS_END\nprintf 'after:%s:%s' \"$?\" \"${HDS_VALUE-unset}\"", "set:\nafter:0:set", "redir", 0)]
    [InlineData("unset HDS_VALUE; HDS_PREFIX=${HDS_VALUE:=set} read -r HDS_READ <<HDS_END\n${HDS_VALUE-unset}:$(printf redir >&2)\nHDS_END\nprintf 'after:%s:%s:%s' \"$?\" \"${HDS_VALUE-unset}\" \"$HDS_READ\"", "after:0:set:set:", "redir", 0)]
    [InlineData("HDS_PREFIX=$(printf prefix >&2) cat <<HDS_END\n$(printf redir >&2)\nHDS_END\nprintf 'after:%s' \"$?\"", "\nafter:0", "prefixredir", 0)]
    [InlineData("HDS_PREFIX=$(printf prefix >&2) : <<HDS_END\n$(printf redir >&2)\nHDS_END\nprintf 'after:%s' \"$?\"", "after:0", "prefixredir", 0)]
    [InlineData("unset HDS_PRE HDS_RED; HDS_PREFIX=${HDS_PRE:?prefix} cat <<HDS_END\n${HDS_RED:?redir}\nHDS_END\nprintf after", "", "HDS_PRE: prefix\n", 1)]
    [InlineData("HDS_PREFIX=$(false) cat <<HDS_END\nstatus:$?\nHDS_END\nprintf 'after:%s' \"$?\"", "status:1\nafter:0", "", 0)]
    [InlineData("readonly HDS_VALUE=; HDS_VALUE=set true; printf 'after:%s' \"$?\"", "after:0", "HDS_VALUE: readonly variable\n", 0)]
    [InlineData("readonly HDS_VALUE=; HDS_VALUE=set false; printf 'after:%s' \"$?\"", "after:1", "HDS_VALUE: readonly variable\n", 0)]
    [InlineData("readonly HDS_VALUE=; HDS_VALUE=set HDS_NEXT=$(printf later >&2) printf executed; printf 'after:%s' \"$?\"", "executedafter:0", "HDS_VALUE: readonly variable\nlater", 0)]
    [InlineData("readonly HDS_VALUE=; HDS_VALUE=set command -x; printf 'after:%s' \"$?\"", "after:2", "HDS_VALUE: readonly variable\ncommand: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("readonly HDS_VALUE=; HDS_VALUE=set cat 2>/dev/null </dev/null; printf 'after:%s' \"$?\"", "after:0", "HDS_VALUE: readonly variable\n", 0)]
    [InlineData("readonly HDS_VALUE=; HDS_VALUE=set; printf 'after:%s' \"$?\"", "", "HDS_VALUE: readonly variable\n", 1)]
    [InlineData("unset HDS_MISSING; HDS_VALUE=${HDS_MISSING:?stop} : 2>/dev/null; printf after", "", "HDS_MISSING: stop\n", 1)]
    [InlineData("unset HDS_MISSING; HDS_VALUE=$(printf wrong >&2) printf '%s' \"${HDS_MISSING:?stop}\"; printf after", "", "HDS_MISSING: stop\n", 1)]
    [InlineData("unset HDS_VALUE; HDS_PREFIX=${HDS_VALUE:=set} cat </dev/null; printf 'after:%s:%s' \"$?\" \"${HDS_VALUE-unset}\"", "after:0:set", "", 0)]
    [InlineData("unset HDS_VALUE; HDS_PREFIX=${HDS_VALUE:=set} :; printf 'after:%s:%s' \"$?\" \"${HDS_VALUE-unset}\"", "after:0:set", "", 0)]
    [InlineData("unset HDS_MISSING; HDS_PREFIX=${HDS_MISSING:?stop} cat </dev/null; printf 'after:%s' \"$?\"", "", "HDS_MISSING: stop\n", 1)]
    [InlineData("unset HDS_MISSING; HDS_PREFIX=${HDS_MISSING:?stop} :; printf 'after:%s' \"$?\"", "", "HDS_MISSING: stop\n", 1)]
    [InlineData("readonly HDS_VALUE=; HDS_VALUE=set cat </dev/null; printf 'after:%s:%s' \"$?\" \"$HDS_VALUE\"", "after:0:", "HDS_VALUE: readonly variable\n", 0)]
    [InlineData("unset E20_VALUE E20_SEEN; E20_VALUE=prefix read -r E20_SEEN <<E20_END\n${E20_VALUE:=redirected}\nE20_END\nprintf '%s:%s' \"$E20_VALUE\" \"$E20_SEEN\"", "redirected:redirected", "", 0)]
    [InlineData("unset E20_VALUE E20_SEEN; E20_PREFIX=prefix read -r E20_SEEN <<E20_END\n${E20_VALUE:=redirected}\nE20_END\nprintf '%s:%s' \"$E20_VALUE\" \"$E20_SEEN\"", "redirected:redirected", "", 0)]
    [InlineData("unset E20_VALUE; e20_fn(){ printf 'fn:%s' \"${E20_VALUE-unset}\"; }; E20_PREFIX=prefix e20_fn <<E20_END\n${E20_VALUE:=redirected}\nE20_END\nprintf 'after:%s' \"$E20_VALUE\"", "fn:redirectedafter:redirected", "", 0)]
    [InlineData("readonly E20_TARGET=old; declare -n E20_REF=E20_TARGET; E20_REF=new printf '%s' \"$E20_TARGET\"; printf 'after:%s:%s' \"$?\" \"$E20_TARGET\"", "oldafter:0:old", "E20_REF: readonly variable\n", 0)]
    [InlineData("readonly R=old; R=${missing:?stop} printf ok; printf ':after:%s' \"$?\"", "ok:after:0", "R: readonly variable\n", 0)]
    [InlineData("count=0; readonly R=old; R=$((count+=1)) NEXT=$(printf later >&2) printf '<%s>' \"$R\"; printf ':count=%s' \"$count\"", "<old>:count=0", "R: readonly variable\nlater", 0)]
    [InlineData("readonly R=old; R+=$(printf wrong >&2) printf ok", "ok", "R: readonly variable\n", 0)]
    [InlineData("readonly R=old; R=(${missing:?stop}) printf ok", "ok", "R: readonly variable\n", 0)]
    [InlineData("readonly R=old; declare -n ref=R; ref=${missing:?stop} printf ok", "ok", "ref: readonly variable\n", 0)]
    [InlineData("items=(old); declare -nr ref='items[0]'; ref=$(printf wrong >&2) printf ok", "ok", "ref: readonly variable\n", 0)]
    [InlineData("items=(old); readonly items; declare -n ref='items[$(printf selector >&2; printf 0)]'; ref=$(printf value >&2; printf new) sh -c 'printf \"<%s>\" \"$ref\"'", "<new>", "value", 0)]
    [InlineData("declare -n ref; ref=${ref:='items[0]'} sh -c 'printf \"<%s>\" \"$ref\"'", "<items[0]>", "", 0)]
    [InlineData("declare -n ref; ref=${ref:=target} sh -c 'printf \"<%s:%s>\" \"$ref\" \"$target\"'; declare -p ref", "<target:>declare -n ref=\"target\"\n", "", 0)]
    [InlineData("readonly target=old; declare -n ref; ref=${ref:=target} sh -c 'printf \"<%s>\" \"$ref\"'", "<target>", "", 0)]
    [InlineData("target=old; declare -n ref; ref+=${ref:=target} sh -c 'printf \"<%s:%s>\" \"$ref\" \"$target\"'", "<targettarget:>", "", 0)]
    [InlineData("declare -n inner; declare -n ref=inner; ref+=${inner:=target} sh -c 'printf \"<%s:%s:%s>\" \"$ref\" \"$inner\" \"$target\"'", "<targettarget::>", "", 0)]
    [InlineData("target=old; declare -n ref=target; ref=new sh -c 'printf \"<%s:%s>\" \"$ref\" \"$target\"'", "<:new>", "", 0)]
    [InlineData("x=1; x+=$((x=2)) sh -c 'printf \"<%s>\" \"$x\"'; printf '<%s>' \"$x\"", "<22><2>", "", 0)]
    [InlineData("count=0; target=old; declare -nr ref=target; ref=$((count+=1)) :; printf '<%s:%s>' \"$count\" \"$target\"", "<1:old>", "", 0)]
    [InlineData("target=old; declare -nr ref=target; ref=new sh -c 'printf \"<%s:%s>\" \"$ref\" \"$target\"'; declare -p ref target", "<:new>declare -nr ref=\"target\"\ndeclare -- target=\"old\"\n", "", 0)]
    [InlineData("target=1; declare -nr ref=target; ref+=$((target=2)) sh -c 'printf \"<%s>\" \"$target\"'; printf '<%s>' \"$target\"", "<22><2>", "", 0)]
    [InlineData("readonly target=old; declare -nr ref=target; ref=${missing:?stop} printf ok", "ok", "ref: readonly variable\n", 0)]
    [InlineData("declare -n inner; declare -nr ref=inner; ref=new eval 'ref=later; printf \"<%s>\" \"$ref\"'; declare -p ref inner", "<later>declare -nr ref=\"inner\"\ndeclare -n inner\n", "", 0)]
    [InlineData("items=(old); readonly items; declare -n inner='items[0]'; declare -nr ref=inner; ref+=new sh -c 'printf \"<%s>\" \"$ref\"'; declare -p ref inner", "<items[0]new>declare -nr ref=\"inner\"\ndeclare -n inner=\"items[0]\"\n", "", 0)]
    [InlineData("declare -nr inner='items[0]'; declare -n ref=inner; ref=${missing:?stop} printf ok", "ok", "ref: readonly variable\n", 0)]
    public async Task PrefixEffectsPrecedeRedirectionsAndReadonlyWritesDoNotSuppressCommands(
        string script, string output, string error, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }
}

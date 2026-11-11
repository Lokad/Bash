using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class CommandControlTestsEndToEnd
{

    [Fact]
    public async Task ExecDirectoryChangesRetainPrefixAssignmentsAndHonorReadonlyVariables()
    {
        var result = await EvaluationTestHelper.RunAsync(
            """exec eval 'PWD=/temporary OLDPWD=/ignored cd /target; """
            + """printf "<%s:%s>" "$PWD" "$OLDPWD"; readonly PWD OLDPWD; cd /; """
            + """printf "<%s:%s:%s>" "$PWD" "$OLDPWD" "$(pwd)"'""");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("</target:/temporary></target:/temporary:/>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("printf -v 'items[$(sh -c \"$code\" >&2; printf 0)]' %s value")]
    [InlineData("read 'items[$(sh -c \"$code\" >&2; printf 0)]' <<< value")]
    [InlineData("unset 'items[$(sh -c \"$code\" >&2; printf 0)]'")]
    public async Task ExecBuiltinSelectorsUseClearedExportsWithoutChangingTheCaller(string command)
    {
        // Virtual exec can select builtins. Their runtime selectors must use the same fork as eval/source.
        var result = await EvaluationTestHelper.RunAsync(
            "export X=outer; code='printf \"<%s>\" \"${X-unset}\"'; "
            + "f(){ local +x X=inner; items=(old); (exec -c " + command + "); sh -c \"$code\" >&2; }; f");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("<unset><outer>", result.StdErr);
    }

    [Theory]
    [InlineData("builtin -- echo", "\n", "", 0)]
    [InlineData("command() { printf '%s:%s\\n' \"$1\" \"$2\"; }; command builtin echo", "builtin:echo\n", "", 0)]
    [InlineData("command -vx echo", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("command -v -x echo", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("command -vV echo", "echo is a shell builtin\n", "", 0)]
    [InlineData("command -Vv echo", "echo\n", "", 0)]
    [InlineData("command -v -V echo", "echo is a shell builtin\n", "", 0)]
    [InlineData("command -V -v echo", "echo\n", "", 0)]
    [InlineData("command -vv echo", "echo\n", "", 0)]
    [InlineData("command -VV echo", "echo is a shell builtin\n", "", 0)]
    [InlineData("command -Vvx echo", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("command -V -v -x echo", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("command -v -- echo", "echo\n", "", 0)]
    [InlineData("command -v -- -x; printf '%s\\n' \"$?\"", "1\n", "", 0)]
    [InlineData("command -v echo -x; printf '%s\\n' \"$?\"", "echo\n0\n", "", 0)]
    [InlineData("command -v -v; printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("command -v '' echo; printf '%s\\n' \"$?\"", "echo\n0\n", "", 0)]
    [InlineData("opts=(-v -V echo); command \"${opts[@]}\"", "echo is a shell builtin\n", "", 0)]
    [InlineData("args=(-V echo printf); command \"${args[@]}\"", "echo is a shell builtin\nprintf is a shell builtin\n", "", 0)]
    [InlineData("command $(false) -V echo", "echo is a shell builtin\n", "", 0)]
    [InlineData("args=(command -V echo); \"${args[@]}\"", "echo is a shell builtin\n", "", 0)]
    [InlineData("opts=(-v -x echo); command \"${opts[@]}\"", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("command -v $(false) -V echo", "echo is a shell builtin\n", "", 0)]
    [InlineData("x=0; command -v -x \"$((x++))\"; printf '%s:%s\\n' \"$?\" \"$x\"", "2:1\n", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("value=$(printf prefix >&2) command -v -x \"$(printf word >&2)\"; printf '%s\\n' \"$?\"", "2\n", "wordprefixcommand: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("command -v -x echo 2>&1; printf '%s\\n' \"$?\"", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n2\n", "", 0)]
    [InlineData("command -v -x \"${missing:?late}\"; printf after", "", "missing: late\n", 1)]
    [InlineData("x=0; opts=(-v -v); command \"${opts[@]}\" \"$((x++))\" echo; printf '%s:%s\\n' \"$?\" \"$x\"", "echo\n0:1\n", "", 0)]
    [InlineData("builtin command -v -V echo", "echo is a shell builtin\n", "", 0)]
    [InlineData("command command -Vv echo", "echo\n", "", 0)]
    [InlineData("command -- declare var='a b'; printf '%s\\n' \"$var\"", "a b\n", "", 0)]
    [InlineData("command -v +v echo", "echo\n", "", 0)]
    [InlineData("command -v - echo; printf '%s\\n' \"$?\"", "echo\n0\n", "", 0)]
    [InlineData("command -v --invalid echo", "", "command: --: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("builtin -- --", "", "builtin: --: not a shell builtin\n", 1)]
    [InlineData("! command -v -x echo", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("command -pv printf; command -Vp printf; command -p -- printf 'command:<%s>\\n' ok; builtin -- printf 'builtin:<%s>\\n' ok", "printf\nprintf is a shell builtin\ncommand:<ok>\nbuiltin:<ok>\n", "", 0)]
    [InlineData("command -pv printf", "printf\n", "", 0)]
    [InlineData("command -vp printf", "printf\n", "", 0)]
    [InlineData("command -ppVp printf", "printf is a shell builtin\n", "", 0)]
    [InlineData("command -Vp -pv printf", "printf\n", "", 0)]
    [InlineData("command -vp -pV printf", "printf is a shell builtin\n", "", 0)]
    [InlineData("command -p echo ok", "ok\n", "", 0)]
    [InlineData("command -pp; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("command -p -- ''; printf '<%s>\\n' \"$?\"", "<127>\n", ": command not found\n", 0)]
    [InlineData("command -p printf '<%s>\\n' -p --", "<-p>\n<-->\n", "", 0)]
    [InlineData("command -pv printf -p", "printf\n", "", 0)]
    [InlineData("command -ppVpxv printf; printf '<%s>\\n' \"$?\"", "<2>\n", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("command -p -x \"$(printf effect >&2)\"; printf '<%s>\\n' \"$?\"", "<2>\n", "effectcommand: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("PATH=/definitely/missing command -p sh -c 'printf \"<%s>\\n\" \"$PATH\"'", "</definitely/missing>\n", "", 0)]
    [InlineData("sh(){ :; }; PATH=/definitely/missing command -pv sh", "sh\n", "", 0)]
    [InlineData("PATH=/definitely/missing command command -pv sh >/dev/null; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("PATH=/definitely/missing command -p command sh -c 'printf \"<%s>\\n\" \"$PATH\"'", "</definitely/missing>\n", "", 0)]
    [InlineData("echo(){ printf function; }; command -p echo ok", "ok\n", "", 0)]
    [InlineData("options='-p -V'; command $options printf", "printf is a shell builtin\n", "", 0)]
    [InlineData("command -p -x printf 2>&1", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", "", 2)]
    [InlineData("unset missing; command -p ${missing:?stop}; printf after", "", "missing: stop\n", 1)]
    [InlineData("exec -cx sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -lx sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -cl -x sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -a name -x sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -aname -x sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -a -x; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("exec -a; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -a: option requires an argument\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -ca; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -a: option requires an argument\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -a ''; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("exec -cc; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("exec -ll; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("exec -cl --; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("exec -a first -a second; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("exec -l sh -c 'printf \"<%s>\\n\" \"$0\"'; printf after", "<-sh>\n", "", 0)]
    [InlineData("exec -a custom sh -c 'printf \"<%s>\\n\" \"$0\"'; printf after", "<custom>\n", "", 0)]
    [InlineData("exec -la custom sh -c 'printf \"<%s>\\n\" \"$0\"'; printf after", "<-custom>\n", "", 0)]
    [InlineData("export MARKER=outer; MARKER=prefix exec -c sh -c 'printf \"<%s>\\n\" \"${MARKER-unset}\"'; printf after", "<unset>\n", "", 0)]
    [InlineData("value=0; exec -cx \"$((value++))\"; printf '<%s:%s>\\n' \"$?\" \"$value\"", "<2:1>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("value=0; exec -a \"$((value++))\" -x \"$((value++))\"; printf '<%s:%s>\\n' \"$?\" \"$value\"", "<2:2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("options='-a name -x'; exec $options sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -a name -x sh 2>&1; printf '<%s>\\n' \"$?\"", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n<2>\n", "", 0)]
    [InlineData("exec -a name -x \"${missing:?stop}\"; printf after", "", "missing: stop\n", 1)]
    [InlineData("command -v printf --help", "printf\n", "", 0)]
    [InlineData("exec -a --help; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    [InlineData("value=0; command --help \"$((value++))\" >/dev/null; printf '<%s:%s>\\n' \"$?\" \"$value\"", "<2:1>\n", "", 0)]
    [InlineData("MARKER=$(printf prefix >&2) exec -c --help \"$(printf word >&2)\" >/dev/null; printf '<%s>\\n' \"$?\"", "", "wordprefix", 0)]
    [InlineData("exec --help \"${missing:?stop}\" >/dev/null; printf after", "", "missing: stop\n", 1)]
    [InlineData("builtin --help >/dev/null 2>&1; printf '<%s>\\n' \"$?\"", "<2>\n", "", 0)]
    [InlineData("command -v -p --help >/dev/null; printf '<%s>\\n' \"$?\"", "<2>\n", "", 0)]
    [InlineData("exec -a --help -cx sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -a '' sh -c 'printf \"<%s>\\n\" \"$0\"'", "<>\n", "", 0)]
    [InlineData("exec -claname sh -c 'printf \"<%s>:<%s>\\n\" \"$0\" \"${MARKER-unset}\"'", "<-name>:<unset>\n", "", 0)]
    [InlineData("exec -a first -a second sh -c 'printf \"<%s>\\n\" \"$0\"'", "<second>\n", "", 0)]
    [InlineData("export MARKER=outer; exec -ca name; printf '<%s:%s>\\n' \"$?\" \"$MARKER\"", "<0:outer>\n", "", 0)]
    [InlineData("value='alias -x'; exec -a $value sh; printf '<%s>\\n' \"$?\"", "<2>\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("value='$missing'; exec -a \"$value\" sh -c 'printf \"<%s>\\n\" \"$0\"'", "<$missing>\n", "", 0)]
    [InlineData("exec -c -a name 2>&1; printf error >&2; printf '<%s>\\n' \"$?\"", "error<0>\n", "", 0)]
    [InlineData("exec -clx sh 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\nafter", "", 0)]
    [InlineData("exec -cl --help >/dev/null 2>&1; printf after >&2", "", "", 0)]
    [InlineData("command exec -cx sh 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\nafter", "", 0)]
    [InlineData("builtin exec -cx sh 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", "after", 0)]
    [InlineData("command builtin exec -cx sh 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", "after", 0)]
    [InlineData("builtin command exec -cx sh 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", "after", 0)]
    [InlineData("command exec --help >/dev/null 2>&1; printf after >&2", "", "", 0)]
    [InlineData("builtin exec --help >/dev/null 2>&1; printf after >&2", "", "after", 0)]
    [InlineData("exec -ca 2>&1; printf after >&2", "exec: -a: option requires an argument\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\nafter", "", 0)]
    [InlineData("builtin exec -ca 2>&1; printf after >&2", "exec: -a: option requires an argument\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", "after", 0)]
    // Help describes the hosted subset; its full text is intentionally not Bash's process documentation.
    [InlineData("builtin --help", "builtin: builtin [shell-builtin [arg ...]]\n    Execute a supported shell builtin, bypassing functions.\n", "", 2)]
    [InlineData("command --help", "command: command [-pVv] command [arg ...]\n    Run a command without function lookup, or describe its resolution.\n", "", 2)]
    [InlineData("exec --help", "exec: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n    Replace this evaluation with a command, or preserve redirections without a command.\n", "", 2)]
    // Platform extension: exec can select evaluator builtins; native Bash requires executable files.
    [InlineData("exec command; printf after", "", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c exec sh -c \"$code\"; printf after", "<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c builtin eval 'sh -c \"$code\"'; printf after", "<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c eval 'sh -c \"$code\"'; printf after", "<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c eval 'export MARKER=inner; sh -c \"$code\"'; printf after", "<inner>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c eval 'MARKER=inner sh -c \"$code\"'; printf after", "<inner>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c eval 'printf \"<%s>\\n\" \"$MARKER\"; sh -c \"$code\"'", "<outer>\n<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c eval 'value=$(sh -c \"$code\"); printf \"%s\\n\" \"$value\"'", "<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c eval 'sh -c \"$code\" | cat'", "<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; exec -c eval '(sh -c \"$code\")'", "<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; f(){ local MARKER=local; exec -c eval 'printf \"<%s>\\n\" \"$MARKER\"; sh -c \"$code\"'; }; f", "<local>\n<unset>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; value=$(exec -c eval 'sh -c \"$code\"'); printf \"%s\\n\" \"$value\"; sh -c \"$code\"", "<unset>\n<outer>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"$0\"'; exec -a custom eval 'printf \"<%s>\\n\" \"$0\"; sh -c \"$code\"'", "<custom>\n<sh>\n", "", 0)]
    [InlineData("exec -a '' eval 'printf \"<%s>\\n\" \"$0\"'", "<>\n", "", 0)]
    [InlineData("exec -la custom eval 'printf \"<%s>\\n\" \"$0\"'", "<-custom>\n", "", 0)]
    [InlineData("exec eval 'printf \"<%s>\\n\" \"$0\"'", "<eval>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"$0\"'; exec -la outer exec sh -c \"$code\"", "<sh>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"$0\"'; exec -a outer exec -la inner sh -c \"$code\"", "<-inner>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"$0\"'; exec -a custom command builtin command sh -c \"$code\"", "<custom>\n", "", 0)]
    [InlineData("code='printf \"<%s>\\n\" \"${MARKER-unset}\"'; export MARKER=outer; printf '%s\\n' 'sh -c \"$code\"' >/script; exec -c source /script; printf after", "<unset>\n", "", 0)]
    [InlineData("printf '%s\\n' 'printf \"<%s>\\n\" \"$0\"' >/script; exec -a custom source /script", "<custom>\n", "", 0)]
    // Native control: a prefix PWD value does not change the inherited working directory.
    [InlineData("export START=$PWD; PWD=/wrong sh -c '[ \"$(pwd)\" = \"$START\" ]'; printf '<%s>\\n' \"$?\"", "<0>\n", "", 0)]
    public async Task CommandControl_PreservesFieldsOrderAndOutcomes(
        string script,
        string expectedOutput,
        string expectedError,
        int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }
}

using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class CommandControlFailureTestsEndToEnd
{
    [Theory]
    [InlineData("builtin -v echo", "", "builtin: -v: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 2)]
    [InlineData("builtin -xyz echo", "", "builtin: -x: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 2)]
    [InlineData("builtin --invalid echo", "", "builtin: --: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 2)]
    [InlineData("builtin -", "", "builtin: -: not a shell builtin\n", 1)]
    [InlineData("builtin +v", "", "builtin: +v: not a shell builtin\n", 1)]
    [InlineData("builtin ''", "", "builtin: : not a shell builtin\n", 1)]
    [InlineData("builtin -- -v echo", "", "builtin: -v: not a shell builtin\n", 1)]
    [InlineData("x=0; builtin -v \"$((x++))\"; printf '%s:%s\\n' \"$?\" \"$x\"", "2:1\n", "builtin: -v: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 0)]
    [InlineData("x=0; builtin missing \"$((x++))\"; printf '%s:%s\\n' \"$?\" \"$x\"", "1:1\n", "builtin: missing: not a shell builtin\n", 0)]
    [InlineData("builtin missing \"${missing:?late}\"; printf after", "", "missing: late\n", 1)]
    [InlineData("builtin -v \"$(printf argument >&2)\"; printf ':%s\\n' \"$?\"", ":2\n", "argumentbuiltin: -v: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 0)]
    [InlineData("builtin missing 2>&1; printf 'status:%s\\n' \"$?\"", "builtin: missing: not a shell builtin\nstatus:1\n", "", 0)]
    [InlineData("builtin missing 2>&-; printf '%s\\n' \"$?\"", "1\n", "", 0)]
    [InlineData("value=${missing:?prefix} builtin missing; printf after", "", "missing: prefix\n", 1)]
    [InlineData("x=0; value=$((x++)) builtin missing \"$((x++))\"; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"${value-unset}\"", "1:2:unset\n", "builtin: missing: not a shell builtin\n", 0)]
    [InlineData("builtin missing 2>\"${missing:?redirect}\"; printf after", "", "missing: redirect\n", 1)]
    [InlineData("builtin command -x echo", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("command builtin -x echo", "", "builtin: -x: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 2)]
    [InlineData("builtin builtin missing", "", "builtin: missing: not a shell builtin\n", 1)]
    [InlineData("builtin() { printf '%s\\n' \"$1\"; }; builtin missing; command builtin missing; printf '%s\\n' \"$?\"", "missing\n1\n", "builtin: missing: not a shell builtin\n", 0)]
    [InlineData("x=0; command -x \"$((x++))\"; printf '%s:%s\\n' \"$?\" \"$x\"", "2:1\n", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("command -x echo 2>&1", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", "", 2)]
    [InlineData("command - echo ok", "", "-: command not found\n", 127)]
    [InlineData("command +v", "", "+v: command not found\n", 127)]
    [InlineData("command --invalid echo", "", "command: --: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("command -- -x", "", "-x: command not found\n", 127)]
    [InlineData("! builtin -v echo", "", "builtin: -v: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 0)]
    [InlineData("! builtin missing", "", "builtin: missing: not a shell builtin\n", 0)]
    [InlineData("exec -x echo; printf 'after:%s\\n' \"$?\"", "after:2\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("builtin exec -x echo; printf 'after:%s\\n' \"$?\"", "after:2\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("builtin missing 9>&- 1>&9; printf '%s\\n' \"$?\"", "1\n", "9: Bad file descriptor\n", 0)]
    [InlineData("command -x 9>&- 1>&9; printf '%s\\n' \"$?\"", "1\n", "9: Bad file descriptor\n", 0)]
    [InlineData("builtin missing \"$(printf arg >&2)\" 2>&1; printf '%s\\n' \"$?\"", "builtin: missing: not a shell builtin\n1\n", "arg", 0)]
    [InlineData("args=(-x echo); command \"${args[@]}\"", "", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 2)]
    [InlineData("args=(-v echo); builtin \"${args[@]}\"", "", "builtin: -v: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 2)]
    [InlineData("builtin $(false) -x echo", "", "builtin: -x: invalid option\nbuiltin: usage: builtin [shell-builtin [arg ...]]\n", 2)]
    [InlineData("args=(\"${missing:-literal*}\"); builtin \"${args[@]}\"", "", "builtin: literal*: not a shell builtin\n", 1)]
    [InlineData("builtin -- --invalid", "", "builtin: --invalid: not a shell builtin\n", 1)]
    [InlineData("value=$(printf prefix >&2) builtin missing \"${missing:?argument}\"; printf after", "", "missing: argument\n", 1)]
    [InlineData("command -x echo 2>&-; printf '%s\\n' \"$?\"", "2\n", "", 0)]
    [InlineData("x=0; value=$((x++)) command -x \"$((x++))\"; printf '%s:%s:%s\\n' \"$?\" \"$x\" \"${value-unset}\"", "2:2:unset\n", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    [InlineData("x=0; exec -x \"$((x++))\"; printf '%s:%s\\n' \"$?\" \"$x\"", "2:1\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("exec -x echo 2>&1; printf '%s\\n' \"$?\"", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n2\n", "", 0)]
    [InlineData("! exec -x echo; printf '%s\\n' \"$?\"", "0\n", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", 0)]
    [InlineData("builtin ]", "", "builtin: ]: not a shell builtin\n", 1)]
    [InlineData("builtin \"$(printf target >&2; printf missing)\" \"$(printf trailing >&2)\"", "", "targettrailingbuiltin: missing: not a shell builtin\n", 1)]
    [InlineData("exec -x echo 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\nafter", "", 0)]
    [InlineData("command exec -x echo 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\nafter", "", 0)]
    [InlineData("builtin exec -x echo 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", "after", 0)]
    [InlineData("builtin command exec -x echo 2>&1; printf after >&2", "exec: -x: invalid option\nexec: usage: exec [-cl] [-a name] [command [argument ...]] [redirection ...]\n", "after", 0)]
    [InlineData("exec -x \"${missing:?late}\"; printf after", "", "missing: late\n", 1)]
    [InlineData("exec -x 9>&- 1>&9; printf '%s\\n' \"$?\"", "1\n", "9: Bad file descriptor\n", 0)]
    [InlineData("command -vx echo 2>&1", "command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", "", 2)]
    [InlineData("command -V -x \"${missing:?late}\"; printf after", "", "missing: late\n", 1)]
    [InlineData("command -v -x \"$(printf argument >&2)\"; printf ':%s\\n' \"$?\"", ":2\n", "argumentcommand: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", 0)]
    public async Task CommandControlFailure_PreservesExpansionOrderAndDiagnostics(
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

    [Theory]
    [InlineData("builtin $'\\xff'", "6275696C74696E3A20FF3A206E6F742061207368656C6C206275696C74696E0A", 1)]
    [InlineData("builtin $'-\\xff'", "6275696C74696E3A202DFF3A20696E76616C6964206F7074696F6E0A6275696C74696E3A2075736167653A206275696C74696E205B7368656C6C2D6275696C74696E205B617267202E2E2E5D5D0A", 2)]
    [InlineData("command $'-\\xff'", "636F6D6D616E643A202DFF3A20696E76616C6964206F7074696F6E0A636F6D6D616E643A2075736167653A20636F6D6D616E64205B2D7056765D20636F6D6D616E64205B617267202E2E2E5D0A", 2)]
    public async Task FailureDiagnosticsPreserveArgumentBytes(string script, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }
}

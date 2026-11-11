using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class CommandSelectionTestsEndToEnd
{

    [Fact]
    public async Task Evaluation_CommandInvocation_CommandPositionPreservesAggregateFields()
    {
        var script = @"command=(echo hello); ""${command[@]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hello\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("builtin")]
    public async Task Evaluation_CommandWrapper_TargetPositionPreservesAggregateFields(string wrapper)
    {
        var script = $@"arguments=(echo hello); {wrapper} ""${{arguments[@]}}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hello\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_EmptyCommandAggregatePromotesNextWord()
    {
        var script = @"command=(); value=assigned ""${command[@]}"" echo ""$value""; echo ""${value-unset}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\nunset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_AllEmptyCommandFieldsCommitAssignments()
    {
        var script = @"command=(); value=assigned ""${command[@]}""; echo ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("assigned\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("$(false); printf '%s\\n' \"$?\"", "1\n", "", 0)]
    [InlineData("a=old; a=new $(false); printf '%s:%s\\n' \"$?\" \"$a\"", "1:new\n", "", 0)]
    [InlineData("$(false) true; printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("$(false) $(true); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("a=$(false) $(true); printf '%s\\n' \"$?\"", "1\n", "", 0)]
    [InlineData("a=$(true) $(false); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("a=old; a=new $missing; printf '%s:%s\\n' \"$?\" \"$a\"", "0:new\n", "", 0)]
    [InlineData("\"$(false)\"; printf '%s\\n' \"$?\"", "127\n", ": command not found\n", 0)]
    [InlineData("$(false)\"\" true; printf '%s\\n' \"$?\"", "127\n", ": command not found\n", 0)]
    [InlineData("unset empty; $empty printf '%s\\n' ok", "ok\n", "", 0)]
    [InlineData("cmd='printf <%s>\\\\n hello'; $cmd world", "<hello>\\n<world>\\n", "", 0)]
    [InlineData("IFS=:; cmd='printf:%s\\n:value'; $cmd", "value\n", "", 0)]
    [InlineData("IFS=; cmd='true false'; $cmd; printf '%s\\n' \"$?\"", "127\n", "true false: command not found\n", 0)]
    [InlineData("set -- printf '<%s>\\n' 'a b' c; \"$@\"", "<a b>\n<c>\n", "", 0)]
    [InlineData("set --; a=old; a=new \"$@\"; printf '%s:%s\\n' \"$?\" \"$a\"", "0:new\n", "", 0)]
    [InlineData("set --; \"$@\" echo ok", "ok\n", "", 0)]
    [InlineData("command=(\"\"); \"${command[@]}\" true; printf '%s\\n' \"$?\"", "127\n", ": command not found\n", 0)]
    [InlineData("cmd=printf; \"${cmd[@]}\" '%s\\n' ok", "ok\n", "", 0)]
    [InlineData("{printf,hello} '%s\\n'", "hello", "", 0)]
    [InlineData("$(printf 'printf %%s\\\\n hello') world", "hello\nworld\n", "", 0)]
    [InlineData("fn() { printf '%s:<%s>\\n' \"$#\" \"$1\"; }; cmd=(fn \"a b\"); \"${cmd[@]}\"", "1:<a b>\n", "", 0)]
    [InlineData("x=0; choose() { printf printf; }; \"$(choose)\" '%s\\n' \"$((x++))\"; printf 'x=%s\\n' \"$x\"", "0\nx=1\n", "", 0)]
    [InlineData("x=0; ${unset-$((x++))}false; printf '%s:%s\\n' \"$?\" \"$x\"", "127:1\n", "0false: command not found\n", 0)]
    [InlineData("unset empty; x=0; $empty ${cmd:=printf} '%s\\n' \"$((x++))\"; printf '%s:%s\\n' \"$cmd\" \"$x\"", "0\nprintf:1\n", "", 0)]
    [InlineData("cmd=(printf '%s\\n' '$(echo BAD)' '${missing:?BAD}' 'a*b'); \"${cmd[@]}\"", "$(echo BAD)\n${missing:?BAD}\na*b\n", "", 0)]
    [InlineData("a=old; cmd=(printf '<%s>\\n'); a=new \"${cmd[@]}\" \"$a\"; printf '%s\\n' \"$a\"", "<old>\nold\n", "", 0)]
    [InlineData("${missing:?error} \"$(printf touched >&2)\"; printf after", "", "missing: error\n", 1)]
    [InlineData("$(false) ${missing:?error} \"$(printf touched >&2)\"; printf after", "", "missing: error\n", 1)]
    [InlineData("cmd=(echo); \"${cmd[@]}\" \"${missing:?error}\" \"$(printf touched >&2)\"; printf after", "", "missing: error\n", 1)]
    [InlineData("value=${missing:?error} $(false); printf after", "", "missing: error\n", 1)]
    [InlineData("! $(false); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("$(false) && printf bad; printf '%s\\n' \"$?\"", "1\n", "", 0)]
    [InlineData("$(false) || printf ok; printf ':%s\\n' \"$?\"", "ok:0\n", "", 0)]
    [InlineData("cmd=(printf \"%s\\n\" value); \"${cmd[@]}\" | cat", "value\n", "", 0)]
    [InlineData("command $(false); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("command $(false) echo ok; printf '%s\\n' \"$?\"", "ok\n0\n", "", 0)]
    [InlineData("target='echo one'; command $target two", "one two\n", "", 0)]
    [InlineData("args=(-- echo hello); command \"${args[@]}\"", "hello\n", "", 0)]
    [InlineData("builtin $(false); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("builtin $(false) echo ok; printf '%s\\n' \"$?\"", "ok\n0\n", "", 0)]
    [InlineData("target='echo one'; builtin $target two", "one two\n", "", 0)]
    [InlineData("args=(-- echo hello); builtin \"${args[@]}\"", "hello\n", "", 0)]
    [InlineData("arguments=(echo hello); exec \"${arguments[@]}\"", "hello\n", "", 0)]
    [InlineData("exec $(false); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("exec $(false) echo ok; printf '%s\\n' \"$?\"", "ok\n", "", 0)]
    [InlineData("target='echo one'; exec $target two", "one two\n", "", 0)]
    [InlineData("args=(-- echo hello); exec \"${args[@]}\"", "hello\n", "", 0)]
    [InlineData("args=(-v echo printf); command \"${args[@]}\"", "echo\nprintf\n", "", 0)]
    [InlineData("command $(false) -v echo; printf '%s\\n' \"$?\"", "echo\n0\n", "", 0)]
    [InlineData("target=(command -- echo hello); builtin \"${target[@]}\"", "hello\n", "", 0)]
    [InlineData("echo() { printf function; }; args=(echo builtin); command \"${args[@]}\"", "builtin\n", "", 0)]
    [InlineData("args=(command echo hello); \"${args[@]}\"", "hello\n", "", 0)]
    [InlineData("x=0; cmd=command; $cmd ${target:=printf} '%s:%s\\n' \"$((x++))\" \"$((x++))\"; printf '%s:%s\\n' \"$x\" \"$target\"", "0:1\n2:printf\n", "", 0)]
    [InlineData("builtin --; printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("command -- $(false); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("exec -- $(false); printf '%s\\n' \"$?\"", "0\n", "", 0)]
    [InlineData("cmd='printf\t%s\\n\tvalue'; $cmd", "value\n", "", 0)]
    [InlineData("command=(); command[2]=printf; command[7]='<%s>\\n'; command[9]='a b'; \"${command[@]}\" c", "<a b>\n<c>\n", "", 0)]
    [InlineData("a=old; a=new command $(false); printf '%s:%s\\n' \"$?\" \"$a\"", "0:old\n", "", 0)]
    [InlineData("a=old; a=new builtin $(false); printf '%s:%s\\n' \"$?\" \"$a\"", "0:old\n", "", 0)]
    [InlineData("cmd=(printf '%s\\n' '${value:=BAD}'); \"${cmd[@]}\"; printf '%s\\n' \"${value-unset}\"", "${value:=BAD}\nunset\n", "", 0)]
    [InlineData("$(false) '' true; printf '%s\\n' \"$?\"", "127\n", ": command not found\n", 0)]
    [InlineData("cmd=' \t '; a=old; a=new $cmd; printf '%s:%s\\n' \"$?\" \"$a\"", "0:new\n", "", 0)]
    [InlineData("IFS=; unset cmd; a=new $cmd; printf '%s:%s\\n' \"$?\" \"$a\"", "0:new\n", "", 0)]
    [InlineData("command $(false) -- $(false) echo ok", "ok\n", "", 0)]
    [InlineData("builtin $(false) -- $(false) echo ok", "ok\n", "", 0)]
    [InlineData("exec $(false) -- $(false) echo ok", "ok\n", "", 0)]
    // Matches Bash with pipefail enabled, the engine's inherited pipeline policy.
    [InlineData("a=old; a=new $(false) | cat; printf '%s:%s\\n' \"$?\" \"$a\"", "1:old\n", "", 0)]
    public async Task Evaluation_CommandSelection_PreservesFieldsStatusAndExpansionOrder(
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
    [InlineData("ech* tail", "echo-z tail\n", "", 0)]
    [InlineData("command ech* tail", "echo-z tail\n", "", 0)]
    [InlineData("builtin ech* tail", "echo-z tail\n", "", 0)]
    [InlineData("[ec]cho tail", "tail\n", "", 0)]
    [InlineData("\"ech*\" tail", "", "ech*: command not found\n", 127)]
    [InlineData("ech\\* tail", "", "ech*: command not found\n", 127)]
    [InlineData("args=(echo \"*\"); \"${args[@]}\"", "*\n", "", 0)]
    public async Task Evaluation_CommandSelection_ExpandsPathnamesOnce(
        string script,
        string expectedOutput,
        string expectedError,
        int expectedExitCode)
    {
        var tree = EndToEndHarness.BuildTree([("/echo", ""), ("/echo-z", "")]);
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }
}

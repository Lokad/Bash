using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class FunctionNameTestsEndToEnd
{
    // Git Bash 5.2.37, empty environment; remove only script-path/line diagnostic prefixes.
    [Theory]
    [InlineData("function probe-f { printf '<ran>\\n'; };\ndeclare -F -- 'probe-f';\n'probe-f';\nprintf 'status=%s\\n' \"$?\"", "probe-f\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("probe-f() { printf '<ran>\\n'; };\ndeclare -F -- 'probe-f';\n'probe-f';\nprintf 'status=%s\\n' \"$?\"", "probe-f\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("probe.f() { printf '<ran>\\n'; };\ndeclare -F -- 'probe.f';\n'probe.f';\nprintf 'status=%s\\n' \"$?\"", "probe.f\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("probe/f() { printf '<ran>\\n'; };\ndeclare -F -- 'probe/f';\n'probe/f';\nprintf 'status=%s\\n' \"$?\"", "probe/f\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("123() { printf '<ran>\\n'; };\ndeclare -F -- '123';\n'123';\nprintf 'status=%s\\n' \"$?\"", "123\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("function -f { printf '<ran>\\n'; };\ndeclare -F -- '-f';\n'-f';\nprintf 'status=%s\\n' \"$?\"", "-f\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("function a=b { printf '<ran>\\n'; };\ndeclare -F -- 'a=b';\n'a=b';\nprintf 'status=%s\\n' \"$?\"", "<ran>\nstatus=0\n", "declare: cannot use `-f' to make functions\n", 0)]
    [InlineData("a[1]() { printf '<ran>\\n'; };\ndeclare -F -- 'a[1]';\n'a[1]';\nprintf 'status=%s\\n' \"$?\"", "a[1]\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("function 'two words' { printf '<ran>\\n'; };\ndeclare -F -- 'two words';\n'two words';\nprintf 'status=%s\\n' \"$?\"", "status=127\n", "`'two words'': not a valid identifier\ntwo words: command not found\n", 0)]
    [InlineData("'probe-f'() { printf '<ran>\\n'; };\ndeclare -F -- 'probe-f';\n'probe-f';\nprintf 'status=%s\\n' \"$?\"", "status=127\n", "`'probe-f'': not a valid identifier\nprobe-f: command not found\n", 0)]
    [InlineData("probe\\-f() { printf '<ran>\\n'; };\ndeclare -F -- 'probe-f';\n'probe-f';\nprintf 'status=%s\\n' \"$?\"", "status=127\n", "`probe\\-f': not a valid identifier\nprobe-f: command not found\n", 0)]
    [InlineData("function ! { printf '<ran>\\n'; };\ndeclare -F -- '!';\n'!';\nprintf 'status=%s\\n' \"$?\"", "!\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("@() { printf '<ran>\\n'; };\ndeclare -F -- '@';\n'@';\nprintf 'status=%s\\n' \"$?\"", "@\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("~() { printf '<ran>\\n'; };\ndeclare -F -- '~';\n'~';\nprintf 'status=%s\\n' \"$?\"", "~\n<ran>\nstatus=0\n", "", 0)]
    [InlineData("function a$b { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=1\n", "`a$b': not a valid identifier\n", 0)]
    [InlineData("a$b() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=1\n", "`a$b': not a valid identifier\n", 0)]
    [InlineData("function a$(printf effect) { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=1\n", "`a$(printf effect)': not a valid identifier\n", 0)]
    [InlineData("a`printf effect`() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=0\ndeclare -f a`printf effect`\n", "", 0)]
    [InlineData("1=x() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=0\ndeclare -f 1=x\n", "", 0)]
    [InlineData("a+b=c() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=0\ndeclare -f a+b=c\n", "", 0)]
    [InlineData("*() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=0\ndeclare -f *\n", "", 0)]
    [InlineData("a'b'() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=1\n", "`a'b'': not a valid identifier\n", 0)]
    [InlineData("function a\"b\" { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=1\n", "`a\"b\"': not a valid identifier\n", 0)]
    [InlineData("probe\\\n-f() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=0\ndeclare -f probe-f\n", "", 0)]
    [InlineData("é() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=0\ndeclare -f é\n", "", 0)]
    [InlineData("[() { :; }; printf 'definition=%s\\n' \"$?\"; declare -F", "definition=0\ndeclare -f [\n", "", 0)]
    [InlineData("probe-f(){ printf old; }; 'probe-f'; export -f -- probe-f; declare -Fp -- probe-f; (unset -f -- probe-f; declare -F -- probe-f); declare -Fp -- probe-f; unset -f -- probe-f; declare -Fp -- probe-f", "olddeclare -fx probe-f\ndeclare -fx probe-f\n", "declare: probe-f: not found\n", 1)]
    [InlineData("function -f { printf old; }; readonly -f -- -f; function -f { printf new; }; printf 'status=%s\\n' \"$?\"; '-f'; unset -f -- -f; printf 'status=%s\\n' \"$?\"; declare -Fp -- -f", "status=1\noldstatus=1\ndeclare -fr -f\n", "-f: readonly function\nunset: -f: cannot unset: readonly function\n", 0)]
    [InlineData("probe_f(){ printf old; }; 'probe_f'(){ printf new; }; printf 'status=%s\\n' \"$?\"; probe_f; declare -F", "status=1\nolddeclare -f probe_f\n", "`'probe_f'': not a valid identifier\n", 0)]
    [InlineData("false && 'bad-name'(){ printf bad; }; printf 'status=%s\\n' \"$?\"; ! 'bad-name'(){ :; }; printf 'negated=%s\\n' \"$?\"; declare -F", "status=1\nnegated=0\n", "`'bad-name'': not a valid identifier\n", 0)]
    [InlineData("i=0; function probe_$((i+=1)) { printf bad; }; printf 'status=%s i=%s\\n' \"$?\" \"$i\"; declare -F", "status=1 i=0\n", "`probe_$((i+=1))': not a valid identifier\n", 0)]
    [InlineData("function a=b { printf literal; }; 'a=b'; declare -Fp 'a=b'; declare -f 'a=b'; printf 'status=%s\\n' \"$?\"; export -f 'a=b'; declare -Fp 'a=b'; unset -f 'a=b'; declare -F", "literaldeclare -f a=b\nstatus=1\ndeclare -f a=b\n", "declare: cannot use `-f' to make functions\nexport: a=b: cannot export\n", 0)]
    [InlineData("probe-f \\\n( \\\n ) { printf yes; }; 'probe-f'", "yes", "", 0)]
    [InlineData("outer(){ 'bad-name'(){ :; }; printf 'status=%s\\n' \"$?\"; }; declare -f outer; outer", "outer () \n{ \n    function 'bad-name' () \n    { \n        :\n    };\n    printf 'status=%s\\n' \"$?\"\n}\nstatus=1\n", "`'bad-name'': not a valid identifier\n", 0)]
    [InlineData("probe-f(){ :; }; declare -f probe-f; type probe-f", "probe-f () \n{ \n    :\n}\nprobe-f is a function\nprobe-f () \n{ \n    :\n}\n", "", 0)]
    [InlineData("function a`printf effect` { :; }; declare -f 'a`printf effect`'; 'a`printf effect`'; printf 'status=%s\\n' \"$?\"", "a`printf effect` () \n{ \n    :\n}\nstatus=0\n", "", 0)]
    [InlineData("function probe-f { :; }; export -f 'probe-f'; printf 'status=%s\\n' \"$?\"; declare -Fp 'probe-f'", "status=0\ndeclare -fx probe-f\n", "", 0)]
    [InlineData("function probe/f { :; }; export -f 'probe/f'; printf 'status=%s\\n' \"$?\"; declare -Fp 'probe/f'", "status=1\ndeclare -f probe/f\n", "export: probe/f: cannot export\n", 0)]
    [InlineData("function a=b { :; }; export -f 'a=b'; printf 'status=%s\\n' \"$?\"; declare -Fp 'a=b'", "status=1\ndeclare -f a=b\n", "export: a=b: cannot export\n", 0)]
    [InlineData("function probe/f { :; }; declare -fx probe/f; printf 'status=%s\\n' \"$?\"; declare -Fp probe/f", "status=0\ndeclare -fx probe/f\n", "", 0)]
    [InlineData("function probe/f { :; }; f(){ local -fx probe/f; }; f; printf 'status=%s\\n' \"$?\"; declare -Fp probe/f", "status=0\ndeclare -fx probe/f\n", "", 0)]
    [InlineData("function probe/f { :; }; export -fn probe/f; printf 'status=%s\\n' \"$?\"; declare -Fp probe/f", "status=0\ndeclare -f probe/f\n", "", 0)]
    [InlineData("function a='b' { :; }; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\n", "`a='b'': not a valid identifier\n", 0)]
    public async Task Evaluation_FunctionNames_PreserveLiteralGrammarAndLifecycle(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("a=b() { :; }")]
    [InlineData("a[1]=x() { :; }")]
    [InlineData("a='b'() { :; }")]
    [InlineData("if() { :; }")]
    [InlineData("!() { :; }")]
    public void Parse_FunctionNames_DoNotBroadenAssignmentsOrReservedGrammar(string script) =>
        Assert.Throws<SyntaxException>(() => Parser.Parse(script));
}

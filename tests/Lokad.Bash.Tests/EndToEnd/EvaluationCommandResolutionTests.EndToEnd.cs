using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationCommandResolutionTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_CommandBuiltin_EmitsRunCommand()
    {
        // This test verifies that "command" executes the target command.
        // The script "command echo hi" must output "hi\n", no STDERR, exit 0.
        // If command does not execute, STDOUT would be empty.
        var script = @"command echo hi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_NoArgumentsReturnsSuccess()
    {
        // This test verifies that "command" with no arguments succeeds.
        // The script "command" must emit no output and exit with code 0.
        // If missing arguments are treated as an error, the exit code would be 1.
        var script = @"command";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryReportsBuiltin()
    {
        var script = @"command -v echo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("echo\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_InvalidOptionReturnsFailure()
    {
        var result = await EvaluationTestHelper.RunAsync("command -x echo");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryUsesHostResolution()
    {
        var result = await EvaluationTestHelper.RunAsync("command -v cat");

        Assert.Equal("/usr/bin/cat\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandQueries_ContinueAfterReadonlyPrefixes()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "readonly VALUE; "
            + "VALUE=changed command -v echo; echo command:$?; "
            + "VALUE=changed type -t echo; echo type:$?");

        Assert.Equal("echo\ncommand:0\nbuiltin\ntype:0\n", result.StdOut);
        Assert.Equal("VALUE: readonly variable\nVALUE: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryReportsFunctionAndKeyword()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "f() { :; }; command -v f if");

        Assert.Equal("f\nif\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandQueries_ReportSymbolicKeywords()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "command -v '!' '{' '}' '[[' ']]'; type -t '!' '{' '}' '[[' ']]'");

        Assert.Equal(
            "!\n{\n}\n[[\n]]\nkeyword\nkeyword\nkeyword\nkeyword\nkeyword\n",
            result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryMissingNameIsSilent()
    {
        var result = await EvaluationTestHelper.RunAsync("command -v missing; echo status:$?");

        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QuerySucceedsWhenAnyNameResolves()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "command -v missing echo also-missing; echo status:$?");

        Assert.Equal("echo\nstatus:0\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryPreservesNestedRedirection()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "builtin command -v echo > /out; cat /out");

        Assert.Equal("echo\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_ReentersOutputBuiltinResolution()
    {
        var result = await EvaluationTestHelper.RunAsync("command pwd");

        Assert.Equal("/\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_ReentersStatefulBuiltinResolution()
    {
        var result = await EvaluationTestHelper.RunAsync("command read value; echo $value");

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UnknownCommand_ReportsNotFound()
    {
        var script = @"missing";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("missing: command not found\n", result.StdErr);
        Assert.Equal(127, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ReportsFunction()
    {
        // Bash prints the definition as well as the function classification.
        var script = @"foo() { :; }; type foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("foo is a function\nfoo () \n{ \n    :\n}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ReportsBuiltin()
    {
        // This test verifies that "type" reports builtins.
        // The script "type echo" must output "echo is a shell builtin\n".
        // If builtins are not detected, the output would say "not found".
        var script = @"type echo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("echo is a shell builtin\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_CompactReportsResolutionKinds()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "f() { :; }; type -t echo f if cat missing; echo status:$?");

        Assert.Equal("builtin\nfunction\nkeyword\nfile\nstatus:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("type -t")]
    [InlineData("type -t --")]
    public async Task Evaluation_TypeBuiltin_CompactWithoutNamesReturnsSuccess(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ReportsNotFound()
    {
        var script = @"type missing";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Equal("type: missing: not found\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_NoArgumentsReturnsSuccess()
    {
        var script = @"type";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_UsesBuiltinEvenWhenFunctionExists()
    {
        // This test verifies that "builtin" bypasses function overrides.
        // The script defines echo() to print "nope" and then runs "builtin echo hi".
        // STDOUT must be "hi\n".
        // If builtin does not bypass functions, STDOUT would be "nope\n".
        var script = @"echo() { command echo nope; }; builtin echo hi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionNamedBuiltinShadowsBuiltinCommand()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "builtin() { echo function:$1; }; type builtin; builtin arg; command builtin echo wrapped");

        Assert.Equal("builtin is a function\nbuiltin () \n{ \n    echo function:$1\n}\nfunction:arg\nwrapped\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_ReportsSuccessForColon()
    {
        // This test verifies that "builtin" can execute ":" successfully.
        // The script "builtin :" must emit no output and return exit code 0.
        // If builtin invocation fails, the exit code would be non-zero.
        var script = @"builtin :";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_ReportsFailureForUnknownName()
    {
        var script = @"builtin missing";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("builtin: missing: not a shell builtin\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_NoArgumentsReturnsSuccess()
    {
        var script = @"builtin";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_TerminatesAfterCommand()
    {
        // This test verifies that "exec" terminates the script after running the command.
        // The script "exec echo first; echo second" must output only "first\n".
        // If execution continues, STDOUT would include "second\n".
        var script = @"exec echo first; echo second";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("first\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_NoArgumentsReturnsSuccess()
    {
        // This test verifies that "exec" with no arguments succeeds.
        // The script "exec" must emit no output and exit with code 0.
        // If missing arguments are treated as an error, the exit code would be non-zero.
        var script = @"exec";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_ReentersBuiltinResolution()
    {
        var result = await EvaluationTestHelper.RunAsync("exec pwd; echo unreachable");

        Assert.Equal("/\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_TerminalNestedBuiltinStillTerminates()
    {
        // The engine permits exec of evaluator builtins; native exec requires an executable file.
        var result = await EvaluationTestHelper.RunAsync("exec builtin missing; echo unreachable");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("builtin: missing: not a shell builtin\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_ExpansionFailureStillTerminates()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "exec cat ${MISSING:?missing}; echo unreachable");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("MISSING: missing\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_ReadonlyPrefixStillRunsAndTerminates()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "readonly VALUE; VALUE=(item) exec cat; echo unreachable");

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal("VALUE: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("! exec false", 1)]
    [InlineData("! exec true", 0)]
    public async Task Evaluation_NegatedExecBuiltin_PreservesTargetStatus(
        string script,
        int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_NoArgumentExecPreservesRedirections()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "command exec 3>/out; echo persisted >&3");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("persisted\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Theory]
    [InlineData("exec", "marker\n")]
    [InlineData("command exec", "marker\n")]
    [InlineData("command command exec", "marker\n")]
    [InlineData("builtin exec", "")]
    [InlineData("builtin command exec", "")]
    [InlineData("command builtin exec", "")]
    [InlineData("builtin builtin exec", "")]
    public async Task Evaluation_NoArgumentExec_RedirectionLifetimeFollowsWrapperScope(
        string invocation,
        string expectedContent)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"{invocation} 3>/out; echo marker >&3; true");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(expectedContent.Length == 0 ? "3: Bad file descriptor\n" : string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedContent, await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_ClosingBracketIsNotACommandBuiltin()
    {
        var result = await EvaluationTestHelper.RunAsync("type ]; builtin ]; echo status:$?");

        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal("type: ]: not found\nbuiltin: ]: not a shell builtin\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionNamedExecDoesNotPreserveRedirections()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "exec() { echo function; }; exec >/out; echo outside; cat /out");

        Assert.Equal("outside\nfunction\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

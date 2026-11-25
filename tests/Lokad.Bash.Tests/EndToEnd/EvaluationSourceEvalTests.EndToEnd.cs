using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationSourceEvalTestsEndToEnd
{
    [Theory]
    [InlineData("PATH=/lib:other", "source item", "lib")]
    [InlineData("PATH=/lib:other", ". ./item", "cwd")]
    [InlineData("PATH=missing", ". item", "cwd")]
    [InlineData("PATH=:lib", "source item", "cwd")]
    [InlineData("PATH=other", "PATH=lib command . item", "lib")]
    [InlineData("HOME=lib; PATH='~'", "source item", "lib")]
    public async Task SourceSearchUsesTheReachedPathAndFilename(string setup, string invocation, string selected)
    {
        var host = EndToEndHarness.BuildTree([("/item", "printf cwd"), ("/lib/item", "printf lib"),
            ("/other/item", "printf other")]);
        var result = await EndToEndHarness.RunScriptAsync(setup + "; " + invocation, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(selected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("source -- ./missing", "./missing: No such file or directory\n")]
    [InlineData(". ''", ": No such file or directory\n")]
    [InlineData("source ./folder", "source: ./folder: is a directory\n")]
    [InlineData(". ./folder", ".: ./folder: is a directory\n")]
    [InlineData("source ./missing 2>&-", "")]
    public async Task SourceReportsReachedFilenameFailures(string invocation, string diagnostic)
    {
        var host = EndToEndHarness.BuildTree([("/folder/file", "")]);
        var result = await EndToEndHarness.RunScriptAsync(invocation, host);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("source ./missing", "./missing: No such file or directory")]
    [InlineData(". ./folder", ".: ./folder: is a directory")]
    public async Task SourceOpenFailureUsesTheRetainedFunctionSource(string invocation, string diagnostic)
    {
        var host = EndToEndHarness.BuildTree([
            ("/definition", "f() {\n  :\n  " + invocation + "\n}\n"), ("/folder/item", "")
        ]);
        var result = await EndToEndHarness.RunScriptAsync("source ./definition\n:\nf", host);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal($"./definition: line 3: {diagnostic}\n", result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("export x=body", "body")]
    [InlineData("readonly x=body", "body")]
    [InlineData("unset x; export x=body", "body")]
    [InlineData("unset x; readonly x=body", "body")]
    [InlineData("local x=local; unset x; export x=body", "global")]
    public async Task Evaluation_SourcePrefix_PromotesOnlySurvivingBindings(string content, string expectedOutput)
    {
        var tree = EndToEndHarness.BuildTree([("/script", content)]);
        var script = "x=global; f() { x=inner source /script; }; x=outer f; printf '%s' \"$x\"";

        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_EvaluatesReadContent()
    {
        // This test verifies that "source" reads a script file and evaluates its contents in place.
        // The file contains "echo hi"; sourcing it must emit "hi\n" and the following "echo after"
        // must also run, producing "after\n". STDERR must remain empty and exit code 0.
        // If the file is not evaluated, STDOUT would miss "hi\n" or "after\n".
        var tree = EndToEndHarness.BuildTree([
            ("/script", "echo hi\n")
        ]);
        var script = @"source /script; echo after";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("hi\nafter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DotBuiltin_EvaluatesReadContent()
    {
        // This test verifies that "." behaves like "source" and evaluates the referenced file.
        // The file contains "echo dot", so a correct run must emit "dot\n", no STDERR, exit code 0.
        // If the file is not executed, STDOUT would be empty.
        var tree = EndToEndHarness.BuildTree([
            ("/script", "echo dot\n")
        ]);
        var script = @". /script";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("dot\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("source")]
    [InlineData(".")]
    public async Task Evaluation_SourceBuiltin_UsesAndRestoresArguments(string builtin)
    {
        var tree = EndToEndHarness.BuildTree([
            ("/script", "echo sourced:$1:$2:$#\n")
        ]);
        var script = $"set -- outer; {builtin} /script inner more; echo after:$1:$#";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("sourced:inner:more:2\nafter:outer:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("set +", "<inner><outer>")]
    [InlineData("set + -- changed", "<changed><changed>")]
    public async Task Evaluation_SourceBarePlusRestoresTheFrameUnlessSetReplacesIt(string command, string expected)
    {
        var host = EndToEndHarness.BuildTree([("/unit", command + "; printf '<%s>' \"$@\"\n")]);
        var result = await EndToEndHarness.RunScriptAsync(
            "set -- outer; source ./unit inner; printf '<%s>' \"$@\"", host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_WithoutArgumentsKeepsCallerParameters()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/script", "echo sourced:$1:$#\n")
        ]);
        var script = "set -- outer; source /script; echo after:$1:$#";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("sourced:outer:1\nafter:outer:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_ArgumentExpansionErrorDoesNotEvaluateFile()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/script", "SIDE_EFFECT=changed\ntrue\n")
        ]);
        var script = "SIDE_EFFECT=outer; source /script ${MISSING:?missing} || echo source-failed; echo $SIDE_EFFECT";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Empty(result.StdOut);
        Assert.Equal("line 1: MISSING: missing\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("source")]
    [InlineData(".")]
    public async Task Evaluation_SourceBuiltin_SetPersistsReplacedArguments(string builtin)
    {
        var tree = EndToEndHarness.BuildTree([
            ("/script", "set -- changed more; echo sourced:$1:$2:$#\n")
        ]);
        var script = $"set -- outer; {builtin} /script inner; echo after:$1:$2:$#";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("sourced:changed:more:2\nafter:changed:more:2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_ShiftDoesNotReplaceCallerArguments()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/script", "shift; echo sourced:$1:$#\n")
        ]);
        var script = "set -- outer; source /script first second; echo after:$1:$#";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("sourced:second:1\nafter:outer:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_FunctionSetDoesNotReplaceCallerArguments()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/script", "f() { set -- function; }; f call; echo sourced:$1:$#\n")
        ]);
        var script = "set -- outer; source /script inner; echo after:$1:$#";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("sourced:inner:1\nafter:outer:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("source ./unit item more", "set -- changed", "1:changed:", "2:function:tail")]
    [InlineData(". ./unit item more", "set --", "0::", "2:function:tail")]
    [InlineData("eval 'source ./unit item more'", "set -- changed", "1:changed:", "2:function:tail")]
    [InlineData("source ./unit", "set -- changed", "1:changed:", "1:changed:")]
    public async Task SourceRestoresFunctionArgumentsAfterReturn(
        string invocation, string replacement, string inside, string afterSource)
    {
        var host = EndToEndHarness.BuildTree([
            ("/unit", replacement + "; printf '[%s:%s:%s]' \"$#\" \"${1-}\" \"${2-}\"; return 7\n")
        ]);
        var script = "set -- root; f() { " + invocation
            + "; printf '<%s:%s:%s:%s>' \"$?\" \"$#\" \"${1-}\" \"${2-}\"; }; f function tail; printf '|%s' \"$1\"";
        var result = await EndToEndHarness.RunScriptAsync(script, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"[{inside}]<7:{afterSource}>|root", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("set -- new; f", ":", "new", "root")]
    [InlineData("set -- new; (f)", ":", "new", "new")]
    [InlineData("set -- new; source ./child nested", "shift", "new", "root")]
    [InlineData("set -- new; source ./child", ":", "new", "root")]
    [InlineData("source ./child nested", "set -- new", "new", "root")]
    [InlineData("source ./child", "set -- new", "new", "new")]
    [InlineData("set -- new; source ./child nested; set -- final", ":", "final", "final")]
    public async Task SourceKeepsOnlyReplacementSinceTheLastSourceOrFunctionBoundary(
        string commands, string child, string beforeReturn, string afterReturn)
    {
        var host = EndToEndHarness.BuildTree([
            ("/unit", commands + "; printf '<%s>' \"${1-}\"\n"),
            ("/child", child + "\n")
        ]);
        var result = await EndToEndHarness.RunScriptAsync(
            "set -- root; f() { :; }; source ./unit item; printf '<%s>' \"${1-}\"", host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<{beforeReturn}><{afterReturn}>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(". ''", "<1:new><root>", "./unit: line 1: : No such file or directory\n")]
    [InlineData("builtin source -- '' temporary", "<1:new><root>", "./unit: line 1: : No such file or directory\n")]
    [InlineData("empty=; . \"$empty\" temporary 2>&-", "<1:new><root>", "")]
    [InlineData("empty=; . $empty", "<2:new><new>", "./unit: line 1: .: filename argument required\n.: usage: . filename [arguments]\n")]
    [InlineData("( . '' temporary )", "<1:new><new>", "./unit: line 1: : No such file or directory\n")]
    public async Task EmptySourceFilenameClearsTheReachedPositionalReplacement(
        string command, string output, string diagnostic)
    {
        var host = EndToEndHarness.BuildTree([
            ("/unit", "set -- new; " + command + "; printf '<%s:%s>' \"$?\" \"$1\"\n")
        ]);
        var result = await EndToEndHarness.RunScriptAsync(
            "set -- root; source ./unit item; printf '<%s>' \"$1\"", host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("source", "source")]
    [InlineData(".", ".")]
    [InlineData("source --", "source")]
    public async Task Evaluation_SourceBuiltin_RequiresFilenameAfterOptionParsing(string script, string name)
    {
        var tree = EndToEndHarness.BuildTree([("/--", "printf decoy\n")]);
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Empty(result.StdOut);
        Assert.StartsWith(name + ": filename argument required\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("set -- -- ./unit inner; source \"$@\"; printf ':%s' \"$1\"", "<inner>:--")]
    [InlineData("fields='-- ./unit inner'; . $fields", "<inner>")]
    [InlineData("set -- outer; source -- ./unit; printf ':%s' \"$1\"", "<outer>:outer")]
    [InlineData("set -- outer; . -- ./unit --; printf ':%s' \"$1\"", "<-->:outer")]
    [InlineData("source -- -- tail", "<tail>")]
    public async Task Evaluation_SourceBuiltin_SelectsFilenameAndRemainingFields(string script, string expected)
    {
        const string Body = "printf '<%s>' \"$@\"\n";
        var tree = EndToEndHarness.BuildTree([("/unit", Body), ("/--", Body)]);
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_InvalidOptionReportsThroughRedirection()
    {
        await using var host = EndToEndHarness.BuildTree([("/unit", ": >selected\n")]);
        var result = await host.RunAsync(". -x ./unit 2>diagnostic", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.StartsWith(".: -x: invalid option\n", host.ReadFile("/diagnostic"));
        Assert.DoesNotContain("selected", host.CaptureEntries().Keys);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_EvalBuiltin_ExecutesParsedContent()
    {
        // This test verifies that "eval" parses and executes its arguments.
        // The script "eval echo hi" must emit "hi\n", no STDERR, and exit code 0.
        // If eval does not execute its content, STDOUT would be empty.
        var script = @"eval echo hi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EvalBuiltin_NoArgumentsReturnsSuccess()
    {
        // This test verifies that "eval" with no arguments succeeds and produces no output.
        // A correct run must emit no STDOUT/STDERR and return exit code 0.
        // If eval errors on empty input, the exit code would be non-zero.
        var script = @"eval";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("eval --", "")]
    [InlineData("eval -- 'printf %s' yes", "yes")]
    [InlineData("eval 'printf %s' --", "--")]
    [InlineData("eval(){ printf '<%s>' \"$@\"; }; eval -- tail", "<--><tail>")]
    public async Task Evaluation_EvalBuiltin_ConsumesOnlyItsLeadingTerminator(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EvalBuiltin_RejectsOptionBeforeRunningJoinedSource()
    {
        var result = await EvaluationTestHelper.RunAsync("f(){ printf ran; }; eval -xyz f; printf '<%s>' \"$?\"");

        Assert.Equal("<2>", result.StdOut);
        Assert.StartsWith("eval: -x: invalid option\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EvalBuiltin_SyntaxErrorReturnsUsageError()
    {
        // This test verifies that "eval" reports a syntax error as exit code 2.
        // The script "eval if" is incomplete, so a correct run must report a syntax error on STDERR,
        // emit no STDOUT, and return 2.
        // If syntax errors are not trapped, the exit code would be 0 or 1.
        var script = @"eval if";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("eval: line 2: syntax error: unexpected end of file\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_SyntaxErrorReportsStderr()
    {
        // This test verifies that "source" reports syntax errors from the sourced script on STDERR.
        // The script file contains "if", which is incomplete, so STDOUT must be empty, STDERR must
        // include a syntax error message, and the exit code must be 2.
        var tree = EndToEndHarness.BuildTree([
            ("/script", "if")
        ]);
        var script = @"source /script";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("/script: line 2: syntax error: unexpected end of file\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }
}

using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationSourceEvalTestsEndToEnd
{
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
        Assert.Equal("MISSING: missing\n", result.StdErr);
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

    [Fact]
    public async Task Evaluation_SourceBuiltin_MissingArgumentReturnsFailure()
    {
        // This test verifies that "source" with no argument fails.
        // The script "source" must emit no output and return exit code 1.
        // If missing arguments are not validated, the exit code would be 0.
        var script = @"source";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
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
        Assert.StartsWith("syntax error:", result.StdErr);
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
        Assert.StartsWith("syntax error:", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }
}

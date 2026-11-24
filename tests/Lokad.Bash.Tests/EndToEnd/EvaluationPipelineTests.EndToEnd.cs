using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationPipelineTestsEndToEnd
{

    [Fact]
    public async Task Evaluation_Pipeline_CreatesPipeAndExecutesSegments()
    {
        // This test verifies that "|" connects STDOUT of the left command to STDIN of the right.
        // The script uses "echo left | cat", so a correct pipeline must output "left\n" with no STDERR
        // and exit code 0 (the right command's status).
        // If the pipe is not created or the right command is skipped, STDOUT would be empty.
        var script = @"echo left | cat";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("left\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task PipeAndErrorUsesExplicitStdoutAliasBeforeDuplicatingStderr()
    {
        var script = @"echo left >&2 |& cat";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Equal("left\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task PipeAndErrorKeepsTheStageLineNumber()
    {
        var result = await EvaluationTestHelper.RunAsync(":\nprintf '%s\\n' \"$LINENO\" |& cat");

        Assert.Equal("2\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(">out", "", "OE", "previous")]
    [InlineData("2>err", "OE", null, "")]
    [InlineData(">out 2>err", "", "OE", "")]
    [InlineData("2>err >&2", "", null, "OE")]
    [InlineData("2>&-", "OE", null, "previous")]
    public async Task PipeAndErrorUsesTheFinalExplicitStdoutMapping(
        string redirections, string stdout, string? outputFile, string errorFile)
    {
        await using var host = new TestHost();
        host.AddFile("/err", "previous");
        var result = await host.RunAsync("{ printf O; printf E >&2; } " + redirections + " |& cat",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(stdout, host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(outputFile, host.FileExists("/out") ? host.ReadFile("/out") : null);
        Assert.Equal(errorFile, host.ReadFile("/err"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task PipeAndErrorWaitsForArgumentAndRedirectionOperandEffects()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "printf '<%s>' \"$(printf argument >&2; printf O)\" " +
            ">\"$(printf reached >>effects; printf setup >&2; printf target)\" |& cat",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.Equal("argumentsetup", host.StandardError.TextContent);
        Assert.Equal("reached", host.ReadFile("/effects"));
        Assert.Equal("<O>", host.ReadFile("/target"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task PipeAndErrorKeepsTheReachedStderrWhenExplicitSetupFails()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(": >before 2>err >&99 >after |& cat", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal("", host.ReadFile("/before"));
        Assert.Equal("99: Bad file descriptor\n", host.ReadFile("/err"));
        Assert.False(host.FileExists("/after"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task FunctionBodyCanOverrideStderrAfterPipelineSetup()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("f(){ printf O; printf E >&2; } 2>body; f >call |& cat",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal("O", host.ReadFile("/call"));
        Assert.Equal("E", host.ReadFile("/body"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_Pipeline_NegationInvertsExitStatus()
    {
        // This test verifies that a leading "!" negates the exit status of the entire pipeline.
        // The script uses "! echo left | cat" which normally succeeds; negation must flip exit code
        // to 1 while preserving the piped output and leaving STDERR empty.
        // If negation is ignored, the exit code would be 0.
        var script = @"! echo left | cat";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("left\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_CommandNameExpansionRunsOnce()
    {
        var script = @"$(echo expansion-side-effect >&2; echo echo) hello | cat";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hello\n", result.StdOut);
        Assert.Equal("expansion-side-effect\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_CommandNameExpansionSideEffectsReachArguments()
    {
        var script = @"unset command_name; ${command_name:=echo} ""${command_name-unset}"" | cat; echo ${command_name-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("echo\nunset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_NegatesCommandAfterReadonlyPrefix()
    {
        var script = "readonly value=outer; ! value=inner echo ignored | cat";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("ignored\n", result.StdOut);
        Assert.Equal("line 1: value: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_UnknownConsumerClosesPipe()
    {
        var line = "0123456789\n";
        var largeContent = string.Concat(Enumerable.Repeat(line, 20000));
        var tree = EndToEndHarness.BuildTree([("/big.txt", largeContent)]);

        var result = await EndToEndHarness.RunScriptAsync("cat /big.txt | missing", tree);

        Assert.Equal(127, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: missing: command not found\n", result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_NegationAppliesAfterPipefailStatus()
    {
        var result = await EvaluationTestHelper.RunAsync("! missing | cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: missing: command not found\n", result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_EvalOnlyPrintfFeedsHostCommand()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '%s\\n' value | cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_InheritsAndBorrowsParentRedirectionDescriptors()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "{ cat /file >&7 | cat; echo after >&7; } 7>/out; cat /out");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("content\nafter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_RebasesPipeAndErrorOntoPipe()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "{ { echo out; echo err >&2; } |& cat; } 2>/errors; cat /errors");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\nerr\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_EvalOnlyReadConsumesInputWithoutCommandNotFound()
    {
        var result = await EvaluationTestHelper.RunAsync("printf 'x\\n' | read value; echo ${value:-unset}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_EvalOnlyReadUsesPipelineLocalVariableState()
    {
        var result = await EvaluationTestHelper.RunAsync("readonly value; printf 'x\\n' | read value; echo status:$?");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal("line 1: value: readonly variable\n", result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_EvalOnlyFixedStatusBuiltinsReturnStatus()
    {
        var result = await EvaluationTestHelper.RunAsync("true | cat; echo true:$?; false | cat; echo false:$?");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("true:0\nfalse:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_EvalOnlySuccessCanCloseInputWithoutFailingProducer()
    {
        var line = "0123456789\n";
        var largeContent = string.Concat(Enumerable.Repeat(line, 20000));
        var tree = EndToEndHarness.BuildTree([("/big.txt", largeContent)]);

        var result = await EndToEndHarness.RunScriptAsync("cat /big.txt | true; echo status:$?", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("status:0\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_EvalOnlyFailureCanCloseInputWithoutProducerOverridingStatus()
    {
        var line = "0123456789\n";
        var largeContent = string.Concat(Enumerable.Repeat(line, 20000));
        var tree = EndToEndHarness.BuildTree([("/big.txt", largeContent)]);

        var result = await EndToEndHarness.RunScriptAsync("cat /big.txt | false; echo status:$?", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_ResolvesOutputAndStatusBuiltinsInEvaluator()
    {
        var result = await EvaluationTestHelper.RunAsync("pwd | cat; test -f /file | cat; echo test:$?");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/\ntest:0\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_IsolatesStateMutatingBuiltins()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "export value=inner | cat; echo export:${value:-unset}; "
            + "printf 'line\\n' | mapfile rows; echo mapfile:${rows[0]:-unset}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("export:unset\nmapfile:unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_IsolatesExternalCommandArgumentExpansions()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "cat ${value:=/file} | cat; echo ${value:-unset}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("content\nunset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_PreservesEnclosingFunctionContext()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "f() { local value=inner | cat; echo local:$?; return 7 | cat; echo return:$?; }; f");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("local:0\nreturn:7\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_PreservesEnclosingLoopContext()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "for value in once; do break | cat; echo break:$?; break; done");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("break:0\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_ResolvesExpandedBuiltinName()
    {
        var result = await EvaluationTestHelper.RunAsync("name=printf; $name 'expanded\\n' | cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("expanded\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_FunctionShadowsBuiltinName()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "printf() { echo function; }; printf ignored | cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("function\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_FunctionNamedBuiltinShadowsBuiltinCommand()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "builtin() { echo function; }; builtin ignored | cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("function\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_CompoundStageUsesEvaluator()
    {
        var result = await EvaluationTestHelper.RunAsync("{ echo out; echo err >&2; } |& cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\nerr\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_CommandBuiltinReentersBuiltinResolution()
    {
        var result = await EvaluationTestHelper.RunAsync("command printf 'command\\n' | cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("command\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_EvalBuiltinExecutesInStageEvaluator()
    {
        var result = await EvaluationTestHelper.RunAsync("eval \"printf 'eval\\\\n'\" | cat");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("eval\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_Pipeline_ClosedStdout_DoesNotCloseSharedDescriptor()
    {
        var result = await EvaluationTestHelper.RunAsync("echo hidden 1>&- | cat; echo after");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after\n", result.StdOut);
        Assert.Equal("echo: write error: Bad file descriptor\n", result.StdErr);
    }
}

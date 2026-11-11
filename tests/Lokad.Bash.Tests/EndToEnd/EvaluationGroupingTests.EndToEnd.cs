using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationGroupingTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_GroupCommand_ExecutesBodyInOrder()
    {
        // This test verifies that a group command executes its body in sequence in the current shell.
        // The script "{ echo first; echo second; }" must emit both lines in order on STDOUT, with no
        // STDERR and exit code 0 (the last command's status).
        // If the group is skipped or ordering is wrong, STDOUT will not match the expected sequence.
        var script = @"{ echo first; echo second; }";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("first\nsecond\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GroupCommand_UsesExitStatusForAndList()
    {
        // This test verifies that a grouped command's exit status controls a following "&&" list.
        // The script "{ false; } && echo after" must skip "echo after", emit no output, and exit 1.
        // If the group exit status is not respected, STDOUT would contain "after\n".
        var script = @"{ false; } && echo after";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GroupCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a group command.
        // The script "! { true; }" produces no output, no STDERR, and must return exit code 1.
        // If negation is ignored, the exit code would be 0.
        var script = @"! { true; }";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GroupCommand_MutatesParentVariables()
    {
        // This test verifies that a group command uses the current shell state.
        // The script assigns "inner" inside "{ ...; }", then echoes the variable afterward.
        // If group commands were isolated like subshells, STDOUT would incorrectly contain "outer".
        var script = @"x=outer; { x=inner; }; echo ""$x""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inner\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_ExecutesBodyInOrder()
    {
        // This test verifies that a subshell executes its body in order and returns the last status.
        // The script "( echo first; echo second; )" must emit both lines on STDOUT, no STDERR, and
        // exit code 0.
        // If the subshell fails to run or output is reordered, STDOUT will not match.
        var script = @"( echo first; echo second; )";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("first\nsecond\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a subshell.
        // The script "! ( true; )" must produce no output, no STDERR, and exit with code 1.
        // If negation is ignored, the exit code would be 0.
        var script = @"! ( true; )";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_DoesNotLeakVariableAssignment()
    {
        // This test verifies that assignments inside a subshell do not mutate parent shell variables.
        // The script sets x to "inner" inside "( ... )", then echoes x in the parent shell.
        // If subshell state leaks, STDOUT would contain "inner".
        var script = @"x=outer; (x=inner); echo ""$x""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_DoesNotLeakUnset()
    {
        // This test verifies that unset inside a subshell does not remove the parent variable.
        // The script unsets x inside "( ... )", then echoes x with a default in the parent shell.
        // If the unset leaks, STDOUT would contain "unset".
        var script = @"x=outer; (unset x); echo ""${x:-unset}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_DoesNotLeakFunctionDefinition()
    {
        // This test verifies that a function defined inside a subshell is not visible in the parent shell.
        // The script defines f inside "( ... )", then asks "type" about f after the subshell completes.
        // If the function definition leaks, STDOUT would contain "leaked".
        var script = @"(f(){ echo hi; }); if type f >/dev/null 2>&1; then echo leaked; else echo none; fi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("none\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_DoesNotLeakFileDescriptor()
    {
        // This test verifies that descriptors opened inside a subshell are not usable by the parent shell.
        // The subshell opens fd 3 for /out, then the parent tries to write through fd 3.
        // If the descriptor leaks, STDOUT would contain "leaked" and /out would contain "leaked\n".
        var script = @"(exec 3> /out); if echo leaked >&3; then echo leaked; else echo isolated; fi";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("isolated\n", result.StdOut);
        Assert.Equal("3: Bad file descriptor\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_ExitDoesNotExitParent()
    {
        // This test verifies that exit inside a subshell terminates only the subshell command.
        // The parent echoes the subshell status afterward, so STDOUT must contain "after:7".
        // If exit leaks to the parent, the echo after the subshell would never run.
        var script = @"(exit 7); echo ""after:$?""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("after:7\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}

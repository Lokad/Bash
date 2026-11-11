using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationOpenIssuesTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_CompoundRedirection_AppliesToSubshell()
    {
        // This test verifies that redirections on a subshell apply to its STDOUT.
        // The subshell writes "out" to STDOUT and "err" to STDERR; with "> /out",
        // STDOUT must go to /out while STDERR remains visible.
        var script = @"(echo out; echo err >&2) > /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("err\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_CompoundRedirection_AppliesToIf()
    {
        // This test verifies that redirections on an if-compound apply to its STDOUT.
        // The compound writes to STDOUT and STDERR; with "> /out", only STDOUT is redirected.
        var script = @"if true; then echo out; echo err >&2; fi > /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("err\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_DescriptorAllocation_PersistsAcrossCommands()
    {
        // This test verifies that {fd}> remains open for subsequent commands.
        // The same allocated descriptor is used twice; /out must include both lines.
        var script = @": {fd}> /out; echo first >&$fd; echo second >&$fd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("first\nsecond\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_ExecDescriptor_PersistsAcrossCommands()
    {
        // This test verifies that exec-opened descriptors remain open after subsequent commands.
        // The script opens fd 3, runs another command, then reads from fd 3.
        var script = @"exec 3< /input; echo after; cat <&3";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("after\ninput\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_ExecClose_ResolvesLogicalDescriptor()
    {
        // Opening fd 9 first shifts the host allocation for logical fd 3 away from physical fd 3.
        // Closing logical fd 3 must not close fd 9's physical descriptor.
        var script = "exec 9> /other\nexec 3> /out\necho before >&3\nexec 3>&-\necho after >&3\necho other >&9\nexec 3>&- 9>&-\ncat /out\ncat /other";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("before\nother\n", result.StdOut);
        Assert.Equal("3: Bad file descriptor\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
        Assert.Equal("other\n", await EndToEndHarness.ReadFileAsync(result.Host, "/other"));
    }

    [Fact]
    public async Task Evaluation_CommandLocalClose_PreservesPersistentDescriptors()
    {
        var script = "exec 9> /other\nexec 3> /out\necho one >&3\necho ignored > /dev/null 3>&-\necho two >&3\necho other >&9\nexec 3>&- 9>&-\ncat /out\ncat /other";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one\ntwo\nother\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellClose_PreservesParentDescriptor()
    {
        var script = "exec 3> /out\n(exec 3>&-)\necho hi >&3\nexec 3>&-\ncat /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellNoOpExec_PreservesInheritedRedirection()
    {
        var result = await EvaluationTestHelper.RunAsync("{ (exec); echo after; } > /out");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_ExecDescriptor_RemainsOpenAfterExternalCommand()
    {
        var script = "exec 3> /out; cat /input >&3; echo after >&3; exec 3>&-; cat /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("input\nafter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionInheritsLogicalExecDescriptorMappings()
    {
        var script = "exec 9> /out; value=$(sh -c 'echo inner >&9'); echo outer >&9; exec 9>&-; cat /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inner\nouter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_NestedShellBorrowsCommandLocalLogicalDescriptor()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "sh -c 'cat /input >&3; echo after >&3' 3> /out; cat /out");

        Assert.Equal("input\nafter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecClose_DoesNotMakeTheOtherFileATerminal()
    {
        // Keep the original script: neither logical fd 9's file nor absent logical fd 4 is a terminal.
        var script = "exec 9> /other; exec 3> /out; exec 3>&-; test -t 9; echo other=$?; test -t 4; echo out=$?";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("other=1\nout=1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecClose_FinalizesAfterLastLogicalAlias()
    {
        var script = "exec 3> /out; exec 4>&3; exec 3>&-; echo hi >&4; exec 4>&-; test -t 3; echo fd=$?; cat /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("fd=1\nhi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecCommandClose_HidesPersistentDescriptorFromReplacement()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "exec 3> /out; exec sh -c 'echo leaked >&3' 3>&-");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_ExecReassignment_LeavesBothFilesNonterminal()
    {
        var script = "exec 9> /first; exec 9> /second; test -t 3; echo first=$?; test -t 4; echo second=$?";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("first=1\nsecond=1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("$FOO")]
    [InlineData("$(printf '%s' \"$FOO\")")]
    public async Task Evaluation_AssignmentPrefix_RedirectionUsesCallerBindings(string target)
    {
        // Redirection expansion sees the caller binding, including through command substitution.
        var script = $"FOO=/out; FOO=/other command echo hi > \"{target}\"; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/out\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hi\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
        Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/other"));
    }
}

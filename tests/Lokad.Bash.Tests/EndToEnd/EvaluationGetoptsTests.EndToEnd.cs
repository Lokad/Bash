using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationGetoptsTestsEndToEnd
{
    [Theory]
    [InlineData("a", "-x")]
    [InlineData("a:", "-a")]
    public async Task ShellClosedStderrKeepsOptionWarningAssignments(string options, string operand)
    {
        var result = await EvaluationTestHelper.RunAsync(
            $"OPTARG=old; getopts '{options}' pick {operand} 2>&-; "
            + "printf '<%s><%s><%s><%s>' \"$?\" \"${pick-unset}\" \"$OPTIND\" \"${OPTARG-unset}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0><?><2><unset>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task HostOptionWarningFailureStillPreventsAssignments()
    {
        var state = new Evaluation.Evaluation(Parser.Parse("OPTARG=old; getopts a pick -x"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Synthetic write failure\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        Assert.False(state.Variables.ContainsKey("pick"));
        Assert.Equal(ShellValue.FromText("1"), state.Variables["OPTIND"]);
        Assert.Equal(ShellValue.FromText("old"), state.Variables["OPTARG"]);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    public async Task Getopts_DiagnosticBackingFollowsThePendingWrite(int nameBytes, bool exhaustBudget)
    {
        var name = new byte[nameBytes];
        name.AsSpan().Fill((byte)'x');
        name[^1] = 0xff;
        var expected = name.Concat(": illegal option -- x\n"u8.ToArray()).ToArray();
        var state = new Evaluation.Evaluation(Parser.Parse("exec -a \"$1\" eval 'getopts a opt -x 2>errors'"));
        state.ReplacePositionalParameters([ShellValue.FromBytes(name)]);
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;

        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        var before = governor.ReservedBytes;
        using var other = exhaustBudget ? governor.Reserve(governor.LimitBytes - before) : null;
        var beforeWrite = governor.ReservedBytes;
        var descriptor = new FileDescriptor(17);
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
        var pending = governor.ReservedBytes;
        var actual = write.Content.ToArray();
        Assert.Equal(descriptor, write.FileDescriptor);
        write.SetResult(DescriptorWriteResult.Open);

        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep());
        var afterWrite = governor.ReservedBytes;
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);
        Assert.Null(await state.GetNextStep());

        if (exhaustBudget && nameBytes == 4096)
        {
            Assert.Equal(1, state.LastExitCode);
            Assert.Equal("getopts: shared memory budget exhausted\n"u8.ToArray(), actual);
            Assert.Equal(governor.LimitBytes, pending);
        }
        else
        {
            Assert.Equal(0, state.LastExitCode);
            Assert.Equal(expected, actual);
            Assert.Equal(nameBytes == 4096 ? expected.Length : 0, pending - beforeWrite);
            if (nameBytes == 4096)
                Assert.True(afterWrite < pending);
        }
    }

    [Theory]
    [InlineData("a=(outer tail); a=temp getopts a a -a; printf '%s:%s' \"${a[0]}\" \"${a[1]}\"", "a:tail")]
    [InlineData("opt=outer; opt=temp getopts a opt -a; sh -c 'printf \"%s\" \"${opt-unset}\"'", "unset")]
    [InlineData("export opt=outer; opt=temp getopts a opt -a; sh -c 'printf \"%s\" \"$opt\"'", "a")]
    [InlineData("x=outer; declare -n ref=x; x=temp getopts a ref -a; printf '%s' \"$x\"", "outer")]
    [InlineData("x=outer; f() { declare -n ref=x; x=temp getopts a ref -a; }; f; printf '%s' \"$x\"", "a")]
    public async Task Evaluation_GetoptsPrefix_PreservesCallerBinding(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("set -- -abVALUE -c; getopts ab:c opt; printf '<%s:%s:%s>' \"$opt\" \"$OPTIND\" \"${OPTARG-unset}\"; getopts ab:c opt; printf '<%s:%s:%s>' \"$opt\" \"$OPTIND\" \"$OPTARG\"; getopts ab:c opt; printf '<%s:%s:%s>' \"$opt\" \"$OPTIND\" \"${OPTARG-unset}\"",
        "<a:1:unset><b:2:VALUE><c:3:unset>")]
    [InlineData("getopts a: opt -a ''; printf '<%s:%s:%s>' \"$opt\" \"$OPTIND\" \"${OPTARG-unset}\"",
        "<a:3:>")]
    [InlineData("getopts a: opt -a --; printf '<%s:%s:%s>' \"$opt\" \"$OPTIND\" \"$OPTARG\"",
        "<a:3:-->")]
    [InlineData("spec='ab opt -ab'; getopts $spec; printf '<%s:%s>' \"$opt\" \"$OPTIND\"",
        "<a:1>")]
    [InlineData("getopts -- ab opt -ab; printf '<%s:%s>' \"$opt\" \"$OPTIND\"",
        "<a:1>")]
    [InlineData("OPTARG=old; getopts a opt -- -a; printf '<%s:%s:%s:%s>' \"$?\" \"$opt\" \"$OPTIND\" \"${OPTARG-unset}\"; getopts a opt -- -a; printf '<%s:%s:%s>' \"$?\" \"$opt\" \"$OPTIND\"",
        "<1:?:2:unset><0:a:3>")]
    [InlineData("OPTARG=old; getopts a opt data -a; printf '<%s:%s:%s:%s>' \"$?\" \"$opt\" \"$OPTIND\" \"${OPTARG-unset}\"",
        "<1:?:1:unset>")]
    public async Task Getopts_ScansBundlesArgumentsAndTerminators(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("OPTIND=1", "<a:1>")]
    [InlineData("printf -v OPTIND '%s' 1", "<a:1>")]
    [InlineData("export OPTIND", "<b:2>")]
    [InlineData("OPTIND=2", "<b:3>")]
    [InlineData("OPTIND=1 :", "<a:1>")]
    [InlineData("OPTIND=1 getopts abc opt", "<a:1>")]
    [InlineData("f(){ local OPTIND=1; getopts cd inner -cd; }; f", "<b:2>")]
    [InlineData("(getopts abc opt)", "<b:2>")]
    [InlineData("captured=$(getopts abc opt)", "<b:2>")]
    public async Task Getopts_ResumesOrResetsTheSavedBundle(string interveningCommand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "set -- -ab -c; getopts abc opt; " + interveningCommand
            + "; getopts abc opt; printf '<%s:%s>' \"$opt\" \"$OPTIND\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task Getopts_TemporarySeekKeepsTheBundleConsumedByTheCommand()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "set -- -ab -c -d; getopts abcd opt; OPTIND=2 getopts abcd opt; "
            + "printf '<%s:%s>' \"$opt\" \"$OPTIND\"; getopts abcd opt; printf '<%s:%s>' \"$opt\" \"$OPTIND\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<b:3><d:4>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("", "a", "-x", "<0:?:2:unset>", "getopts: illegal option -- x\n")]
    [InlineData("", ":a", "-x", "<0:?:2:x>", "")]
    [InlineData("", "a:", "-a", "<0:?:2:unset>", "getopts: option requires an argument -- a\n")]
    [InlineData("", ":a:", "-a", "<0:::2:a>", "")]
    [InlineData("OPTERR=0; ", "a:", "-a", "<0:?:2:unset>", "")]
    [InlineData("readonly OPTARG=old; ", "a", "data", "<1:?:1:unset>", "")]
    public async Task Getopts_PublishesTheDocumentedErrorResult(
        string setup, string options, string operand, string expected, string diagnostics)
    {
        var result = await EvaluationTestHelper.RunAsync("OPTARG=old; " + setup
            + "getopts " + options + " opt " + operand
            + "; printf '<%s:%s:%s:%s>' \"$?\" \"$opt\" \"$OPTIND\" \"${OPTARG-unset}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(diagnostics, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_PromotesPrefixedOutputVariables()
    {
        var script = @"opt=outer; OPTIND=1; opt=temp OPTIND=1 getopts a opt -a; echo $opt/$OPTIND";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a/2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_GetoptsBuiltin_ParsesOptionsAndUpdatesOptind()
    {
        // This test verifies that getopts parses options and increments OPTIND across calls.
        // The script parses "-a -b" twice and echoes the option and OPTIND after each call.
        // A correct run must emit "a 2\nb 3\n", with no STDERR and exit code 0.
        // If OPTIND is not updated, the second line would repeat "a 2".
        var script = @"getopts ab opt -a -b; echo $opt $OPTIND; getopts ab opt -a -b; echo $opt $OPTIND";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a 2\nb 3\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_NoOptionsReturnsFailure()
    {
        // This test verifies that getopts returns failure when no options are available.
        // The script "getopts ab opt" has no arguments, so it must emit no output and exit with 1.
        // If it succeeds incorrectly, the exit code would be 0.
        var script = @"getopts ab opt";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("getopts")]
    [InlineData("getopts a")]
    public async Task Evaluation_GetoptsBuiltin_MissingArgumentsReportsUsage(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("getopts: usage: getopts optstring name [arg ...]\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_FatalPartialOperandSkipsAssignmentPrefix()
    {
        var script = @"X=$(echo assignment-side-effect >&2) getopts ""${MISSING:?word failed}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.DoesNotContain("assignment-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }
}

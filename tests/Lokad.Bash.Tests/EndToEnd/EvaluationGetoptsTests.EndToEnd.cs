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
    [Fact]
    public async Task ReadonlyOptionTargetKeepsTheFirstOperandLineAndScanStatus()
    {
        var result = await EvaluationTestHelper.RunAsync("readonly option=old\ngetopts \\\na \\\noption -a\n"
            + "printf '<%s><%s><%s>' \"$?\" \"$option\" \"$OPTIND\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2><old><2>", result.StdOut);
        Assert.Equal("line 3: option: readonly variable\n", result.StdErr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetoptsHelpOwnsTheOutputUntilItsWriteSettles(bool cancel)
    {
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = new Evaluation.Evaluation(Parser.Parse("getopts --help >documentation"));
        state.Memory.Budget = owner;
        var evaluation = state.EnsureRunning(cancellation.Token);

        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep(deadline.Token));
        var before = governor.ReservedBytes;
        var descriptor = new FileDescriptor(17);
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var write = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        var content = write.Content.ToArray();
        var pending = governor.ReservedBytes;
        var writeDescriptor = write.FileDescriptor;
        if (cancel)
            cancellation.Cancel();
        var closeStep = state.GetNextStep(deadline.Token);
        var retained = governor.ReservedBytes;
        var closedBeforeWrite = closeStep.IsCompleted;
        write.SetResult(cancel ? DescriptorWriteResult.Open : DescriptorWriteResult.WriteFailure);

        var close = Assert.IsType<CloseDescriptorOperation>(await closeStep);
        var afterWrite = governor.ReservedBytes;
        close.SetResult(null);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation.WaitAsync(deadline.Token));
        else
        {
            await evaluation.WaitAsync(deadline.Token);
            Assert.Equal(2, state.LastExitCode);
        }

        Assert.Equal(descriptor, writeDescriptor);
        Assert.Equal(descriptor, close.Descriptor);
        Assert.StartsWith("getopts: getopts optstring name [arg ...]\nParse", Utf8Text.Decode(content));
        Assert.Equal(content.Length, pending - before);
        Assert.Equal(pending, retained);
        Assert.False(closedBeforeWrite);
        Assert.True(pending - afterWrite >= content.Length);
        Assert.Empty(state.GetDescriptorsForCleanup());
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task GetoptsHelpSkipsOutputWhenTheShellClosesStdout()
    {
        var state = new Evaluation.Evaluation(Parser.Parse("getopts --help >&-"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);

        await new Engine().RunAsync(state, host);

        Assert.Equal(2, state.LastExitCode);
        Assert.Empty(host.Appends);
    }

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetoptsDestinationNameCopyUsesTheCommandAllowance(bool constrain)
    {
        var name = new string('n', 4096);
        var state = new Evaluation.Evaluation(Parser.Parse(name + "=old; getopts a \"$1\" -a 2>diagnostic"));
        state.ReplacePositionalParameters([ShellValue.FromText(name)]);
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;

        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        using var occupied = constrain ? governor.Reserve(governor.LimitBytes - governor.ReservedBytes - 4096) : null;
        var descriptor = new FileDescriptor(17);
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var step = await state.GetNextStep();
        byte[] diagnostic = [];
        if (step is WriteOperation write)
        {
            Assert.Equal(descriptor, write.FileDescriptor);
            diagnostic = write.Content.ToArray();
            write.SetResult(DescriptorWriteResult.Open);
            step = await state.GetNextStep();
        }
        var close = Assert.IsType<CloseDescriptorOperation>(step);
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(constrain ? 1 : 0, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("2"), state.Variables["OPTIND"]);
        Assert.Equal(ShellValue.FromText(constrain ? "old" : "a"), state.Variables[name]);
        Assert.Equal(constrain ? "getopts: shared memory budget exhausted\n"u8.ToArray() : [], diagnostic);
        occupied?.Dispose();
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task GetoptsInvalidDestinationKeepsItsOriginalDiagnosticBytes()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "getopts a $'x\\377' -a 2>diagnostic; printf '<%s:%s>' \"$?\" \"$OPTIND\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:2>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal<byte>([.. "line 1: getopts: `x"u8, 0xff, .. "': not a valid identifier\n"u8], host.ReadFileBytes("/diagnostic"));
        Assert.Equal(0, host.OpenFileCount);
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

    [Fact]
    public async Task GetoptsReportsTheRawMissingResultAndContinuesTheBundle()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            set -- $'-\375a' -a
            spec=$':a\375'; OPTARG=seed
            getopts "$spec" flag
            printf '<%s:%s:%s:%s>\n' "$?" "$flag" "$OPTIND" "$OPTARG"
            getopts "$spec" flag
            printf '<%s:%s:%s:%s>\n' "$?" "$flag" "$OPTIND" "${OPTARG-unset}"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal<byte>([.. "<0:::1:"u8, 0xfd, .. ">\n<0:a:2:unset>\n"u8], host.StandardOutput.Content.ToArray());
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Fact]
    public async Task GetoptsConsumesTheRequiredArgumentBeforeReportingTheRawUnknownResult()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            set -- $'-\376' arg -a
            spec=$':a\376:'; OPTARG=seed
            getopts "$spec" flag
            printf '<%s:%s:%s:%s>\n' "$?" "$flag" "$OPTIND" "$OPTARG"
            getopts "$spec" flag
            printf '<%s:%s:%s:%s>\n' "$?" "$flag" "$OPTIND" "${OPTARG-unset}"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal<byte>([.. "<0:?:3:"u8, 0xfe, .. ">\n<0:a:4:unset>\n"u8], host.StandardOutput.Content.ToArray());
        Assert.True(host.StandardError.Content.IsEmpty);
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

    [Theory]
    [InlineData(@"$'\v2'", "<0:b:3>")]
    [InlineData(@"$'\f2'", "<0:b:3>")]
    [InlineData("2suffix", "<0:b:3>")]
    [InlineData("0x2", "<0:a:2>")]
    public async Task GetoptsSeeksUsingTheStoredDecimalPrefix(string operand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            set -- -a -b -c
            declare +i OPTIND
            OPTIND={{operand}}
            getopts abc opt
            printf '<%s:%s:%s>' "$?" "$opt" "$OPTIND"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData(@"$'\v1'", true)]
    [InlineData(@"$'\f1'", true)]
    [InlineData(@"$' \v+1rest'", true)]
    [InlineData("0rest", false)]
    public async Task GetoptsSelectsWarningsUsingTheStoredDecimalPrefix(string operand, bool warns)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($$"""
            OPTERR={{operand}}
            OPTARG=old
            getopts abc opt -z 2>notices
            printf '<%s:%s:%s:%s>' "$?" "$opt" "$OPTIND" "${OPTARG-unset}"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0:?:2:unset>", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(warns ? "getopts: illegal option -- z\n" : "", host.ReadFile("/notices"));
        Assert.Equal(0, host.OpenFileCount);
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
    [InlineData("-4", "<0:4><0:a:3>")]
    [InlineData("-4a", "<0:4><0:a:2>")]
    public async Task Getopts_WritingOptindAsTheResultKeepsTheSavedCursor(string firstOperand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            set -- {{firstOperand}} -a -b -c
            getopts 4a OPTIND
            printf '<%s:%s>' "$?" "$OPTIND"
            getopts abc pick
            printf '<%s:%s:%s>' "$?" "$pick" "$OPTIND"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("OPTIND", "<0:4><0:c:5>")]
    [InlineData("index_ref", "<0:2><0:a:3>")]
    public async Task Getopts_PrefixedIndexResultDependsOnTheSelectedBinding(string destination, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            declare -n index_ref=OPTIND
            set -- -4 -a -b -c
            OPTIND=1 getopts 4a {{destination}}
            printf '<%s:%s>' "$?" "$OPTIND"
            getopts abc pick
            printf '<%s:%s:%s>' "$?" "$pick" "$OPTIND"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("a:", "-avalue", "<0:a:2>")]
    [InlineData(":a", "-z", "<0:z:2>")]
    public async Task Getopts_ResultAndOptionArgumentShareTheSelectedBinding(string specification, string operand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            OPTARG=old
            getopts {{specification}} OPTARG {{operand}}
            printf '<%s:%s:%s>' "$?" "${OPTARG-unset}" "$OPTIND"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task Getopts_PublishesEveryDigitWhenTheArgumentIndexWidens()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            set -- -a -a -a -a -a -a -a -a -ab -c
            OPTIND=9
            for step in 1 2 3 4; do
                getopts abc opt
                printf '<%s:%s:%s>' "$?" "$opt" "$OPTIND"
            done
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0:a:9><0:b:10><0:c:11><1:?:11>", result.StdOut);
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

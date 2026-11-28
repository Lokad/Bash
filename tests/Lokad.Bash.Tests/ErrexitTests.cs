using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ErrexitTests
{
    private const string ReportOption = "flag(){ if [[ -o errexit ]]; then printf on; else printf off; fi; }; ";
    private const string SetUsage = "set: usage: set [-e|+e] [-o errexit|+o errexit] [-|+|--] [arg ...]\n";
    private const string SetClosedOutput = "set: write error: Bad file descriptor\n";
    private const string TopLevelSetClosedOutput = "line 1: " + SetClosedOutput;

    [Theory]
    [InlineData("-e", "+e")]
    [InlineData("-e", "+o errexit")]
    [InlineData("-o errexit", "+e")]
    [InlineData("-o errexit", "+o errexit")]
    public async Task OptionSpellingsEnableAndDisableTheSameState(string enable, string disable)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"set {enable}; printf '%s' \"$-\"; set {disable}; false; printf ':continued:%s' \"${{-}}\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("e:continued:", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task InspectionReportsSupportedStateWithoutReplacingPositionals()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "set -- keep; set -o; set -e; set +o; set +e; set -o; printf '%s' \"$1\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("errexit\toff\nset -o errexit\nerrexit\toff\nkeep", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task ShoptReportsReachedStateAndProcessesLaterNamesAfterAnError()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            shopt -s localvar_inherit invalid_test_option inherit_errexit
            printf 'set:%s\n' "$?"
            shopt -p localvar_inherit invalid_test_option inherit_errexit 2>&1
            printf 'query:%s\n' "$?"
            shopt -u localvar_inherit
            shopt -pq localvar_inherit
            printf 'quiet:%s\n' "$?"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("set:1\nshopt -s localvar_inherit\n"
            + "line 3: shopt: invalid_test_option: invalid shell option name\n"
            + "shopt -s inherit_errexit\nquery:1\nquiet:1\n", host.StandardOutput.TextContent);
        Assert.Equal("line 1: shopt: invalid_test_option: invalid shell option name\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("printf 'capture:%s\\n' \"$(printf before; false; printf after)\"", 0, "capture:before\nouter\n")]
    [InlineData("row=$(false; printf after)", 1, "")]
    [InlineData("if row=$(false; printf after); then printf 'value:%s\\n' \"$row\"; fi", 0, "value:after\nouter\n")]
    public async Task InheritErrexitRespectsTheCapturesReachedStatusContext(string capture, int status, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("set -e\nshopt -s inherit_errexit\n" + capture + "\nprintf 'outer\\n'\n",
            CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task LocalDashRestoresTheFirstErrexitInheritanceSetting()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            shopt -s inherit_errexit
            f() {
                local -
                shopt -u inherit_errexit
                local -
                shopt -s localvar_inherit
                shopt -p inherit_errexit
            }
            f
            shopt -p inherit_errexit localvar_inherit
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("shopt -u inherit_errexit\nshopt -s inherit_errexit\nshopt -s localvar_inherit\n",
            host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task OptionBindingsFollowTheReachedCaptureAndParentState()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            set -e
            shopt -s localvar_inherit
            printf 'capture:%s\n' "$(printf '%s/%s' "$BASHOPTS" "$SHELLOPTS")"
            shopt -s inherit_errexit
            printf 'capture:%s\n' "$(shopt -u localvar_inherit; printf '%s/%s' "$BASHOPTS" "$SHELLOPTS")"
            printf 'parent:%s/%s\n' "$BASHOPTS" "$SHELLOPTS"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("capture:globskipdots:localvar_inherit/braceexpand:pipefail\n"
            + "capture:globskipdots:inherit_errexit/braceexpand:errexit:pipefail\n"
            + "parent:globskipdots:inherit_errexit:localvar_inherit/braceexpand:errexit:pipefail\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BashOptionsProjectionReusesReadsAndDoesNotBorrowAChangedChildValue(bool inlineSubshell)
    {
        var script = Parser.Parse(":");
        var parent = new EvaluationState(script, ShellPath.Root, inheritedDescriptorTable: null);
        var memory = new EvaluationMemory(new MemoryGovernor(16 * 1024));
        var parentBaseline = parent.RetainedShellStorageBytes;

        parent.SetOption(ShellOption.Dotglob, true);
        Assert.Equal(parentBaseline, parent.RetainedShellStorageBytes);
        var initial = parent.ReadBashOptions();
        var owned = MemoryCapacity.ArrayBackingBytes<byte>(initial.Bytes.Length);
        Assert.Equal("dotglob:globskipdots"u8.ToArray(), initial.Bytes.ToArray());
        Assert.Equal(owned, parent.RetainedShellStorageBytes - parentBaseline);

        var child = inlineSubshell ? parent.CloneForSubshell(memory, inheritedExitBindings: null)
            : parent.CloneForNestedEvaluation(script, EvaluationDescriptorSetup.Standard, ShellForkKind.CommandSubstitution,
                memory, inheritedExitBindings: null);
        var childBaseline = child.RetainedShellStorageBytes;
        Assert.True(initial.Bytes.Equals(child.ReadBashOptions().Bytes));
        Assert.Equal(childBaseline, child.RetainedShellStorageBytes);

        child.SetOption(ShellOption.Nullglob, true);
        var changed = child.ReadBashOptions();
        child.SetOption(ShellOption.Errexit, true);
        Assert.True(changed.Bytes.Equals(child.ReadBashOptions().Bytes));
        Assert.Equal("dotglob:globskipdots:nullglob"u8.ToArray(), changed.Bytes.ToArray());
        Assert.Equal(MemoryCapacity.ArrayBackingBytes<byte>(changed.Bytes.Length), child.RetainedShellStorageBytes - childBaseline);
        Assert.True(initial.Bytes.Equals(parent.ReadBashOptions().Bytes));
        Assert.Equal(owned, parent.RetainedShellStorageBytes - parentBaseline);
        memory.Finish(retainVariables: false);
    }

    [Fact]
    public async Task ExportedBashOptionsKeepTheBytesCapturedBeforeLaterOptionChanges()
    {
        var evaluation = new Eval(Parser.Parse("""
            shopt -s dotglob
            export BASHOPTS
            inspect
            shopt -u dotglob
            shopt -s failglob
            inspect
            shopt -s nullglob
            """));

        var first = Assert.IsType<RunCommand>(await evaluation.GetNextStep());
        var firstOptions = Assert.Single(first.Command.Environment, entry => entry.Name == "BASHOPTS").Value;
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = Assert.IsType<RunCommand>(await evaluation.GetNextStep());
        var secondOptions = Assert.Single(second.Command.Environment, entry => entry.Name == "BASHOPTS").Value;
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await evaluation.GetNextStep());
        var lastOptions = evaluation.Variables["BASHOPTS"];
        Assert.Equal("dotglob:globskipdots", firstOptions.ToUtf8String());
        Assert.Equal("failglob:globskipdots", secondOptions.ToUtf8String());
        Assert.Equal("failglob:globskipdots:nullglob", lastOptions.ToUtf8String());
        Assert.True(lastOptions.Bytes.Equals(evaluation.Variables["BASHOPTS"].Bytes));
    }

    [Theory]
    [InlineData("set", "BASHOPTS=dotglob:globskipdots", "SHELLOPTS=braceexpand:pipefail")]
    [InlineData("declare -p", "declare -r BASHOPTS=\"dotglob:globskipdots\"", "declare -r SHELLOPTS=\"braceexpand:pipefail\"")]
    public async Task OperandFreeVariableListingsIncludeTheLiveOptions(string command, string bashOptions, string setOptions)
    {
        var result = await EvaluationTestHelper.RunAsync("shopt -s dotglob\n" + command);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdErr);
        var lines = result.StdOut.Split('\n');
        Assert.Contains(bashOptions, lines);
        Assert.Contains(setOptions, lines);
    }

    [Theory]
    [InlineData("BASHOPTS")]
    [InlineData("SHELLOPTS")]
    public async Task OptionBindingsRetainReadonlyProtectionAcrossAssignmentAndUnsetModes(string name)
    {
        var result = await EvaluationTestHelper.RunAsync(name + "=ignored\n"
            + "printf 'assignment:%s\\n' \"$?\"\n"
            + "unset -n " + name + "\n"
            + "printf 'unset:%s:%s\\n' \"$?\" \"${" + name + "@a}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("assignment:1\nunset:1:r\n", result.StdOut);
        Assert.Equal($"line 1: {name}: readonly variable\nline 3: unset: {name}: cannot unset: readonly variable\n", result.StdErr);
    }

    [Theory]
    [InlineData('-', "+e", "errexit\toff\n", "errexit\ton\n", "e")]
    [InlineData('+', "-e", "set -o errexit\n", "set +o errexit\n", "")]
    public async Task GroupedSetInspectionsObserveFlagRunsAndReuseStableOutput(
        char sign, string initial, string before, string after, string flags)
    {
        var run = new string('e', 128);
        var evaluation = new Eval(Parser.Parse($"set {initial}; set {sign}o{run}o{run}o -- tail; flags=$-"));

        var first = await ReadInspectionAsync();
        var second = await ReadInspectionAsync();
        var third = await ReadInspectionAsync();
        Assert.Null(await evaluation.GetNextStep());

        Assert.All(new[] { first, second, third }, write => Assert.Equal(FileDescriptor.StdOut, write.FileDescriptor));
        Assert.Equal(ShellValue.FromText(before).Bytes.ToArray(), first.Bytes);
        Assert.Equal(ShellValue.FromText(after).Bytes.ToArray(), second.Bytes);
        Assert.Equal(ShellValue.FromText(after).Bytes.ToArray(), third.Bytes);
        Assert.True(second.Content.Equals(third.Content));
        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(ShellValue.FromText(flags), evaluation.Variables["flags"]);
        Assert.Equal(ShellValue.FromText("tail"), Assert.Single(evaluation.PositionalParameters));

        async Task<(FileDescriptor FileDescriptor, ReadOnlyMemory<byte> Content, byte[] Bytes)> ReadInspectionAsync()
        {
            var write = Assert.IsType<WriteOperation>(await evaluation.GetNextStep());
            var inspection = (write.FileDescriptor, write.Content, write.Content.ToArray());
            write.SetResult(DescriptorWriteResult.Open);
            return inspection;
        }
    }

    [Theory]
    [InlineData("-o -o -e -o")]
    [InlineData("-ooe +o")]
    public async Task SetContinuesApplyingOptionsAndPositionalsAfterInspectionCloses(string options)
    {
        var evaluation = new Eval(Parser.Parse($"set {options} -- tail; status=$?; flags=$-"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ShellValue.FromText("0"), evaluation.Variables["status"]);
        Assert.Equal(ShellValue.FromText("e"), evaluation.Variables["flags"]);
        Assert.Equal(ShellValue.FromText("tail"), Assert.Single(evaluation.PositionalParameters));
        var inspection = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdOut, inspection.Descriptor);
        Assert.Equal("errexit\toff\n"u8.ToArray(), inspection.Content);
    }

    [Theory]
    [InlineData("-o invalid", 2, false, "line 1: set: invalid: invalid option name\n")]
    [InlineData("-o invalid", 2, true, "")]
    [InlineData("-oz", 1, true, "")]
    public async Task SetValidatesLaterOptionsAfterInspectionCloses(
        string invalid, int status, bool merged, string diagnostic)
    {
        var evaluation = new Eval(Parser.Parse("set -- keep; "
            + $"if set -o -e -o {invalid} -- tail {(merged ? "2>&1" : "")}; then status=0; else status=$?; fi; flags=$-"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);
        if (!merged)
            host.EnqueueAppendResult(DescriptorWriteResult.Open);

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ShellValue.FromText(status.ToString()), evaluation.Variables["status"]);
        Assert.Equal(ShellValue.FromText("e"), evaluation.Variables["flags"]);
        Assert.Equal(ShellValue.FromText("keep"), Assert.Single(evaluation.PositionalParameters));
        FileDescriptor[] expected = merged ? [FileDescriptor.StdOut] : [FileDescriptor.StdOut, FileDescriptor.StdErr];
        Assert.Equal(expected, host.Appends.Select(static write => write.Descriptor));
        Assert.Equal("errexit\toff\n"u8.ToArray(), host.Appends[0].Content);
        if (!merged)
            Assert.Equal(ShellValue.FromText(diagnostic).Bytes.ToArray(), host.Appends[1].Content);
    }

    [Fact]
    public async Task SetKeepsAnEarlierInspectionFailureAfterDownstreamClosure()
    {
        var evaluation = new Eval(Parser.Parse(
            "if set -o -o -e -o -- tail; then status=0; else status=$?; fi; flags=$-"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Synthetic write failure\n")));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ShellValue.FromText("1"), evaluation.Variables["status"]);
        Assert.Equal(ShellValue.FromText("e"), evaluation.Variables["flags"]);
        Assert.Equal(ShellValue.FromText("tail"), Assert.Single(evaluation.PositionalParameters));
        Assert.Equal(new[] { FileDescriptor.StdOut, FileDescriptor.StdErr, FileDescriptor.StdOut },
            host.Appends.Select(static write => write.Descriptor));
        Assert.Equal("errexit\toff\n"u8.ToArray(), host.Appends[0].Content);
        Assert.Equal("line 1: set: write error: Synthetic write failure\n"u8.ToArray(), host.Appends[1].Content);
        Assert.Equal("errexit\toff\n"u8.ToArray(), host.Appends[2].Content);
    }

    [Theory]
    [InlineData("-o", "<1><1><keep>off", TopLevelSetClosedOutput)]
    [InlineData("-o -o", "<1><1><keep>off", TopLevelSetClosedOutput)]
    [InlineData("-o +oo", "<1><1><keep>off", TopLevelSetClosedOutput + TopLevelSetClosedOutput)]
    [InlineData("-o -o --", "<0><0><>off", "")]
    [InlineData("-o -e -- tail", "<0><1><tail>on", "")]
    [InlineData("-o -o invalid -- tail", "<2><1><keep>off", "line 1: set: invalid: invalid option name\n")]
    [InlineData("-o ''", "<0><1><>off", "")]
    public async Task SetChecksShellClosedOutputForInspectionsWithoutAFollowingArgument(
        string options, string output, string diagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption
            + $"set -- keep; if set {options} 1>&- 2>diagnostic; then status=0; else status=$?; fi; "
            + "printf '<%s><%s><%s>' \"$status\" \"$#\" \"$1\"; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(diagnostic, host.ReadFile("/diagnostic"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("+e", "-e --help changed", "off")]
    [InlineData("-e", "+o errexit --help changed", "on")]
    [InlineData("+e", "-o --help", "off")]
    [InlineData("+e", "-o invalid --help", "off")]
    public async Task SetHelpKeepsEarlierOptionChangesAndPositionalsUnpublished(
        string initial, string options, string expectedMode)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption
            + $"set -- keep; set {initial}; if set {options}; then status=0; else status=$?; fi; "
            + "printf '<%s><%s>' \"$status\" \"$1\"; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("set: set [-e|+e]", host.StandardOutput.TextContent);
        Assert.EndsWith("<2><keep>" + expectedMode, host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Theory]
    [InlineData("-e -z", "off", "line 1: set: -z: invalid option\n" + SetUsage)]
    [InlineData("-e -o invalid", "on", "line 1: set: invalid: invalid option name\n")]
    public async Task SetSeparatesShortFlagValidationFromApplyingNamedOptions(
        string options, string expectedMode, string expectedDiagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption
            + $"if set {options}; then status=0; else status=$?; fi; "
            + "printf '<%s>' \"$status\"; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2>" + expectedMode, host.StandardOutput.TextContent);
        Assert.Equal(expectedDiagnostic, host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("set -e -Q", 2, "off", "set: -Q: invalid option\n" + SetUsage)]
    [InlineData("set -e -o bogus", 2, "on", "set: bogus: invalid option name\n")]
    [InlineData("set + -e -Q", 1, "on", "set: -Q: invalid option\n" + SetUsage)]
    [InlineData("set +o >&-", 1, "off", SetClosedOutput)]
    public async Task SetErrorsIncludeTheSourcedLineAndKeepReachedEffects(
        string invocation, int status, string mode, string diagnostic)
    {
        await using var host = new TestHost();
        host.AddFile("/body", $"# Leading line\nif {invocation}; then status=0; else status=$?; fi\n");
        var result = await host.RunAsync(ReportOption + "set -- keep\nsource ./body\nprintf '<%s><%s>' \"$status\" \"$1\"; flag",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<{status}><keep>{mode}", host.StandardOutput.TextContent);
        Assert.Equal("./body: line 2: " + diagnostic, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task RedirectedSetErrorUsesTheContinuedFlagLineInRetainedSource()
    {
        const string script = "source ./body\nf; printf '<%s>' \"$?\"";
        await using var host = new TestHost();
        host.AddFile("/body", "# Leading line\nf(){\nset \\\n-Q \\\n2>diagnostic\n}\n");
        var result = await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2>", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal("./body: line 4: set: -Q: invalid option\n" + SetUsage, host.ReadFile("/diagnostic"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("-e + --help ignored", "--", "on")]
    [InlineData("-e + +ezq ignored", "+z", "off")]
    [InlineData("-oz errexit", "-z", "on")]
    [InlineData("-oeezq errexit", "-z", "on")]
    public async Task InvalidFlagReachedDuringSetApplicationKeepsEarlierChanges(
        string options, string flagSpelling, string expectedMode)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption
            + $"set -- keep; if set {options}; then status=0; else status=$?; fi; "
            + "printf '<%s><%s>' \"$status\" \"$1\"; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1><keep>" + expectedMode, host.StandardOutput.TextContent);
        Assert.Equal($"line 1: set: {flagSpelling}: invalid option\n" + SetUsage, host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("$'-e\\377z'", "<2>off", (byte)'-', (byte)0xff)]
    [InlineData("-e + $'+e\\376z'", "<1>off", (byte)'+', (byte)0xfe)]
    public async Task SetInvalidFlagReportsTheSignAndTheFirstInvalidByte(
        string options, string expected, byte sign, byte flag)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption
            + $"if set {options}; then status=0; else status=$?; fi; "
            + "printf '<%s>' \"$status\"; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        byte[] diagnostic = [.. "line 1: set: "u8, sign, flag, .. ": invalid option\n"u8, .. System.Text.Encoding.ASCII.GetBytes(SetUsage)];
        Assert.Equal(diagnostic, host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("2>&1", "line 1: set: --: invalid option\n" + SetUsage)]
    [InlineData("2>&-", "")]
    public async Task SetInvalidFlagUsesTheReachedDiagnosticDescriptor(string redirection, string diagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption
            + $"if set -e + --help {redirection}; then status=0; else status=$?; fi; "
            + "printf '<%s>' \"$status\"; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(diagnostic + "<1>on", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Fact]
    public async Task SetHelpOnClosedStdoutLeavesErrexitEnabled()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("set -e; set +e --help >&-; printf unreachable", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Fact]
    public async Task SetInspectionLeavesTheOptionTerminatorForPositionalReplacement()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("set -- old; set -eo -- new; printf '<%s><%s>' \"$1\" \"$-\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("errexit\ton\n<new><e>", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
    }

    [Theory]
    [InlineData("+", "|1|<old>off")]
    [InlineData("+ +e", "|1|<old>off")]
    [InlineData("+ --", "|0|<>off")]
    [InlineData("+ + -- a", "|1|<a>off")]
    [InlineData("+ - -- a", "|2|<--><a>off")]
    [InlineData("-- + -e", "|2|<+><-e>off")]
    [InlineData("+ -e '' x", "|2|<><x>on")]
    public async Task BarePlusContinuesScanningSetOperands(string operands, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption + "set -- old; set " + operands
            + "; printf '|%s|' \"$#\"; printf '<%s>' \"$@\"; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("-u")]
    [InlineData("+x")]
    [InlineData("-o unknown")]
    [InlineData("+o pipefail")]
    public async Task UnsupportedOptionsReturnAnErrorAndDiagnostic(string option)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("set " + option, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.StartsWith("line 1: set: ", host.StandardError.TextContent);
        Assert.Contains("option", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("set +e; f(){ local -; set -e; flag; }; f; flag", 0, "onoff")]
    [InlineData("set -e; f(){ local -; set +o errexit; flag; }; f; flag; false; printf unreached", 1, "offon")]
    [InlineData("set +e; f(){ set -e; local -; set +e; flag; }; f; flag", 0, "offon")]
    [InlineData("set +e; f(){ local -; set -e; local -; flag; }; f; flag", 0, "onoff")]
    [InlineData("set +e; g(){ local -; set +e; flag; }; f(){ local -; set -e; g; flag; }; f; flag", 0, "offonoff")]
    [InlineData("""set +e; f(){ local -; set -e; false; flag; return 7; }; if f; then printf taken; else printf '%s:' "$?"; fi; flag""", 0, "on7:off")]
    [InlineData("""set +e; f(){ local -; set -e; flag; return 7; }; ! f; printf '%s:' "$?"; flag""", 0, "on0:off")]
    [InlineData("set +e; f(){ prefix=yes eval 'local -; set -e'; flag; }; f; flag", 0, "onoff")]
    [InlineData("set +e; f(){ (local -; set -e; flag); set -e; }; f; flag", 0, "onon")]
    [InlineData("""set +e; f(){ x=$(local -; set -e; flag); printf '<%s>' "$x"; set -e; }; f; flag""", 0, "<on>on")]
    [InlineData("set +e; f(){ local -; set -e; g(){ local -; set +e; flag; }; g | cat; flag; }; f; flag", 0, "offonoff")]
    [InlineData("""set +e; f(){ set -- -; command local "$@"; set -e; flag; }; f; flag""", 0, "onoff")]
    [InlineData("""set +e; f(){ builtin local -- "-" value=kept; set -e; printf '<%s>' "$value"; }; f; flag""", 0, "<kept>off")]
    [InlineData("set +e; f(){ local -f -; set -e; flag; }; f; flag", 0, "onoff")]
    public async Task LocalDashRestoresSavedOptionsAtTheOwningFunctionBoundary(string script, int status, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption + script, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task LocalDashReachedThroughPrefixedSourceOutlivesTheSourceReturn()
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "local -; set -e; return 0\n");
        var result = await host.RunAsync(ReportOption
            + "set +e; f(){ prefix=yes source ./unit; flag; }; f; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("onoff", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("set -e; exit 7", 7, "7:on")]
    [InlineData("set -e; false", 1, "1:on")]
    [InlineData("set -e; return 0", 0, "0:off")]
    public async Task ExitTrapObservesTheOptionStateAtShellTermination(string body, int status, string output)
    {
        var memory = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        var result = await host.RunAsync(
            ReportOption + """set +e; trap 'printf '%s:' "$?"; flag' EXIT; f(){ local -; """ + body + "; }; f",
            CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task LocalDashInspectionFollowsFunctionAndChildScope()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption + """
            set +e
            g(){ local -p -; printf 'g=%s;' "$?"; declare -p -; }
            f(){ local value=kept -; local; g; (local -p -); set -e; }
            f
            flag
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("local -\ndeclare -- value=\"kept\"\ng=1;declare -- -\ndeclare -- -\noff", host.StandardOutput.TextContent);
        Assert.Equal("line 2: local: -: not found\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task DeclareNamedInspectionCanSeeTheSnapshotWithoutListingIt()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            f() {
                local -
                declare -p | while IFS= read -r line; do
                    if [[ $line == 'declare -- -' ]]; then printf unexpected; fi
                done
                declare -p -
            }
            f
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -- -\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("f(){ local x=inner; false; }; f callee", 1, "1:callee:outer")]
    [InlineData("g(){ local x=deep; false; }; f(){ local x=inner; g nested; }; f callee", 1, "1:nested:outer")]
    [InlineData("f(){ false; }; x=prefix f callee", 1, "1:callee:outer")]
    [InlineData("f(){ local x=inner; eval 'false'; }; f callee", 1, "1:callee:outer")]
    [InlineData("f(){ local x=inner; source ./unit sourced; }; f callee", 1, "1:sourced:outer")]
    [InlineData("f(){ local x=inner; return 7; }; f callee", 7, "7:caller:outer")]
    [InlineData("f(){ local x=inner; false; }; f callee || printf handled", 0, "handled0:caller:outer")]
    public async Task ErrexitExitTrapUsesTheReachedArgumentsAfterLocalRestoration(string body, int exitCode, string output)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "false\n");
        var result = await host.RunAsync(
            """set -- caller; x=outer; set -e; trap 'printf "%s:%s:%s" "$?" "$1" "$x"' EXIT; """
            + body, CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ErrexitUnwindsOnlyTheNearestActiveSimpleCallRedirection()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            set -- caller
            x=outer
            set -e
            trap 'printf "%s:%s:%s" "$?" "$1" "$x"' EXIT
            g(){ local x=deep; false >direct; }
            f(){ local x=inner; g nested >inner; }
            f callee >outer
            """, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.ReadFileBytes("/direct"));
        Assert.Empty(host.ReadFileBytes("/inner"));
        Assert.Equal("1:callee:outer", host.ReadFile("/outer"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("((0))")]
    [InlineData("false | true")]
    [InlineData("(false)")]
    public async Task CompoundErrexitRunsTheExitActionInsideTheCallRedirection(string failure)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            """set -- caller; x=outer; set -e; trap 'printf "%s:%s:%s" "$?" "$1" "$x"' EXIT; """
            + "f(){ local x=inner; " + failure + "; }; f callee >out", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("1:callee:outer", host.ReadFile("/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ErrexitReportsActiveTimingBeforeTheExitAction()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            set -- caller
            set -e
            TIMEFORMAT=timed
            trap 'printf "trap:%s:%s" "$?" "$1" >&2' EXIT
            f(){ time { false; } 2>err; }
            f callee
            """, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("timed\ntrap:1:callee", host.ReadFile("/err"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("-e", "callee:inner")]
    [InlineData("+e", "caller:outer")]
    public async Task RequiredParameterExitKeepsItsOwnScopePolicy(string option, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "set -- caller; x=outer; set " + option
            + """; trap 'printf "%s:%s" "$1" "$x"' EXIT; f(){ local x=inner; : ${missing?required}; }; f callee""",
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal("line 1: missing: required\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("unset v; : \"${v:?required}\"", "v: required", 127)]
    [InlineData("set -e; unset v; : \"${v:?required}\"", "v: required", 1)]
    [InlineData("v=x; : \"${v@X}\"", "${v@X}: bad substitution", 127)]
    public async Task CommandStringExpansionExitKeepsTheFailedExpansionPipelineStatus(string command, string diagnostic, int status)
    {
        await using var host = new TestHost();
        var script = """trap 'printf "[%s:%s]" "$?" "${PIPESTATUS[*]}"' EXIT; """ + command;
        var invocation = new CommandInvocation("bash", [ShellValue.FromText("-c"), ShellValue.FromText(script), ShellValue.FromText("label")],
            ShellPath.Root, [], null, null, null);

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, CancellationToken.None);

        Assert.Equal(status, result.ShellExitCode);
        Assert.Equal($"[{status}:1]", host.StandardOutput.TextContent);
        Assert.Equal("label: line 1: " + diagnostic + "\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task TestedSourceUnwindsTheFunctionAndItsRedirectionsBeforeCommandStringExit()
    {
        await using var host = EndToEndHarness.BuildTree([("/reader", ": \"${v:?required}\"\n")]);
        const string script = """
            unset v; set -e; tag=outer
            trap 'printf "[%s:%s:%s]" "$?" "${PIPESTATUS[*]}" "$tag" >&2' EXIT
            f(){ local tag=inner; if source ./reader 2>errors; then :; fi; }
            f
            """;
        var invocation = new CommandInvocation("bash", [ShellValue.FromText("-c"), ShellValue.FromText(script), ShellValue.FromText("label")],
            ShellPath.Root, [], null, null, null);

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, CancellationToken.None);

        Assert.Equal(127, result.ShellExitCode);
        Assert.Equal("[127:1:outer]", host.StandardError.TextContent);
        Assert.Equal("./reader: line 1: v: required\n", host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(": \"${v:?required}\"", "[127:1][127:0 127]", 127)]
    [InlineData(": <\"${v:?required}\"", "[127:0 127]", 127)]
    [InlineData("eval ': \"${v:?required}\"'", "[1:0 1]", 1)]
    [InlineData("{ : \"${v:?required}\"; }", "[1:0 1]", 1)]
    public async Task CommandStringPipelineUsesTheReachedExitBoundary(string stage, string output, int status)
    {
        await using var host = new TestHost();
        var script = """unset v; trap 'printf "[%s:%s]" "$?" "${PIPESTATUS[*]}"' EXIT; : | """ + stage;
        var invocation = new CommandInvocation("bash", [ShellValue.FromText("-c"), ShellValue.FromText(script), ShellValue.FromText("label")],
            ShellPath.Root, [], null, null, null);

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, CancellationToken.None);

        Assert.Equal(status, result.ShellExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal("label: line 1: v: required\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("false", "callee:inner")]
    [InlineData("eval 'false'", "callee:outer")]
    [InlineData("source ./unit sourced", "sourced:outer")]
    public async Task SubshellErrexitUsesTheReachedExecutionBoundary(string failure, string output)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "false\n");
        var result = await host.RunAsync(
            """set -- caller; x=outer; set -e; (trap 'printf "%s:%s" "$1" "$x"' EXIT; f(){ local x=inner; """
            + failure + "; }; f callee)", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ErrexitExitActionRetainsTheReturnTarget()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            set -e
            trap 'printf "%s:%s" "$?" "$1"; return 9; printf wrong' EXIT
            f(){ false; }
            f callee
            """, CancellationToken.None);

        Assert.Equal(9, result.ExitCode);
        Assert.Equal("1:callee", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task ErrexitKeepsTheCompoundDescriptorThroughTheActionAndAwaitsItsClose()
    {
        var memory = new MemoryGovernor(128 * 1024);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = memory,
            RecordAppends = true,
            BeforeDescriptorClose = _ => closeEntered.TrySetResult()
        };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueCloseDescriptorTask(releaseClose.Task);
        var running = BashScript.RunAsync(host,
            """set -e; set -- caller; trap 'printf "%s:%s" "$?" "$1"' EXIT; f(){ { false; } >out; }; f callee""",
            EvaluationOptions.Default, CancellationToken.None);
        try
        {
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes > 0);
            var output = Assert.Single(host.Appends);
            Assert.Equal(new FileDescriptor(9), output.Descriptor);
            Assert.Equal("1:callee"u8.ToArray(), output.Content);
        }
        finally
        {
            releaseClose.TrySetResult();
        }

        Assert.Equal(1, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Single(host.Appends);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task ErrexitWaitsForDescriptorCleanupAndRunsExitTrapOnce()
    {
        var memory = new MemoryGovernor(64 * 1024);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = memory,
            RecordAppends = true,
            BeforeDescriptorClose = _ => closeEntered.SetResult()
        };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueCloseDescriptorTask(closeComplete.Task);
        var running = BashScript.RunAsync(host,
            "set -e; exec 3>/out; trap 'printf trap:%s \"$?\"' EXIT; false; printf wrong",
            EvaluationOptions.Default, CancellationToken.None);
        try
        {
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes > 0);
            var output = Assert.Single(host.Appends);
            Assert.Equal(FileDescriptor.StdOut, output.Descriptor);
            Assert.Equal("trap:1"u8.ToArray(), output.Content);
        }
        finally
        {
            closeComplete.TrySetResult();
        }

        Assert.Equal(1, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Single(host.Appends);
        Assert.Equal(0, memory.ReservedBytes);
    }
}

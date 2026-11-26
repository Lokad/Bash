using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("!\n", 1)]
    [InlineData("!; ", 1)]
    [InlineData("! !; ", 0)]
    [InlineData("! # empty command\n", 1)]
    [InlineData("time; ", 0)]
    [InlineData("time !; ", 1)]
    [InlineData("! ! time; ", 0)]
    public async Task BarePipelinePrefixPublishesSuccessBeforeNegationAndClearsLastArgument(string prefix, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync("TIMEFORMAT=; : anchored; (exit 7) | (exit 3)\n" + prefix
            + """printf '<%s|%s|%s>' "$?" "${PIPESTATUS[*]}" "$_" """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<{exitCode}|0|>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Theory]
    [InlineData("export item[0]=b | :", "<4|4 0|anchored>",
        "line 2: export: `item[0]': not a valid identifier\n")]
    [InlineData(": | command readonly item[0]+=b 2>&-", "<4|0 4|anchored>", "")]
    [InlineData("readonly item=old; : anchored; export item=new | :", "<4|4 0|anchored>",
        "line 2: item: readonly variable\n")]
    [InlineData("declare -A item=([key]=old); : anchored; export -a item=new | :", "<4|4 0|anchored>",
        "line 2: export: item: cannot convert associative to indexed array\n")]
    [InlineData("declare -n item; : anchored; builtin export item=bad-name | :", "<4|4 0|anchored>",
        "line 2: export: `bad-name': not a valid identifier\nline 2: export: `': not a valid identifier\n")]
    [InlineData("readonly item[0]=b bad-name | :", "<4|4 0|anchored>",
        "line 2: readonly: `item[0]': not a valid identifier\nline 2: readonly: `bad-name': not a valid identifier\n")]
    [InlineData("{ export item[0]=b; } | :", "<1|1 0|anchored>",
        "line 2: export: `item[0]': not a valid identifier\n")]
    [InlineData("export 'bad-name=b' | :", "<1|1 0|anchored>",
        "line 2: export: `bad-name=b': not a valid identifier\n")]
    public async Task DirectPipelineDeclarationStagesExposeAssignmentFailureStatus(
        string command, string expectedOutput, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync(": anchored\n" + command
            + "\nprintf '<%s|%s|%s>' \"$?\" \"${PIPESTATUS[*]}\" \"$_\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task PipelineDeclarationAssignmentFailureReachesTheExitTrap()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            trap 'printf "EXIT:%s:%s" "$?" "${PIPESTATUS[*]}"' EXIT
            set -e
            export item[0]=b | :
            printf skipped
            """);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal("EXIT:4:4 0", result.StdOut);
        Assert.Equal("line 3: export: `item[0]': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Theory]
    [InlineData("PIPESTATUS=manual; false", "<1|manual|>")]
    [InlineData("declare PIPESTATUS; false | true", "<1|unset|>")]
    [InlineData("declare -a PIPESTATUS; false | true", "<1|unset|a>")]
    [InlineData("export PIPESTATUS; false", "<1|unset|x>")]
    [InlineData("PIPESTATUS=(); false | true", "<1|1 0|a>")]
    [InlineData("f(){ local PIPESTATUS=manual; false; printf '%s:' \"$PIPESTATUS\"; }; f", "manual:<0|0|a>")]
    public async Task PipelineStatusKeepsUnassignedBindingsInvisible(string setup, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + """; printf '<%s|%s|%s>' "$?" "${PIPESTATUS[*]-unset}" "${PIPESTATUS@a}" """);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("PIPESTATUS=manual false", "<1|7 3>")]
    [InlineData("PIPESTATUS=manual command -v true", "true\n<0|7 3>")]
    [InlineData("PIPESTATUS=manual exec", "<0|7 3>")]
    [InlineData("PIPESTATUS=manual let 'PIPESTATUS=2'", "<0|2 3>")]
    [InlineData("PIPESTATUS=manual eval false", "<1|1>")]
    [InlineData("PIPESTATUS=manual read -a other <<< value", "<0|0>")]
    [InlineData("PIPESTATUS=manual false 2>/dev/null >missing-parent/out", "<1|1>")]
    public async Task PipelineStatusSelectsTheBuiltinPublicationPhaseAfterRedirectionSetup(string command, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("(exit 7) | (exit 3); " + command
            + """; printf '<%s|%s>' "$?" "${PIPESTATUS[*]-unset}" """);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("PIPESTATUS=([5]=9)", "<0|0|5>")]
    [InlineData("PIPESTATUS=([5]=9); (exit 7) | (exit 3)", "<3|3 7|1 5>")]
    [InlineData("PIPESTATUS=([1]=9); (exit 7) | (exit 3)", "<3|3|1>")]
    [InlineData("PIPESTATUS=([3]=9 [5]=8)", "<0|0|0>")]
    public async Task PipelineStatusReusesPresentPositionsWhenThePriorArrayFits(string setup, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + """; printf '<%s|%s|%s>' "$?" "${PIPESTATUS[*]}" "${!PIPESTATUS[*]}" """);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("PIPESTATUS[7]=manual", "<0|0|7 3 manual>")]
    [InlineData("PIPESTATUS+=([7]=manual)", "<0|0|7 3 manual>")]
    [InlineData("PIPESTATUS=([7]=manual)", "<0|7|manual>")]
    public async Task PipelineStatusAssignmentRevealsOnlyTheRetainedIndexedValues(string assignment, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "declare -a PIPESTATUS; (exit 7) | (exit 3); " + assignment + """
             captured="${PIPESTATUS[*]}"; printf '<%s|%s|%s>' "${PIPESTATUS[*]}" "${!PIPESTATUS[*]}" "$captured"
            """);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("dest=(9 8)", "<3|7 3>:<0>")]
    [InlineData("dest=scalar", "<3|scalar>:<scalar>")]
    [InlineData("unset dest", "<3|unset>:<unset>")]
    public async Task PipelineStatusResolvesTheReachedNamerefBinding(string setup, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(setup + """
            ; f(){ local -n PIPESTATUS=dest; (exit 7) | (exit 3); printf '<%s|%s>' "$?" "${PIPESTATUS[*]-unset}"; }
            f
            printf ':<%s>' "${dest[*]-unset}"
            """);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("((0))")]
    [InlineData("[[ x == y ]]")]
    public async Task PipelineStatusPublishesTheNegatedCompoundStatus(string command)
    {
        var result = await EvaluationTestHelper.RunAsync("! " + command
            + """; printf '<%s|%s>' "$?" "${PIPESTATUS[*]}" """);

        Assert.Equal("<0|0>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("((0))", "<1|0>")]
    [InlineData("[[ x == y ]]", "<1|0>")]
    [InlineData("PIPESTATUS=manual false", "<1|0>")]
    [InlineData("false", "<1|1>")]
    public async Task PipelineStatusInExitActionUsesTheReachedPublicationPhase(string command, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            """set -e; trap 'printf "<%s|%s>" "$?" "${PIPESTATUS[*]}"' EXIT; """ + command);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("! ", 0)]
    public async Task PipelineStatusPublishesAFailedSimpleCommandAfterItsDiagnosticAndClose(string negation, int expectedStatus)
    {
        var state = new Eval(Parser.Parse(negation + ": 2>errors >operand"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        var stderr = new FileDescriptor(17);
        var open = Assert.IsType<OpenFileOperation>(await NextStepAsync(state));
        open.SetResult(FileOpenResult.Opened(stderr, readSize: null));
        var operand = Assert.IsType<OpenFileOperation>(await NextStepAsync(state));
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        operand.SetResult(FileOpenResult.Failed(new OperationError("provider refused\n")));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(stderr, diagnostic.FileDescriptor);
        Assert.Equal("operand: provider refused\n", Text(diagnostic.Content));
        Assert.False(state.IndexedArrays.ContainsKey("PIPESTATUS"));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        var close = Assert.IsType<CloseDescriptorOperation>(await NextStepAsync(state));
        Assert.Equal(stderr, close.Descriptor);
        Assert.False(state.IndexedArrays.ContainsKey("PIPESTATUS"));
        close.SetResult(new OperationError("secondary close failure\n"));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(expectedStatus, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("1"), Assert.Single(state.IndexedArrays["PIPESTATUS"]));
    }

    [Fact]
    public async Task PipelineStatusPublishesAllSettledStagesWhileTheAllowanceIsFull()
    {
        var state = new Eval(Parser.Parse(":; ! first | second | third"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        for (var index = 0; index < 2; index++)
        {
            var creation = Assert.IsType<CreatePipeOperation>(await NextStepAsync(state));
            creation.SetResult(PipeCreationResult.Created(new PipeDescriptors(10 + 2 * index, 11 + 2 * index)));
        }
        var pipeline = Assert.IsType<RunPipelineEvaluations>(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("0"), Assert.Single(state.IndexedArrays["PIPESTATUS"]));
        state.Memory.ReleaseUnusedCapacity();
        using var blocking = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        pipeline.SetResult([7, 0, 4]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new[] { ShellValue.FromText("7"), ShellValue.FromText("0"), ShellValue.FromText("4") },
            state.IndexedArrays["PIPESTATUS"]);
    }

    [Fact]
    public void PipelineStatusSharesZeroBytesAcrossPublications()
    {
        var variables = Variables.CreateEmpty();
        variables.SetPipelineStatuses([0, 127, 0, 255], default, CancellationToken.None);
        var first = variables.IndexedArrays["PIPESTATUS"].PresentValues.ToArray();

        Assert.Equal(["0", "127", "0", "255"], first.Select(status => status.ToUtf8String()));
        Assert.True(first[0].Bytes.Equals(first[2].Bytes));

        variables.SetPipelineStatuses([0], default, CancellationToken.None);
        var next = Assert.Single(variables.IndexedArrays["PIPESTATUS"].PresentValues);
        Assert.True(first[0].Bytes.Equals(next.Bytes));
    }

    [Fact]
    public void PipelineStatusCancellationDuringTheLastResultKeepsThePreviousArray()
    {
        var variables = Variables.CreateEmpty();
        variables.SetPipelineStatuses([7, 3], default, CancellationToken.None);
        var previous = variables.IndexedArrays["PIPESTATUS"];
        using var cancellation = new CancellationTokenSource();
        var statuses = new CancelOnLastStatus([1, 2, 3], cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            variables.SetPipelineStatuses(statuses, default, cancellation.Token));

        Assert.Same(previous, variables.IndexedArrays["PIPESTATUS"]);
        Assert.Equal(["7", "3"], previous.PresentValues.Select(status => status.ToUtf8String()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PipelineStatusReplacesABorrowedArrayWithoutCopyingIt(bool indirect)
    {
        var caller = Variables.CreateEmpty();
        BindingName name = indirect ? "target" : "PIPESTATUS";
        Assert.True(caller.TryAssignArray(name, Enumerable.Repeat(ShellValue.FromText("old"), 64).ToArray(),
            ArrayKind.Indexed));
        if (indirect)
            Assert.True(caller.TrySetNameref("PIPESTATUS", name.ToValue()));
        caller.SetReadonlyBinding(name);
        var previous = caller.IndexedArrays[name];

        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var command = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
        var before = memory.Workspace.Bytes;
        Assert.True(command.IsBorrowedBinding(name));

        command.SetPipelineStatuses([7], default, CancellationToken.None);

        Assert.Equal(before, memory.Workspace.Bytes);
        Assert.False(command.IsBorrowedBinding(name));
        Assert.Equal(indirect, command.IsBorrowedBinding("PIPESTATUS"));
        Assert.True(command.IsReadonlyBinding(name));
        Assert.Equal(ShellValue.FromText("7"), Assert.Single(command.IndexedArrays[name].PresentValues));
        Assert.Same(previous, caller.IndexedArrays[name]);
        Assert.Equal(64, previous.Count);
        Assert.All(previous.PresentValues, value => Assert.Equal(ShellValue.FromText("old"), value));
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void PipelineStatusCancellationLeavesThePriorArrayBorrowed()
    {
        var caller = Variables.CreateEmpty();
        caller.SetPipelineStatuses([7, 3], default, CancellationToken.None);
        var previous = caller.IndexedArrays["PIPESTATUS"];
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        using var cancellation = new CancellationTokenSource();
        var command = Variables.CreateCommandScope(caller, memory, cancellation.Token);
        var before = memory.Workspace.Bytes;
        var statuses = new CancelOnLastStatus([1], cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            command.SetPipelineStatuses(statuses, default, cancellation.Token));

        Assert.True(command.IsBorrowedBinding("PIPESTATUS"));
        Assert.Equal(before, memory.Workspace.Bytes);
        Assert.Same(previous, caller.IndexedArrays["PIPESTATUS"]);
        Assert.Equal(["7", "3"], previous.PresentValues.Select(status => status.ToUtf8String()));
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task PipelineStatusRemainsAvailableUntilTheNextCommandCompletes()
    {
        var state = new Eval(Parser.Parse("false | true; printf '%s' \"${PIPESTATUS[*]}\""));
        var creation = Assert.IsType<CreatePipeOperation>(await NextStepAsync(state));
        creation.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));
        var pipeline = Assert.IsType<RunPipelineEvaluations>(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("PIPESTATUS"));
        pipeline.SetResult([1, 0]);

        var write = await NextWriteAsync(state);
        Assert.Equal("1 0", Text(write.Content));
        Assert.Equal(new[] { ShellValue.FromText("1"), ShellValue.FromText("0") }, state.IndexedArrays["PIPESTATUS"]);
        Assert.False(state.Completion.IsCompleted);
        write.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("0"), Assert.Single(state.IndexedArrays["PIPESTATUS"]));
    }

    private sealed class CancelOnLastStatus(int[] statuses, CancellationTokenSource cancellation) : IReadOnlyList<int>
    {
        public int Count => statuses.Length;

        public int this[int index]
        {
            get
            {
                if (index == statuses.Length - 1)
                    cancellation.Cancel();
                return statuses[index];
            }
        }

        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)statuses).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

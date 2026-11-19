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
}

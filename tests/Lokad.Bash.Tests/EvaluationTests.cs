using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData(new[] { 7, 7, 9, 9, 11, 11, 7 }, new[] { 7, 9, 11 })]
    [InlineData(new[] { 2, 9, 9, 7, 0 }, new[] { 9, 7 })]
    [InlineData(new int[0], new int[0])]
    public void Evaluation_CollectsBorrowedDescriptorsInFirstEncounterOrder(int[] borrowedIds, int[] expectedIds)
    {
        var borrowed = borrowedIds.Select(id => new FileDescriptor(id)).ToArray();
        var descriptors = new EvaluationDescriptorSetup(DescriptorTable.Snapshot.Standard, borrowed);
        var state = new Eval(
            Parser.Parse(":"), descriptors, publishNextStep: null, onComplete: null,
            EvaluationOptions.Default, new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes), sourceMemory: null);

        Assert.Equal(
            new[] { 0, 1, 2 }.Concat(expectedIds),
            state.CollectDescriptorsAvailableToNestedEvaluation().Select(descriptor => descriptor.Id));
        Assert.Equal(borrowedIds, borrowed.Select(descriptor => descriptor.Id));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_EvalCommitsNonPrefixedSideEffects()
    {
        var script = Parser.Parse("x=1 eval 'y=$x'");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("x"));
        Assert.Equal("1", GetVariable(state, "y"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_FunctionCommitsNonPrefixedSideEffects()
    {
        var script = Parser.Parse("f() { y=$x; x=2; }; x=1 f");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("x"));
        Assert.Equal("1", GetVariable(state, "y"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_ExportPromotesDeclaredPrefixedName()
    {
        var script = Parser.Parse("x=1 export x");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("1", GetVariable(state, "x"));
        Assert.True(HasExported(state, "x"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_SetCommitsPositionalSideEffects()
    {
        var script = Parser.Parse("x=1 set -- a b");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("x"));
        Assert.Equal(2, state.PositionalParameters.Count);
        Assert.Equal(ShellValue.FromText("a"), state.PositionalParameters[0]);
        Assert.Equal(ShellValue.FromText("b"), state.PositionalParameters[1]);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_LetReadsTemporaryScopeAndCommitsSideEffects()
    {
        var script = Parser.Parse("x=1 let 'y=x+1'");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("x"));
        Assert.Equal("2", GetVariable(state, "y"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_FunctionAttributesPromoteResolvedNamerefTargets()
    {
        var script = Parser.Parse("declare -n export_ref=export_target; declare -n readonly_ref=readonly_target; export_target=outer; readonly_target=outer; f() { export export_ref=exported; readonly readonly_ref=locked; }; export_ref=temp readonly_ref=temp f");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("exported", GetVariable(state, "export_target"));
        Assert.Equal("locked", GetVariable(state, "readonly_target"));
        Assert.True(HasExported(state, "export_target"));
        Assert.True(HasReadonly(state, "readonly_target"));
    }

    [Fact]
    public async Task Evaluation_YieldsRunCommandOperation()
    {
        var script = Parser.Parse("cmd hi");
        var state = new Eval(script);

        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);
        var runCommand = Assert.IsType<RunCommand>(operation);
        var command = runCommand.Command;
        Assert.Equal("cmd", command.CommandName);

        var arguments = command.Arguments.ToList();
        Assert.Single(arguments);
        Assert.Equal("hi", arguments[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_CompletesAfterResult()
    {
        var script = Parser.Parse("cmd hi");
        var state = new Eval(script);

        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        var runCommand = Assert.IsType<RunCommand>(operation);
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        var next = await NextStepAsync(state);
        Assert.Null(next);
    }

    [Theory]
    [InlineData("cmd", 2)]
    [InlineData("cmd || after=1", 0)]
    [InlineData("! cmd; after=1", 0)]
    public async Task Evaluation_OrdinaryStatusControlsFollowingCommands(string script, int expectedExitCode)
    {
        var state = new Eval(Parser.Parse(script));
        var operation = await NextCommandAsync(state, "cmd");
        var result = new CommandExecutionResult.Completed(2);

        operation.SetResult(result);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(expectedExitCode, state.LastExitCode);
        Assert.Equal(script != "cmd", state.Variables.ContainsKey("after"));
    }

    [Fact]
    public async Task Evaluation_BuiltinColon_ReturnsSuccessWithoutOperation()
    {
        var script = Parser.Parse(":");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinTrue_ReturnsSuccessWithoutOperation()
    {
        var script = Parser.Parse("true");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinFalse_ReturnsFailureWithoutOperation()
    {
        var script = Parser.Parse("false");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ExitBuiltin_TerminatesScript()
    {
        var script = Parser.Parse("exit; cmd later");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ExitBuiltin_UsesProvidedStatus()
    {
        var script = Parser.Parse("exit 7; cmd later");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(7, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_LongList_CancellationStopsRemainingCommands()
    {
        var script = Parser.Parse("first; " + string.Concat(Enumerable.Repeat("after=1; ", 5_000)));
        var state = new Eval(script);
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<RunCommand>(await state.GetNextStep(CancellationToken.None));

        cancellation.Cancel();
        operation.SetResult(new CommandExecutionResult.Completed(0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Fact]
    public async Task Evaluation_ListAnd_ExecutesRightOnSuccess()
    {
        var script = Parser.Parse("first && second");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_ListAnd_SkipsRightOnFailure()
    {
        var script = Parser.Parse("first && second");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(1));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ListOr_SkipsRightOnSuccess()
    {
        var script = Parser.Parse("first || second");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ListSequence_AlwaysExecutesRight()
    {
        var script = Parser.Parse("first; second");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(1));

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_ListLeftAssociativity_RespectsExitStatus()
    {
        var script = Parser.Parse("first && second || third");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(1));

        var third = await NextCommandAsync(state, "third");
        third.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_Pipeline_CreatesPipeAndExecutesSegments()
    {
        var script = Parser.Parse("left | right");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));

        var pipeline = await NextPipelineAsync(state);
        Assert.Equal(2, pipeline.Stages.Count);

        var left = await NextCommandAsync(pipeline.Stages[0].Evaluation, "left");
        Assert.Equal(11, left.Command.StdOut.Id);
        left.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(pipeline.Stages[0].Evaluation));

        var right = await NextCommandAsync(pipeline.Stages[1].Evaluation, "right");
        Assert.Equal(10, right.Command.StdIn.Id);
        right.SetResult(new CommandExecutionResult.Completed(7));
        Assert.Null(await NextStepAsync(pipeline.Stages[1].Evaluation));

        pipeline.SetResult([0, 7]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(7, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_PipeAndError_UsesStdErr()
    {
        var script = Parser.Parse("left |& right");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(20, 21)));

        var pipeline = await NextPipelineAsync(state);
        Assert.Equal(2, pipeline.Stages.Count);

        var left = await NextCommandAsync(pipeline.Stages[0].Evaluation, "left");
        Assert.Equal(21, left.Command.StdOut.Id);
        Assert.Equal(21, left.Command.StdErr.Id);
        left.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(pipeline.Stages[0].Evaluation));

        var right = await NextCommandAsync(pipeline.Stages[1].Evaluation, "right");
        Assert.Equal(20, right.Command.StdIn.Id);
        right.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(pipeline.Stages[1].Evaluation));

        pipeline.SetResult([0, 0]);

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_Pipeline_BuiltinStageUsesIsolatedEvaluator()
    {
        var script = Parser.Parse("printf x | cat");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(25, 26)));

        var operation = await NextStepAsync(state);
        var pipeline = Assert.IsType<RunPipelineEvaluations>(operation);
        Assert.Equal(2, pipeline.Stages.Count);

        var append = Assert.IsType<WriteOperation>(await NextStepAsync(pipeline.Stages[0].Evaluation));
        Assert.Equal(26, append.FileDescriptor.Id);
        Assert.Equal("x", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(pipeline.Stages[0].Evaluation));

        var cat = await NextCommandAsync(pipeline.Stages[1].Evaluation, "cat");
        Assert.Equal(25, cat.Command.StdIn.Id);
        cat.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(pipeline.Stages[1].Evaluation));

        pipeline.SetResult([0, 0]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! left | right");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(30, 31)));

        var pipeline = await NextPipelineAsync(state);
        pipeline.SetResult([0, 0]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_FatalForItemExpansion_DoesNotAssignTheLoopVariableOrCreatePipes()
    {
        var state = new Eval(Parser.Parse("i=before; for i in ok ${MISSING:?bad}; do printf changed | cat; done"));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: MISSING: bad\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("before", GetVariable(state, "i"));
    }

    [Fact]
    public async Task Evaluation_Pipeline_ReadonlyPrefixClosesStageRedirection()
    {
        var script = Parser.Parse("readonly X; X=1 left > out | right");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));

        var pipeline = await NextPipelineAsync(state);
        Assert.Equal(2, pipeline.Stages.Count);

        var diagnostic = await NextWriteAsync(pipeline.Stages[0].Evaluation);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: X: readonly variable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        var open = await NextOpenFileAsync(pipeline.Stages[0].Evaluation);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, open.Mode);
        open.SetResult(FileOpenResult.Opened(12, readSize: null));

        var left = await NextCommandAsync(pipeline.Stages[0].Evaluation, "left");
        left.SetResult(new CommandExecutionResult.Completed(1));

        var closeOut = await NextCloseDescriptorAsync(pipeline.Stages[0].Evaluation);
        Assert.Equal(12, closeOut.Descriptor.Id);
        closeOut.SetResult(null);
        Assert.Null(await NextStepAsync(pipeline.Stages[0].Evaluation));

        var right = await NextCommandAsync(pipeline.Stages[1].Evaluation, "right");
        right.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(pipeline.Stages[1].Evaluation));

        pipeline.SetResult([1, 0]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_ReadonlyPrefixClosesStageHereStringDescriptor()
    {
        var script = Parser.Parse("readonly X; X=1 left <<< value | right");
        var state = new Eval(script);

        var pipelinePipe = await NextPipeAsync(state);
        pipelinePipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));

        var pipeline = await NextPipelineAsync(state);
        Assert.Equal(2, pipeline.Stages.Count);

        var diagnostic = await NextWriteAsync(pipeline.Stages[0].Evaluation);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: X: readonly variable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        var hereStringPipe = await NextPipeAsync(pipeline.Stages[0].Evaluation);
        hereStringPipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(12, 13)));

        var inputHost = await StartHereInputAsync(pipeline.Stages[0].Evaluation, 13, "value\n");

        var left = await NextCommandAsync(pipeline.Stages[0].Evaluation, "left");
        left.SetResult(new CommandExecutionResult.Completed(1));

        var closeHereStringRead = await NextCloseDescriptorAsync(pipeline.Stages[0].Evaluation);
        Assert.Equal(12, closeHereStringRead.Descriptor.Id);
        await ShellOperationDispatcher.DispatchAsync(closeHereStringRead, inputHost, CancellationToken.None);
        Assert.Null(await NextStepAsync(pipeline.Stages[0].Evaluation));

        var right = await NextCommandAsync(pipeline.Stages[1].Evaluation, "right");
        right.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(pipeline.Stages[1].Evaluation));

        pipeline.SetResult([1, 0]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_NegatedCommand_InvertsExitStatus()
    {
        var script = Parser.Parse("! left");
        var state = new Eval(script);

        var left = await NextCommandAsync(state, "left");
        left.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_GroupCommand_ExecutesBodyInOrder()
    {
        var script = Parser.Parse("{ first; second; }");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(1));

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_GroupCommand_UsesExitStatusForAndList()
    {
        var script = Parser.Parse("{ first; } && second");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_GroupCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! { first; }");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_ExecutesBodyInOrder()
    {
        var script = Parser.Parse("( first; second; )");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(1));

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_SubshellCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! ( first; )");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_IfCommand_ExecutesThenBranch()
    {
        var script = Parser.Parse("if cond; then yes; else no; fi");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(0));

        var yes = await NextCommandAsync(state, "yes");
        yes.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_IfCommand_ExecutesElseBranch()
    {
        var script = Parser.Parse("if cond; then yes; else no; fi");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(1));

        var no = await NextCommandAsync(state, "no");
        no.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_IfCommand_ExecutesElifBranch()
    {
        var script = Parser.Parse("if c1; then a; elif c2; then b; else c; fi");
        var state = new Eval(script);

        var c1 = await NextCommandAsync(state, "c1");
        c1.SetResult(new CommandExecutionResult.Completed(1));

        var c2 = await NextCommandAsync(state, "c2");
        c2.SetResult(new CommandExecutionResult.Completed(0));

        var b = await NextCommandAsync(state, "b");
        b.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_IfCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! if cond; then yes; fi");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(0));

        var yes = await NextCommandAsync(state, "yes");
        yes.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_ExecutesSingleIteration()
    {
        var script = Parser.Parse("while cond; do body; done");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(0));

        var body = await NextCommandAsync(state, "body");
        body.SetResult(new CommandExecutionResult.Completed(0));

        var condAgain = await NextCommandAsync(state, "cond");
        condAgain.SetResult(new CommandExecutionResult.Completed(1));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_BreakExitsLoop()
    {
        var script = Parser.Parse("while cond; do break; done");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_ContinueSkipsBody()
    {
        var script = Parser.Parse("while cond; do continue; done");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(0));

        var condAgain = await NextCommandAsync(state, "cond");
        condAgain.SetResult(new CommandExecutionResult.Completed(1));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_BreakInvalidArgumentReturnsFailure()
    {
        var script = Parser.Parse("while cond; do break nope; done");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(0));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: break: nope: numeric argument required\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(128, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_AllowsZeroIterations()
    {
        var script = Parser.Parse("while cond; do body; done");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(1));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_UntilCommand_ExecutesSingleIteration()
    {
        var script = Parser.Parse("until cond; do body; done");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(1));

        var body = await NextCommandAsync(state, "body");
        body.SetResult(new CommandExecutionResult.Completed(0));

        var condAgain = await NextCommandAsync(state, "cond");
        condAgain.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WhileCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! while cond; do body; done");
        var state = new Eval(script);

        var cond = await NextCommandAsync(state, "cond");
        cond.SetResult(new CommandExecutionResult.Completed(0));

        var body = await NextCommandAsync(state, "body");
        body.SetResult(new CommandExecutionResult.Completed(0));

        var condAgain = await NextCommandAsync(state, "cond");
        condAgain.SetResult(new CommandExecutionResult.Completed(1));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ForCommand_ExecutesBodyForEachItem()
    {
        var script = Parser.Parse("for i in a b; do body; done");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "body");
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = await NextCommandAsync(state, "body");
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ForCommand_EmptyListReturnsZero()
    {
        var script = Parser.Parse("for i in; do body; done");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ForCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! for i in a; do body; done");
        var state = new Eval(script);

        var body = await NextCommandAsync(state, "body");
        body.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionDefinition_DoesNotEmitOperations()
    {
        var script = Parser.Parse("foo() { cmd hi; }");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionDefinition_AllowsFollowingCommands()
    {
        var script = Parser.Parse("foo() { cmd hi; }; cmd there");
        var state = new Eval(script);

        var cmd = await NextCommandAsync(state, "cmd");
        cmd.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_FunctionDefinition_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! foo() { cmd hi; }");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ExecutesBodyCommands()
    {
        var script = Parser.Parse("foo() { first; second; }; foo; third");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(0));

        var third = await NextCommandAsync(state, "third");
        third.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_UsesLastBodyExitStatus()
    {
        var script = Parser.Parse("foo() { first; second; }; foo && after");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(1));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ReturnBuiltin_ExitsFunctionWithStatus()
    {
        var script = Parser.Parse("foo() { first; return 7; second; }; foo");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(7, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ReturnBuiltin_UsesLastExitStatusWhenOmitted()
    {
        var script = Parser.Parse("foo() { first; return; second; }; foo");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(3));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(3, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_SetsAndRestoresPositionalParameters()
    {
        var script = Parser.Parse("foo() { shift; command echo hi; }; foo a b");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("outer")]);

        var append = await NextWriteAsync(state);
        Assert.Single(state.PositionalParameters);
        Assert.Equal("b", state.PositionalParameters[0].ToUtf8String());
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Single(state.PositionalParameters);
        Assert.Equal("outer", state.PositionalParameters[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ReturnRestoresPositionalParameters()
    {
        var script = Parser.Parse("foo() { return 0; command echo hi; }; foo a");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("outer")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Single(state.PositionalParameters);
        Assert.Equal("outer", state.PositionalParameters[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_LocalBuiltin_AssignsAndClearsVariable()
    {
        var script = Parser.Parse("foo() { local bar=baz; command echo hi; }; foo");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("baz", GetVariable(state, "bar"));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("bar"));
    }

    [Fact]
    public async Task Evaluation_LocalBuiltin_RestoresPreviousValue()
    {
        var script = Parser.Parse("declare bar=old; foo() { local bar=new; command echo hi; }; foo");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("new", GetVariable(state, "bar"));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("old", GetVariable(state, "bar"));
    }

    [Fact]
    public async Task Evaluation_LocalBuiltin_OutsideFunctionReturnsFailure()
    {
        var script = Parser.Parse("local foo");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: local: can only be used in a function\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CallerBuiltin_ReportsFunctionName()
    {
        var script = Parser.Parse("foo() { caller; }; foo");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("0 foo\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CallerBuiltin_OutsideFunctionReturnsFailure()
    {
        var script = Parser.Parse("caller");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_EvaluatesReadContent()
    {
        var script = Parser.Parse("source file; echo after");
        var state = new Eval(script);

        var openFile = await NextOpenFileAsync(state);
        Assert.Equal("/file", openFile.Path.Path);
        Assert.Equal(FileOpenMode.Read, openFile.Mode);
        openFile.SetResult(FileOpenResult.Opened(10, readSize: null));

        var read = await NextReadAsync(state);
        Assert.Equal(10, read.FileDescriptor.Id);
        read.SetResult(ReadResult.Success(Utf8Bytes("echo hi"), [], reachedEoF: true));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(10, close.Descriptor.Id);
        close.SetResult(null);

        var fromFile = await NextWriteAsync(state);
        Assert.Equal("hi\n", Text(fromFile.Content));
        fromFile.SetResult(DescriptorWriteResult.Open);

        var after = await NextWriteAsync(state);
        Assert.Equal("after\n", Text(after.Content));
        after.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_DotBuiltin_EvaluatesReadContent()
    {
        var script = Parser.Parse(". file");
        var state = new Eval(script);

        var openFile = await NextOpenFileAsync(state);
        Assert.Equal("/file", openFile.Path.Path);
        Assert.Equal(FileOpenMode.Read, openFile.Mode);
        openFile.SetResult(FileOpenResult.Opened(11, readSize: null));

        var read = await NextReadAsync(state);
        Assert.Equal(11, read.FileDescriptor.Id);
        read.SetResult(ReadResult.Success(Utf8Bytes("echo dot"), [], reachedEoF: true));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(11, close.Descriptor.Id);
        close.SetResult(null);

        var append = await NextWriteAsync(state);
        Assert.Equal("dot\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_SourceBuiltin_UsageErrorDoesNotOpenAFile()
    {
        var script = Parser.Parse("source --");
        var state = new Eval(script);

        var error = await NextWriteAsync(state);
        Assert.Equal(2, error.FileDescriptor.Id);
        Assert.Equal("source: filename argument required\n", Text(error.Content));
        error.SetResult(DescriptorWriteResult.Open);

        var usage = await NextWriteAsync(state);
        Assert.Equal(2, usage.FileDescriptor.Id);
        Assert.Equal("source: usage: source filename [arguments]\n", Text(usage.Content));
        usage.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EvalBuiltin_ExecutesParsedContent()
    {
        var script = Parser.Parse("eval echo hi");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("hi\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EvalBuiltin_NoArgumentsReturnsSuccess()
    {
        var script = Parser.Parse("eval");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EvalBuiltin_SyntaxErrorReturnsUsageError()
    {
        var script = Parser.Parse("eval if");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal(2, append.FileDescriptor.Id);
        Assert.Contains("syntax error", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_ParsesOptionsAndUpdatesOptind()
    {
        var script = Parser.Parse("getopts ab opt; getopts ab opt");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("-a"), ShellValue.FromText("-b")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("b", GetVariable(state, "opt"));
        Assert.Equal("3", GetVariable(state, "OPTIND"));
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_NoOptionsReturnsFailure()
    {
        var script = Parser.Parse("getopts ab opt");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("?", GetVariable(state, "opt"));
        Assert.Equal("1", GetVariable(state, "OPTIND"));
    }

    [Fact]
    public async Task Evaluation_GetoptsBuiltin_MissingArgumentsReportsUsage()
    {
        var script = Parser.Parse("getopts");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("getopts: usage: getopts optstring name [arg ...]\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentOnlyCommand_AssignsVariable()
    {
        var script = Parser.Parse("FOO=bar");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("bar", GetVariable(state, "FOO"));
    }

    [Theory]
    [InlineData("readonly FOO=bar; before=old after=old; before=new FOO=baz after=new; printf should-not-run")]
    [InlineData("readonly FOO=bar; declare -n reference=FOO; before=old after=old; before=new reference=baz after=new; printf should-not-run")]
    [InlineData("readonly FOO=bar; declare -n reference=FOO; declare -n indirect=reference; before=old after=old; before=new indirect=baz after=new; printf should-not-run")]
    public async Task Evaluation_AssignmentOnlyCommand_ReadonlyReturnsFailure(string source)
    {
        var script = Parser.Parse(source);
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: FOO: readonly variable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("bar", GetVariable(state, "FOO"));
        Assert.Equal("new", GetVariable(state, "before"));
        Assert.Equal("old", GetVariable(state, "after"));
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_ReturnsSuccessForNonZero()
    {
        var script = Parser.Parse("let 1");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_ReturnsFailureForZero()
    {
        var script = Parser.Parse("let 0");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_LetBuiltin_UsesLastArgumentResult()
    {
        var script = Parser.Parse("let 1 0");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TestBuiltin_EvaluatesStringPredicate()
    {
        var script = Parser.Parse("test foo");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TestBuiltin_FalseReturnsFailure()
    {
        var script = Parser.Parse("test \"\"");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BracketBuiltin_StripsClosingBracket()
    {
        var script = Parser.Parse("[ foo ]");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BracketBuiltin_MissingClosingBracketReturnsUsageError()
    {
        var script = Parser.Parse("[ foo");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: [: missing `]'\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_PwdBuiltin_EmitsWriteOperation()
    {
        var script = Parser.Parse("pwd");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal(1, operation.FileDescriptor.Id);
        Assert.Equal("/\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_PwdBuiltin_ReturnsFailureForRejectedOutput()
    {
        var script = Parser.Parse("pwd");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        operation.SetResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: pwd: write error: Write failed\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_PwdVariable_Initialized()
    {
        var script = Parser.Parse("echo $PWD");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("/\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_UpdatesCurrentDirectory()
    {
        var script = Parser.Parse("cd target");
        var state = new Eval(script);

        var operation = await NextPathKindsAsync(state);
        Assert.Single(operation.Paths);
        Assert.Equal("/target", operation.Paths[0].Path);
        operation.SetResult([PathKindResult(FilePathKind.Directory)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("/target", Text(state.CurrentDirectory));
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_UpdatesPwdVariables()
    {
        var script = Parser.Parse("cd target; echo $PWD $OLDPWD");
        var state = new Eval(script);

        var operation = await NextPathKindsAsync(state);
        operation.SetResult([PathKindResult(FilePathKind.Directory)]);

        var append = await NextWriteAsync(state);
        Assert.Equal("/target /\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_FailureDoesNotUpdateDirectory()
    {
        var script = Parser.Parse("cd ./target");
        var state = new Eval(script);
        var initialDirectory = state.CurrentDirectory;

        var operation = await NextPathKindsAsync(state);
        Assert.Equal("/target", Assert.Single(operation.Paths).Path);
        operation.SetResult([PathKindResult(FilePathKind.Missing)]);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: cd: ./target: No such file or directory\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(Text(initialDirectory), Text(state.CurrentDirectory));
    }

    [Fact]
    public async Task Evaluation_CdBuiltin_NoArgumentsReturnsFailure()
    {
        var script = Parser.Parse("unset HOME; cd");
        var state = new Eval(script);
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: cd: HOME not set\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task CdPreviousAnnouncesTheOperandAfterLookupAndBeforeBookkeeping()
    {
        var state = new Eval(Parser.Parse("OLDPWD=target; readonly OLDPWD; cd -"));
        var lookup = await NextPathKindsAsync(state);
        Assert.Equal("/target", Assert.Single(lookup.Paths).Path);
        Assert.Equal("/", state.CurrentDirectory.Path);
        lookup.SetResult([PathKindResult(FilePathKind.Directory)]);

        var announcement = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, announcement.FileDescriptor);
        Assert.Equal("target\n", Text(announcement.Content));
        Assert.Equal("/target", state.CurrentDirectory.Path);
        announcement.SetResult(DescriptorWriteResult.Open);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: OLDPWD: readonly variable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_EmitsWriteOperation()
    {
        var script = Parser.Parse("echo a b");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal(1, operation.FileDescriptor.Id);
        Assert.Equal("a b\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AllowsNoArguments()
    {
        var script = Parser.Parse("echo");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal(1, operation.FileDescriptor.Id);
        Assert.Equal("\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_LeadingDashN_SuppressesTrailingNewline()
    {
        var script = Parser.Parse("echo -n a b");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal(1, operation.FileDescriptor.Id);
        Assert.Equal("a b", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsLastExitStatus()
    {
        var script = Parser.Parse("false; echo $?");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("1\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsPositionalParameters()
    {
        var script = Parser.Parse("echo $1 $#");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("alpha"), ShellValue.FromText("beta")]);

        var operation = await NextWriteAsync(state);
        Assert.Equal("alpha 2\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNamedVariables()
    {
        var script = Parser.Parse("FOO=bar; echo ${FOO}");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_SingleQuotedIsLiteral()
    {
        var script = Parser.Parse("echo '$1'");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("$1\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_SingleQuotedGlobIsLiteral()
    {
        var script = Parser.Parse("echo '*.txt'");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("*.txt\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_DoubleQuotedGlobIsLiteral()
    {
        var script = Parser.Parse("echo \"*.txt\"");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("*.txt\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_MixedQuotedGlobIsLiteral()
    {
        var script = Parser.Parse("echo a'*'.txt");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("a*.txt\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RespectsQuotedBracketSegment()
    {
        var script = Parser.Parse("echo a*'[bc]'");
        var state = new Eval(script);

        var list = await NextStepAsync(state);
        Assert.NotNull(list);
        var dirList = Assert.IsType<ListDirectoryOperation>(list);
        Assert.Equal("/", dirList.Path.Path);
        dirList.SetResult(DirectoryListingResult.Success([
            new DirectoryEntry("ax[bc]", FilePathKind.File),
            new DirectoryEntry("axb", FilePathKind.File),
            new DirectoryEntry("axc", FilePathKind.File)
        ]));

        var append = await NextWriteAsync(state);
        Assert.Equal("ax[bc]\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsArithmeticLiteral()
    {
        var script = Parser.Parse("echo $((42))");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("42\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsArithmeticShift()
    {
        var script = Parser.Parse("echo $(( 1 << 3 ))");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("8\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsArithmeticVariable()
    {
        var script = Parser.Parse("FOO=7; echo $(( ${FOO} ))");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("7\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNestedArithmetic()
    {
        var script = Parser.Parse("echo $(( $((3)) ))");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("3\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsHomeTilde()
    {
        var script = Parser.Parse("HOME=/home; echo ~");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("/home\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsHomeTildeWithPath()
    {
        var script = Parser.Parse("HOME=/home; echo ~/src");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("/home/src\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsOldPwdTilde()
    {
        var script = Parser.Parse("OLDPWD=/prev; echo ~-");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("/prev\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_TildeQuotedIsLiteral()
    {
        var script = Parser.Parse("echo \"~\"");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("~\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNamedVariableWithoutBraces()
    {
        var script = Parser.Parse("FOO=bar; echo $FOO");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNamedVariableInDoubleQuotes()
    {
        var script = Parser.Parse("FOO=bar; echo \"$FOO\"");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsBraceList()
    {
        var script = Parser.Parse("echo a{b,c}d");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("abd acd\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsBraceSequence()
    {
        var script = Parser.Parse("echo {1..3}");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("1 2 3\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_LeavesQuotedBracesLiteral()
    {
        var script = Parser.Parse("echo \"{a,b}\"");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("{a,b}\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_OpensOutputFileForEcho()
    {
        var script = Parser.Parse("echo hi > out");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, open.Mode);
        open.SetResult(FileOpenResult.Opened(9, readSize: null));

        var append = await NextWriteAsync(state);
        Assert.Equal(9, append.FileDescriptor.Id);
        Assert.Equal("hi\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(9, close.Descriptor.Id);
        close.SetResult(null);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_OrdersDuplicateBeforeOpen()
    {
        var script = Parser.Parse("cmd 2>&1 > out");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, open.Mode);
        open.SetResult(FileOpenResult.Opened(9, readSize: null));

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(9, run.Command.StdOut.Id);
        Assert.Equal(FileDescriptor.StdOut, run.Command.StdErr);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(9, close.Descriptor.Id);
        close.SetResult(null);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_AssignsStdInAndStdOut()
    {
        var script = Parser.Parse("cmd < in > out");
        var state = new Eval(script);

        var openIn = await NextOpenFileAsync(state);
        Assert.Equal("/in", openIn.Path.Path);
        Assert.Equal(FileOpenMode.Read, openIn.Mode);
        openIn.SetResult(FileOpenResult.Opened(3, readSize: null));

        var openOut = await NextOpenFileAsync(state);
        Assert.Equal("/out", openOut.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, openOut.Mode);
        openOut.SetResult(FileOpenResult.Opened(4, readSize: null));

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(3, run.Command.StdIn.Id);
        Assert.Equal(4, run.Command.StdOut.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var closeOut = await NextCloseDescriptorAsync(state);
        Assert.Equal(4, closeOut.Descriptor.Id);
        closeOut.SetResult(null);

        var closeIn = await NextCloseDescriptorAsync(state);
        Assert.Equal(3, closeIn.Descriptor.Id);
        closeIn.SetResult(null);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_OutputAndError()
    {
        var script = Parser.Parse("cmd &> out");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, open.Mode);
        open.SetResult(FileOpenResult.Opened(7, readSize: null));

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(7, run.Command.StdOut.Id);
        Assert.Equal(7, run.Command.StdErr.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(7, close.Descriptor.Id);
        close.SetResult(null);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_OutputAndErrorAppend()
    {
        var script = Parser.Parse("cmd &>> out");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteAppend, open.Mode);
        open.SetResult(FileOpenResult.Opened(8, readSize: null));

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(8, run.Command.StdOut.Id);
        Assert.Equal(8, run.Command.StdErr.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(8, close.Descriptor.Id);
        close.SetResult(null);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_DuplicateOutputNonNumericRedirectsBoth()
    {
        var script = Parser.Parse("cmd >& out");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, open.Mode);
        open.SetResult(FileOpenResult.Opened(12, readSize: null));

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(12, run.Command.StdOut.Id);
        Assert.Equal(12, run.Command.StdErr.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(12, close.Descriptor.Id);
        close.SetResult(null);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_DuplicatesInputDescriptor()
    {
        var script = Parser.Parse("cmd <&3");
        var bindings = new DescriptorTable();
        bindings.Set(new LogicalDescriptor(3), new FileDescriptor(900));
        var state = new Eval(script, new EvaluationDescriptorSetup(bindings.Capture(), [new FileDescriptor(900)]),
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes), sourceMemory: null);

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(900, run.Command.StdIn.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_DuplicatesNonStandardInputDescriptor()
    {
        var script = Parser.Parse("cmd 5<&0");
        var state = new Eval(script);

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(FileDescriptor.StdIn, run.Command.StdIn);
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_BorrowsExplicitlyBoundDescriptor()
    {
        var script = Parser.Parse("first >&9; second >&9");
        var bindings = new DescriptorTable();
        bindings.Set(new LogicalDescriptor(9), new FileDescriptor(900));
        var state = new Eval(script, new EvaluationDescriptorSetup(bindings.Capture(), [new FileDescriptor(900)]),
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes), sourceMemory: null);

        var first = await NextCommandAsync(state, "first");
        Assert.Equal(new FileDescriptor(900), first.Command.StdOut);
        Assert.DoesNotContain(new FileDescriptor(900), first.Command.DescriptorsToClose);
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = await NextCommandAsync(state, "second");
        Assert.Equal(new FileDescriptor(900), second.Command.StdOut);
        Assert.DoesNotContain(new FileDescriptor(900), second.Command.DescriptorsToClose);
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereStringCreatesPipe()
    {
        var script = Parser.Parse("cmd <<< value");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));

        var inputHost = await StartHereInputAsync(state, 11, "value\n");

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(10, run.Command.StdIn.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(10, closeRead.Descriptor.Id);
        await ShellOperationDispatcher.DispatchAsync(closeRead, inputHost, CancellationToken.None);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_FatalHereStringExpansionSkipsPipe()
    {
        var script = Parser.Parse("cmd <<< ${MISSING:?failed}");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: MISSING: failed\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentCreatesPipe()
    {
        var script = Parser.Parse("cmd <<EOF\nline1\nline2\nEOF\n");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(20, 21)));

        var inputHost = await StartHereInputAsync(state, 21, "line1\nline2\n");

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(20, run.Command.StdIn.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(20, closeRead.Descriptor.Id);
        await ShellOperationDispatcher.DispatchAsync(closeRead, inputHost, CancellationToken.None);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentStripsTabs()
    {
        var script = Parser.Parse("cmd <<-EOF\n\tline1\n\t\tline2\n\tEOF\n");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(22, 23)));

        var inputHost = await StartHereInputAsync(state, 23, "line1\nline2\n");

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(22, run.Command.StdIn.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(22, closeRead.Descriptor.Id);
        await ShellOperationDispatcher.DispatchAsync(closeRead, inputHost, CancellationToken.None);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentExpandsWhenUnquoted()
    {
        var script = Parser.Parse("FOO=bar; cmd <<EOF\n$FOO\nEOF\n");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(24, 25)));

        var inputHost = await StartHereInputAsync(state, 25, "bar\n");

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(24, run.Command.StdIn.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(24, closeRead.Descriptor.Id);
        await ShellOperationDispatcher.DispatchAsync(closeRead, inputHost, CancellationToken.None);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentSkipsExpansionWhenQuoted()
    {
        var script = Parser.Parse("FOO=bar; cmd <<'EOF'\n$FOO\nEOF\n");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(26, 27)));

        var inputHost = await StartHereInputAsync(state, 27, "$FOO\n");

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(26, run.Command.StdIn.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(26, closeRead.Descriptor.Id);
        await ShellOperationDispatcher.DispatchAsync(closeRead, inputHost, CancellationToken.None);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_CommandAfterHereDocumentIsStillInvoked()
    {
        var script = Parser.Parse("cat > /out << 'EOF'\nhi\nEOF\ncat /out");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, open.Mode);
        open.SetResult(FileOpenResult.Opened(32, readSize: null));

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(30, 31)));

        var inputHost = await StartHereInputAsync(state, 31, "hi\n");

        var first = await NextCommandAsync(state, "cat");
        Assert.Equal(30, first.Command.StdIn.Id);
        Assert.Equal(32, first.Command.StdOut.Id);
        Assert.Empty(first.Command.Arguments);
        first.SetResult(new CommandExecutionResult.Completed(0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(30, closeRead.Descriptor.Id);
        await ShellOperationDispatcher.DispatchAsync(closeRead, inputHost, CancellationToken.None);

        var closeFile = await NextCloseDescriptorAsync(state);
        Assert.Equal(32, closeFile.Descriptor.Id);
        closeFile.SetResult(null);

        var second = await NextCommandAsync(state, "cat");
        Assert.Equal("/out", second.Command.Arguments.Single().ToUtf8String());
        Assert.Equal(FileDescriptor.StdOut, second.Command.StdOut);
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    private static async Task<TestOperationVisitor> StartHereInputAsync(Eval state, int writer, string content)
    {
        var operation = Assert.IsType<StartPipeInputOperation>(await NextStepAsync(state));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueCloseDescriptor();
        host.EnqueueCloseDescriptor();
        await ShellOperationDispatcher.DispatchAsync(operation, host, CancellationToken.None);
        var append = Assert.Single(host.Appends);
        Assert.Equal(writer, append.Descriptor.Id);
        Assert.Equal(content, Text(append.Content));
        Assert.Equal(writer, Assert.Single(host.ClosedDescriptors).Id);
        return host;
    }

    [Fact]
    public async Task Evaluation_Redirection_AllocatesOutputDescriptor()
    {
        var script = Parser.Parse("cmd {fd}> out >&$fd");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/out", open.Path.Path);
        Assert.Equal(FileOpenMode.WriteTruncate, open.Mode);
        open.SetResult(FileOpenResult.Opened(9, readSize: null));

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(new FileDescriptor(9), run.Command.StdOut);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(9, close.Descriptor.Id);
        close.SetResult(null);

        Assert.False(state.Variables.ContainsKey("fd"));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_AllocatesInputDescriptor()
    {
        var script = Parser.Parse("cmd {fd}< in <&$fd");
        var state = new Eval(script);

        var open = await NextOpenFileAsync(state);
        Assert.Equal("/in", open.Path.Path);
        Assert.Equal(FileOpenMode.Read, open.Mode);
        open.SetResult(FileOpenResult.Opened(8, readSize: null));

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(new FileDescriptor(8), run.Command.StdIn);
        run.SetResult(new CommandExecutionResult.Completed(0));

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(8, close.Descriptor.Id);
        close.SetResult(null);

        Assert.False(state.Variables.ContainsKey("fd"));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayElement()
    {
        var script = Parser.Parse("arr=(zero one two); echo ${arr[1]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("one\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssignsIndexedArrayElement()
    {
        var script = Parser.Parse("arr[1]=one; echo ${arr[1]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("one\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssignsAssociativeArrayElement()
    {
        var script = Parser.Parse("map[key]=value; echo ${map[key]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("value\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_CompoundArrayAssignment()
    {
        var script = Parser.Parse("arr=(a b); echo ${arr[1]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("b\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_CompoundArrayAssignmentWithIndices()
    {
        var script = Parser.Parse("arr=([2]=two [0]=zero); echo ${arr[2]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("two\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssociativeCompoundKeyValuePairs()
    {
        var script = Parser.Parse("declare -A map; map=(one 1 two 2); echo ${map[two]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("2\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssociativeCompoundKeyWithMissingValue()
    {
        var script = Parser.Parse("declare -A map; map=(alpha); echo ${map[alpha]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAssociativeArrayElement()
    {
        var script = Parser.Parse("declare -A map; map[key]=value; echo ${map[key]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("value\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayAllElements()
    {
        var script = Parser.Parse("arr=(zero one); echo ${arr[@]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("zero one\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayStar()
    {
        var script = Parser.Parse("arr=(zero one); echo ${arr[*]}");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("zero one\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayStarQuoted()
    {
        var script = Parser.Parse("arr=(zero one); echo \"${arr[*]}\"");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("zero one\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_ExpandsQuotedIndexedArrayAtAsSeparateArguments()
    {
        var script = Parser.Parse("arr=(\"a b\" c); cmd \"${arr[@]}\"");
        var state = new Eval(script);

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(["a b", "c"], run.Command.Arguments.Select(argument => argument.ToUtf8String()));
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_ExpandsQuotedIndexedArrayAtWithAffixes()
    {
        var script = Parser.Parse("arr=(a b); cmd pre\"${arr[@]}\"post");
        var state = new Eval(script);

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(["prea", "bpost"], run.Command.Arguments.Select(argument => argument.ToUtf8String()));
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayStarQuotedWithFirstIfsCharacter()
    {
        var script = Parser.Parse("arr=(a b c); IFS=,; echo \"${arr[*]}\"");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("a,b,c\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAtUnquoted()
    {
        var script = Parser.Parse("echo $@");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("first"), ShellValue.FromText("second")]);

        var append = await NextWriteAsync(state);
        Assert.Equal("first second\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAtQuoted()
    {
        var script = Parser.Parse("echo \"$@\"");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("first"), ShellValue.FromText("second")]);

        var append = await NextWriteAsync(state);
        Assert.Equal("first second\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_UnquotedUnsetScalarDoesNotEmitEmptyArgument()
    {
        var script = Parser.Parse("cmd $MISSING");
        var state = new Eval(script);

        var run = await NextCommandAsync(state, "cmd");
        Assert.Empty(run.Command.Arguments);
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_ExpandsQuotedAtAsSeparateArguments()
    {
        var script = Parser.Parse("cmd \"$@\"");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a b"), ShellValue.FromText("c")]);

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(["a b", "c"], run.Command.Arguments.Select(argument => argument.ToUtf8String()));
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_ExpandsQuotedAtWithAffixes()
    {
        var script = Parser.Parse("cmd pre\"$@\"post");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a"), ShellValue.FromText("b")]);

        var run = await NextCommandAsync(state, "cmd");
        Assert.Equal(["prea", "bpost"], run.Command.Arguments.Select(argument => argument.ToUtf8String()));
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsStarUnquoted()
    {
        var script = Parser.Parse("echo $*");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("first"), ShellValue.FromText("second")]);

        var append = await NextWriteAsync(state);
        Assert.Equal("first second\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsStarQuotedWithFirstIfsCharacter()
    {
        var script = Parser.Parse("IFS=,; echo \"$*\"");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a"), ShellValue.FromText("b"), ShellValue.FromText("c")]);

        var append = await NextWriteAsync(state);
        Assert.Equal("a,b,c\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsStarQuoted()
    {
        var script = Parser.Parse("echo \"$*\"");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("first"), ShellValue.FromText("second")]);

        var append = await NextWriteAsync(state);
        Assert.Equal("first second\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsDefaultWhenUnset()
    {
        var script = Parser.Parse("echo ${FOO:-fallback}");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("fallback\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsDefaultWhenEmpty()
    {
        var script = Parser.Parse("FOO=; echo ${FOO:-fallback}");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("fallback\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAlternateWhenSet()
    {
        var script = Parser.Parse("FOO=bar; echo ${FOO:+alt}");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("alt\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AssignsDefaultWhenUnset()
    {
        var script = Parser.Parse("echo ${FOO:=bar}; echo $FOO");
        var state = new Eval(script);

        var first = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(first.Content));
        first.SetResult(DescriptorWriteResult.Open);

        var second = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(second.Content));
        second.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AssignsDefaultWhenEmpty()
    {
        var script = Parser.Parse("FOO=; echo ${FOO:=bar}; echo $FOO");
        var state = new Eval(script);

        var first = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(first.Content));
        first.SetResult(DescriptorWriteResult.Open);

        var second = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(second.Content));
        second.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ErrorOnUnsetWithMessage()
    {
        var script = Parser.Parse("echo ${FOO:?missing}");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: FOO: missing\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_NoAssignWhenEmptyWithoutColon()
    {
        var script = Parser.Parse("FOO=; echo ${FOO=bar}; echo $FOO");
        var state = new Eval(script);

        var first = await NextWriteAsync(state);
        Assert.Equal("\n", Text(first.Content));
        first.SetResult(DescriptorWriteResult.Open);

        var second = await NextWriteAsync(state);
        Assert.Equal("\n", Text(second.Content));
        second.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AssignsWhenUnsetWithoutColon()
    {
        var script = Parser.Parse("echo ${FOO=bar}; echo $FOO");
        var state = new Eval(script);

        var first = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(first.Content));
        first.SetResult(DescriptorWriteResult.Open);

        var second = await NextWriteAsync(state);
        Assert.Equal("bar\n", Text(second.Content));
        second.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_NoErrorWhenEmptyWithoutColon()
    {
        var script = Parser.Parse("FOO=; echo ${FOO?missing}");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal("\n", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ErrorWhenUnsetWithoutColon()
    {
        var script = Parser.Parse("echo ${FOO?missing}");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: FOO: missing\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_UsesCapturedOutput()
    {
        var script = Parser.Parse("echo $(cmd)");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));

        var capture = Assert.IsType<RunCapturedEvaluation>(await NextStepAsync(state));
        Assert.Equal(10, capture.Input.Id);
        var run = await NextCommandAsync(capture.Stage.Evaluation);
        Assert.Equal("cmd", run.Command.CommandName);
        Assert.Equal(11, run.Command.StdOut.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(capture.Stage.Evaluation));
        capture.SetResult(new CapturedEvaluationResult(new BoundedReadResult.Complete(Utf8Bytes("a\n")), 0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(10, closeRead.Descriptor.Id);
        closeRead.SetResult(null);

        var append = await NextWriteAsync(state);
        Assert.Equal("a\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_StripsTrailingNewlines()
    {
        var script = Parser.Parse("echo $(cmd)");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(12, 13)));

        var capture = Assert.IsType<RunCapturedEvaluation>(await NextStepAsync(state));
        Assert.Equal(12, capture.Input.Id);
        var run = await NextCommandAsync(capture.Stage.Evaluation);
        Assert.Equal("cmd", run.Command.CommandName);
        Assert.Equal(13, run.Command.StdOut.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(capture.Stage.Evaluation));
        capture.SetResult(new CapturedEvaluationResult(new BoundedReadResult.Complete(Utf8Bytes("a\n\n")), 0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(12, closeRead.Descriptor.Id);
        closeRead.SetResult(null);

        var append = await NextWriteAsync(state);
        Assert.Equal("a\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_SplitsUnquotedOutput()
    {
        var script = Parser.Parse("printf %s-%s $(cmd)");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(14, 15)));

        var capture = Assert.IsType<RunCapturedEvaluation>(await NextStepAsync(state));
        Assert.Equal(14, capture.Input.Id);
        var run = await NextCommandAsync(capture.Stage.Evaluation);
        Assert.Equal("cmd", run.Command.CommandName);
        Assert.Equal(15, run.Command.StdOut.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(capture.Stage.Evaluation));
        capture.SetResult(new CapturedEvaluationResult(new BoundedReadResult.Complete(Utf8Bytes("a b\n")), 0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(14, closeRead.Descriptor.Id);
        closeRead.SetResult(null);

        var append = await NextWriteAsync(state);
        Assert.Equal("a-b", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_SkipsSplitInDoubleQuotes()
    {
        var script = Parser.Parse("printf %s-%s \"$(cmd)\"");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(16, 17)));

        var capture = Assert.IsType<RunCapturedEvaluation>(await NextStepAsync(state));
        Assert.Equal(16, capture.Input.Id);
        var run = await NextCommandAsync(capture.Stage.Evaluation);
        Assert.Equal("cmd", run.Command.CommandName);
        Assert.Equal(17, run.Command.StdOut.Id);
        run.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(capture.Stage.Evaluation));
        capture.SetResult(new CapturedEvaluationResult(new BoundedReadResult.Complete(Utf8Bytes("a b\n")), 0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(16, closeRead.Descriptor.Id);
        closeRead.SetResult(null);

        var append = await NextWriteAsync(state);
        Assert.Equal("a b-", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_UsesDefaultIfs()
    {
        var script = Parser.Parse("var=\"a b\"; printf '%s|%s' $var");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("a|b", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_UsesCustomIfs()
    {
        var script = Parser.Parse("IFS=,; var=\"a,b,c\"; printf '%s|%s|%s' $var");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("a|b|c", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_PreservesEmptyFieldsForNonWhitespaceIfs()
    {
        var script = Parser.Parse("IFS=,; var=\"a,,b,\"; printf '%s|%s|%s|%s' $var");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("a||b|", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_RespectsEmptyIfs()
    {
        var script = Parser.Parse("IFS=; var=\"a b\"; printf '%s|%s' $var");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("a b|", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RequestsDirectoryListing()
    {
        var script = Parser.Parse("echo *.txt");
        var state = new Eval(script);

        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);
        Assert.IsType<ListDirectoryOperation>(operation);
    }

    [Theory]
    [InlineData("No such file or directory\n")]
    [InlineData("Permission denied\n")]
    public async Task Evaluation_GlobExpansion_ListingFailureLeavesPatternLiteralWithoutDiagnostic(string message)
    {
        var state = new Eval(Parser.Parse("echo /dir/*.txt"));
        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        listing.SetResult(DirectoryListingResult.Failure(new OperationError(message)));

        var append = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, append.FileDescriptor);
        Assert.Equal("/dir/*.txt\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RecursesSegments()
    {
        var script = Parser.Parse("echo a/*/c");
        var state = new Eval(script);

        var listA = await NextStepAsync(state);
        Assert.NotNull(listA);
        var aList = Assert.IsType<ListDirectoryOperation>(listA);
        Assert.Equal("/a", aList.Path.Path);
        aList.SetResult(DirectoryListingResult.Success([
            new DirectoryEntry("x", FilePathKind.Directory),
            new DirectoryEntry("y", FilePathKind.Directory),
            new DirectoryEntry("file", FilePathKind.File)
        ]));

        var listX = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal("/a/x", listX.Path.Path);
        listX.SetResult(DirectoryListingResult.Success([new DirectoryEntry("c", FilePathKind.File)]));

        var listY = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal("/a/y", listY.Path.Path);
        listY.SetResult(DirectoryListingResult.Success([new DirectoryEntry("c", FilePathKind.File)]));

        var append = await NextWriteAsync(state);
        Assert.Equal("a/x/c a/y/c\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_EmitsWriteOperation()
    {
        var script = Parser.Parse("printf %s a");
        var state = new Eval(script);

        var operation = await NextWriteAsync(state);
        Assert.Equal(1, operation.FileDescriptor.Id);
        Assert.Equal("a", Text(operation.Content));
        operation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_PrintfBuiltin_AllowsOnlyFormatWithoutEmptyHostWrites()
    {
        var script = Parser.Parse("printf %s");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_EmitsReadLineOperation()
    {
        var script = Parser.Parse("read name");
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        Assert.Equal(0, operation.FileDescriptor.Id);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_AssignsVariables()
    {
        var script = Parser.Parse("read first second");
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.Success(Utf8Bytes("a b c\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("a", GetVariable(state, "first"));
        Assert.Equal("b c", GetVariable(state, "second"));
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_UsesAssignmentPrefixIfs()
    {
        var script = Parser.Parse("IFS=, read first second");
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.Success(Utf8Bytes("a,b,c\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("a", GetVariable(state, "first"));
        Assert.Equal("b,c", GetVariable(state, "second"));
    }

    [Theory]
    [InlineData("read value", "ab")]
    [InlineData("read -r value", "a\\b")]
    public async Task Evaluation_ReadBuiltin_ControlsBackslashEscaping(string command, string expected)
    {
        var script = Parser.Parse(command);
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.Success(Utf8Bytes("a\\b\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(expected, GetVariable(state, "value"));
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_ContinuesEscapedPhysicalLines()
    {
        var script = Parser.Parse("read value");
        var state = new Eval(script);

        var first = await NextReadAsync(state);
        first.SetResult(ReadResult.Success(Utf8Bytes("a\\\n"), [], reachedEoF: false));
        var second = await NextReadAsync(state);
        second.SetResult(ReadResult.Success(Utf8Bytes("b\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("ab", GetVariable(state, "value"));
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_UnterminatedContinuationAssignsAndReturnsFailure()
    {
        var script = Parser.Parse("read value");
        var state = new Eval(script);

        var first = await NextReadAsync(state);
        first.SetResult(ReadResult.Success(Utf8Bytes("a\\\n"), [], reachedEoF: false));
        var second = await NextReadAsync(state);
        second.SetResult(ReadResult.Success(Utf8Bytes("b"), [], reachedEoF: true));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("ab", GetVariable(state, "value"));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_UnterminatedTrailingBackslashIsConsumed()
    {
        var script = Parser.Parse("read value");
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.Success(Utf8Bytes("a\\"), [], reachedEoF: true));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("a", GetVariable(state, "value"));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_TrimsEscapedIfsWhitespaceFromFinalRemainder()
    {
        var script = Parser.Parse("IFS=' ,' read first rest");
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.Success(Utf8Bytes("a,b,c\\ \n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("a", GetVariable(state, "first"));
        Assert.Equal("b,c", GetVariable(state, "rest"));
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_DefaultReplyPreservesWhitespace()
    {
        var script = Parser.Parse("read");
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.Success(Utf8Bytes("  a   b  \n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("  a   b  ", GetVariable(state, "REPLY"));
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_InvalidOptionDoesNotRead()
    {
        var script = Parser.Parse("read -z value");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: read: -z: invalid option\nread: usage: read [-ers] [-a array] [-d delim] [-i text] [-n count] [-N count] [-p prompt] [-u fd] [name ...]\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
        Assert.Equal(string.Empty, GetVariableOrEmpty(state, "value"));
    }


    [Fact]
    public async Task Evaluation_ReadBuiltin_FailureDoesNotAssign()
    {
        var script = Parser.Parse("read value");
        var state = new Eval(script);
        state.Variables.TryGetValue("value", out var initialValue);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.Failure(error: new OperationError("read error\n")));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: read: read error: 0: read error\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(initialValue.ToUtf8String(), GetVariableOrEmpty(state, "value"));
    }

    [Fact]
    public async Task Evaluation_ReadBuiltin_EmptyEofClearsTarget()
    {
        var script = Parser.Parse("value=old; read value");
        var state = new Eval(script);

        var operation = await NextReadAsync(state);
        operation.SetResult(ReadResult.EndOfFile);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(string.Empty, GetVariableOrEmpty(state, "value"));
    }

    [Fact]
    public async Task Evaluation_UnsetBuiltin_RemovesVariable()
    {
        var script = Parser.Parse("read name; unset name");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        read.SetResult(ReadResult.Success(Utf8Bytes("value\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("name"));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_UnsetBuiltin_MissingVariableReturnsSuccess()
    {
        var script = Parser.Parse("unset missing");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_DirsBuiltin_EmitsWriteOperation()
    {
        var script = Parser.Parse("dirs");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal(1, append.FileDescriptor.Id);
        Assert.Equal("/\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_DirsClearOption_ResetsStack()
    {
        var script = Parser.Parse("pushd target; dirs -c; dirs");
        var state = new Eval(script);

        var pushTests = await NextPathKindsAsync(state);
        pushTests.SetResult([PathKindResult(FilePathKind.Directory)]);

        var pushOutput = await NextWriteAsync(state);
        pushOutput.SetResult(DescriptorWriteResult.Open);

        var dirsOutput = await NextWriteAsync(state);
        Assert.Equal("/target\n", Text(dirsOutput.Content));
        dirsOutput.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_EmitsReadLineOperation()
    {
        var script = Parser.Parse("mapfile arr");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        Assert.Equal(0, read.FileDescriptor.Id);
        read.SetResult(ReadResult.Success(Utf8Bytes("a\nb\n"), [2], reachedEoF: false));

        var eof = await NextReadAsync(state);
        eof.SetResult(ReadResult.EndOfFile);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }


    [Fact]
    public async Task Evaluation_ReadarrayBuiltin_EmitsReadLineOperation()
    {
        var script = Parser.Parse("readarray arr");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        Assert.Equal(0, read.FileDescriptor.Id);
        read.SetResult(ReadResult.Success(Utf8Bytes("x\ny\n"), [2], reachedEoF: false));

        var eof = await NextReadAsync(state);
        eof.SetResult(ReadResult.EndOfFile);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_HashBuiltin_EmitsTheEmptyTableToStdout()
    {
        var script = Parser.Parse("hash");
        var state = new Eval(script);

        var write = Assert.IsType<WriteOperation>(await NextStepAsync(state));
        Assert.Equal(FileDescriptor.StdOut, write.FileDescriptor);
        Assert.Equal("hash: hash table empty\n", Text(write.Content));
        write.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluation_HashTargetMissesUseOnlyDiagnosticWrites(bool rejectFirst)
    {
        var state = new Eval(Parser.Parse("hash -t first second"));
        var first = Assert.IsType<WriteOperation>(await NextStepAsync(state));
        Assert.Equal(FileDescriptor.StdErr, first.FileDescriptor);
        Assert.Equal("hash: first: not found\n", Text(first.Content));
        first.SetResult(rejectFirst ? DescriptorWriteResult.WriteFailure : DescriptorWriteResult.Open);

        if (!rejectFirst)
        {
            var second = Assert.IsType<WriteOperation>(await NextStepAsync(state));
            Assert.Equal(FileDescriptor.StdErr, second.FileDescriptor);
            Assert.Equal("hash: second: not found\n", Text(second.Content));
            second.SetResult(DescriptorWriteResult.Open);
        }
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_RemovesFirstParameter()
    {
        var script = Parser.Parse("shift");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a"), ShellValue.FromText("b")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Single(state.PositionalParameters);
        Assert.Equal("b", state.PositionalParameters[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_RemovesExplicitCount()
    {
        var script = Parser.Parse("shift 2");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a"), ShellValue.FromText("b"), ShellValue.FromText("c")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Single(state.PositionalParameters);
        Assert.Equal("c", state.PositionalParameters[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_TooLargeReturnsFailure()
    {
        var script = Parser.Parse("shift 3");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a"), ShellValue.FromText("b")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(2, state.PositionalParameters.Count);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_InvalidCountReportsErrorAndPreservesPositionals()
    {
        var script = Parser.Parse("shift nope");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("kept")]);

        var diagnostic = Assert.IsType<WriteOperation>(await NextStepAsync(state));
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: shift: nope: numeric argument required\n", Text(diagnostic.Content));
        Assert.Equal("kept", Assert.Single(state.PositionalParameters).ToUtf8String());
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("kept", Assert.Single(state.PositionalParameters).ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_ReplacesPositionalParameters()
    {
        var script = Parser.Parse("set a b");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("x")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(2, state.PositionalParameters.Count);
        Assert.Equal("a", state.PositionalParameters[0].ToUtf8String());
        Assert.Equal("b", state.PositionalParameters[1].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_DoubleDashClearsParameters()
    {
        var script = Parser.Parse("set --");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a"), ShellValue.FromText("b")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Empty(state.PositionalParameters);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_DoubleDashKeepsLeadingDashArgument()
    {
        var script = Parser.Parse("set -- -x");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Single(state.PositionalParameters);
        Assert.Equal("-x", state.PositionalParameters[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_DoubleDashKeepsQuotedAtArgumentsSeparate()
    {
        var script = Parser.Parse("set -- \"$@\"");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a b"), ShellValue.FromText("c")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(2, state.PositionalParameters.Count);
        Assert.Equal("a b", state.PositionalParameters[0].ToUtf8String());
        Assert.Equal("c", state.PositionalParameters[1].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_SingleDashLeavesParametersUnchanged()
    {
        var script = Parser.Parse("set -");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a")]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Single(state.PositionalParameters);
        Assert.Equal("a", state.PositionalParameters[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_InvalidOptionReturnsFailure()
    {
        var script = Parser.Parse("set -x");
        var state = new Eval(script);
        state.ReplacePositionalParameters([ShellValue.FromText("a")]);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("set: -x: unsupported option\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
        Assert.Single(state.PositionalParameters);
        Assert.Equal("a", state.PositionalParameters[0].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_ReentersBuiltinDispatch()
    {
        var script = Parser.Parse("command echo hi");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("hi\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_DoubleDashAllowsLeadingDashArgs()
    {
        var script = Parser.Parse("command -- basename -n");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "basename");
        Assert.Single(runCommand.Command.Arguments);
        Assert.Equal("-n", runCommand.Command.Arguments[0].ToUtf8String());
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_NoArgumentsReturnsSuccess()
    {
        var script = Parser.Parse("command");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_InvalidOptionReturnsFailure()
    {
        var script = Parser.Parse("command -x echo");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, append.FileDescriptor);
        Assert.Equal("command: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryReportsBuiltin()
    {
        var script = Parser.Parse("command -v echo");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("echo\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryUsesHostResolution()
    {
        var script = Parser.Parse("command -v cat");
        var state = new Eval(script);

        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["cat"], lookup.CommandNames);
        lookup.SetResult([new CommandLookupResult.Found("/tools/cat")]);

        var append = await NextWriteAsync(state);
        Assert.Equal("/tools/cat\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("command -v")]
    [InlineData("type -t")]
    public async Task Evaluation_ExecQueryClearsVisibleHiddenAndPrefixExports(string command)
    {
        var state = new Eval(Parser.Parse("export SECRET=value; f(){ local +x SECRET=local; "
            + "OTHER=prefix exec -c " + command + " custom; }; f; printf unexpected"));

        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["custom"], lookup.CommandNames);
        Assert.Empty(lookup.Environment);
        lookup.SetResult([new CommandLookupResult.Found("/tools/custom")]);

        var output = await NextWriteAsync(state);
        output.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("command -v", "/tools/cat\n", "x=global; f(){ local x=local; declare -gx x; ", "f")]
    [InlineData("type -t", "file\n", "x=global; f(){ local x=local; declare -gx x; ", "f")]
    [InlineData("command -v", "/tools/cat\n", "export x=global; f(){ local +x x=local; ", "x=prefix f")]
    [InlineData("type -t", "file\n", "export x=global; f(){ local +x x=local; ", "x=prefix f")]
    [InlineData("command -v", "/tools/cat\n", "export x=global; f(){ local x; unset x; export x; ", "x=prefix f")]
    [InlineData("type -t", "file\n", "export x=global; f(){ local x; unset x; export x; ", "x=prefix f")]
    public async Task Evaluation_CommandQuery_IncludesHiddenGlobalExports(string command, string output, string setup, string invocation)
    {
        var state = new Eval(Parser.Parse(setup + command + " external; }; " + invocation));

        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal("global", FindEnvironmentValue(lookup.Environment, "x"));
        Assert.Single(lookup.Environment, entry => entry.Name == "x");
        lookup.SetResult([new CommandLookupResult.Found("/tools/cat")]);

        var append = await NextWriteAsync(state);
        Assert.Equal(output, Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandBuiltin_QueryContinuesAfterReadonlyPrefix()
    {
        var script = Parser.Parse("readonly VALUE; VALUE=changed command -v cat");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: VALUE: readonly variable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["cat"], lookup.CommandNames);
        lookup.SetResult([new CommandLookupResult.Found("/tools/cat")]);

        var output = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, output.FileDescriptor);
        Assert.Equal("/tools/cat\n", Text(output.Content));
        output.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandQueries_ReportSymbolicKeywords()
    {
        var script = Parser.Parse(            "command -v '!' '{' '}' '[[' ']]'; type -t '!' '{' '}' '[[' ']]'");
        var state = new Eval(script);

        var commandOutput = await NextWriteAsync(state);
        Assert.Equal("!\n{\n}\n[[\n]]\n", Text(commandOutput.Content));
        commandOutput.SetResult(DescriptorWriteResult.Open);

        var typeOutput = await NextWriteAsync(state);
        Assert.Equal("keyword\nkeyword\nkeyword\nkeyword\nkeyword\n", Text(typeOutput.Content));
        typeOutput.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ReportsFunction()
    {
        var script = Parser.Parse("foo() { :; }; type foo");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("foo is a function\nfoo () \n{ \n    :\n}\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ReportsBuiltin()
    {
        var script = Parser.Parse("type echo");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("echo is a shell builtin\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ReportsNotFound()
    {
        var script = Parser.Parse("type missing");
        var state = new Eval(script);

        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["missing"], lookup.CommandNames);
        lookup.SetResult([new CommandLookupResult.Missing()]);

        var append = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, append.FileDescriptor);
        Assert.Equal("line 1: type: missing: not found\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_CompactReportsResolutionKinds()
    {
        var script = Parser.Parse("f() { :; }; type -t echo f if cat missing");
        var state = new Eval(script);

        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["cat", "missing"], lookup.CommandNames);
        lookup.SetResult(
        [
            new CommandLookupResult.Found("cat"),
            new CommandLookupResult.Missing()
        ]);

        var append = await NextWriteAsync(state);
        Assert.Equal("builtin\nfunction\nkeyword\nfile\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeAll_ReportsLocalMatchesEvenWhenTheHostLookupFails()
    {
        var state = new Eval(Parser.Parse("echo(){ :; }; type -at echo if more"));
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["echo", "if", "more"], lookup.CommandNames);
        lookup.SetResult([
            new CommandLookupResult.Failed(new OperationError("lookup unavailable\n")),
            new CommandLookupResult.Missing(),
            new CommandLookupResult.Found("/synthetic/more")
        ]);

        var local = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, local.FileDescriptor);
        Assert.Equal("function\nbuiltin\n", Text(local.Content));
        local.SetResult(DescriptorWriteResult.Open);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: type: echo: lookup unavailable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        var remaining = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, remaining.FileDescriptor);
        Assert.Equal("keyword\nfile\n", Text(remaining.Content));
        remaining.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_PathOnlyUsesTheSelectedClassWithoutFallback()
    {
        var state = new Eval(Parser.Parse("cat(){ :; }; type -p cat printf if"));

        // Any host lookup or write would be the next operation.
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_SkipsFunctionsAndPreservesHostPathAndFailure()
    {
        var state = new Eval(Parser.Parse("cat(){ :; }; type -pf printf cat if missing damaged"));
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["cat", "missing", "damaged"], lookup.CommandNames);
        lookup.SetResult([
            new CommandLookupResult.Found("/synthetic/cat"),
            new CommandLookupResult.Missing(),
            new CommandLookupResult.Failed(new OperationError("lookup unavailable\n"))
        ]);

        var output = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, output.FileDescriptor);
        Assert.Equal("/synthetic/cat\n", Text(output.Content));
        output.SetResult(DescriptorWriteResult.Open);
        var error = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, error.FileDescriptor);
        Assert.Equal("line 1: type: damaged: lookup unavailable\n", Text(error.Content));
        error.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Theory]
    [InlineData("type -t")]
    [InlineData("type -t --")]
    public async Task Evaluation_TypeBuiltin_CompactWithoutNamesReturnsSuccess(string scriptText)
    {
        var script = Parser.Parse(scriptText);
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_NoArgumentsReturnsSuccess()
    {
        var script = Parser.Parse("type");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_UsesBuiltinEvenWhenFunctionExists()
    {
        var script = Parser.Parse("echo() { command echo nope; }; builtin echo hi");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("hi\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_ReportsSuccessForColon()
    {
        var script = Parser.Parse("builtin :");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_ReportsFailureForUnknownName()
    {
        var script = Parser.Parse("builtin missing");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, append.FileDescriptor);
        Assert.Equal("builtin: missing: not a shell builtin\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_BuiltinBuiltin_NoArgumentsReturnsSuccess()
    {
        var script = Parser.Parse("builtin");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_TerminatesAfterCommand()
    {
        var script = Parser.Parse("exec first; second");
        var state = new Eval(script);

        var first = await NextCommandAsync(state, "first");
        first.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ExecBuiltin_NoArgumentsReturnsSuccess()
    {
        var script = Parser.Parse("exec");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_AssignsVariable()
    {
        var script = Parser.Parse("declare foo=bar");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("bar", GetVariable(state, "foo"));
    }

    [Fact]
    public async Task Evaluation_TypesetBuiltin_DeclaresUnsetVariable()
    {
        var script = Parser.Parse("typeset foo");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.False(state.Variables.ContainsKey("foo"));
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_AppliesExportAttribute()
    {
        var script = Parser.Parse("declare -x foo");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.True(HasExported(state, "foo"));
        Assert.False(state.Variables.ContainsKey("foo"));
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_RejectsInvalidName()
    {
        var script = Parser.Parse("declare 1bad");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: declare: `1bad': not a valid identifier\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_CreatesArrayDeclarations()
    {
        var script = Parser.Parse("declare -a arr; declare -A map");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);

        Assert.True(state.IndexedArrays.ContainsKey("arr"));
        Assert.True(state.AssociativeArrays.ContainsKey("map"));
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_IgnoresIndexedArraySubscript()
    {
        var script = Parser.Parse("declare -a arr[1]");
        var state = new Eval(script);

        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        listing.SetResult(DirectoryListingResult.Success([]));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);

        Assert.True(state.IndexedArrays.ContainsKey("arr"));
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_IgnoresAssociativeArraySubscript()
    {
        var script = Parser.Parse("declare -A map[key]");
        var state = new Eval(script);

        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        listing.SetResult(DirectoryListingResult.Success([]));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);

        Assert.True(state.AssociativeArrays.ContainsKey("map"));
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_NamerefExpandsTarget()
    {
        var script = Parser.Parse("declare -n ref=target; target=value; echo $ref");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("value\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_NamerefAssignsThrough()
    {
        var script = Parser.Parse("declare -n ref=target; ref=value; echo $target");
        var state = new Eval(script);

        var append = await NextWriteAsync(state);
        Assert.Equal("value\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_DeclareBuiltin_NamerefUnsetRemovesReferent()
    {
        var script = Parser.Parse("target=value; declare -n ref=target; unset ref");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("target", state.Namerefs["ref"].ToUtf8String());
        Assert.False(state.Variables.ContainsKey("target"));
    }

    [Fact]
    public async Task Evaluation_PushdBuiltin_UpdatesStackAndOutputs()
    {
        var script = Parser.Parse("pushd target");
        var state = new Eval(script);

        var fileTests = await NextPathKindsAsync(state);
        Assert.Single(fileTests.Paths);
        Assert.Equal("/target", fileTests.Paths[0].Path);
        fileTests.SetResult([PathKindResult(FilePathKind.Directory)]);

        var append = await NextWriteAsync(state);
        Assert.Equal("/target /\n", Text(append.Content));
        append.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("/target", Text(state.CurrentDirectory));
    }

    [Fact]
    public async Task Evaluation_PopdBuiltin_UpdatesStackAndOutputs()
    {
        var script = Parser.Parse("pushd target; popd");
        var state = new Eval(script);

        var pushTests = await NextPathKindsAsync(state);
        pushTests.SetResult([PathKindResult(FilePathKind.Directory)]);

        var pushOutput = await NextWriteAsync(state);
        pushOutput.SetResult(DescriptorWriteResult.Open);

        var popTests = await NextPathKindsAsync(state);
        Assert.Equal("/", Assert.Single(popTests.Paths).Path);
        popTests.SetResult([PathKindResult(FilePathKind.Directory)]);

        var popOutput = await NextWriteAsync(state);
        Assert.Equal("/\n", Text(popOutput.Content));
        popOutput.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("/", Text(state.CurrentDirectory));
    }

    [Fact]
    public async Task Evaluation_PopdBuiltin_EmptyStackReturnsFailure()
    {
        var script = Parser.Parse("popd");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: popd: directory stack empty\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ExportBuiltin_MarksExistingVariable()
    {
        var script = Parser.Parse("read name; export name");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        read.SetResult(ReadResult.Success(Utf8Bytes("value\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.True(HasExported(state, "name"));
    }

    [Fact]
    public async Task Evaluation_ExportBuiltin_AssignsAndMarksVariable()
    {
        var script = Parser.Parse("export foo=bar");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("bar", GetVariable(state, "foo"));
        Assert.True(HasExported(state, "foo"));
    }

    [Fact]
    public async Task Evaluation_ExportBuiltin_MissingVariableRemainsUnset()
    {
        var script = Parser.Parse("export missing");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("missing"));
        Assert.True(HasExported(state, "missing"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1name")]
    [InlineData("bad-name")]
    public async Task Evaluation_ExportBuiltin_RejectsInvalidBareNames(string name)
    {
        var state = new Eval(Parser.Parse("export '" + name + "'"));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: export: `" + name + "': not a valid identifier\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.False(state.Variables.ContainsKey(name));
        Assert.False(HasExported(state, name));
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_IncludesExportedEnvironment()
    {
        var script = Parser.Parse("export FOO=bar; cmd");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_ExcludesUnexportedVariables()
    {
        var script = Parser.Parse("declare FOO=bar; cmd");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Null(FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_IncludesAssignmentPrefixes()
    {
        var script = Parser.Parse("FOO=bar cmd");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("FOO"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_DoesNotAffectCommandArguments()
    {
        var script = Parser.Parse("FOO=outer; FOO=bar cmd $FOO");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal(["outer"], runCommand.Command.Arguments.Select(argument => argument.ToUtf8String()));
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("outer", GetVariable(state, "FOO"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_DoesNotAffectCommandBuiltinArguments()
    {
        var script = Parser.Parse("FOO=outer; FOO=bar command cmd $FOO");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal(["outer"], runCommand.Command.Arguments.Select(argument => argument.ToUtf8String()));
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("outer", GetVariable(state, "FOO"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_CommandSubstitutionUsesEarlierPrefixAssignments()
    {
        var script = Parser.Parse("FOO=outer; FOO=bar BAZ=$(command echo $FOO) cmd");
        var state = new Eval(script);

        var pipe = await NextPipeAsync(state);
        pipe.SetResult(PipeCreationResult.Created(new PipeDescriptors(20, 21)));

        var capture = Assert.IsType<RunCapturedEvaluation>(await NextStepAsync(state));
        var echo = await NextWriteAsync(capture.Stage.Evaluation);
        Assert.Equal(21, echo.FileDescriptor.Id);
        Assert.Equal("bar\n", Text(echo.Content));
        echo.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(capture.Stage.Evaluation));
        capture.SetResult(new CapturedEvaluationResult(new BoundedReadResult.Complete(Utf8Bytes("bar\n")), 0));

        var closeRead = await NextCloseDescriptorAsync(state);
        Assert.Equal(20, closeRead.Descriptor.Id);
        closeRead.SetResult(null);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "BAZ"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("outer", GetVariable(state, "FOO"));
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_EnvironmentReflectsCommandNameExpansionAssignments()
    {
        var script = Parser.Parse("export FOO; ${FOO:=cmd}");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal("cmd", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("cmd", GetVariable(state, "FOO"));
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_EnvironmentReflectsArgumentExpansionAssignments()
    {
        var script = Parser.Parse("export FOO; cmd ${FOO:=bar}");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal(["bar"], runCommand.Command.Arguments.Select(argument => argument.ToUtf8String()));
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("bar", GetVariable(state, "FOO"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_EnvironmentReflectsAssignmentValueExpansionAssignments()
    {
        var script = Parser.Parse("export FOO; X=${FOO:=bar} cmd");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "X"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("bar", GetVariable(state, "FOO"));
        Assert.False(state.Variables.ContainsKey("X"));
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_SeesArgumentExpansionAssignments()
    {
        var script = Parser.Parse("export X; X=$FOO cmd ${FOO:=bar}");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal(["bar"], runCommand.Command.Arguments.Select(argument => argument.ToUtf8String()));
        Assert.Equal("bar", FindEnvironmentValue(runCommand.Command.Environment, "X"));
        Assert.Null(FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("bar", GetVariable(state, "FOO"));
        Assert.False(state.Variables.ContainsKey("X"));
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_AssignmentPrefixOverridesExpansionEnvironment()
    {
        var script = Parser.Parse("export FOO; FOO=prefix cmd ${FOO:=bar}");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal(["bar"], runCommand.Command.Arguments.Select(argument => argument.ToUtf8String()));
        Assert.Equal("prefix", FindEnvironmentValue(runCommand.Command.Environment, "FOO"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("bar", GetVariable(state, "FOO"));
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_AssignmentPrefixThroughNamerefExportsResolvedName()
    {
        var script = Parser.Parse("declare -n ref=target; target=outer; ref=temp cmd");
        var state = new Eval(script);

        var runCommand = await NextCommandAsync(state, "cmd");
        Assert.Equal("temp", FindEnvironmentValue(runCommand.Command.Environment, "target"));
        Assert.Null(FindEnvironmentValue(runCommand.Command.Environment, "ref"));
        runCommand.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("outer", GetVariable(state, "target"));
    }

    [Fact]
    public async Task Evaluation_UnsetBuiltin_RemovesExportMarker()
    {
        var script = Parser.Parse("export foo=bar; unset foo");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("foo"));
        Assert.False(HasExported(state, "foo"));
    }

    [Fact]
    public async Task Evaluation_ReadonlyBuiltin_AssignsAndMarksVariable()
    {
        var script = Parser.Parse("readonly foo=bar");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("bar", GetVariable(state, "foo"));
        Assert.True(HasReadonly(state, "foo"));
    }

    [Fact]
    public async Task Evaluation_ReadonlyBuiltin_PreventsUnset()
    {
        var script = Parser.Parse("readonly foo=bar; unset foo");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: unset: foo: cannot unset: readonly variable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal("bar", GetVariable(state, "foo"));
        Assert.True(HasReadonly(state, "foo"));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadonlyBuiltin_PreventsReadAssignment()
    {
        var script = Parser.Parse("readonly foo; read foo");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        read.SetResult(ReadResult.Success(Utf8Bytes("value\n"), [], reachedEoF: false));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal("line 1: foo: readonly variable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("foo"));
        Assert.True(HasReadonly(state, "foo"));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticCommand_YieldsOperation()
    {
        var script = Parser.Parse("((1))");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticCommand_ZeroResultReturnsFailure()
    {
        var script = Parser.Parse("((0))");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_EvaluatesExpression()
    {
        var script = Parser.Parse("[[ -n 1 ]]");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_FalseReturnsFailure()
    {
        var script = Parser.Parse("[[ -n \"\" ]]");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! [[ -n 1 ]]");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ConditionalCommand_UsesPathInfosOperation()
    {
        var script = Parser.Parse("[[ -d target ]]");
        var state = new Eval(script);

        var operation = await NextPathKindsAsync(state);
        Assert.Single(operation.Paths);
        Assert.Equal("/target", operation.Paths[0].Path);
        operation.SetResult([PathKindResult(FilePathKind.Directory)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CaseCommand_SelectsMatchingItem()
    {
        var script = Parser.Parse("case foo in bar) first ;; foo) second ;; esac");
        var state = new Eval(script);

        var second = await NextCommandAsync(state, "second");
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("hit", "mis")]
    [InlineData("h[ia]t", "m[io]s")]
    public async Task Case_ReusesPatternWorkspaceBeforeRunningTheBody(string match, string miss)
    {
        var patterns = Enumerable.Repeat(miss, 128).ToArray();
        patterns[0] = match;
        var first = await WorkspaceAtBodyAsync(patterns);
        patterns[0] = miss;
        patterns[^1] = match;
        var last = await WorkspaceAtBodyAsync(patterns);

        Assert.Equal(first, last);

        static async Task<(long Workspace, long Peak)> WorkspaceAtBodyAsync(string[] patterns)
        {
            var governor = new MemoryGovernor(4 * 1024 * 1024);
            using var owner = governor.CreateScope();
            var state = new Eval(Parser.Parse($"case hit in {string.Join('|', patterns)}) printf selected;; esac"));
            state.Memory.Budget = owner;

            var body = await NextWriteAsync(state);
            Assert.Equal(FileDescriptor.StdOut, body.FileDescriptor);
            Assert.Equal("selected", Text(body.Content));
            var workspace = state.Memory.Workspace.Bytes;
            var peak = governor.PeakBytes;
            body.SetResult(DescriptorWriteResult.Open);
            Assert.Null(await NextStepAsync(state));
            Assert.Equal(0, state.LastExitCode);

            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return (workspace, peak);
        }
    }

    [Fact]
    public async Task Case_RetainsSubjectDuringBodyOnlyWhenMatchingResumes()
    {
        var fallThrough = await MeasureAsync(";&");
        var retest = await MeasureAsync(";;&");

        var subjectBytes = 128 * 64 + 127;
        Assert.Equal(MemoryCapacity.ArrayBackingBytes<byte>(subjectBytes), retest.First - fallThrough.First);
        Assert.Equal(fallThrough.Second, retest.Second);

        static async Task<(long First, long Second)> MeasureAsync(string terminator)
        {
            var governor = new MemoryGovernor(4 * 1024 * 1024);
            using var owner = governor.CreateScope();
            var state = new Eval(Parser.Parse("case \"$@\" in *) printf first" + terminator + " *) printf second;; esac"));
            state.Memory.Budget = owner;
            var field = ShellValue.FromText(new string('x', 64));
            state.ReplacePositionalParameters(Enumerable.Repeat(field, 128).ToArray());

            var first = await NextWriteAsync(state);
            Assert.Equal("first", Text(first.Content));
            var firstBytes = state.Memory.Workspace.Bytes;
            first.SetResult(DescriptorWriteResult.Open);
            var second = await NextWriteAsync(state);
            Assert.Equal("second", Text(second.Content));
            var secondBytes = state.Memory.Workspace.Bytes;
            second.SetResult(DescriptorWriteResult.Open);
            Assert.Null(await NextStepAsync(state));

            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return (firstBytes, secondBytes);
        }
    }

    [Fact]
    public async Task Case_PatternFieldsFitWithoutAJoinedSegmentList()
    {
        var state = new Eval(Parser.Parse("""case "$*" in $@) printf matched;; *) printf missed;; esac"""));
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        const int FieldCount = 256;
        state.ReplacePositionalParameters(Enumerable.Range(0, FieldCount)
            .Select(index => ShellValue.FromText(new string((char)('a' + index % 26), index == 0 ? 16 : 15)))
            .ToArray());

        var body = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, body.FileDescriptor);
        Assert.Equal("matched", Text(body.Content));
        body.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task Evaluation_CaseCommand_NoMatchReturnsZero()
    {
        var script = Parser.Parse("case foo in bar) first ;; esac");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_CaseCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! case foo in bar) first ;; esac");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticForCommand_ExecutesSingleIteration()
    {
        var script = Parser.Parse("for ((1; 1; 0)); do body; break; done");
        var state = new Eval(script);

        var body = await NextCommandAsync(state, "body");
        body.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticForCommand_AllowsZeroIterations()
    {
        var script = Parser.Parse("for ((1; 0; 0)); do body; done");
        var state = new Eval(script);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_ArithmeticForCommand_NegationInvertsExitStatus()
    {
        var script = Parser.Parse("! for ((1; 1; 0)); do body; break; done");
        var state = new Eval(script);

        var body = await NextCommandAsync(state, "body");
        body.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    /// <summary>
    /// Encodes a string as UTF-8 bytes for content inputs.
    /// </summary>
    private static ReadOnlyMemory<byte> Utf8Bytes(string value)
    {
        return string.IsNullOrEmpty(value) ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(value);
    }

    /// <summary>
    /// Gets the next operation from the evaluator under test.
    /// </summary>
    private static Task<EvaluationOperation?> NextStepAsync(Eval state)
    {
        return state.GetNextStep();
    }

    /// <summary>
    /// Reads the next operation and asserts it is a create-pipe operation.
    /// </summary>
    private static async Task<CreatePipeOperation> NextPipeAsync(
        Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        var pipe = Assert.IsType<CreatePipeOperation>(operation);
        return pipe;
    }

    /// <summary>
    /// Reads the next operation and asserts a single command with an expected name.
    /// </summary>
    private static async Task<RunCommand> NextCommandAsync(Eval state, string expectedName)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        var runCommand = Assert.IsType<RunCommand>(operation);
        Assert.Equal(expectedName, runCommand.Command.CommandName);
        return runCommand;
    }

    /// <summary>
    /// Reads the next operation and asserts it is a run-command operation.
    /// </summary>
    private static async Task<RunCommand> NextCommandAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<RunCommand>(operation);
    }

    /// <summary>
    /// Reads the next operation and asserts it is a command-resolution operation.
    /// </summary>
    private static async Task<ResolveCommandsOperation> NextResolveCommandsAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<ResolveCommandsOperation>(operation);
    }

    private static async Task<RunPipelineEvaluations> NextPipelineAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<RunPipelineEvaluations>(operation);
    }

    /// <summary>
    /// Reads the next operation and asserts it is a path-kind lookup.
    /// </summary>
    private static async Task<PathInfosOperation> NextPathKindsAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        var paths = Assert.IsType<PathInfosOperation>(operation);
        Assert.Equal(FilePathInfoRequest.KindOnly, paths.Request);
        return paths;
    }

    private static async Task<TerminalStatusesOperation> NextTerminalStatusesAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<TerminalStatusesOperation>(operation);
    }

    /// <summary>
    /// Reads the next operation and asserts it is an append operation.
    /// </summary>
    private static async Task<WriteOperation> NextWriteAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<WriteOperation>(operation);
    }

    /// <summary>
    /// Reads the next operation and asserts it is an open-file operation.
    /// </summary>
    private static async Task<OpenFileOperation> NextOpenFileAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<OpenFileOperation>(operation);
    }

    /// <summary>
    /// Reads the next operation and asserts it is a read-line operation.
    /// </summary>
    private static async Task<ReadLineOperation> NextReadAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<ReadLineOperation>(operation);
    }

    /// <summary>
    /// Reads the next operation and asserts it is a close-descriptor operation.
    /// </summary>
    private static async Task<CloseDescriptorOperation> NextCloseDescriptorAsync(Eval state)
    {
        var operation = await NextStepAsync(state);
        Assert.NotNull(operation);

        return Assert.IsType<CloseDescriptorOperation>(operation);
    }

    /// <summary>
    /// Finds a variable value in an environment list using a string name.
    /// </summary>
    private static string? FindEnvironmentValue(IReadOnlyList<EnvironmentVariable> environment, string name)
    {
        foreach (var var in environment)
        {
            if (var.Name == name)
            {
                return var.Value.ToUtf8String();
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a variable value from evaluation state or throws if missing.
    /// </summary>
    private static string GetVariable(Eval state, string name)
    {
        if (!state.Variables.TryGetValue(name, out var value))
        {
            throw new InvalidOperationException("Variable was not assigned.");
        }

        return value.ToUtf8String();
    }

    /// <summary>
    /// Returns a variable value or an empty buffer when unset.
    /// </summary>
    private static string GetVariableOrEmpty(Eval state, string name)
    {
        return state.Variables.TryGetValue(name, out var value)
            ? value.ToUtf8String()
            : string.Empty;
    }

    /// <summary>
    /// Returns true when a variable name is marked as exported.
    /// </summary>
    private static bool HasExported(Eval state, string name)
    {
        foreach (var exported in state.ExportedVariables)
        {
            if (exported == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when a variable name is marked as readonly.
    /// </summary>
    private static bool HasReadonly(Eval state, string name)
    {
        foreach (var item in state.ReadonlyVariables)
        {
            if (item == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the path string from a ShellPath for test assertions.
    /// </summary>
    private static string Text(ShellPath path) => path.Path;

    /// <summary>
    /// Decodes UTF-8 bytes to a managed string for test assertions.
    /// </summary>
    private static string Text(ReadOnlyMemory<byte> content)
    {
        return Encoding.UTF8.GetString(content.Span);
    }
}

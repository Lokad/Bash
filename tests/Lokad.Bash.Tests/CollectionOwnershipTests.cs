using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class CollectionOwnershipTests
{
    [Fact]
    public void FunctionReplacementRetiresThePreviousNamesBacking()
    {
        var first = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }\n#" + new string('a', 8192)).Commands));
        var next = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }\n#" + new string('b', 16384)).Commands));
        var state = new EvaluationState(Parser.Parse(""), ShellPath.Root, inheritedDescriptorTable: null);

        var memory = new EvaluationMemory(new MemoryGovernor(1024 * 1024));
        Assert.True(state.TryRegisterFunction(first, sourceMemory: null, memory));
        Assert.True(state.TryRegisterFunction(next, sourceMemory: null, memory));

        var name = Assert.Single(state.Functions.Keys);
        Assert.True(name.Bytes.Equals(next.Name.Value.Bytes));
        Assert.False(name.Bytes.Equals(first.Name.Value.Bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildDirectoryStackRequiresIndependentBackingAdmission(bool inlineSubshell)
    {
        var script = Parser.Parse(":");
        var previous = ShellPath.Root;
        var target = new ShellPath("/work");
        var parent = new EvaluationState(script, previous, inheritedDescriptorTable: null);
        var staging = new EvaluationMemory(new MemoryGovernor(64 * 1024));
        try
        {
            parent.ApplyDirectoryLookup(DirectoryChangeKind.PushDirectory, target, succeeded: true);
            for (var index = 0; index < 512; index++)
                parent.CompleteDirectoryChange(DirectoryChangeKind.PushDirectory, previous, staging);
        }
        finally { staging.Finish(retainVariables: false); }

        var tight = new MemoryGovernor(1024);
        var refused = new EvaluationMemory(tight);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() => Fork(refused));
            Assert.Equal(0, tight.ReservedBytes);
        }
        finally { refused.Finish(retainVariables: false); }

        var budget = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            var child = Fork(memory);
            Assert.Equal(513, child.DirectoryStack.Count);
            Assert.Same(target, child.CurrentDirectory);
            Assert.Same(previous, child.DirectoryStack[1]);
            Assert.True(budget.ReservedBytes >= child.RetainedDirectoryStackBytes);
            child.ResetDirectoryStack();
            Assert.Same(target, Assert.Single(child.DirectoryStack));
            Assert.Equal(513, parent.DirectoryStack.Count);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);

        parent.ResetDirectoryStack();
        var compact = new EvaluationMemory(tight);
        try
        {
            var child = Fork(compact);
            Assert.Same(target, Assert.Single(child.DirectoryStack));
            Assert.True(child.RetainedDirectoryStackBytes < parent.RetainedDirectoryStackBytes);
        }
        finally { compact.Finish(retainVariables: false); }
        Assert.Equal(0, tight.ReservedBytes);

        EvaluationState Fork(EvaluationMemory owner) => inlineSubshell ? parent.CloneForSubshell(owner)
            : parent.CloneForNestedEvaluation(script, EvaluationDescriptorSetup.Standard, ShellForkKind.PipelineSimpleCommand, owner);
    }

    [Fact]
    public void NestedTrapForkAdmitsItsMapsAndSharesActionBytes()
    {
        var script = Parser.Parse(":");
        var parent = new EvaluationState(script, ShellPath.Root, inheritedDescriptorTable: null);
        var action = ShellValue.FromText("#" + new string('x', 2048));
        var staging = new EvaluationMemory(new MemoryGovernor(64 * 1024));
        try
        {
            parent.GetOrCreateTraps().Set(TrapCondition.Exit, action, staging);
            parent.GetOrCreateTraps().Set(TrapCondition.Interrupt, ShellValue.Empty, staging);
        }
        finally { staging.Finish(retainVariables: false); }

        var tight = new MemoryGovernor(parent.RetainedDirectoryStackBytes);
        var refused = new EvaluationMemory(tight);
        try { Assert.Throws<MemoryBudgetExceededException>(() => Fork(refused)); }
        finally { refused.Finish(retainVariables: false); }
        Assert.Equal(0, tight.ReservedBytes);

        // The copied maps fit even though a second copy of the action could not fit.
        var budget = new MemoryGovernor(1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            var child = Fork(memory);
            var traps = Assert.IsType<ShellTraps>(child.Traps);
            Assert.True(traps.ListedHandlers[TrapCondition.Exit].Bytes.Equals(action.Bytes));
            Assert.False(traps.TryBeginExit(0, out _));
            traps.DiscardInheritedListing();
            Assert.True(traps.ListedHandlers[TrapCondition.Interrupt].IsEmpty);
            Assert.True(parent.GetOrCreateTraps().ListedHandlers.ContainsKey(TrapCondition.Exit));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);

        EvaluationState Fork(EvaluationMemory owner) =>
            parent.CloneForNestedEvaluation(script, EvaluationDescriptorSetup.Standard, ShellForkKind.CommandSubstitution, owner);
    }

    [Fact]
    public void SyntaxCollections_RetainCompletedConstructorInputs()
    {
        var wordParts = new List<WordPart> { WordPart.FromText(WordPartKind.Unquoted, "value") };
        var word = new Word(wordParts);
        var command = new SimpleCommand([], [word], [], SimpleCommand.NoDeclarationAssignments);
        var commands = new List<ICommand> { command };
        var script = new ParsedScript(commands);
        var loopItems = new List<Word> { word };
        var loop = new ForCommand("item", loopItems, command);
        var patterns = new List<Word> { word };
        var caseItem = new CaseItem(patterns, command);
        var caseItems = new List<CaseItem> { caseItem };
        var caseCommand = new CaseCommand(word, caseItems);
        var elifClauses = new List<ElifClause> { new(command, command) };
        var ifCommand = new IfCommand(command, command, elifClauses, null);
        var redirections = new List<Redirection>();
        var redirected = new RedirectedCommand(command, redirections);

        // Construction retains completed lists; callers must not mutate them afterward.
        Assert.Same(wordParts, word.Parts);
        Assert.Same(commands, script.Commands);
        Assert.Same(loopItems, loop.Items);
        Assert.Same(patterns, caseItem.Patterns);
        Assert.Same(caseItems, caseCommand.Items);
        Assert.Same(elifClauses, ifCommand.ElifClauses);
        Assert.Same(redirections, redirected.Redirections);
    }

    [Fact]
    public void ExpandedWord_RetainsCompletedSegments()
    {
        var segments = new List<ExpandedSegment>
        {
            new(ShellValue.FromText("first"), isGlobQuoted: false)
        };
        var word = new ExpandedWord(segments);

        Assert.Same(segments, word.Segments);
        Assert.Equal("first", word.ToText(new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes))));
        Assert.False(word.Segments[0].IsGlobQuoted);
    }
}

using System.Reflection;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class CollectionOwnershipTests
{
    [Fact]
    public void RefusedCaseConversionPreservesThePreviousScalar()
    {
        var variables = Variables.CreateEmpty();
        var original = ShellValue.FromText("old");
        Assert.True(variables.TrySetScalar("value", original));
        variables.SetCaseAttributes("value", default(Variables.CaseAttributeChange)
            .Include(Variables.BindingAttributes.Uppercase, remove: false));
        var governor = new MemoryGovernor(0);
        var memory = new EvaluationMemory(governor);

        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() =>
                variables.TrySetScalar("value", ShellValue.FromText("new"), memory));
            Assert.True(variables.TryGetScalar("value", out var after));
            Assert.Equal(original, after);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void RefusedRejectedNamerefArrayPreparationPreservesBothBindingOwners()
    {
        var owner = Variables.CreateEmpty();
        var command = Variables.CreateEmpty();
        var oldTarget = ShellValue.FromText("dest");
        Assert.True(owner.TrySetNameref("slot", oldTarget));
        Assert.True(command.TrySetNameref("slot", oldTarget));
        var locals = new Variables.LocalBindings(owner, parent: null, prefixes: null);
        var governor = new MemoryGovernor(0);
        var memory = new EvaluationMemory(governor);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() =>
                locals.PrepareRejectedNamerefArray(command, "slot", ArrayKind.Indexed, initialized: true,
                    (Variables.BindingAttributes.Exported, Variables.BindingAttributes.None), memory));
            Assert.True(command.TryGetNameref("slot", out var commandTarget));
            Assert.Equal(oldTarget, commandTarget);
            Assert.False(command.IsExported("slot"));
            Assert.True(owner.TryGetNameref("slot", out var ownerTarget));
            Assert.Equal(oldTarget, ownerTarget);
            Assert.False(owner.IsExported("slot"));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void IndexedWriteAdmitsBackingOverlapBeforeAddingAPosition()
    {
        var variables = Variables.CreateEmpty();
        var original = ShellValue.FromText("original");
        Assert.True(variables.TryAssignArray("a", Enumerable.Repeat(original, 1024).ToArray(), ArrayKind.Indexed));
        var array = variables.IndexedArrays["a"];
        var elements = Assert.IsType<Dictionary<long, ShellValue?>>(typeof(IndexedArray)
            .GetField("_elements", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(array));
        var capacity = elements.EnsureCapacity(0);
        for (var index = array.Count; index < capacity; index++)
        {
            Assert.True(IndexedArrayIndex.TryCreate(index, out var position));
            Assert.True(variables.TrySetArrayElement("a", position, original));
        }
        Assert.True(IndexedArrayIndex.TryCreate(capacity, out var next));
        var positionToAdd = new WordExpansion.ResolvedVariableReference.Indexed("a", next);
        var budget = new MemoryGovernor(8 * 1024 * 1024);
        var evaluation = new Eval(Parser.Parse(":"));
        evaluation.Memory.Budget = budget;
        var scope = new ScopedEvaluation(evaluation, variables, default);
        try
        {
            using var command = evaluation.Memory.BeginCommand(scope);
            evaluation.Memory.ReleaseUnusedCapacity();
            var before = evaluation.Memory.Workspace.Bytes;
            var oldBacking = CollectionBacking.ArrayPayloadBytes(elements);
            // Permit one prepaid increment for the new entry, below the backing overlap.
            var limit = (before + 2048 + 65535) / 65536 * 65536;
            Exception? failure;
            using (budget.Reserve(budget.LimitBytes - limit))
                failure = Record.Exception(() => positionToAdd.TryPublishValue(scope, "a", original));
            var after = CollectionBacking.ArrayPayloadBytes(elements);
            Assert.True(failure is MemoryBudgetExceededException,
                $"Assignment result {failure?.GetType().Name ?? "published"}, backing {oldBacking} -> {after} B, workspace {before} -> {evaluation.Memory.Workspace.Bytes} B.");
            Assert.Equal(oldBacking, after);
            Assert.Equal(capacity, array.Count);
            Assert.False(array.TryGet(next, out _));

            Assert.True(positionToAdd.TryPublishValue(scope, "a", original));
            Assert.True(array.TryGet(next, out var inserted));
            Assert.Equal(original, inserted);
            Assert.Equal(capacity + 1, array.Count);
        }
        finally { evaluation.Memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RefusedArrayWriteKeepsThePreviousBinding(bool associative, bool replaceElement)
    {
        var variables = Variables.CreateEmpty();
        var original = ShellValue.FromText("original");
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromBytes(new byte[] { 0xFF }), out var key));
        if (!replaceElement)
            Assert.True(variables.TrySetScalar("a", original));
        else if (associative)
            Assert.True(variables.TrySetArrayElement("a", key, original));
        else
            Assert.True(variables.TrySetArrayElement("a", default(IndexedArrayIndex), original));
        var target = associative ? (WordExpansion.ResolvedVariableReference)new WordExpansion.ResolvedVariableReference.Associative("a", key)
            : new WordExpansion.ResolvedVariableReference.Indexed("a", default);
        var replacement = ShellValue.FromText(new string('x', 2048));
        var budget = new MemoryGovernor(1024 * 1024);
        var evaluation = new Eval(Parser.Parse(":"));
        evaluation.Memory.Budget = budget;
        var scope = new ScopedEvaluation(evaluation, variables, default);
        try
        {
            using var command = evaluation.Memory.BeginCommand(scope);
            evaluation.Memory.ReleaseUnusedCapacity();
            Exception? failure;
            using (budget.Reserve(budget.LimitBytes - budget.ReservedBytes))
                failure = Record.Exception(() => target.TryPublishValue(scope, "a", replacement));
            Assert.IsType<MemoryBudgetExceededException>(failure);
            if (replaceElement)
            {
                Assert.True(target.TryRead(variables, out var retained));
                Assert.Equal(original, retained);
            }
            else
            {
                Assert.Equal(original, variables.Scalars["a"]);
                Assert.Empty(variables.IndexedArrays);
                Assert.Empty(variables.AssociativeArrays);
            }
        }
        finally { evaluation.Memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void RefusedArrayPromotionKeepsAnEarlierRetainedOutput()
    {
        var variables = Variables.CreateEmpty();
        var original = ShellValue.FromText("original");
        var output = ShellValue.FromText("output");
        Assert.True(variables.TryAssignArray("a", [original], ArrayKind.Indexed));
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("a", ShellValue.FromText(new string('x', 65536))));
        var budget = new MemoryGovernor(1024 * 1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            IndexedArray earlier;
            using (var bindings = new Variables.CommandBindings(variables, prepared, new HashSet<BindingName> { "a" },
                suppressTemporaryLookup: false, memory, CancellationToken.None))
            {
                var retained = bindings.SelectOutputVariables("a", memory, CancellationToken.None);
                Assert.True(retained.TrySetArrayElement("a", default(IndexedArrayIndex), output));
                bindings.CompleteOutput("a", retained);
                earlier = retained.IndexedArrays["a"];
                memory.ReleaseUnusedCapacity();
                var before = memory.Workspace.Bytes;
                // The copied candidate fits, but the new retained value does not.
                using (budget.Reserve(budget.LimitBytes - 65536))
                    Assert.Throws<MemoryBudgetExceededException>(() => bindings.RetainBinding("a", memory, CancellationToken.None));
                Assert.True(memory.Workspace.Bytes > before);
                Assert.Same(earlier, retained.IndexedArrays["a"]);
            }
            Assert.Same(earlier, variables.IndexedArrays["a"]);
            Assert.True(earlier.TryGet(default, out var restored));
            Assert.Equal(output, restored);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void FunctionReplacementRetiresThePreviousNamesBacking()
    {
        var first = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }\n#" + new string('a', 8192)).Commands));
        var next = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }\n#" + new string('b', 16384)).Commands));
        var state = new EvaluationState(Parser.Parse(""), ShellPath.Root, inheritedDescriptorTable: null);

        var memory = new EvaluationMemory(new MemoryGovernor(1024 * 1024));
        Assert.True(state.TryRegisterFunction(first, context: default, memory));
        Assert.True(state.TryRegisterFunction(next, context: default, memory));

        var name = Assert.Single(state.EnumerateFunctions()).Key;
        Assert.True(name.Bytes.Equals(next.Name.Value.Bytes));
        Assert.False(name.Bytes.Equals(first.Name.Value.Bytes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FunctionForkAdmitsLiveBindingsAcrossVacatedSlots(bool inlineSubshell, bool keepBindings)
    {
        var script = Parser.Parse(":");
        var parent = new EvaluationState(script, ShellPath.Root, inheritedDescriptorTable: null);
        var setup = new EvaluationMemory(new MemoryGovernor(128 * 1024));
        var budget = new MemoryGovernor(2048);
        var memory = new EvaluationMemory(budget);
        var live = new List<FunctionBinding>();
        EvaluationState? child = null;
        try
        {
            var bindings = Enumerable.Range(0, 257).Select(index =>
            {
                var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse($"f{index}(){{ :; }}").Commands));
                var attributes = index == 64 ? FunctionAttributes.None : FunctionAttributes.Exported;
                var binding = new FunctionBinding(definition, attributes, sourceMemory: null, setup);
                parent.SetFunctionBinding(binding);
                return binding;
            }).ToArray();
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                if (keepBindings && index is 63 or 64 or 256)
                    live.Add(binding);
                else
                    Assert.True(parent.TryUnsetFunction(binding.Definition.Name.Value));
            }
            Assert.True(parent.EstimatedFunctionMemoryBytes > budget.LimitBytes);

            child = inlineSubshell ? parent.CloneForSubshell(memory, inheritedExitBindings: null)
                : parent.CloneForNestedEvaluation(script, EvaluationDescriptorSetup.Standard, ShellForkKind.PipelineSimpleCommand,
                    memory, inheritedExitBindings: null);

            Assert.Equal(live.Count, child.FunctionCount);
            Assert.Equal(child.EstimatedFunctionMemoryBytes + child.RetainedDirectoryStackBytes, memory.Workspace.Bytes);
            var exported = live.Where(binding => binding.Attributes == FunctionAttributes.Exported);
            Assert.Equal(exported, child.GetExportedFunctions(memory).ToArray());
            foreach (var binding in live)
            {
                Assert.True(child.TryGetFunction(binding.Definition.Name.Value, out var current));
                Assert.Same(binding, current);
                Assert.True(child.TryUnsetFunction(binding.Definition.Name.Value));
            }
            Assert.Equal(0, child.FunctionCount);
            Assert.Equal(exported, parent.GetExportedFunctions(setup).ToArray());
            Assert.Equal(live.Count, parent.FunctionCount);
        }
        finally
        {
            child?.ReleaseFunctions();
            parent.ReleaseFunctions();
            memory.Finish(retainVariables: false);
            setup.Finish(retainVariables: false);
        }
        Assert.Equal(0, budget.ReservedBytes);
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
            var previousEntry = parent.ApplyDirectoryLookup(DirectoryChangeKind.PushDirectory, target, succeeded: true, staging);
            for (var index = 0; index < 512; index++)
                parent.CompleteDirectoryChange(DirectoryChangeKind.PushDirectory, previousEntry, staging);
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
            Assert.Equal(513, child.DirectoryStackCount);
            Assert.Same(target, child.CurrentDirectory);
            Assert.Same(previous, child.GetDirectoryStackEntry(1).Path);
            Assert.True(budget.ReservedBytes >= child.RetainedDirectoryStackBytes);
            child.ResetDirectoryStack();
            Assert.Equal(1, child.DirectoryStackCount);
            Assert.Same(target, child.CurrentDirectory);
            Assert.Equal(513, parent.DirectoryStackCount);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);

        parent.ResetDirectoryStack();
        var compact = new EvaluationMemory(tight);
        try
        {
            var child = Fork(compact);
            Assert.Equal(1, child.DirectoryStackCount);
            Assert.Same(target, child.CurrentDirectory);
            Assert.True(child.RetainedDirectoryStackBytes < parent.RetainedDirectoryStackBytes);
        }
        finally { compact.Finish(retainVariables: false); }
        Assert.Equal(0, tight.ReservedBytes);

        EvaluationState Fork(EvaluationMemory owner) => inlineSubshell ? parent.CloneForSubshell(owner, inheritedExitBindings: null)
            : parent.CloneForNestedEvaluation(script, EvaluationDescriptorSetup.Standard, ShellForkKind.PipelineSimpleCommand,
                owner, inheritedExitBindings: null);
    }

    [Fact]
    public void RefusingTheFirstTrapActionDoesNotPublishEmptyState()
    {
        var parent = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        var budget = new MemoryGovernor(0);
        var memory = new EvaluationMemory(budget);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() => parent.SetTrap(
                TrapCondition.Exit, ShellValue.FromText(":"), memory));
            Assert.Null(parent.Traps);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
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
            parent.SetTrap(TrapCondition.Exit, action, staging);
            parent.SetTrap(TrapCondition.Interrupt, ShellValue.Empty, staging);
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
            Assert.False(traps.TryBeginExit(0, useInheritedPipelineAction: false, out _));
            traps.DiscardInheritedListing();
            Assert.True(traps.ListedHandlers[TrapCondition.Interrupt].IsEmpty);
            Assert.True(Assert.IsType<ShellTraps>(parent.Traps).ListedHandlers.ContainsKey(TrapCondition.Exit));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);

        EvaluationState Fork(EvaluationMemory owner) =>
            parent.CloneForNestedEvaluation(script, EvaluationDescriptorSetup.Standard, ShellForkKind.CommandSubstitution,
                owner, inheritedExitBindings: null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/other")]
    public void RefusedDirectoryOutputLeavesTheBorrowedPathAvailable(string home)
    {
        var target = new ShellPath("/" + new string('é', 1024));
        var script = Parser.Parse(":");
        var state = new EvaluationState(script, target, inheritedDescriptorTable: null);
        var smaller = new EvaluationState(script, ShellPath.Root, inheritedDescriptorTable: null);
        var budget = new MemoryGovernor(1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() => state.FormatDirectoryStack(
                null, DirectoryStackLayout.Numbered, ShellValue.FromText(home), memory, CancellationToken.None));
            Assert.Same(target, state.CurrentDirectory);
            Assert.Equal(1, state.DirectoryStackCount);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.Equal("/\n", Utf8Text.Decode(smaller.FormatDirectoryStack(
                null, DirectoryStackLayout.Compact, ShellValue.Empty, memory, CancellationToken.None)));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    public void DirectoryOutputFitsWithBoundedHomeDecoding(int homeBytes)
    {
        var home = "/" + new string('a', homeBytes - 5) + "𐐀";
        var state = new EvaluationState(Parser.Parse(":"), new ShellPath(home), inheritedDescriptorTable: null);
        var budget = new MemoryGovernor(64);
        var memory = new EvaluationMemory(budget);
        try
        {
            Assert.Equal("~\n", Utf8Text.Decode(state.FormatDirectoryStack(
                0, DirectoryStackLayout.Compact, ShellValue.FromText(home), memory, CancellationToken.None)));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void DirectoryOutputAdmitsLongDecodedHomeBeforeFormatting()
    {
        var home = "/" + new string('a', 252) + "𐐀";
        var state = new EvaluationState(Parser.Parse(":"), new ShellPath(home), inheritedDescriptorTable: null);
        var budget = new MemoryGovernor(64);
        var memory = new EvaluationMemory(budget);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() => state.FormatDirectoryStack(
                0, DirectoryStackLayout.Compact, ShellValue.FromText(home), memory, CancellationToken.None));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void SelectedDeferredDirectoryFormatsLongHomeAsBytesWithoutDecoding()
    {
        var home = "/" + new string('é', 512);
        var state = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        var setup = new EvaluationMemory(new MemoryGovernor(4096));
        try
        {
            state.PushDeferredDirectory(ShellValue.FromText(home + "/saved"), setup);
            var budget = new MemoryGovernor(64);
            var outputMemory = new EvaluationMemory(budget);
            try
            {
                Assert.Equal("~/saved\n", Utf8Text.Decode(state.FormatDirectoryStack(
                    1, DirectoryStackLayout.Compact, ShellValue.FromText(home), outputMemory, CancellationToken.None)));
            }
            finally { outputMemory.Finish(retainVariables: false); }
            Assert.Equal(0, budget.ReservedBytes);
        }
        finally { setup.Finish(retainVariables: false); }
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
        var memory = new EvaluationMemory(new MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        try { Assert.Equal(segments[0].Content, word.ToValue(memory)); }
        finally { memory.Finish(retainVariables: false); }
        Assert.False(word.Segments[0].IsGlobQuoted);
    }
}

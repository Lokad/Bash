using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Memory;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class HostedFunctionExportTests
{
    [Fact]
    public async Task HostedExportSelectionFollowsNamespaceOrderAndBindingChanges()
    {
        var state = new Eval(Parser.Parse("""
            f(){ printf old; }; g(){ :; }; h(){ :; }
            export -f h f; inspect
            g(){ printf never; }; inspect
            export -fn h; export -f g; inspect
            f(){ printf new; }; inspect
            unset -f f; f(){ printf fresh; }; inspect
            export -f f; inspect
            exec -c inspect
            """));
        string[][] expectedNames = [["f", "h"], ["f", "h"], ["f", "g"], ["f", "g"], ["g"], ["f", "g"], []];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ReadOnlyMemory<byte> original = default;
        for (var index = 0; index < expectedNames.Length; index++)
        {
            var invocation = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
            Assert.Equal("inspect", invocation.Command.CommandName);
            var exports = invocation.Command.Functions;
            Assert.Equal(expectedNames[index], exports.Select(export => Assert.IsType<FunctionDefinition>(
                Assert.Single(Parser.Parse(export.Source).Commands)).Name.Value.ToUtf8String()));
            if (index == 0)
                original = exports[0].Source;
            if (index == 1)
                Assert.Equal(original, exports[0].Source);
            if (index == 3)
                Assert.Contains("new", Utf8Text.Decode(exports[0].Source));
            if (index == 5)
                Assert.Contains("fresh", Utf8Text.Decode(exports[0].Source));
            invocation.SetResult(new CommandExecutionResult.Completed(0));
        }
        Assert.Null(await state.GetNextStep(deadline.Token));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public async Task HostedExportPayloadUsesExactBackingOnRepeatedCalls(int count)
    {
        var names = Enumerable.Range(0, count).Select(index => $"f{index}").ToArray();
        var definitions = string.Concat(names.Select(name => name + "(){ :; }\n"));
        var export = count == 0 ? "" : "export -f " + string.Join(' ', names) + "\n";
        var state = new Eval(Parser.Parse(definitions + "inspect\ninspect\n" + export + "inspect\ninspect"));
        var admission = MemoryCapacity.ArrayBackingBytes<ExportedFunction>(count)
            + MemoryCapacity.ArrayBackingBytes<FunctionBinding>(count);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var warmup = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        warmup.SetResult(new CommandExecutionResult.Completed(0));
        var unexported = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        Assert.Empty(unexported.Command.Functions);
        var workspaceWithoutExports = state.Memory.Workspace.Bytes;
        unexported.SetResult(new CommandExecutionResult.Completed(0));

        var first = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        var firstPayload = Assert.IsType<ExportedFunction[]>(first.Command.Functions);
        Assert.Equal(count, firstPayload.Length);
        first.SetResult(new CommandExecutionResult.Completed(0));

        var second = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        var secondPayload = Assert.IsType<ExportedFunction[]>(second.Command.Functions);
        Assert.Equal(count, secondPayload.Length);
        Assert.Equal(firstPayload, secondPayload);
        // Both measured calls follow the same last-argument publication.
        Assert.Equal(admission, state.Memory.Workspace.Bytes - workspaceWithoutExports);
        second.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await state.GetNextStep(deadline.Token));
        Assert.Equal(0, state.LastExitCode);
        state.ReleaseFunctions();
    }

    [Fact]
    public void ExportSelectionReusesAdmissionAcrossUnexportedChanges()
    {
        var governor = new MemoryGovernor(4096);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var state = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        var f = Add("f", FunctionAttributes.Exported);
        Assert.Same(f, state.GetExportedFunctions(memory)[0]);
        var admitted = governor.ReservedBytes;
        Assert.True(admitted > 0);
        using (governor.Reserve(governor.LimitBytes - admitted))
        {
            Add("unexported", FunctionAttributes.None);
            Assert.Same(f, state.GetExportedFunctions(memory)[0]);
        }
        Assert.True(state.TryUnsetFunction(f.Definition.Name.Value));
        Assert.True(state.GetExportedFunctions(memory).IsEmpty);
        Assert.Equal(0, governor.ReservedBytes);
        state.ReleaseFunctions();

        FunctionBinding Add(string name, FunctionAttributes attributes)
        {
            var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(name + "(){ :; }").Commands));
            var binding = new FunctionBinding(definition, attributes, sourceMemory: null, memory);
            state.SetFunctionBinding(binding);
            return binding;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingFunctionExportsAdmitsReplacementsBeforeChangingTheNamespace(bool warm)
    {
        var governor = new MemoryGovernor(4096);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var state = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }").Commands));
        var binding = new FunctionBinding(definition, FunctionAttributes.Readonly | FunctionAttributes.Exported,
            sourceMemory: null, memory);
        state.SetFunctionBinding(binding);
        if (warm)
            Assert.Same(binding, state.GetExportedFunctions(memory)[0]);

        var before = governor.ReservedBytes;
        using (governor.Reserve(governor.LimitBytes - before))
        {
            Assert.Throws<MemoryBudgetExceededException>(() => state.BeginClearFunctionExports(memory, CancellationToken.None));
            Assert.Same(binding, state.Functions[definition.Name.Value]);
        }
        Assert.Equal(before, governor.ReservedBytes);

        using (var clear = Assert.IsType<EvaluationState.FunctionExportClear>(
                   state.BeginClearFunctionExports(memory, CancellationToken.None)))
        {
            Assert.Same(definition, state.Functions[definition.Name.Value].Definition);
            Assert.Equal(FunctionAttributes.Readonly, state.Functions[definition.Name.Value].Attributes);
            Assert.True(state.GetExportedFunctions(memory).IsEmpty);
            Assert.True(governor.ReservedBytes > before);
            clear.Commit();
        }
        Assert.Equal(0, governor.ReservedBytes);
        state.ReleaseFunctions();
    }

    [Theory]
    [InlineData("exec -c command -v item", false, "")]
    [InlineData("exec -c type -t item", false, "type")]
    [InlineData("exec -c eval 'printf unexpected'", true, "eval")]
    public async Task RefusedExecPreparationPreservesTheCallersFunctionExports(string exec, bool copiedScript, string name)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        var budget = new RefusingReservationBudget(owner);
        var state = new Eval(Parser.Parse("f(){ :; }; export -f f; inspect\n" + exec + "\ninspect \"$?\""));
        state.Memory.Budget = budget;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        var cached = Assert.Single(first.Command.Functions).Source;
        var queryAdmission = Eval.EstimateCommandQueryResolutionBytes(nameCount: 1);
        budget.RefuseNext = copiedScript ? (child, bytes) => child && bytes > 0
            : (child, bytes) => !child && bytes == queryAdmission;
        first.SetResult(new CommandExecutionResult.Completed(0));

        var error = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        Assert.Equal(FileDescriptor.StdErr, error.FileDescriptor);
        Assert.Equal(name + ": shared memory budget exhausted\n", Utf8Text.Decode(error.Content));
        Assert.Equal(1, budget.Refusals);
        error.SetResult(DescriptorWriteResult.Open);

        var later = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        Assert.Equal("inspect", later.Command.CommandName);
        Assert.Equal("1", Assert.Single(later.Command.Arguments).ToUtf8String());
        Assert.Equal(cached, Assert.Single(later.Command.Functions).Source);
        later.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await state.GetNextStep(deadline.Token));
        state.ReleaseFunctions();
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RefusedExecEvalPreservesEarlierFunctionEdits()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        var budget = new RefusingReservationBudget(owner);
        var state = new Eval(Parser.Parse("f(){ :; }; g(){ :; }; export -f f g;\n"
            + "exec -c eval 'unset -f g\nprintf ready\n:'\ninspect \"$?\""));
        state.Memory.Budget = budget;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var marker = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        Assert.Equal("ready", Utf8Text.Decode(marker.Content));
        budget.RefuseNext = (child, bytes) => child && bytes > 0;
        marker.SetResult(DescriptorWriteResult.Open);

        var error = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        Assert.Equal("eval: shared memory budget exhausted\n", Utf8Text.Decode(error.Content));
        Assert.Equal(1, budget.Refusals);
        error.SetResult(DescriptorWriteResult.Open);
        var later = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        Assert.Equal("1", Assert.Single(later.Command.Arguments).ToUtf8String());
        var definition = Assert.IsType<FunctionDefinition>(
            Assert.Single(Parser.Parse(Assert.Single(later.Command.Functions).Source).Commands));
        Assert.Equal(ShellValue.FromText("f"), definition.Name.Value);
        later.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await state.GetNextStep(deadline.Token));
        state.ReleaseFunctions();
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RefusedExportPreparationPreservesEarlierSerializationAndAllowsRetry()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        var budget = new RefusingReservationBudget(owner);
        var state = new Eval(Parser.Parse("f(){ :; }; g(){ :; }; h(){ :; }; export -f f; inspect\n"
            + "export -f g h; printf ready\ninspect\ninspect \"$?\""));
        state.Memory.Budget = budget;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initial = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        var cached = Assert.Single(initial.Command.Functions).Source;
        initial.SetResult(new CommandExecutionResult.Completed(0));
        var marker = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        Assert.Equal("ready", Utf8Text.Decode(marker.Content));
        // The first binding is cached. Refuse the next binding's cold cache metadata.
        budget.RefuseNext = (child, bytes) => child && bytes == 192;
        marker.SetResult(DescriptorWriteResult.Open);

        var error = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
        Assert.Equal(FileDescriptor.StdErr, error.FileDescriptor);
        Assert.Equal("inspect: shared memory budget exhausted\n", Utf8Text.Decode(error.Content));
        Assert.Equal(1, budget.Refusals);
        error.SetResult(DescriptorWriteResult.Open);

        var retry = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        Assert.Equal("inspect", retry.Command.CommandName);
        Assert.Equal("1", Assert.Single(retry.Command.Arguments).ToUtf8String());
        Assert.Equal(new[] { "f", "g", "h" }, retry.Command.Functions.Select(export => Assert.IsType<FunctionDefinition>(
            Assert.Single(Parser.Parse(export.Source).Commands)).Name.Value.ToUtf8String()));
        Assert.Equal(cached, retry.Command.Functions[0].Source);
        retry.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await state.GetNextStep(deadline.Token));
        state.ReleaseFunctions();
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class RefusingReservationBudget(IMemoryBudget budget) : IMemoryBudget
    {
        public Func<bool, long, bool>? RefuseNext { get; set; }
        public int Refusals { get; private set; }

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
            TryReserve(budget, false, bytes, out reservation);

        public IMemoryScope CreateScope() => new Scope(budget.CreateScope(), this);

        private bool TryReserve(IMemoryBudget inner, bool child, long bytes,
            [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (RefuseNext?.Invoke(child, bytes) == true)
            {
                RefuseNext = null;
                Refusals++;
                reservation = null;
                return false;
            }
            return inner.TryReserve(bytes, out reservation);
        }

        private sealed class Scope(IMemoryScope inner, RefusingReservationBudget owner) : IMemoryScope
        {
            public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) =>
                owner.TryReserve(inner, true, bytes, out reservation);
            public IMemoryScope CreateScope() => new Scope(inner.CreateScope(), owner);
            public void Dispose() => inner.Dispose();
        }
    }

    [Theory]
    [InlineData(0, 17000)]
    [InlineData(55, 20000)]
    public async Task ExportLayoutDoesNotExhaustTheChildParsersTokenOrByteLimit(int depth, int commands)
    {
        // Pretty output adds one token per command and indentation per nested group.
        // Import the body without executing this many commands or changing parser limits.
        var definition = "f(){ " + string.Concat(Enumerable.Repeat("{ ", depth))
            + string.Concat(Enumerable.Repeat(":;", commands))
            + string.Concat(Enumerable.Repeat("};", depth)) + "}";
        var result = await EndToEndHarness.RunScriptAsync(
            definition + "; export -f f; sh -c 'declare -F f'", EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("f\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task ExportQuotingKeepsOneLiteralFromBecomingThousandsOfWordParts()
    {
        var definition = "f(){ : $'" + string.Concat(Enumerable.Repeat("\\'", 22000)) + "'; }";
        var result = await EndToEndHarness.RunScriptAsync(
            definition + "; export -f f; sh -c 'declare -F f'", EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("f\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task LongElifChainsRemainFlatAcrossExportAndImport()
    {
        var definition = "f(){ if false; then :; "
            + string.Concat(Enumerable.Repeat("elif false; then :; ", 64))
            + "else printf ok; fi; }";
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(definition).Commands));
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        var reparsed = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(output.ToMemory()).Commands));
        var conditional = Assert.IsType<IfCommand>(Assert.IsType<GroupCommand>(reparsed.Body).Body);
        Assert.Equal(64, conditional.ElifClauses.Count);

        var result = await EndToEndHarness.RunScriptAsync(definition + "; f; export -f f; sh -c f",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("okok", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public void CachedExportsRespectEachCallersBudgetAndSurviveAttributeCopies()
    {
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }").Commands));
        const long MemoryBudgetBytes = 4096;
        var memory = new MemoryGovernor(MemoryBudgetBytes);
        var exportMemory = new EvaluationMemory(memory);
        var binding = new FunctionBinding(definition, FunctionAttributes.Exported, sourceMemory: null, exportMemory);
        var readonlyCopy = binding with { Attributes = FunctionAttributes.Exported | FunctionAttributes.Readonly };
        using var lifetime = new FunctionBinding.Borrow(binding);
        Assert.False(binding.TryGetExport(1, out _));
        Assert.Equal(0, memory.ReservedBytes);
        Assert.True(binding.TryGetExport(4096, out var first));
        Assert.InRange(memory.ReservedBytes, first.Source.Length, memory.LimitBytes);
        using var pressure = memory.Reserve(memory.LimitBytes - memory.ReservedBytes);
        Assert.False(binding.TryGetExport(first.Source.Length - 1, out _));
        Assert.True(readonlyCopy.TryGetExport(first.Source.Length, out var second));
        Assert.True(first.Source.Equals(second.Source)); // Memory identity, not merely equal contents.
        var other = new FunctionBinding(definition, FunctionAttributes.Exported, sourceMemory: null, exportMemory);
        using var otherLifetime = new FunctionBinding.Borrow(other);
        Assert.Throws<MemoryBudgetExceededException>(() => other.TryGetExport(4096, out _));
    }

    [Fact]
    public void UnpublishedExportRefundsItsReservationWhenAnotherReaderWins()
    {
        const int FunctionBodyBytes = 64 * 1024;
        const int ExportLimitBytes = 1024 * 1024;
        const long MemoryBudgetBytes = 4 * ExportLimitBytes;
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(
            Parser.Parse("f(){ : '" + new string('x', FunctionBodyBytes) + "'; }").Commands));
        var memory = new MemoryGovernor(MemoryBudgetBytes);
        var exportMemory = new EvaluationMemory(memory);
        var binding = new FunctionBinding(definition, FunctionAttributes.Exported, sourceMemory: null, exportMemory);
        using var lifetime = new FunctionBinding.Borrow(binding);
        ExportedFunction published = default;
        long publishedBytes = 0;
        var competingReader = new ReservationCallbackBudget(memory, () =>
        {
            // Publish after the first reader misses the cache, before it finishes rendering.
            Assert.True(binding.TryGetExport(ExportLimitBytes, out published));
            publishedBytes = memory.ReservedBytes;
        });

        exportMemory.Budget = competingReader;
        Assert.True(binding.TryGetExport(ExportLimitBytes, out var result));

        Assert.True(result.Source.Equals(published.Source));
        Assert.InRange(publishedBytes, published.Source.Length, memory.LimitBytes);
        Assert.Equal(publishedBytes, memory.ReservedBytes);
    }

    [Fact]
    public void RefusedCompetingExportRefundsItsCandidateWithoutFallingBackToTheWinner()
    {
        const int BodyBytes = 64 * 1024;
        const int ExportLimit = 1024 * 1024;
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(
            Parser.Parse("f(){ : '" + new string('x', BodyBytes) + "'; }").Commands));
        var governor = new MemoryGovernor(4 * ExportLimit);
        var memory = new EvaluationMemory(governor);
        var binding = new FunctionBinding(definition, FunctionAttributes.Exported, sourceMemory: null, memory);
        using var pressure = governor.Reserve(0);
        using (new FunctionBinding.Borrow(binding))
        {
            ExportedFunction winner = default;
            long winnerBytes = 0;
            memory.Budget = new ReservationCallbackBudget(governor, () =>
            {
                Assert.True(binding.TryGetExport(ExportLimit, out winner));
                winnerBytes = governor.ReservedBytes;
                // Admit a partial losing candidate, then refuse its material buffer growth.
                pressure.Resize(governor.LimitBytes - governor.ReservedBytes - 1024);
            });

            Assert.Throws<MemoryBudgetExceededException>(() => binding.TryGetExport(ExportLimit, out _));
            Assert.Equal(winnerBytes + pressure.Bytes, governor.ReservedBytes);
            pressure.Resize(0);
            Assert.True(binding.TryGetExport(ExportLimit, out var retained));
            Assert.True(winner.Source.Equals(retained.Source));
            Assert.Equal(winnerBytes, governor.ReservedBytes);
        }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task HostCommandsReuseExportsUntilTheDefinitionChanges()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(
            "f(){ :; }; export -f f; inspect; export -fn f; inspect; "
            + "export -f f; inspect; f(){ printf changed; }; inspect"));
        var host = new MockFileSystem();

        await new Engine().RunAsync(evaluation, host);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(4, host.CommandInvocations.Count);
        var original = Assert.Single(host.CommandInvocations[0].Functions).Source;
        Assert.Empty(host.CommandInvocations[1].Functions);
        Assert.True(original.Equals(Assert.Single(host.CommandInvocations[2].Functions).Source));
        var changed = Assert.Single(host.CommandInvocations[3].Functions).Source;
        Assert.False(original.Equals(changed));
        Assert.Contains("printf changed", Utf8Text.Decode(changed));
    }

    [Fact]
    public async Task ImportDoesNotRunBodyOrItsRedirections()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "f(){ printf wrong; } > /side-effect; export -f f; sh -c 'printf ready'",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/side-effect"));
    }

    [Fact]
    public async Task InvocationSeparatesFunctionsFromScalarsAndChildRetainsTheCompletedList()
    {
        var state = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(
            "f(){ :; }; f=value; export f; export -f f; inspect"));
        var host = new MockFileSystem();

        await new Engine().RunAsync(state, host);

        var invocation = Assert.Single(host.CommandInvocations);
        var exported = Assert.Single(invocation.Functions);
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(exported.Source).Commands));
        Assert.Equal(ShellValue.FromText("f"), definition.Name.Value);
        Assert.Contains(invocation.Environment, variable => variable.Name == "f" && variable.Value == ShellValue.FromText("value"));
        Assert.DoesNotContain(invocation.Environment, variable => variable.Name.ToValue().Bytes.Span.StartsWith("BASH_FUNC_"u8));
        var child = invocation.CreateChild("sh", [], [],
            invocation.StdIn, invocation.StdOut, invocation.StdErr);
        Assert.Same(invocation.Functions, child.Functions);
        Assert.Empty(child.Environment);
        Assert.Equal(invocation.CurrentDirectory, child.CurrentDirectory);
    }

    private sealed class ReservationCallbackBudget(IMemoryBudget budget, Action beforeFirstReservation) : IMemoryBudget
    {
        private bool _called;

        public IMemoryScope CreateScope() => new CallbackScope(budget.CreateScope(), this);

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            BeforeReservation();
            return budget.TryReserve(bytes, out reservation);
        }

        private void BeforeReservation()
        {
            if (_called) return;
            _called = true;
            beforeFirstReservation();
        }

        private sealed class CallbackScope(IMemoryScope inner, ReservationCallbackBudget owner) : IMemoryScope
        {
            public IMemoryScope CreateScope() => new CallbackScope(inner.CreateScope(), owner);
            public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
            {
                owner.BeforeReservation();
                return inner.TryReserve(bytes, out reservation);
            }
            public void Dispose() => inner.Dispose();
        }
    }
}

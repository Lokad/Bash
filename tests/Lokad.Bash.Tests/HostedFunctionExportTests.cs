using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using System.Buffers;
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
    public async Task ExportedConditionalKeepsItsDecodedLiteralAndCanBeReadAgain()
    {
        var state = new Eval(Parser.Parse("f(){ [[ $'a\\'b' == $'a\\x27b' ]] && printf hit; }; export -f f; inspect"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
        var source = Utf8Text.Decode(Assert.Single(invocation.Command.Functions).Source);
        Assert.Contains("[[ 'a'\\''b' == 'a'\\''b' ]]", source, StringComparison.Ordinal);
        invocation.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await state.GetNextStep(deadline.Token));
        state.ReleaseFunctions();

        await using var host = new TestHost();
        var result = await host.RunAsync(source + "\nf", deadline.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hit", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

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
    [InlineData(64)]
    public async Task WarmHostedExportOrdersEachFunctionWithOneNameRead(int padding)
    {
        using var first = new ObservedName("probe0"u8.ToArray());
        using var second = new ObservedName("probe145"u8.ToArray());
        var body = Assert.Single(Parser.Parse(":").Commands);
        var commands = Parser.Parse("export -f probe0 probe145\ninspect\ninspect").Commands;
        var unexported = Enumerable.Range(0, padding).Select(index =>
            new FunctionDefinition(new FunctionName(ShellValue.FromText($"padding{index}"), FunctionNameSyntax.Plain), body));
        var state = new Eval(new ParsedScript([
            new FunctionDefinition(new FunctionName(ShellValue.FromBytes(first.Memory), FunctionNameSyntax.Plain), body),
            .. unexported,
            new FunctionDefinition(new FunctionName(ShellValue.FromBytes(second.Memory), FunctionNameSyntax.Plain), body),
            .. commands]));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var initial = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
            var firstReads = first.Reads;
            var secondReads = second.Reads;
            initial.SetResult(new CommandExecutionResult.Completed(0));

            var warm = Assert.IsType<RunCommand>(await state.GetNextStep(deadline.Token));
            try
            {
                Assert.Equal(firstReads + 1, first.Reads);
                Assert.Equal(secondReads + 1, second.Reads);
                var inheritance = Assert.IsType<CommandContext<BashCommandInheritance>>(warm.Command.Context).Inheritance;
                var imports = Assert.IsType<BashCommandInheritance.FunctionImports>(inheritance.Imports);
                Assert.Same(warm.Command.Functions, imports.Functions);
                Assert.Equal(new[] { 1, 0 }, imports.Entries.Select(entry => entry.Index));
            }
            finally
            {
                warm.SetResult(new CommandExecutionResult.Completed(0));
                Assert.Null(await state.GetNextStep(deadline.Token));
            }
            Assert.Equal(0, state.LastExitCode);
        }
        finally { state.ReleaseFunctions(); }
    }

    private sealed class ObservedName(byte[] bytes) : MemoryManager<byte>
    {
        internal int Reads { get; private set; }

        public override Span<byte> GetSpan()
        {
            Reads++;
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex) => throw new NotSupportedException();
        public override void Unpin() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }

    [Theory]
    [InlineData("", "", "bash", false)]
    [InlineData("((1)); ", "", "bash", false)]
    [InlineData("", " reader", "reader", false)]
    [InlineData("", "", "bash", true)]
    public async Task ChildSkipsItsTruncatedExportAndRunsWithTheRemainingDefinitions(
        string beginning, string argumentZero, string reader, bool conditional)
    {
        var capture = "\"$(" + beginning + "printf seen >&2; printf %s \"${v:-$'a\\0hidden'}\")\"";
        var body = conditional ? "[[ " + capture + " == a ]] && printf live" : "printf %s " + capture;
        var script = "unset v\nf(){ " + body + "; }; "
            + "g(){ printf '[good]'; }; export -f f g\nbash -c 'g; f'" + argumentZero + "; printf '[child:%s]' \"$?\"";
        byte[] conditionDiagnostic = conditional
            ? [.. "bash: f: line 2: unexpected token `"u8, 0xff, .. "' in conditional command\n"u8]
            : [];
        byte[] stderr = [.. "bash: f: line 0: unexpected EOF while looking for matching `\"'\n"u8,
            .. conditionDiagnostic, .. "bash: error importing function definition for `f'\n"u8,
            .. Utf8Text.Encode($"{reader}: line 1: f: command not found\n").Span];
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("[good][child:127]", host.StandardOutput.TextContent);
            Assert.Equal(stderr, host.StandardError.Content.ToArray());
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Fact]
    public async Task ChildGroupsRejectedExportsInBucketOrderAlongsideValidExports()
    {
        const string script = """
            unset v
            town界(){ printf %s "$(printf %s "${v:-$'a\0hidden'}")"; }
            good(){ printf '[good]'; }
            e(){ [[ "$(printf %s "${v:-$'a\0hidden'}")" == a ]] && printf unreached; }
            export -f town界 good e
            bash -c good
            """;
        byte[] errors = [.. "bash: e: line 0: unexpected EOF while looking for matching `\"'\n"u8,
            .. "bash: e: line 2: unexpected token `"u8, 0xff, .. "' in conditional command\n"u8,
            .. "bash: error importing function definition for `e'\n"u8,
            .. "bash: town界: line 0: unexpected EOF while looking for matching `\"'\n"u8,
            .. "bash: error importing function definition for `town界'\n"u8];

        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[good]", host.StandardOutput.TextContent);
        Assert.Equal(errors, host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false, "probe145", "probe0")]
    [InlineData(true, "probe0", "probe145")]
    public async Task CollidingImportDiagnosticsRetainInsertionHistoryAcrossRedefinitionAndFork(
        bool removeAndFork, string first, string last)
    {
        static string Damaged(string name) => name + "(){ printf %s \"$(printf %s \"${v:-$'a\\0hidden'}\")\"; }\n";

        var initial = "unset v\n" + Damaged("probe0") + Damaged("probe145");
        var fork = removeAndFork ? "unset -f probe0\n(\n" : "";
        var script = initial + fork + Damaged("probe0") + "export -f probe0 probe145\nbash -c 'printf alive'\n"
            + (removeAndFork ? ")" : "");
        var expected = string.Concat(new[] { first, last }.Select(name =>
            $"bash: {name}: line 0: unexpected EOF while looking for matching `\"'\nbash: error importing function definition for `{name}'\n"));

        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("alive", host.StandardOutput.TextContent);
        Assert.Equal(expected, host.StandardError.TextContent);
    }

    [Fact]
    public async Task ImportDiagnosticBucketGrowthSurvivesAnEmptySubshellNamespace()
    {
        const string body = "(){ printf %s \"$(printf %s \"${v:-$'a\\0hidden'}\")\"; }\n";
        var names = Enumerable.Range(0, 1025).Select(i => $"padding{i}").ToArray();
        var pad = string.Concat(names.Select(name => name + "(){ :; }\n"));
        var remove = "unset -f " + string.Join(' ', names) + "\n";
        var script = pad + remove + "(\nprobe0" + body + "probe30" + body
            + "export -f probe0 probe30\nbash -c 'printf alive'\n)";
        var expected = string.Concat(new[] { "probe30", "probe0" }.Select(name =>
            $"bash: {name}: line 0: unexpected EOF while looking for matching `\"'\nbash: error importing function definition for `{name}'\n"));
        var governor = new MemoryGovernor(8 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("alive", host.StandardOutput.TextContent);
        Assert.Equal(expected, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyImportsKeepBucketHistoryForLaterRedefinitions(bool reuseSlot)
    {
        const string body = "(){ printf %s \"$(printf %s \"${v:-$'a\\0hidden'}\")\"; }\n";
        var fixture = "probe0" + body + "probe145" + body + "bash -c 'printf alive'\n";
        var setup = reuseSlot ? "vacant(){ :; }\nprobe0(){ :; }\nunset -f vacant\n" : "probe0(){ :; }\n";
        var script = "unset v\n" + setup + "probe145(){ :; }\nexport -f probe0 probe145\nbash ./fixture";
        var expected = string.Concat(new[] { "probe0", "probe145" }.Select(name =>
            $"bash: {name}: line 0: unexpected EOF while looking for matching `\"'\nbash: error importing function definition for `{name}'\n"));
        await using var host = EndToEndHarness.BuildTree([("/fixture", fixture)]);

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("alive", host.StandardOutput.TextContent);
        Assert.Equal(expected, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ValidChildImportsPublishInTheOriginalPayloadOrder()
    {
        var parent = new Eval(Parser.Parse("probe0(){ :; }; probe145(){ :; }; export -f probe0 probe145; inspect"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var passed = Assert.IsType<RunCommand>(await parent.GetNextStep(deadline.Token));
        var child = ShellEvaluationStartup.CreateInherited(passed.Command, Parser.Parse("inspect"), ShellValue.FromText("bash"), []);
        try
        {
            var returned = Assert.IsType<RunCommand>(await child.GetNextStep(deadline.Token));
            try
            {
                Assert.Equal(new[] { "probe0", "probe145" }, returned.Command.Functions.Select(export =>
                    Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(export.Source).Commands)).Name.Value.ToUtf8String()));
            }
            finally
            {
                returned.SetResult(new CommandExecutionResult.Completed(0));
                Assert.Null(await child.GetNextStep(deadline.Token));
            }
        }
        finally
        {
            child.ReleaseFunctions();
            passed.SetResult(new CommandExecutionResult.Completed(0));
            Assert.Null(await parent.GetNextStep(deadline.Token));
            parent.ReleaseFunctions();
        }
    }

    [Fact]
    public async Task ImportedFunctionUsesItsCompactEnvironmentLines()
    {
        const string script = """
            f() {
                printf '[%s]' "$LINENO"
                missing_second
                eval 'missing_eval'
                g() {
                    printf '[%s]' "$LINENO"
                    missing_nested
                }
                g
            }
            export -f f
            bash -c f label
            """;
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(127, result.ExitCode);
        Assert.Equal("[0][5]", host.StandardOutput.TextContent);
        Assert.Equal("environment: line 1: missing_second: command not found\n"
            + "environment: line 2: missing_eval: command not found\n"
            + "environment: line 6: missing_nested: command not found\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ImportedLineZeroDistinguishesCommandLookupAndBuiltinUsage()
    {
        const string script = """
            a(){ missing_a; }
            b(){ unset -z; }
            export -f a b
            bash -c 'a; b' label
            """;
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("environment: missing_a: command not found\n"
            + "environment: line 0: unset: -z: invalid option\nunset: usage: unset [-f] [-v] [-n] [name ...]\n",
            host.StandardError.TextContent);
    }

    [Fact]
    public async Task ReplacementExportListKeepsStrictStartupValidation()
    {
        var outer = new Eval(Parser.Parse("f(){ printf %s \"$(printf %s \"${v:-$'a\\0hidden'}\")\"; }; export -f f; inspect"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operation = Assert.IsType<RunCommand>(await outer.GetNextStep(deadline.Token));
        var original = operation.Command;
        try
        {
            var offered = new CommandInvocation("bash", [], original.CurrentDirectory, original.Environment,
                original.StdIn, original.StdOut, original.StdErr, original.Context)
            {
                Functions = original.Functions.ToArray()
            };
            var child = ShellEvaluationStartup.CreateInherited(offered, Parser.Parse("printf unreached"), ShellValue.FromText("bash"), []);
            await using var host = new TestHost();
            var result = await new Engine().RunAsync(child, host, deadline.Token);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(host.StandardOutput.Content.ToArray());
            Assert.Equal("syntax error: Unterminated command substitution.\n", host.StandardError.TextContent);
            Assert.Equal(0, child.RetainedFunctionMemoryBytes);
            Assert.Equal(0, host.OpenFileCount);
        }
        finally
        {
            operation.SetResult(new CommandExecutionResult.Completed(0));
            Assert.Null(await outer.GetNextStep(deadline.Token));
            outer.ReleaseFunctions();
        }
    }

    [Theory]
    [InlineData("''", "f", new byte[] { })]
    [InlineData("$'\\377'", "f", new byte[] { 0xff })]
    [InlineData("$'\\377'", "eval f", new byte[] { 0xff })]
    public async Task RecoveredChildUsesTheReaderNameBytes(string argumentZero, string call, byte[] name)
    {
        var script = "unset v\nf(){ printf %s \"$(printf %s \"${v:-$'a\\0hidden'}\")\"; }; export -f f\n"
            + "bash -c 'printf \"[zero:%s]\" \"$0\"; " + call + "' " + argumentZero + "; printf '[child:%s]' \"$?\"";
        byte[] stdout = [.. "[zero:"u8, .. name, .. "][child:127]"u8];
        byte[] stderr = [.. "bash: f: line 0: unexpected EOF while looking for matching `\"'\n"u8,
            .. "bash: error importing function definition for `f'\n"u8, .. name, .. ": line 1: f: command not found\n"u8];
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(stdout, host.StandardOutput.Content.ToArray());
            Assert.Equal(stderr, host.StandardError.Content.ToArray());
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
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
        if (count > 1)
            admission += 32L + MemoryCapacity.ArrayBackingBytes<BashCommandInheritance.ImportEntry>(count);
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
    public void ExportReplacementReusesTheSelectedSlot(bool redefine)
    {
        var governor = new MemoryGovernor(4096);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var state = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        var first = NewBinding("first(){ :; }");
        var removed = NewBinding("removed(){ :; }") with { Attributes = FunctionAttributes.None };
        var original = NewBinding("target(){ :; }");
        var last = NewBinding("last(){ :; }");
        state.SetFunctionBinding(first);
        state.SetFunctionBinding(removed);
        state.SetFunctionBinding(original);
        state.SetFunctionBinding(last);
        Assert.True(state.TryUnsetFunction(removed.Definition.Name.Value));
        Assert.Equal(new[] { first, original, last }, state.GetExportedFunctions(memory).ToArray());

        var replacement = redefine ? NewBinding("target(){ printf new; }")
            : original with { Attributes = FunctionAttributes.Exported | FunctionAttributes.Readonly };
        var admitted = governor.ReservedBytes;
        using (governor.Reserve(governor.LimitBytes - admitted))
        {
            state.SetFunctionBinding(replacement);
            Assert.Equal(governor.LimitBytes, governor.ReservedBytes);
            Assert.Same(replacement, state.GetExportedFunctions(memory)[1]);
        }

        state.ReleaseExportedFunctionSelection();
        Assert.Equal(new[] { first, replacement, last }, state.GetExportedFunctions(memory).ToArray());
        state.ReleaseFunctions();
        Assert.Equal(0, governor.ReservedBytes);

        FunctionBinding NewBinding(string text) => new(
            Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(text).Commands)),
            FunctionAttributes.Exported, sourceMemory: null, memory);
    }

    [Fact]
    public void ExportMembershipKeepsSlotOrderAcrossBlockBoundariesAndReuse()
    {
        var governor = new MemoryGovernor(4096);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var state = new EvaluationState(Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        var bindings = Enumerable.Range(0, 193).Select(index => Add("f" + index, index is 0 or 63 or 64 or 192)).ToArray();
        Assert.Equal(new[] { bindings[0], bindings[63], bindings[64], bindings[192] }, state.GetExportedFunctions(memory).ToArray());

        Assert.True(state.TryUnsetFunction(bindings[64].Definition.Name.Value));
        Assert.True(state.TryUnsetFunction(bindings[63].Definition.Name.Value));
        Assert.Equal(new[] { bindings[0], bindings[192] }, state.GetExportedFunctions(memory).ToArray());
        var earlier = Add("earlier", exported: true);
        var later = Add("later", exported: true);
        var promoted = bindings[130] with { Attributes = FunctionAttributes.Exported };
        state.SetFunctionBinding(promoted);
        Assert.Equal(new[] { bindings[0], earlier, later, promoted, bindings[192] }, state.GetExportedFunctions(memory).ToArray());

        using (state.BeginClearFunctionExports(memory, CancellationToken.None))
            Assert.True(state.GetExportedFunctions(memory).IsEmpty);
        Assert.Equal(new[] { bindings[0], earlier, later, promoted, bindings[192] }, state.GetExportedFunctions(memory).ToArray());
        state.ReleaseFunctions();
        Assert.Equal(0, governor.ReservedBytes);

        FunctionBinding Add(string name, bool exported)
        {
            var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(name + "(){ :; }").Commands));
            var binding = new FunctionBinding(definition, exported ? FunctionAttributes.Exported : FunctionAttributes.None,
                sourceMemory: null, memory);
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
            Assert.True(state.TryGetFunction(definition.Name.Value, out var current));
            Assert.Same(binding, current);
        }
        Assert.Equal(before, governor.ReservedBytes);

        using (var clear = Assert.IsType<EvaluationState.FunctionExportClear>(
                   state.BeginClearFunctionExports(memory, CancellationToken.None)))
        {
            Assert.True(state.TryGetFunction(definition.Name.Value, out var current));
            Assert.Same(definition, current.Definition);
            Assert.Equal(FunctionAttributes.Readonly, current.Attributes);
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

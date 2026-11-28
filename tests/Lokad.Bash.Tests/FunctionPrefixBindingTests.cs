using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed partial class FunctionPrefixBindingTests
{
    [Fact]
    public void BindingOwnerLookupPreservesPrefixSelectionAndTheCallerBoundary()
    {
        var variables = Variables.CreateEmpty();
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        var memory = new EvaluationMemory(governor);
        var name = ShellValue.FromText("f");
        try
        {
            Assert.True(variables.TrySetScalar("target", ShellValue.FromText("global")));
            var locals = new Variables.LocalBindings(variables, parent: null, prefixes: null);
            using var function = new Eval.FunctionContext(name, locals, enclosing: null);
            Declare("target", "function", isNameref: false);
            Declare("reference", "target", isNameref: true);

            using var outerPrefix = InstallPrefix("other");
            using var outerScript = new Eval.FunctionContext(name, locals.WithTemporaryPrefixes(outerPrefix), function);
            var outerPrefixes = new Eval.GlobalPrefixBindings(outerPrefix, outerScript.Depth, null);
            using var scriptPrefix = InstallPrefix("target");
            using var script = new Eval.FunctionContext(name, outerScript.Locals.WithTemporaryPrefixes(scriptPrefix), outerScript);
            var scriptPrefixes = new Eval.GlobalPrefixBindings(scriptPrefix, script.Depth, outerPrefixes);

            var calleeLocals = new Variables.LocalBindings(variables, parent: null, prefixes: null);
            using var callee = new Eval.FunctionContext(ShellValue.FromText("callee"), calleeLocals, script);
            var context = new Eval.EvaluationContext(0, callee, Eval.ReturnTargetKind.Function)
                { GlobalPrefixes = scriptPrefixes };
            var saved = Assert.IsType<Variables>(locals.SavedBindings);
            Assert.Same(saved, context.GetGlobalBindingOwner("target", variables));

            Assert.True(variables.TryUnset("target"));
            List<Variables.CommandBindings>? selected = null;
            Assert.Same(scriptPrefix.SavedVariables,
                context.GetVisibleBindingOwner("target", variables, memory, ref selected));
            var exposed = Assert.IsType<List<Variables.CommandBindings>>(selected);
            Assert.Same(scriptPrefix, Assert.Single(exposed));
            exposed.Clear();

            script.Locals.ShadowNamerefTarget("target", variables, memory);
            script.Locals.RecordUnset("target");
            Assert.Same(variables, context.GetVisibleBindingOwner("target", variables, memory, ref selected));
            Assert.Empty(exposed);

            Assert.True(variables.TrySetNameref("target", ShellValue.Empty));
            Assert.True(context.TryResolveLocalNamerefAssignment("reference", variables, memory, out var fallback));
            var write = Assert.IsType<Eval.LocalNamerefAssignment.Write>(fallback);
            Assert.Same(saved, write.Binding.Owner);

            void Declare(BindingName binding, string value, bool isNameref) =>
                Assert.Equal(LocalDeclarationResult.Declared, locals.TryDeclare(variables, variables,
                    new VariableOperand(VariableOperandKind.Assignment, VariableReference.ForName(binding), ShellValue.FromText(value)),
                    prefixes: null, ArrayAssignmentKind.Auto, isNameref, inherit: false, caseChange: default,
                    elementAttributes: default, memory, enclosingFunction: null));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);

        Variables.CommandBindings InstallPrefix(BindingName binding)
        {
            var prepared = Variables.CreateEmpty();
            Assert.True(prepared.TrySetScalar(binding, ShellValue.FromText("temporary")));
            return new Variables.CommandBindings(variables, prepared, new HashSet<BindingName> { binding },
                suppressTemporaryLookup: false, memory, CancellationToken.None);
        }
    }

    [Fact]
    public void EnclosingLocalLookupDistinguishesDeclarationsFromTemporaryPrefixes()
    {
        var variables = Variables.CreateEmpty();
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        var memory = new EvaluationMemory(governor);
        var name = ShellValue.FromText("f");
        var value = ShellValue.FromText("value");
        try
        {
            var locals = new Variables.LocalBindings(variables, parent: null, prefixes: null);
            using var function = new Eval.FunctionContext(name, locals, enclosing: null);
            Declare(locals, "inherited");
            using var scriptBindings = InstallPrefixes("inherited", "prefix");
            using var script = new Eval.FunctionContext(name, locals.WithTemporaryPrefixes(scriptBindings), function);
            using var nestedBindings = InstallPrefixes("nested");
            using var nested = new Eval.FunctionContext(name, script.Locals.WithTemporaryPrefixes(nestedBindings), script);

            Assert.False(nested.Locals.Declares("inherited"));
            Assert.True(nested.IsLocal("inherited"));
            Assert.False(nested.IsLocal("prefix"));
            Assert.False(nested.IsLocal("nested"));
            Assert.False(nested.IsLocal("absent"));

            Declare(script.Locals, "prefix");
            Assert.True(script.Locals.OwnsDeclaration("prefix"));
            Assert.False(nested.Locals.OwnsDeclaration("prefix"));
            Assert.True(nested.Locals.Declares("prefix"));
            Assert.True(nested.IsLocal("prefix"));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);

        void Declare(Variables.LocalBindings locals, BindingName bindingName) =>
            Assert.Equal(LocalDeclarationResult.Declared, locals.TryDeclare(variables, variables,
                new VariableOperand(VariableOperandKind.Assignment, VariableReference.ForName(bindingName), value),
                prefixes: null, ArrayAssignmentKind.Auto, isNameref: false, inherit: false,
                caseChange: default, elementAttributes: default, memory, enclosingFunction: null));

        Variables.CommandBindings InstallPrefixes(params BindingName[] names)
        {
            var prepared = Variables.CreateEmpty();
            foreach (var prefix in names)
                Assert.True(prepared.TrySetScalar(prefix, value));
            return new Variables.CommandBindings(variables, prepared, names.ToHashSet(),
                suppressTemporaryLookup: false, memory, CancellationToken.None);
        }
    }

    [Fact]
    public void SavedLocalTotalFollowsHiddenWritesForksAndRestoration()
    {
        var state = new Eval(Parser.Parse(":"));
        var variables = Variables.CreateEmpty();
        var outerLocals = SaveLocal("x");
        using var outer = new Eval.FunctionContext(ShellValue.FromText("outer"), outerLocals, enclosing: null);
        var outerSaved = Assert.IsType<Variables>(outerLocals.SavedBindings);
        var innerLocals = SaveLocal("y");
        using var inner = new Eval.FunctionContext(ShellValue.FromText("inner"), innerLocals, outer);
        var total = Assert.IsType<EvaluationMemory.SavedLocalMemory>(innerLocals.SavedMemoryTracker);
        Assert.Same(total, outerLocals.SavedMemoryTracker);
        Assert.Equal(outerLocals.SavedMemoryBytes + innerLocals.SavedMemoryBytes, total.Bytes);

        // A global write can reach an outer saved store while the inner frame is active.
        Assert.True(outerSaved.TrySetScalar("x", ShellValue.FromText(new string('x', 4096))));
        Assert.Equal(outerLocals.SavedMemoryBytes + innerLocals.SavedMemoryBytes, total.Bytes);
        var context = new Eval.EvaluationContext(0, inner, Eval.ReturnTargetKind.Function);
        var (_, fork) = context.Fork(variables, state.Memory, CancellationToken.None);
        var forkedLeaf = Assert.IsType<Eval.FunctionContext>(fork.FunctionContext);
        using var forkedOuter = Assert.IsType<Eval.FunctionContext>(forkedLeaf.Enclosing);
        using var forkedInner = forkedLeaf;
        var forkedTotal = Assert.IsType<EvaluationMemory.SavedLocalMemory>(forkedInner.Locals.SavedMemoryTracker);
        Assert.NotSame(total, forkedTotal);
        Assert.Equal(total.Bytes, forkedTotal.Bytes);

        var parentBytes = total.Bytes;
        var forkedSaved = Assert.IsType<Variables>(forkedOuter.Locals.SavedBindings);
        Assert.True(forkedSaved.TrySetScalar("x", ShellValue.FromText("child")));
        Assert.Equal(forkedOuter.Locals.SavedMemoryBytes + forkedInner.Locals.SavedMemoryBytes, forkedTotal.Bytes);
        Assert.Equal(parentBytes, total.Bytes);

        innerLocals.Restore();
        inner.Dispose();
        Assert.Equal(outerLocals.SavedMemoryBytes, total.Bytes);
        Assert.Equal("hidden", variables.Scalars["y"].ToUtf8String());
        outerLocals.Restore();
        outer.Dispose();
        Assert.Equal(0, total.Bytes);
        Assert.Equal(4096, variables.Scalars["x"].Bytes.Length);

        Variables.LocalBindings SaveLocal(BindingName name)
        {
            var locals = new Variables.LocalBindings(variables, parent: null, prefixes: null);
            Assert.True(variables.TrySetScalar(name, ShellValue.FromText("hidden")));
            locals.ShadowNamerefTarget(name, variables, state.Memory);
            Assert.True(variables.TrySetScalar(name, ShellValue.FromText("visible")));
            return locals;
        }
    }

    [Theory]
    [MemberData(nameof(SelectedBindings))]
    public async Task SelectedBindingsPreserveNativeValuesAndCompleteEffects(
        string name, string script, string outputHex, string error, int status, string? source, bool prepared)
    {
        Assert.NotEmpty(name);
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        if (source != null) host.AddFile("/unit", source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, deadline.Token)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, deadline.Token);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(outputHex, Convert.ToHexString(host.StandardOutput.Content.Span));
        Assert.Equal(error, host.StandardError.TextContent);
        var entries = host.CaptureEntries();
        if (source == null) Assert.Empty(entries);
        else
        {
            Assert.Equal("unit", Assert.Single(entries).Key);
            Assert.Equal((FilePathKind.File, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(source))), entries["unit"]);
        }
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesObserveTheSelectedBindingAndItsRestoration(bool local)
    {
        var declaration = local ? "local x; " : "";
        var script = "x=outer; f(){ " + declaration
            + "printf A; unset x; printf B; x=changed; printf C; }; x=prefix f; printf D";
        var evaluation = new Eval(Parser.Parse(script));
        await using var host = new ObservingHost(evaluation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new Engine().RunAsync(evaluation, host, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ABCD", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(new[]
        {
            (true, ShellValue.FromText("prefix")),
            (!local, local ? ShellValue.Empty : ShellValue.FromText("outer")),
            (true, ShellValue.FromText("changed")),
            (true, ShellValue.FromText(local ? "outer" : "changed"))
        }, host.Bindings);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PendingCleanupPreservesBindingsAndEarlierCancellation(
        bool local, bool canceled, bool closeThrows)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new PendingCloseHost(closeThrows) { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var script = "x=outer; f(){ " + (local ? "local x; " : "")
            + "unset x; x=changed; : >effect; }; x=prefix f; printf tail";
        var evaluation = new Eval(Parser.Parse(script));
        var running = new Engine().RunAsync(evaluation, host, owner, cancellation.Token);
        Exception? failure = null;
        RunResult? result = null;
        try
        {
            await host.Entered.Task.WaitAsync(deadline.Token);
            Assert.False(running.IsCompleted);
            Assert.Equal(ShellValue.FromText("changed"), evaluation.Variables["x"]);
            Assert.Equal(1, host.OpenFileCount);
            Assert.True(governor.ReservedBytes > 0);
            if (canceled) cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.Equal(ShellValue.FromText("changed"), evaluation.Variables["x"]);
        }
        finally
        {
            host.Release.TrySetResult();
            try { result = await running.WaitAsync(deadline.Token); }
            catch (Exception error) { failure = error; }
        }

        if (canceled)
        {
            var error = Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else if (closeThrows)
            Assert.Same(host.Failure, failure);
        else
        {
            Assert.Null(failure);
            Assert.Equal(0, Assert.IsType<RunResult>(result).ExitCode);
            Assert.Equal("tail", host.StandardOutput.TextContent);
        }
        Assert.Equal(ShellValue.FromText(local ? "outer" : "changed"), evaluation.Variables["x"]);
        Assert.Equal(1, host.Closes);
        Assert.Equal(0, host.OpenFileCount);
        var entry = Assert.Single(host.CaptureEntries());
        Assert.Equal("effect", entry.Key);
        Assert.Equal((FilePathKind.File, ""), entry.Value);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void RevealingTheSavedCallerTransfersItsArrayAndEndsRestoration()
    {
        var caller = Variables.CreateEmpty();
        Assert.True(caller.TryAssignArray("x", [ShellValue.FromText("zero"), ShellValue.FromText("one")], ArrayKind.Indexed));
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("x", ShellValue.FromText("prefix")));
        using var setup = new MemoryGovernor(TestMemoryBudget.EvaluationBytes).CreateScope();
        var memory = new EvaluationMemory(setup);
        var names = new HashSet<BindingName> { "x" };
        using var bindings = new Variables.CommandBindings(caller, prepared, names,
            suppressTemporaryLookup: false, memory, CancellationToken.None);
        var saved = bindings.SavedVariables.IndexedArrays["x"];

        Assert.True(caller.TryUnset("x"));
        bindings.ExposeBinding("x", bindings.SavedVariables);

        Assert.Same(saved, caller.IndexedArrays["x"]);
        Assert.False(bindings.Contains("x"));
        Assert.Contains((BindingName)"x", names);
        Assert.False(bindings.SavedVariables.HasBinding("x"));
        Assert.True(caller.TrySetArrayElement("x", default(IndexedArrayIndex), ShellValue.FromText("changed")));
        bindings.Dispose();
        Assert.Same(saved, caller.IndexedArrays["x"]);
        Assert.True(saved.TryGet(default, out var value));
        Assert.Equal(ShellValue.FromText("changed"), value);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public void PrefixMembershipBorrowsPreparedNamesUntilAnExposureNeedsAPrivateSet()
    {
        var caller = Variables.CreateEmpty();
        var prepared = Variables.CreateEmpty();
        var names = new HashSet<BindingName> { "first", "second", "third" };
        var original = ShellValue.FromText("original");
        var temporary = ShellValue.FromText("temporary");
        foreach (var name in names)
        {
            Assert.True(caller.TrySetScalar(name, original));
            Assert.True(prepared.TrySetScalar(name, temporary));
        }
        var governor = new MemoryGovernor(64 * 1024);
        using var setup = governor.CreateScope();
        var memory = new EvaluationMemory(setup);
        using (var bindings = new Variables.CommandBindings(caller, prepared, names,
            suppressTemporaryLookup: false, memory, CancellationToken.None))
        {
            var forwarding = ScopedEvaluation.ExpansionBindings.ForSavedPrefixes(bindings);
            Assert.Same(names, bindings.PrefixNames);
            Assert.Same(names, forwarding.PrefixNames);

            memory.ReleaseUnusedCapacity();
            using var hold = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
            bindings.ExposeBinding("first", bindings.SavedVariables);
            var owned = bindings.PrefixNames;
            Assert.NotSame(names, owned);
            Assert.Same(owned, forwarding.PrefixNames);
            Assert.False(forwarding.PrefixNames.Contains("first"));
            Assert.True(forwarding.PrefixNames.Contains("second"));
            Assert.Equal(original, caller.Scalars["first"]);
            Assert.Equal(temporary, caller.Scalars["second"]);

            bindings.ExposeBinding("second", bindings.SavedVariables);
            Assert.Same(owned, bindings.PrefixNames);
            Assert.Single(forwarding.PrefixNames);
            Assert.True(caller.TrySetScalar("first", temporary));
        }
        Assert.Equal(3, names.Count);
        Assert.Equal(temporary, caller.Scalars["first"]);
        Assert.Equal(original, caller.Scalars["second"]);
        Assert.Equal(original, caller.Scalars["third"]);
        setup.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentAndChildExposeIndependentPrefixNames(bool exposeBeforeFork)
    {
        var caller = Variables.CreateEmpty();
        var prepared = Variables.CreateEmpty();
        var names = new HashSet<BindingName> { "first", "second", "third" };
        var original = ShellValue.FromText("original");
        var temporary = ShellValue.FromText("temporary");
        foreach (var name in names)
        {
            Assert.True(caller.TrySetScalar(name, original));
            Assert.True(prepared.TrySetScalar(name, temporary));
        }
        using var setup = new MemoryGovernor(TestMemoryBudget.EvaluationBytes).CreateScope();
        var memory = new EvaluationMemory(setup);
        using var parent = new Variables.CommandBindings(caller, prepared, names,
            suppressTemporaryLookup: false, memory, CancellationToken.None);
        if (exposeBeforeFork)
            parent.ExposeBinding("third", parent.SavedVariables);
        var child = caller.Clone();
        using var childBindings = parent.Fork(child);
        Assert.Equal(!exposeBeforeFork, childBindings.Contains("third"));

        childBindings.ExposeBinding("first", childBindings.SavedVariables);
        parent.ExposeBinding("second", parent.SavedVariables);
        Assert.True(parent.Contains("first"));
        Assert.True(childBindings.Contains("second"));
        Assert.Equal(temporary, caller.Scalars["first"]);
        Assert.Equal(temporary, child.Scalars["second"]);

        parent.Dispose();
        Assert.True(childBindings.Contains("second"));
        Assert.True(child.TrySetScalar("first", temporary));
        childBindings.Dispose();
        Assert.Equal(original, caller.Scalars["first"]);
        Assert.Equal(temporary, child.Scalars["first"]);
        Assert.Equal(original, child.Scalars["second"]);
        Assert.Equal(3, names.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefixRestorationTransfersTheSelectedArray(bool retainedOutput)
    {
        var caller = Variables.CreateEmpty();
        var original = ShellValue.FromText("original");
        var changed = ShellValue.FromText("output");
        Assert.True(caller.TryAssignArray("x", [original, original], ArrayKind.Indexed));
        caller.SetExported("x", true);
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("x", ShellValue.FromText("prefix")));
        using var setup = new MemoryGovernor(TestMemoryBudget.EvaluationBytes).CreateScope();
        var memory = new EvaluationMemory(setup);
        using var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" },
            suppressTemporaryLookup: false, memory, CancellationToken.None);
        var donor = retainedOutput ? bindings.SelectOutputVariables("x", memory, CancellationToken.None) : bindings.SavedVariables;
        var array = donor.IndexedArrays["x"];
        if (retainedOutput)
        {
            Assert.True(donor.TrySetArrayElement("x", default(IndexedArrayIndex), changed));
            bindings.CompleteOutput("x", donor);
        }

        var child = caller.Clone();
        using var childBindings = bindings.Fork(child);
        var childArray = childBindings.SavedVariables.IndexedArrays["x"];
        Assert.NotSame(bindings.SavedVariables.IndexedArrays["x"], childArray);

        bindings.Dispose();
        Assert.Same(array, caller.IndexedArrays["x"]);
        Assert.False(donor.HasBinding("x"));
        Assert.False(bindings.SavedVariables.HasBinding("x"));
        Assert.Contains((BindingName)"x", caller.ExportedNames);
        Assert.False(caller.IsTemporaryBinding("x"));
        Assert.True(array.TryGet(default, out var first));
        Assert.Equal(retainedOutput ? changed : original, first);

        childBindings.Dispose();
        Assert.Same(childArray, child.IndexedArrays["x"]);
        Assert.True(childArray.TryGet(default, out var childValue));
        Assert.Equal(original, childValue);

        Assert.True(caller.TryUnset("x"));
        bindings.Dispose();
        Assert.False(caller.HasBinding("x"));
    }

    [Fact]
    public void LocalRestorationTransfersTheSavedAssociativeArray()
    {
        var caller = Variables.CreateEmpty();
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromBytes(new byte[] { 0xff }), out var key));
        var original = ShellValue.FromText("original");
        Assert.True(caller.TrySetArrayElement("map", key, original));
        caller.SetExported("map", true);
        var locals = new Variables.LocalBindings(caller, parent: null, prefixes: null);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            Assert.Equal(LocalDeclarationResult.Declared, locals.TryDeclare(caller, caller,
                new VariableOperand(VariableOperandKind.Assignment, VariableReference.ForName("map"), ShellValue.FromText("local")),
                prefixes: null, ArrayAssignmentKind.Auto, isNameref: false, inherit: false, caseChange: default,
                elementAttributes: default, memory, enclosingFunction: null));
            var donor = Assert.IsType<Variables>(locals.SavedBindings);
            var saved = donor.AssociativeArrays["map"];

            locals.Restore();
            Assert.Same(saved, caller.AssociativeArrays["map"]);
            Assert.False(donor.HasBinding("map"));
            Assert.Contains((BindingName)"map", caller.ExportedNames);
            Assert.True(saved.TryGet(key, out var restored));
            Assert.Equal(original, restored);

            Assert.True(caller.TryUnset("map"));
            locals.Restore();
            Assert.False(caller.HasBinding("map"));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void RefusingInheritedTemporaryArrayAdmissionLeavesBothOwnersIntact()
    {
        var caller = Variables.CreateEmpty();
        Assert.True(caller.TrySetScalar("x", ShellValue.FromText("outer")));
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("x", ShellValue.FromText("prefix")));
        using var setup = new MemoryGovernor(TestMemoryBudget.EvaluationBytes).CreateScope();
        var installation = new EvaluationMemory(setup);
        using var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" },
            suppressTemporaryLookup: false, installation, CancellationToken.None);
        Assert.True(caller.TryAssignArray("x", Enumerable.Repeat(ShellValue.FromText("value"), 256).ToArray(), ArrayKind.Indexed));
        var array = caller.IndexedArrays["x"];
        var locals = new Variables.LocalBindings(caller, parent: null, prefixes: null);
        var governor = new MemoryGovernor(0);
        var memory = new EvaluationMemory(governor);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() => locals.TryDeclare(caller, caller,
                new VariableOperand(VariableOperandKind.Name, VariableReference.ForName("x"), ShellValue.Empty),
                prefixes: null, ArrayAssignmentKind.Auto, isNameref: false, inherit: false, caseChange: default,
                elementAttributes: default, memory, enclosingFunction: null));
            Assert.Empty(locals.SavedNames);
            Assert.Same(array, caller.IndexedArrays["x"]);
            Assert.Equal(256, array.Count);
            Assert.True(bindings.Contains("x"));
            Assert.True(bindings.SavedVariables.TryGetScalar("x", out var original));
            Assert.Equal(ShellValue.FromText("outer"), original);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertedTemporaryArraysKeepTheirStructuredValue(bool prepared)
    {
        const string Script = "x=outer; f(){ local -a x=(one two); g(){ local x; declare -p x; }; g; declare -p x; }; x=prefix f; declare -p x";
        const string Expected = "declare -ax x=([0]=\"one\" [1]=\"two\")\n"
            + "declare -ax x=([0]=\"one\" [1]=\"two\")\ndeclare -- x=\"outer\"\n";
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(Script)), host, deadline.Token)
            : await host.RunAsync(Script, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    private sealed class ObservingHost(Eval evaluation) : TestHost
    {
        public List<(bool Assigned, ShellValue Value)> Bindings { get; } = [];

        public override ValueTask<DescriptorWriteResult> WriteAsync(
            FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancel)
        {
            if (descriptor == FileDescriptor.StdOut)
            {
                var assigned = evaluation.Variables.TryGetValue("x", out var value);
                Bindings.Add((assigned, value));
            }
            return base.WriteAsync(descriptor, content, cancel);
        }
    }

    private sealed class PendingCloseHost(bool closeThrows) : TestHost
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InvalidOperationException Failure { get; } = new("synthetic close failure");
        public int Closes { get; private set; }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            Closes++;
            Entered.TrySetResult();
            await Release.Task;
            var result = await base.CloseDescriptorAsync(descriptor);
            if (closeThrows) throw Failure;
            return result;
        }

        public override async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await base.DisposeAsync();
        }
    }
}

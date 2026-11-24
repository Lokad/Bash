using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class PrefixScopeOwnershipTests
{
    [Fact]
    public void ReachedBindingsDetachArraysAttributesAndHiddenNames()
    {
        var caller = Variables.CreateEmpty();
        Assert.True(caller.TrySetScalar("scalar", ShellValue.FromText("outer")));
        Assert.True(caller.TrySetScalar("hidden", ShellValue.FromText("present")));
        Assert.True(caller.TryAssignArray("indexed", [ShellValue.FromText("zero"), ShellValue.FromText("one")], ArrayKind.Indexed));
        var keyBytes = ShellValue.FromBytes(new byte[] { 0xff });
        Assert.True(AssociativeArrayKey.TryCreate(keyBytes, out var key));
        Assert.True(caller.TrySetArrayElement("map", key, ShellValue.FromText("outer")));
        Assert.True(caller.TrySetNameref("reference", ShellValue.FromText("scalar")));
        caller.SetExported("scalar", true);
        var governor = new MemoryGovernor(1024 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var command = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            Assert.True(command.TrySetScalar("reference", ShellValue.FromText("inner")));
            command.SetReadonlyBinding("scalar");
            command.SetExported("scalar", false);
            Assert.True(command.TrySetArrayElement("indexed", default(IndexedArrayIndex), ShellValue.FromText("changed")));
            Assert.True(command.TrySetArrayElement("map", key, ShellValue.FromText("inner")));
            Assert.True(command.TryUnset("hidden"));
            Assert.False(command.HasBinding("hidden"));
            Assert.DoesNotContain((BindingName)"hidden", command.BindingNames);
            Assert.True(command.TryGetScalar("scalar", out var scalar));
            Assert.Equal("inner", scalar.ToUtf8String());

            Assert.True(caller.TryGetScalar("scalar", out var original));
            Assert.Equal("outer", original.ToUtf8String());
            Assert.False(caller.IsReadonly("scalar"));
            Assert.Contains((BindingName)"scalar", caller.ExportedNames);
            Assert.DoesNotContain((BindingName)"scalar", command.ExportedNames);
            Assert.True(caller.HasBinding("hidden"));
            Assert.True(caller.IndexedArrays["indexed"].TryGet(default, out var zero));
            Assert.Equal("zero", zero.ToUtf8String());
            Assert.True(caller.AssociativeArrays["map"].TryGet(key, out var mapValue));
            Assert.Equal("outer", mapValue.ToUtf8String());
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void ExportSnapshotReadsThroughMultipleOverlaysWithoutDetaching()
    {
        var caller = Variables.CreateEmpty();
        var original = ShellValue.FromBytes(new byte[] { 0xff, 0x20 });
        Assert.True(caller.TrySetScalar("value", original));
        caller.SetExported("value", true);
        Assert.True(caller.TryAssignArray("array", [original], ArrayKind.Indexed));
        caller.SetExported("array", true);
        Assert.True(caller.TrySetScalar("hidden", original));
        caller.SetExported("hidden", true);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var middle = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            Assert.True(middle.TryUnset("hidden"));
            var inner = Variables.CreateCommandScope(middle, memory, CancellationToken.None);
            memory.ReleaseUnusedCapacity();
            var workspace = memory.Workspace.Bytes;
            using var hold = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);

            var snapshot = inner.BuildExportedEnvironment();
            Assert.Equal(new EnvironmentVariable("value", original), Assert.Single(snapshot));
            Assert.Equal(workspace, memory.Workspace.Bytes);

            var replacement = ShellValue.FromText("later");
            Assert.True(caller.TrySetScalar("value", replacement));
            Assert.Equal(original, snapshot[0].Value);
            Assert.Equal(replacement, Assert.Single(inner.BuildExportedEnvironment()).Value);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void BorrowedExportProjectionObservesCancellation()
    {
        var caller = Variables.CreateEmpty();
        Assert.True(caller.TrySetScalar("value", ShellValue.FromText("caller")));
        caller.SetExported("value", true);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var overlay = Variables.CreateCommandScope(caller, memory, cancellation.Token);
            cancellation.Cancel();

            var failure = Assert.Throws<OperationCanceledException>(() => overlay.BuildExportedEnvironment());
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(ArrayKind.Indexed)]
    [InlineData(ArrayKind.Associative)]
    public void ArrayReadsBorrowAcrossOverlaysUntilAnElementIsWritten(ArrayKind kind)
    {
        var caller = Variables.CreateEmpty();
        var original = ShellValue.FromBytes(new byte[] { 0xff, 0x20 });
        var replacement = ShellValue.FromText("changed");
        var zero = ShellValue.FromText("0");
        var entries = kind == ArrayKind.Indexed ? new[] { original } : new[] { zero, original };
        Assert.True(caller.TryAssignArray("array", entries, kind));
        WordExpansion.ResolvedVariableReference element = kind == ArrayKind.Indexed
            ? new WordExpansion.ResolvedVariableReference.Indexed("array", default)
            : new WordExpansion.ResolvedVariableReference.Associative("array", AssociativeArrayKey.Zero);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var middle = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            var inner = Variables.CreateCommandScope(middle, memory, CancellationToken.None);
            memory.ReleaseUnusedCapacity();
            var workspace = memory.Workspace.Bytes;
            using (governor.Reserve(governor.LimitBytes - governor.ReservedBytes))
            {
                Assert.True(element.TryRead(inner, out var selected));
                Assert.Equal(original, selected);
                Assert.True(inner.TryGetExpansionValue("array", out var bareValue));
                Assert.Equal(original, bareValue);
                Assert.False(inner.TryGetScalar("array", out _));
                Assert.Equal(new Variables.ArrayMetadata(kind, 1, true, kind == ArrayKind.Indexed ? 0L : null),
                    inner.GetArrayMetadata("array"));
                Assert.Equal(workspace, memory.Workspace.Bytes);
            }

            Assert.True(inner.TrySetArrayElement("array", zero, replacement, kind));
            Assert.True(element.TryRead(inner, out var changed));
            Assert.Equal(replacement, changed);
            Assert.True(element.TryRead(middle, out var retained));
            Assert.Equal(original, retained);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(ArrayKind.Indexed)]
    [InlineData(ArrayKind.Associative)]
    public void WholeArrayReplacementBorrowsAttributesAcrossOverlays(ArrayKind kind)
    {
        var caller = Variables.CreateEmpty();
        var original = ShellValue.FromBytes(new byte[] { 0xff });
        var replacement = ShellValue.FromText("changed");
        var zero = ShellValue.FromText("0");
        var one = ShellValue.FromText("1");
        var originalEntries = kind == ArrayKind.Indexed ? new[] { original, original } : new[] { zero, original, one, original };
        var replacementEntries = kind == ArrayKind.Indexed ? new[] { replacement } : new[] { zero, replacement };
        Assert.True(caller.TryAssignArray("array", originalEntries, kind));
        caller.SetExported("array", true);
        Assert.True(caller.TrySetNameref("reference", ShellValue.FromText("array")));
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var middle = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            var inner = Variables.CreateCommandScope(middle, memory, CancellationToken.None);
            var workspace = memory.Workspace.Bytes;

            Assert.True(inner.TryAssignArray("reference", replacementEntries, kind));

            Assert.Equal(workspace, memory.Workspace.Bytes);
            Assert.True(middle.IsBorrowedBinding("array"));
            Assert.False(inner.IsBorrowedBinding("array"));
            Assert.Contains((BindingName)"array", inner.ExportedNames);
            Assert.Equal(1, inner.GetArrayMetadata("array")?.Count);
            Assert.True(inner.TryGetExpansionValue("array", out var changed));
            Assert.Equal(replacement, changed);
            Assert.Equal(2, caller.GetArrayMetadata("array")?.Count);
            Assert.True(caller.TryGetExpansionValue("array", out var retained));
            Assert.Equal(original, retained);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void BindingSizeEstimateLeavesTheArrayBorrowed()
    {
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromText("original");
        Assert.True(caller.TryAssignArray("x", Enumerable.Repeat(value, 32).ToArray(), ArrayKind.Indexed));
        var expected = caller.EstimateBindingBytes("x");
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var source = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            var workspace = memory.Workspace.Bytes;

            Assert.Equal(expected, source.EstimateBindingBytes("x"));
            Assert.Equal(workspace, memory.Workspace.Bytes);
            Assert.True(source.IsBorrowedBinding("x"));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void BindingCopyOwnsAnIndependentArrayWhileLeavingTheSourceBorrowed()
    {
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromBytes(new byte[] { 0xff, 0x20 });
        Assert.True(caller.TryAssignArray("x", Enumerable.Repeat(value, 32).ToArray(), ArrayKind.Indexed));
        caller.SetExported("x", true);
        var destination = Variables.CreateEmpty();
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var source = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            var workspace = memory.Workspace.Bytes;

            destination.CopyBindingFrom(source, "x");

            Assert.True(source.IsBorrowedBinding("x"));
            Assert.Equal(workspace + caller.EstimateBindingBytes("x"), memory.Workspace.Bytes);
            Assert.Contains((BindingName)"x", destination.ExportedNames);
            Assert.Equal(32, destination.GetArrayMetadata("x")?.Count);
            Assert.True(destination.TryGetExpansionValue("x", out var copied));
            Assert.Equal(value, copied);
            var changed = ShellValue.FromText("changed");
            Assert.True(destination.TrySetArrayElement("x", default(IndexedArrayIndex), changed));
            Assert.True(source.TryGetExpansionValue("x", out var retained));
            Assert.Equal(value, retained);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(ArrayKind.Indexed)]
    [InlineData(ArrayKind.Associative)]
    public Task AggregateAlternateUsesBorrowedArrayBeforeExpandingItsWord(ArrayKind kind) =>
        AssertBorrowedAggregateExpansionAsync(kind, "${array[@]:+chosen}", ShellValue.FromText("chosen"));

    [Theory]
    [InlineData(ArrayKind.Indexed, " -1:1")]
    [InlineData(ArrayKind.Associative, " -1:1")]
    [InlineData(ArrayKind.Indexed, "0x0:2#1")]
    [InlineData(ArrayKind.Associative, "0x0:0")]
    public Task LiteralArraySliceUsesBorrowedBacking(ArrayKind kind, string bounds) =>
        AssertBorrowedAggregateExpansionAsync(kind, "${array[@]:" + bounds + "}",
            ShellValue.FromBytes(new byte[] { 0xff }));

    [Theory]
    [InlineData(ArrayKind.Indexed, " -((2-1)):8>>3")]
    [InlineData(ArrayKind.Associative, "0+0:1?2#1:2")]
    public Task ComputedArraySliceBorrowsAcrossReferenceFreeBounds(ArrayKind kind, string bounds) =>
        AssertBorrowedAggregateExpansionAsync(kind, "${array[@]:" + bounds + "}",
            ShellValue.FromBytes(new byte[] { 0xff }));

    [Theory]
    [InlineData(ArrayKind.Indexed)]
    [InlineData(ArrayKind.Associative)]
    public Task OutOfRangeArraySliceSkipsLengthAndItsDetachment(ArrayKind kind) =>
        AssertBorrowedAggregateExpansionAsync(kind, "${array[@]:2048+1:$((side=1))}", expected: null);

    [Theory]
    [InlineData(ArrayKind.Indexed, "^^")]
    [InlineData(ArrayKind.Associative, ",,[xX]")]
    [InlineData(ArrayKind.Indexed, "##x")]
    [InlineData(ArrayKind.Associative, "%x")]
    [InlineData(ArrayKind.Indexed, "//x/y")]
    [InlineData(ArrayKind.Associative, "/%x/y")]
    public Task LiteralArrayPatternsUseBorrowedBacking(ArrayKind kind, string operation) =>
        AssertBorrowedAggregateExpansionAsync(kind, "${array[*]" + operation + "}",
            ShellValue.FromBytes(Enumerable.Repeat((byte)0xff, 1024).ToArray()));

    [Theory]
    [InlineData(ArrayKind.Indexed)]
    [InlineData(ArrayKind.Associative)]
    public Task ComputedSliceLengthWritesToItsSelectedCommandArray(ArrayKind kind) =>
        AssertSelectedCommandArrayWriteAsync(kind, "${array[@]:0+0:$((array[0]=23,1))}", "23");

    [Theory]
    [InlineData(ArrayKind.Indexed, "#$((array[0]=72,7))", "2")]
    [InlineData(ArrayKind.Associative, "^^$((array[0]=72,7))", "72")]
    [InlineData(ArrayKind.Indexed, "/7/$((array[0]=72,3))", "32")]
    [InlineData(ArrayKind.Associative, "/7/$((array[0]=72,3))", "32")]
    public Task DynamicArrayOperandsWriteToTheirSelectedCommandArray(ArrayKind kind, string operation, string expected) =>
        AssertSelectedCommandArrayWriteAsync(kind, "${array[@]" + operation + "}", expected);

    private static async Task AssertSelectedCommandArrayWriteAsync(ArrayKind kind, string expression, string expected)
    {
        var caller = Variables.CreateEmpty();
        var initial = ShellValue.FromText("1");
        ShellValue[] entries = kind == ArrayKind.Indexed ? [initial] : [ShellValue.FromText("0"), initial];
        Assert.True(caller.TryAssignArray("array", entries, kind));
        var governor = new MemoryGovernor(64 * 1024);
        var evaluation = new Eval(Parser.Parse(""), EvaluationDescriptorSetup.Standard,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, governor, sourceMemory: null);
        try
        {
            var command = Variables.CreateCommandScope(caller, evaluation.Memory, CancellationToken.None);
            var scope = new ScopedEvaluation(evaluation, command, default);
            var word = new Word([WordPart.FromText(WordPartKind.DoubleQuoted, expression)]);

            var fields = await WordExpansion.ExpandToWordsAsync(word, scope);

            Assert.Equal(ShellValue.FromText(expected), Assert.Single(fields).ToValue(evaluation.Memory));
            Assert.True(caller.TryGetExpansionValue("array", out var retained));
            Assert.Equal(initial, retained);
        }
        finally { evaluation.Memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    private static async Task AssertBorrowedAggregateExpansionAsync(ArrayKind kind, string expression, ShellValue? expected)
    {
        const int Count = 1024;
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromBytes(new byte[] { 0xff });
        var entries = kind == ArrayKind.Indexed ? Enumerable.Repeat(value, Count).ToArray()
            : Enumerable.Range(0, Count).SelectMany(index => new[] { ShellValue.FromInteger(index), value }).ToArray();
        Assert.True(caller.TryAssignArray("array", entries, kind));
        Assert.True(caller.TrySetScalar("IFS", ShellValue.Empty));

        var governor = new MemoryGovernor(64 * 1024);
        var evaluation = new Eval(Parser.Parse(""), EvaluationDescriptorSetup.Standard,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, governor, sourceMemory: null);
        try
        {
            var variables = Variables.CreateCommandScope(caller, evaluation.Memory, CancellationToken.None);
            var scope = new ScopedEvaluation(evaluation, variables, default);
            var word = new Word([WordPart.FromText(WordPartKind.DoubleQuoted, expression)]);

            var fields = await WordExpansion.ExpandToWordsAsync(word, scope);

            if (expected is { } valueExpected)
                Assert.Equal(valueExpected, Assert.Single(fields).ToValue(evaluation.Memory));
            else
                Assert.Empty(fields);
            Assert.False(variables.HasBinding("side"));
            Assert.Equal(Count, caller.GetArrayMetadata("array")?.Count);
        }
        finally { evaluation.Memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void FullChildCloneDetachesMergedBindingsAndPositionalEntries()
    {
        var caller = Variables.CreateEmpty();
        caller.PositionalParameters = [ShellValue.FromText("first"), ShellValue.FromText("second")];
        Assert.True(caller.TrySetScalar("borrowed", ShellValue.FromText("outer")));
        Assert.True(caller.TrySetScalar("hidden", ShellValue.FromText("present")));
        Assert.True(caller.TryAssignArray("indexed", [ShellValue.FromText("zero")], ArrayKind.Indexed));
        var governor = new MemoryGovernor(1024 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var command = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            Assert.True(command.TrySetScalar("prepared", ShellValue.FromText("prefix")));
            Assert.True(command.TryUnset("hidden"));
            var context = default(Eval.EvaluationContext);
            var (child, _) = context.Fork(command, memory, CancellationToken.None);
            Assert.False(child.HasBinding("hidden"));
            Assert.True(child.TryGetScalar("prepared", out var prepared));
            Assert.Equal("prefix", prepared.ToUtf8String());
            Assert.True(child.TrySetScalar("borrowed", ShellValue.FromText("child")));
            Assert.True(child.TrySetArrayElement("indexed", default(IndexedArrayIndex), ShellValue.FromText("changed")));
            Assert.True(child.TryShiftPositionalParameters(1));

            Assert.True(command.TryGetScalar("borrowed", out var outer));
            Assert.Equal("outer", outer.ToUtf8String());
            Assert.True(command.IndexedArrays["indexed"].TryGet(default, out var zero));
            Assert.Equal("zero", zero.ToUtf8String());
            Assert.Equal(2, caller.PositionalParameters.Count);
            Assert.Equal(2, command.PositionalParameters.Count);
            Assert.Equal("second", Assert.Single(child.PositionalParameters).ToUtf8String());
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void UnrelatedLargeStateNeedsNoAdditionalCopyAdmission()
    {
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromText("value");
        for (var index = 0; index < 4096; index++)
        {
            Assert.True(caller.TrySetScalar($"retained_{index}", value));
            Assert.True(IndexedArrayIndex.TryCreate(index, out var position));
            Assert.True(caller.TrySetArrayElement("unrelated_array", position, value));
        }
        caller.PositionalParameters = Enumerable.Repeat(value, 4096).ToArray();
        var governor = new MemoryGovernor(caller.EstimatedMemoryBytes + 64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            memory.Charge(caller.EstimatedMemoryBytes);
            var command = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            Assert.True(command.TrySetScalar("temporary", value));
            Assert.True(command.TryGetScalar("retained_4095", out var retained));
            Assert.Equal(value, retained);
            Assert.Equal(4098, command.BindingNames.Count());
            Assert.Equal((BindingName)"unrelated_array", Assert.Single(command.IndexedArrays.Keys));
            Assert.Equal(4096, command.PositionalParameters.Count);
            Assert.InRange(governor.PeakBytes, 1, governor.LimitBytes);

            // A real child snapshot owns its whole store and must still be admitted.
            var context = default(Eval.EvaluationContext);
            Assert.Throws<MemoryBudgetExceededException>(() => context.Fork(command, memory, CancellationToken.None));
            Assert.Equal(4096, caller.IndexedArrays["unrelated_array"].Count);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("function-prefix")]
    [InlineData("script-prefix")]
    public void FullChildForkAdmitsHiddenArraysAndKeepsThemIndependent(string owner)
    {
        var caller = Variables.CreateEmpty();
        var original = ShellValue.FromText("outer");
        var visible = ShellValue.FromText("visible");
        Assert.True(caller.TryAssignArray("x", Enumerable.Repeat(original, 512).ToArray(), ArrayKind.Indexed));
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("x", visible));
        using var setup = new MemoryGovernor(TestMemoryBudget.EvaluationBytes).CreateScope();
        var installation = new EvaluationMemory(setup);
        using var bindings = owner == "local" ? null
            : new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" }, installation, CancellationToken.None);
        var locals = new Variables.LocalBindings(caller, parent: null, bindings);
        if (owner == "local")
        {
            var staging = new EvaluationMemory(new MemoryGovernor(1024 * 1024));
            try
            {
                Assert.True(locals.TryDeclare(caller, caller,
                    new VariableOperand(VariableOperandKind.Assignment, VariableReference.ForName("x"), visible),
                    prefixes: null, ArrayAssignmentKind.Auto, isNameref: false, inherit: false, caseChange: default, staging));
            }
            finally { staging.Finish(retainVariables: false); }
        }
        var function = new Eval.FunctionContext(ShellValue.FromText("f"), locals, enclosing: null)
            { PrefixBindings = bindings };
        var context = owner == "script-prefix" && bindings != null
            ? new Eval.EvaluationContext(0, null, Eval.ReturnTargetKind.Source)
                { GlobalPrefixes = new Eval.GlobalPrefixBindings(bindings, 0, null) }
            : new Eval.EvaluationContext(0, function, Eval.ReturnTargetKind.Function);

        var saved = context.GetGlobalBindingOwner("x", caller);
        var savedArray = saved.IndexedArrays["x"];
        Assert.True(caller.TryGetScalar("x", out var callerValue));
        Assert.Equal(visible, callerValue);
        Assert.Empty(caller.IndexedArrays);

        var tightBudget = new MemoryGovernor(caller.EstimatedMemoryBytes + 4096);
        var tightMemory = new EvaluationMemory(tightBudget);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() => context.Fork(caller, tightMemory, CancellationToken.None));
            Assert.Same(savedArray, context.GetGlobalBindingOwner("x", caller).IndexedArrays["x"]);
            Assert.Equal(0, tightBudget.ReservedBytes);
        }
        finally { tightMemory.Finish(retainVariables: false); }

        var budget = new MemoryGovernor(1024 * 1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            var (child, childContext) = context.Fork(caller, memory, CancellationToken.None);
            var childSaved = childContext.GetGlobalBindingOwner("x", child);
            Assert.NotSame(savedArray, childSaved.IndexedArrays["x"]);
            Assert.True(budget.ReservedBytes >= child.EstimatedMemoryBytes + childSaved.EstimatedMemoryBytes);
            Assert.True(childSaved.TrySetArrayElement("x", default(IndexedArrayIndex), ShellValue.FromText("changed")));
            Assert.True(savedArray.TryGet(default, out var item));
            Assert.Equal(original, item);
            Assert.Equal(visible, Assert.Single(child.Scalars).Value);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void RefusedReachedArrayCopyLeavesTheCallerIntact()
    {
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromText("outer");
        for (var index = 0; index < 4096; index++)
        {
            Assert.True(IndexedArrayIndex.TryCreate(index, out var position));
            Assert.True(caller.TrySetArrayElement("array", position, value));
        }
        var governor = new MemoryGovernor(caller.EstimatedMemoryBytes + 64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            memory.Charge(caller.EstimatedMemoryBytes);
            var command = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            Assert.Throws<MemoryBudgetExceededException>(() =>
                command.TrySetArrayElement("array", default(IndexedArrayIndex), ShellValue.FromText("changed")));
            Assert.True(caller.IndexedArrays["array"].TryGet(default, out var retained));
            Assert.Equal(value, retained);
            Assert.Equal(4096, caller.IndexedArrays["array"].Count);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(":")]
    [InlineData("f")]
    [InlineData("eval :")]
    [InlineData("source ./unit.sh")]
    [InlineData("cat </empty >\"$sink\"")]
    [InlineData("cat </empty >\"${scratch:=/sink}\"")]
    public async Task RepeatedPrefixesFitBesideRetainedVariablesAndArrays(string command)
    {
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        host.AddFile("/unit.sh", ":\n");
        host.AddFile("/empty", "");
        var script = "x=outer; sink=/sink; f(){ :; };\n"
            + "for ((i=0;i<2048;i++)); do printf -v \"retained_$i\" '%s' value; done\n"
            + "for ((i=0;i<4096;i++)); do a[i]=value; done\n"
            + "for ((i=0;i<100;i++)); do x=temporary " + command + "; done\n"
            + "printf '%s:%s:%s:%s' \"${#a[@]}\" \"$retained_2047\" \"$x\" \"${scratch-unset}\"";
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("4096:value:outer:unset", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
        Assert.InRange(governor.PeakBytes, 1, governor.LimitBytes);
    }

    [Theory]
    [InlineData("local", "-a", "i", "4095")]
    [InlineData("declare", "-A", "key$i", "key4095")]
    public async Task PrefixedLocalShadowNeedsRoomForOneSavedArray(
        string declaration, string arrayFlag, string index, string lastIndex)
    {
        var governor = new MemoryGovernor(2600 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var script = $"declare {arrayFlag} vector\n"
            + $"for ((i=0;i<4096;i++)); do vector[{index}]=outer; done\n"
            + $"f(){{ FLAG=temporary {declaration} vector=inner; printf '%s:%s/' \"$?\" \"$vector\"; }}; f\n"
            + $"printf '%s:%s:%s' \"${{#vector[@]}}\" \"${{vector[{lastIndex}]}}\" \"${{FLAG-missing}}\"";

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0:inner/4096:outer:missing", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void CancellationDuringReachedCopyAdmissionLeavesTheCallerIntact()
    {
        using var cancellation = new CancellationTokenSource();
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromText("outer");
        Assert.True(caller.TryAssignArray("array", Enumerable.Repeat(value, 512).ToArray(), ArrayKind.Indexed));
        var governor = new MemoryGovernor(1024 * 1024);
        var admissions = new CountingMemoryBudget(governor);
        var memory = new EvaluationMemory(admissions);
        try
        {
            var command = Variables.CreateCommandScope(caller, memory, cancellation.Token);
            admissions.AdmissionSucceeded += cancellation.Cancel;
            var failure = Assert.ThrowsAny<OperationCanceledException>(() =>
                command.TrySetArrayElement("array", default(IndexedArrayIndex), ShellValue.FromText("changed")));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.True(caller.IndexedArrays["array"].TryGet(default, out var retained));
            Assert.Equal(value, retained);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefixInstallationMovesTheOriginalAndPreparedArrays(bool borrowed)
    {
        var ancestor = Variables.CreateEmpty();
        var value = ShellValue.FromText("original");
        Assert.True(ancestor.TryAssignArray("x", Enumerable.Repeat(value, 512).ToArray(), ArrayKind.Indexed));
        var original = ancestor.IndexedArrays["x"];
        var governor = new MemoryGovernor(1024 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var caller = borrowed ? Variables.CreateCommandScope(ancestor, memory, CancellationToken.None) : ancestor;
            var prepared = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            Assert.True(prepared.TryAssignArray("x", [ShellValue.FromText("temporary")], ArrayKind.Indexed));
            var temporary = prepared.IndexedArrays["x"];
            Assert.NotSame(original, temporary);
            Assert.Equal(borrowed, caller.IsBorrowedBinding("x"));
            var baseBytes = memory.Workspace.Bytes;
            IndexedArray saved;
            using (var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" }, memory, CancellationToken.None))
            {
                saved = bindings.SavedVariables.IndexedArrays["x"];
                if (borrowed)
                {
                    Assert.NotSame(original, saved);
                    Assert.Same(original, ancestor.IndexedArrays["x"]);
                }
                else
                    Assert.Same(original, saved);
                Assert.Same(temporary, caller.IndexedArrays["x"]);
                Assert.False(prepared.HasBinding("x"));
                Assert.True(caller.IsTemporaryBinding("x"));
                Assert.True(memory.Workspace.Bytes - baseBytes >= bindings.SavedVariables.EstimatedMemoryBytes);
            }
            Assert.Same(saved, caller.IndexedArrays["x"]);
            Assert.Equal(512, caller.GetArrayMetadata("x")?.Count);
            Assert.False(caller.IsTemporaryBinding("x"));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefixInstallationAdmitsTheEntireSavedSetBeforePublishing(bool cancelAtAdmission)
    {
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromText("original");
        Assert.True(caller.TryAssignArray("x", Enumerable.Repeat(value, 512).ToArray(), ArrayKind.Indexed));
        Assert.True(caller.TrySetScalar("first", value));
        var original = caller.IndexedArrays["x"];
        var prepared = Variables.CreateEmpty();
        var replacement = ShellValue.FromText("replacement");
        Assert.True(prepared.TrySetScalar("x", replacement));
        Assert.True(prepared.TrySetScalar("first", replacement));
        var names = new HashSet<BindingName> { "first", "x" };
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(cancelAtAdmission ? 1024 * 1024 : 4096);
        var counted = new CountingMemoryBudget(governor);
        var memory = new EvaluationMemory(counted);
        try
        {
            if (cancelAtAdmission)
            {
                counted.AdmissionSucceeded += cancellation.Cancel;
                var error = Assert.ThrowsAny<OperationCanceledException>(() =>
                    new Variables.CommandBindings(caller, prepared, names, memory, cancellation.Token));
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                Assert.Throws<MemoryBudgetExceededException>(() =>
                    new Variables.CommandBindings(caller, prepared, names, memory, cancellation.Token));
            }
            Assert.Same(original, caller.IndexedArrays["x"]);
            Assert.Equal(value, caller.Scalars["first"]);
            Assert.Equal(replacement, prepared.Scalars["first"]);
            Assert.Equal(replacement, prepared.Scalars["x"]);
            Assert.False(caller.IsTemporaryBinding("first"));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("selector")]
    [InlineData("promotion")]
    [InlineData("resolved promotion")]
    public void RefusingAnOutputCopyKeepsTheSavedArraySelected(string route)
    {
        var caller = Variables.CreateEmpty();
        var element = ShellValue.FromText("saved");
        Assert.True(caller.TryAssignArray("x", Enumerable.Repeat(element, 512).ToArray(), ArrayKind.Indexed));
        Assert.True(caller.TrySetNameref("link", ShellValue.FromText("x")));
        var array = caller.IndexedArrays["x"];
        var prepared = Variables.CreateEmpty();
        var replacement = ShellValue.FromText("active");
        Assert.True(prepared.TrySetScalar("x", replacement));
        var budget = new MemoryGovernor(1024 * 1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            using var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" }, memory, CancellationToken.None);
            memory.ReleaseUnusedCapacity();
            using var hold = budget.Reserve(budget.LimitBytes - budget.ReservedBytes);
            Assert.Throws<MemoryBudgetExceededException>(() =>
            {
                switch (route)
                {
                    case "output": bindings.SelectOutputVariables("x", memory, CancellationToken.None); break;
                    case "selector": bindings.RetainSelectorBinding("x", memory, CancellationToken.None); break;
                    case "promotion": bindings.RetainBinding("x", memory, CancellationToken.None); break;
                    case "resolved promotion": bindings.Retain("link", memory, CancellationToken.None); break;
                    default: throw new ArgumentOutOfRangeException(nameof(route));
                }
            });
            Assert.Same(array, bindings.SavedVariables.IndexedArrays["x"]);
            Assert.Equal(replacement, caller.Scalars["x"]);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Same(array, caller.IndexedArrays["x"]);
        Assert.True(array.TryGet(default, out var restored));
        Assert.Equal(element, restored);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void RefusedIndexedDirectoryWriteLeavesTheSavedAssociativeBinding()
    {
        var caller = Variables.CreateEmpty();
        var value = ShellValue.FromText("saved");
        Assert.True(AssociativeArrayKey.TryCreate(ShellValue.FromText("key"), out var key));
        Assert.True(caller.TrySetArrayElement("x", key, value));
        var original = caller.AssociativeArrays["x"];
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("x", ShellValue.FromText("prefix")));
        var budget = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            using var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" },
                memory, CancellationToken.None);
            var location = new WordExpansion.ResolvedVariableReference.Indexed("x", default) { ReplaceAssociative = true };
            memory.ReleaseUnusedCapacity();
            using var hold = budget.Reserve(budget.LimitBytes - budget.ReservedBytes);
            Assert.Throws<MemoryBudgetExceededException>(() => location.TryAssignUnassigned(bindings.SavedVariables, memory));
            Assert.Same(original, bindings.SavedVariables.AssociativeArrays["x"]);
        }
        finally { memory.Finish(retainVariables: false); }

        Assert.Same(original, caller.AssociativeArrays["x"]);
        Assert.True(original.TryGet(key, out var restored));
        Assert.Equal(value, restored);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void UnrelatedAttributeResultDoesNotReservePrefixCopyStorage()
    {
        var caller = Variables.CreateEmpty();
        var saved = ShellValue.FromText("saved");
        Assert.True(caller.TrySetScalar("prefix", saved));
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("prefix", ShellValue.FromText("temporary")));
        var name = new string('v', 1024);
        Assert.True(caller.TrySetScalar(name, ShellValue.FromText("unrelated")));
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            using var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "prefix" },
                memory, CancellationToken.None);
            memory.ReleaseUnusedCapacity();
            var workspace = memory.Workspace.Bytes;
            using var hold = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
            bindings.Retain(name, memory, CancellationToken.None);
            Assert.Equal(workspace, memory.Workspace.Bytes);
        }
        finally { memory.Finish(retainVariables: false); }

        Assert.Equal(saved, caller.Scalars["prefix"]);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("raw selector", true)]
    [InlineData("literal referent", true)]
    [InlineData("element referent", false)]
    [InlineData("uninitialized referent", false)]
    [InlineData("circular referent", false)]
    public void RetentionSelectsTheWholePrefixWithoutEvaluatingSelectors(string route, bool retained)
    {
        var caller = Variables.CreateEmpty();
        var saved = ShellValue.FromText("saved");
        var tail = ShellValue.FromText("tail");
        Assert.True(caller.TryAssignArray("x", [saved, tail], ArrayKind.Indexed));
        var prepared = Variables.CreateEmpty();
        var replacement = ShellValue.FromText("temporary");
        Assert.True(prepared.TrySetScalar("x", replacement));
        var spelling = ShellValue.FromBytes(new byte[] { (byte)'x', (byte)'[', 0xff, (byte)']' });
        var literal = BindingName.FromValue(spelling);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            if (route == "literal referent")
                Assert.True(caller.TrySetResolvedScalar(literal, ShellValue.FromText("opaque"), memory));
            if (route == "circular referent")
                Assert.True(caller.TrySetNameref("next", ShellValue.FromText("link")));
            if (route != "raw selector")
                Assert.True(caller.TrySetNameref("link", route switch
                {
                    "uninitialized referent" => ShellValue.Empty,
                    "circular referent" => ShellValue.FromText("next"),
                    _ => spelling
                }));

            using var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" },
                memory, CancellationToken.None);
            bindings.Retain(route == "raw selector" ? literal : (BindingName)"link", memory, CancellationToken.None);
        }
        finally { memory.Finish(retainVariables: false); }

        var array = caller.IndexedArrays["x"];
        Assert.True(array.TryGet(default, out var first));
        Assert.Equal(retained ? replacement : saved, first);
        Assert.True(array.TryGet(IndexedArrayIndex.FromUnsigned(1), out var second));
        Assert.Equal(tail, second);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void RestagingAnUncommittedOutputReusesAdmission()
    {
        var caller = Variables.CreateEmpty();
        var original = ShellValue.FromText("original");
        Assert.True(caller.TryAssignArray("x", Enumerable.Repeat(original, 512).ToArray(), ArrayKind.Indexed));
        var prepared = Variables.CreateEmpty();
        Assert.True(prepared.TrySetScalar("x", ShellValue.FromText("temporary")));
        var budget = new MemoryGovernor(1024 * 1024);
        var memory = new EvaluationMemory(budget);
        try
        {
            using var bindings = new Variables.CommandBindings(caller, prepared, new HashSet<BindingName> { "x" }, memory, CancellationToken.None);
            var baseline = memory.Workspace.Bytes;
            var selected = bindings.SelectOutputVariables("x", memory, CancellationToken.None);
            Assert.True(memory.Workspace.Bytes - baseline >= selected.EstimatedMemoryBytes);
            Assert.True(selected.TrySetArrayElement("x", default(IndexedArrayIndex), ShellValue.FromText("different")));
            _ = bindings.SelectOutputVariables("x", memory, CancellationToken.None);
            var capacity = memory.Workspace.Bytes;
            for (var i = 0; i < 8; i++)
                Assert.Same(selected, bindings.SelectOutputVariables("x", memory, CancellationToken.None));
            Assert.Equal(capacity, memory.Workspace.Bytes);
            Assert.True(selected.IndexedArrays["x"].TryGet(default, out var restored));
            Assert.Equal(original, restored);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, budget.ReservedBytes);
    }
}

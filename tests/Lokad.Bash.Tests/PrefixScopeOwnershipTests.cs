using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Tests.EndToEnd;

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
            var child = memory.CloneVariables(command);
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
            Assert.Throws<MemoryBudgetExceededException>(() => memory.CloneVariables(command));
            Assert.Equal(4096, caller.IndexedArrays["unrelated_array"].Count);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
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
}

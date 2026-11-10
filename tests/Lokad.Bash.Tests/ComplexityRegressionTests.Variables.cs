using Lokad.Bash.Memory;
using System;
using System.Linq;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
    [Fact]
    public void NamedLookupAfterRepeatedClonesDoesNotAllocate()
    {
        var variables = Variables.CreateEmpty();
        var expected = ShellValue.FromText("present");
        Assert.True(variables.TrySetScalar("root_value", expected));
        // Scope state is copied in the current evaluator, not linked through parent frames.
        for (var depth = 0; depth < 512; depth++)
            variables = variables.Clone();

        Assert.True(variables.TryGetScalar("root_value", out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = ShellValue.Empty;
        var foundEveryTime = true;
        for (var iteration = 0; iteration < 10_000; iteration++)
            foundEveryTime &= variables.TryGetScalar("root_value", out value);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(foundEveryTime);
        Assert.Equal(expected, value);
        Assert.True(
            allocated <= 1_024,
            $"Named lookup allocated {allocated:N0} bytes after repeated scope copies.");
    }

    [Fact]
    public void NamedLookupAllocationDoesNotScaleWithBindingsInOneFrame()
    {
        var narrow = CreateFrame(64);
        var wide = CreateFrame(65_536);
        Lookup(narrow, 1);
        Lookup(wide, 1);

        var narrowAllocation = MeasureAllocation(() => Lookup(narrow, 10_000));
        var wideAllocation = MeasureAllocation(() => Lookup(wide, 10_000));

        Assert.True(
            wideAllocation <= narrowAllocation + 1_024,
            $"Named lookup allocated {narrowAllocation:N0} bytes with 64 bindings and "
            + $"{wideAllocation:N0} bytes with 65,536 bindings.");

        static Variables CreateFrame(int bindingCount)
        {
            var frame = Variables.CreateEmpty();
            for (var index = 0; index < bindingCount; index++)
                Assert.True(frame.TrySetScalar($"value_{index:D5}", ShellValue.Empty));
            Assert.True(frame.TrySetScalar("target", ShellValue.Empty));
            return frame;
        }

        static void Lookup(Variables frame, int repetitions)
        {
            for (var index = 0; index < repetitions; index++)
            {
                if (!frame.TryGetScalar("target", out _))
                    throw new InvalidOperationException("Expected target binding was absent.");
            }
        }

        static long MeasureAllocation(Action action)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact]
    public void ScalarVariableCloneAllocationIsLinearInVisibleBindings()
    {
        var variables = Variables.CreateEmpty();
        for (var index = 0; index < 2_000; index++)
            Assert.True(variables.TrySetScalar($"value_{index:D4}", ShellValue.FromText(index.ToString())));

        _ = variables.Clone();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = variables.Clone();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2_000, snapshot.Scalars.Count);
        Assert.True(
            allocated < 8 * 1024 * 1024,
            $"A 2,000-binding clone allocated {allocated:N0} bytes; expected linear detached storage.");
    }

    [Fact]
    public void ExportedEnvironmentAllocationIsLinearInExportedBindings()
    {
        var variables = Variables.CreateEmpty();
        for (var index = 0; index < 2_000; index++)
        {
            var name = $"VALUE_{index:D4}";
            Assert.True(variables.TrySetScalar(name, ShellValue.FromText(index.ToString())));
            variables.SetExported(name, true);
        }

        _ = variables.BuildExportedEnvironment();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var environment = variables.BuildExportedEnvironment();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2_000, environment.Count);
        Assert.True(
            allocated < 4 * 1024 * 1024,
            $"A 2,000-entry child environment allocated {allocated:N0} bytes; expected one linear projection.");
    }

    [Fact]
    public void ExportedEnvironmentDoesNotRetainNonExportedSlotProjection()
    {
        var variables = Variables.CreateEmpty();
        for (var index = 0; index < 4_000; index++)
            Assert.True(variables.TrySetScalar($"hidden_{index:D4}", ShellValue.FromText(index.ToString())));
        Assert.True(variables.TrySetScalar("VISIBLE", ShellValue.FromText("yes")));
        variables.SetExported("VISIBLE", true);

        _ = variables.BuildExportedEnvironment();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var environment = variables.BuildExportedEnvironment();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new EnvironmentVariable("VISIBLE", ShellValue.FromText("yes")), Assert.Single(environment));
        Assert.True(
            allocated < 1024 * 1024,
            $"One exported value behind 4,000 non-exported names allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void VariableNameInspectionDoesNotCopyArrayElements()
    {
        var variables = Variables.CreateEmpty();
        var value = ShellValue.FromText("value");
        for (var index = 0; index < 100_000; index++)
        {
            Assert.True(IndexedArrayIndex.TryCreate(index, out var position));
            Assert.True(variables.TrySetArrayElement("large", position, value));
        }

        _ = InspectNames();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var output = InspectNames();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("declare -a large\n", Utf8Text.Decode(output));
        Assert.True(
            allocated < 256 * 1024,
            $"Summarizing one large array allocated {allocated:N0} bytes; array elements must not be copied.");

        ReadOnlyMemory<byte> InspectNames()
        {
            const long MemoryBudgetBytes = 4096;
            using var output = new ByteMemoryBuilder(128, new MemoryGovernor(MemoryBudgetBytes));
            foreach (var name in variables.BindingNames.Distinct())
                Assert.True(variables.TryAppendVariable(name, output, VariableDisplayStyle.DeclarationName));
            return output.DetachSnapshot();
        }
    }
}

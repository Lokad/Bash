using Lokad.Bash.Memory;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
    [Fact]
    public void WholeVariableUnsetAllocatesOnlyOneReferenceWalk()
    {
        const int repetitions = 64;
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetNameref("first", ShellValue.FromText("second")));
        Assert.True(variables.TrySetNameref("second", ShellValue.FromText("missing")));
        Assert.True(variables.TryResolveBinding("first", selectOwner: null, out _));
        Assert.True(variables.TryUnset("first"));

        var resolved = true;
        var beforeLookup = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < repetitions; index++)
            resolved &= variables.TryResolveBinding("first", selectOwner: null, out _);
        var lookupBytes = GC.GetAllocatedBytesForCurrentThread() - beforeLookup;

        var unset = true;
        var beforeUnset = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < repetitions; index++)
            unset &= variables.TryUnset("first");
        var unsetBytes = GC.GetAllocatedBytesForCurrentThread() - beforeUnset;

        Assert.True(resolved);
        Assert.True(unset);
        Assert.True(variables.TryGetNameref("first", out _));
        Assert.True(unsetBytes <= lookupBytes + 1_024,
            $"Unset allocated {unsetBytes:N0} bytes for the same reference walks that used {lookupBytes:N0} bytes.");
    }

    [Fact]
    public async Task ExplicitFunctionUnsetDoesNotAdmitVariableNameCopies()
    {
        const int operandCount = 64;
        const int nameBytes = 512;
        var operands = string.Join(' ', Enumerable.Repeat(new string('q', nameBytes), operandCount));

        var functionPeak = await PeakAllowanceAsync("-fn");
        var variablePeak = await PeakAllowanceAsync("-vn");

        Assert.True(variablePeak - functionPeak >= operandCount * nameBytes,
            $"Function unset reserved {functionPeak:N0} bytes and variable unset reserved {variablePeak:N0} bytes.");

        async Task<long> PeakAllowanceAsync(string options)
        {
            var parsed = Parser.Parse("spare(){ :; }; unset " + options + " " + operands);
            var governor = new MemoryGovernor(4 * 1024 * 1024);
            using var owner = governor.CreateScope();
            var evaluation = new Eval(parsed);
            evaluation.Memory.Budget = owner;

            Assert.Null(await evaluation.GetNextStep());
            Assert.Equal(0, evaluation.LastExitCode);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return governor.PeakBytes;
        }
    }

    [Fact]
    public async Task GetoptsDoesNotDecodeStoredOptindOnEveryCall()
    {
        const int calls = 32;
        // A readonly binding leaves this large decimal visible while the scan advances.
        var script = Parser.Parse("declare +i OPTIND; printf -v OPTIND '%0262144d' 1; readonly OPTIND; "
            + string.Concat(Enumerable.Repeat("getopts a opt -a; ", calls)));
        var state = new Eval(script);
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        for (var index = 0; index < calls; index++)
            host.EnqueueAppendResult(DescriptorWriteResult.Open);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await new Engine().RunAsync(state, host);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("?", state.Variables["opt"].ToUtf8String());
        Assert.Equal(256 * 1024, state.Variables["OPTIND"].Bytes.Length);
        Assert.Equal(calls, host.Appends.Count);
        Assert.All(host.Appends, diagnostic =>
        {
            Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
            Assert.Equal("line 1: OPTIND: readonly variable\n", Utf8Text.Decode(diagnostic.Content));
        });
        Assert.True(allocated < 4 * 1024 * 1024,
            $"Repeated getopts calls allocated {allocated:N0} bytes for an unchanged OPTIND.");
    }

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
        var value = ShellValue.Empty;
        var foundEveryTime = true;
        AllocationAssert.SustainedReuse(() => foundEveryTime &= variables.TryGetScalar("root_value", out value),
            iterations: 10_000, maximumBytes: 1_024);

        Assert.True(foundEveryTime);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void NamedLookupAllocationDoesNotScaleWithBindingsInOneFrame()
    {
        const int lookupsPerSample = 10_000;
        const int maximumAdditionalBytes = 1_024;
        var narrow = CreateFrame(64);
        var wide = CreateFrame(65_536);
        Lookup(narrow);
        Lookup(wide);

        var narrowAllocation = AllocationAssert.MeasureAllocatedBytes(() => Lookup(narrow), lookupsPerSample);
        var wideAllocation = AllocationAssert.MeasureAllocatedBytes(() => Lookup(wide), lookupsPerSample);

        Assert.True(
            wideAllocation.Median <= narrowAllocation.Median + maximumAdditionalBytes,
            $"Named lookup allocated {narrowAllocation.Minimum:N0}, {narrowAllocation.Median:N0}, and "
            + $"{narrowAllocation.Maximum:N0} bytes with 64 bindings and {wideAllocation.Minimum:N0}, "
            + $"{wideAllocation.Median:N0}, and {wideAllocation.Maximum:N0} bytes with 65,536 bindings "
            + $"over {lookupsPerSample:N0} lookups per sample. "
            + $"The median allowance for growth is {maximumAdditionalBytes:N0} bytes.");

        static Variables CreateFrame(int bindingCount)
        {
            var frame = Variables.CreateEmpty();
            for (var index = 0; index < bindingCount; index++)
                Assert.True(frame.TrySetScalar($"value_{index:D5}", ShellValue.Empty));
            Assert.True(frame.TrySetScalar("target", ShellValue.Empty));
            return frame;
        }

        static void Lookup(Variables frame)
        {
            if (!frame.TryGetScalar("target", out _))
                throw new InvalidOperationException("Expected target binding was absent.");
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
    public void StandaloneExportSnapshotAllocatesOnlyOutputAndFixedOverhead()
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
        var entries = Assert.IsType<List<EnvironmentVariable>>(environment);
        var outputBytes = (long)entries.Capacity * Unsafe.SizeOf<EnvironmentVariable>();
        // The list's geometric growth uses less than twice its final backing, plus headers.
        Assert.True(
            allocated <= 2 * outputBytes + 4096,
            $"A 2,000-entry child environment allocated {allocated:N0} bytes for {outputBytes:N0} bytes of final backing.");
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
    public void LocalInspectionRendersArrayWithoutDetachingItsStorage()
    {
        const int outputBytes = 128 * 1024;
        var variables = Variables.CreateEmpty();
        var values = Enumerable.Repeat(ShellValue.FromText("value"), 4096).ToArray();
        Assert.True(variables.TryAssignArray("large", values, ArrayKind.Indexed));
        var governor = new MemoryGovernor(outputBytes + 64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var command = Variables.CreateCommandScope(variables, memory, CancellationToken.None);
            using var output = new ByteMemoryBuilder(outputBytes, governor);
            Assert.True(command.TryAppendVariable("large", output, VariableDisplayStyle.DeclarationWithoutScalarValues));
            Assert.True(output.ToMemory().Span.StartsWith("declare -a large=([0]=\"value\" "u8));
            Assert.True(output.ToMemory().Span.EndsWith(" [4095]=\"value\")\n"u8));
            Assert.Equal(outputBytes, output.Capacity);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }
}

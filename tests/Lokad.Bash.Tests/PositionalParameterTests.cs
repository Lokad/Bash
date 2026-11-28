using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class PositionalParameterTests
{
    [Fact]
    public void LiveViewFollowsShiftsAndOwnedReplacements()
    {
        var variables = Variables.CreateEmpty();
        var view = variables.PositionalParameters;
        var values = new[] { ShellValue.FromText("drop"), ShellValue.FromBytes(new byte[] { 0xff }), ShellValue.Empty };
        variables.PositionalParameters = values;
        values[1] = ShellValue.FromText("changed by caller");

        Assert.True(variables.TryShiftPositionalParameters(1));
        Assert.Equal(new byte[] { 0xff }, view[0].Bytes.ToArray());
        Assert.Equal(ShellValue.Empty, view[1]);
        Assert.False(variables.TryShiftPositionalParameters(3));
        Assert.Equal(2, view.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[2]);

        // A live view may itself supply replacement values; copy before changing the active frame.
        variables.PositionalParameters = view;
        Assert.Equal(new byte[] { 0xff }, view[0].Bytes.ToArray());
        Assert.True(variables.TryShiftPositionalParameters(2));
        Assert.Empty(view);
        Assert.True(variables.TryShiftPositionalParameters(0));
        variables.PositionalParameters = [ShellValue.FromText("replacement")];
        Assert.Equal("replacement", Assert.Single(view).ToUtf8String());
    }

    [Fact]
    public void CloneAfterShiftOwnsItsEntriesAndSharesImmutableBytes()
    {
        var variables = Variables.CreateEmpty();
        var retained = ShellValue.FromBytes(new byte[] { 0xff, 0xc3 });
        variables.PositionalParameters = [ShellValue.Empty, retained, ShellValue.FromText("last")];
        Assert.True(variables.TryShiftPositionalParameters(1));

        var clone = variables.Clone();
        Assert.True(retained.Bytes.Equals(clone.PositionalParameters[0].Bytes));
        Assert.True(clone.TryShiftPositionalParameters(1));
        Assert.Equal(2, variables.PositionalParameters.Count);
        Assert.Equal(retained, variables.PositionalParameters[0]);
        Assert.Equal("last", Assert.Single(clone.PositionalParameters).ToUtf8String());
        variables.PositionalParameters = [];
        Assert.Single(clone.PositionalParameters);
    }

    [Fact]
    public void RefusedCallerArgumentSnapshotLeavesBothViewsPendingForARetry()
    {
        var variables = Variables.CreateEmpty();
        variables.InitializeCallerArrays();
        variables.PositionalParameters = [ShellValue.FromText("first"), ShellValue.FromText("retained")];
        var counts = variables.IndexedArrays["BASH_ARGC"];
        var arguments = variables.IndexedArrays["BASH_ARGV"];
        var governor = new MemoryGovernor(1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        using (governor.Reserve(governor.LimitBytes))
            Assert.Throws<MemoryBudgetExceededException>(() => variables.InitializeCallerArgumentSnapshot("BASH_ARGV", memory));
        Assert.Empty(counts.PresentValues);
        Assert.Empty(arguments.PresentValues);
        Assert.Equal(0, governor.ReservedBytes);

        Assert.True(variables.TryShiftPositionalParameters(1));
        variables.InitializeCallerArgumentSnapshot("BASH_ARGC", memory);
        Assert.Equal(ShellValue.FromByte((byte)'1'), Assert.Single(counts.PresentValues));
        Assert.Equal(ShellValue.FromText("retained"), Assert.Single(arguments.PresentValues));
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void NestedScopesRestoreCallerWindowsAndIgnoreFunctionReplacements()
    {
        var variables = Variables.CreateEmpty();
        variables.PositionalParameters = [ShellValue.FromText("drop"), ShellValue.FromText("outer"), ShellValue.Empty];
        Assert.True(variables.TryShiftPositionalParameters(1));
        var view = variables.PositionalParameters;
        var source = variables.EnterSourcePositionals(
            [ShellValue.FromText("first"), ShellValue.FromText("source")], 0, PositionalParametersRestorePolicy.UnlessReplaced);
        Assert.True(variables.TryShiftPositionalParameters(1));
        using (variables.EnterFunctionPositionals([ShellValue.FromText("function")]))
        {
            variables.ReplacePositionalParametersFromSet([ShellValue.FromText("explicit replacement")], 0, inFunction: true);
            Assert.True(variables.TryShiftPositionalParameters(1));
        }
        Assert.Equal("source", Assert.Single(view).ToUtf8String());
        source.Dispose();
        Assert.Equal(new[] { ShellValue.FromText("outer"), ShellValue.Empty }, view);
        variables.PositionalParameters = [ShellValue.FromText("after")];
        source.Dispose();
        Assert.Equal("after", Assert.Single(view).ToUtf8String());
    }

    [Fact]
    public void SourceReplacementPersistsEvenWhenValuesAreUnchangedOrShiftedAway()
    {
        var variables = Variables.CreateEmpty();
        variables.PositionalParameters = [ShellValue.FromText("outer")];
        using (variables.EnterSourcePositionals([ShellValue.FromText("inner")], 0, PositionalParametersRestorePolicy.UnlessReplaced))
        {
            variables.ReplacePositionalParametersFromSet(variables.PositionalParameters, 0, inFunction: false);
            Assert.True(variables.TryShiftPositionalParameters(1));
        }
        Assert.Empty(variables.PositionalParameters);
    }

    [Fact]
    public void RepeatedShiftPreservesEverySurvivingArgument()
    {
        var arguments = Enumerable.Range(0, 32_768).Select(index => ShellValue.FromText(index.ToString())).ToArray();
        var variables = Variables.CreateEmpty();
        variables.PositionalParameters = arguments;
        var view = variables.PositionalParameters;
        for (var index = 0; index < arguments.Length; index++)
        {
            Assert.Equal(arguments.Length - index, view.Count);
            Assert.Equal(arguments[index], view[0]);
            Assert.True(variables.TryShiftPositionalParameters(1));
        }
        Assert.Empty(view);
        Assert.False(variables.TryShiftPositionalParameters(1));
    }
}

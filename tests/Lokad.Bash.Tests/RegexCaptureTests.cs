using System.Runtime.InteropServices;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class RegexCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyCapturePublicationReusesOnlyTheSelectedOwnersEmptyArray(bool useCommandScope)
    {
        var caller = Variables.CreateEmpty();
        var name = Variables.RegexCaptureName;
        if (useCommandScope)
            caller.ClearRegexCaptures();
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var owner = useCommandScope ? Variables.CreateCommandScope(caller, memory, CancellationToken.None) : caller;

            owner.ClearRegexCaptures();

            Assert.False(owner.IsBorrowedBinding(name));
            var first = owner.IndexedArrays[name];
            Assert.True(first.IsInitialized);
            Assert.Empty(first.PresentValues);
            if (useCommandScope)
                Assert.NotSame(caller.IndexedArrays[name], first);
            var bytes = owner.EstimatedMemoryBytes;

            owner.ClearRegexCaptures();

            Assert.Same(first, owner.IndexedArrays[name]);
            Assert.Equal(bytes, owner.EstimatedMemoryBytes);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void EmptyCapturePublicationReleasesPreviouslyUsedArrayCapacity()
    {
        var variables = Variables.CreateEmpty();
        var name = Variables.RegexCaptureName;
        variables.SetRegexCaptures(IndexedArray.FromSequential([ShellValue.FromText("match")]));
        var previous = variables.IndexedArrays[name];
        using (variables.TrackBindingMemory(name))
            previous.Remove(default);
        Assert.Empty(previous.PresentValues);
        Assert.True(previous.EstimatedMemoryBytes > 0);
        var before = variables.EstimatedMemoryBytes;

        variables.ClearRegexCaptures();

        var current = variables.IndexedArrays[name];
        Assert.NotSame(previous, current);
        Assert.True(current.IsInitialized);
        Assert.Empty(current.PresentValues);
        Assert.Equal(before - previous.EstimatedMemoryBytes, variables.EstimatedMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturePublicationReplacesTheSelectedSlotAndItsAttributes(bool useCommandScope)
    {
        var caller = Variables.CreateEmpty();
        var name = Variables.RegexCaptureName;
        var outside = ShellValue.FromText("outside");
        Assert.True(caller.TrySetScalar("target", outside));
        Assert.True(caller.TrySetNameref(name, ShellValue.FromText("target")));
        caller.SetExported(name, true);
        caller.SetReadonlyBinding(name);
        var previous = caller.EstimateBindingBytes(name);
        var before = caller.EstimatedMemoryBytes;
        var captures = new[] { ShellValue.FromText("ab"), ShellValue.FromText("a"), ShellValue.Empty };
        var replacement = Variables.CreateEmpty();
        Assert.True(replacement.TryAssignArray(name, captures, ArrayKind.Indexed));
        var expected = before + replacement.EstimateBindingBytes(name) - (useCommandScope ? 0 : previous);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var owner = useCommandScope ? Variables.CreateCommandScope(caller, memory, CancellationToken.None) : caller;

            owner.SetRegexCaptures(IndexedArray.FromSequential(captures));

            Assert.Equal<ShellValue>(captures, owner.IndexedArrays[name].PresentValues);
            Assert.DoesNotContain(name, owner.ExportedNames);
            Assert.False(owner.IsReadonlyBinding(name));
            Assert.False(owner.TryGetNameref(name, out _));
            Assert.Equal(expected, owner.EstimatedMemoryBytes);
            Assert.True(caller.TryGetScalar("target", out var retained));
            Assert.Equal(outside, retained);
            if (useCommandScope)
            {
                Assert.Contains(name, caller.ExportedNames);
                Assert.True(caller.IsReadonlyBinding(name));
                Assert.True(caller.TryGetNameref(name, out var reference));
                Assert.Equal(ShellValue.FromText("target"), reference);
            }
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, 136 * 1024)]
    [InlineData(true, 47 * 1024)]
    public async Task PatternPreparationFitsWithoutUnusedScratch(bool quotedAndClass, int allowance)
    {
        var source = quotedAndClass
            ? "set -- '" + new string('a', 1012) + ".' '[[:alpha:]]'; [[ ! % =~ \"$1\"$2 ]]"
            : "set -- '" + new string('a', 4096) + "'; [[ ! % =~ $1 ]]";
        var evaluation = new Eval(Parser.Parse(source));
        var governor = new MemoryGovernor(allowance);
        await using var host = new TestHost { MemoryBudget = governor };
        using var owner = governor.CreateScope();

        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(host.StandardError.Content.IsEmpty);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(64 * 1024, false, false)]
    [InlineData(2 * 1024 * 1024, true, false)]
    [InlineData(64 * 1024, false, true)]
    [InlineData(2 * 1024 * 1024, true, true)]
    public async Task CaptureOwnershipIsAdmittedBeforeReplacingEarlierResults(long limit, bool fits, bool partialUnicode)
    {
        var governor = new MemoryGovernor(limit);
        await using var host = new TestHost { MemoryBudget = governor };
        var value = partialUnicode ? new string('é', 512) : new string('a', 1024);
        var subject = partialUnicode ? "x" + value + "y" : value;
        var pattern = new string('(', 100) + (partialUnicode ? "é+" : "a+") + new string(')', 100);
        var script = "[[ old =~ (o) ]]; value='" + subject + "'; [[ $value =~ " + pattern + " ]]; "
            + "printf '%s:%s:<%s>' \"$?\" \"${#BASH_REMATCH[@]}\" \"${BASH_REMATCH[1]}\"";
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(fits ? "0:101:<" + value + ">" : "1:2:<o>", host.StandardOutput.TextContent);
        if (fits) Assert.Empty(host.StandardError.Content.ToArray());
        else Assert.Contains("memory", host.StandardError.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CancellationDuringCaptureAdmissionKeepsEarlierResults()
    {
        const int Groups = 256;
        var pattern = new string('(', Groups) + "x" + new string(')', Groups);
        var evaluation = new Eval(Parser.Parse($"[[ old =~ (o) ]]; [[ x =~ {pattern} ]]"));
        var governor = new MemoryGovernor(1024 * 1024);
        var budget = new CountingMemoryBudget(governor);
        using var cancellation = new CancellationTokenSource();
        await using var host = new TestHost { MemoryBudget = budget };
        using var owner = budget.CreateScope();
        budget.AdmissionSucceeded += () =>
        {
            // Only the second match's capture admission reaches this threshold.
            if (governor.ReservedBytes >= 64 * 1024)
                cancellation.Cancel();
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Engine().RunAsync(evaluation, host, owner, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal<ShellValue>([ShellValue.FromText("o"), ShellValue.FromText("o")],
            evaluation.IndexedArrays[Variables.RegexCaptureName]);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("[[ '😀x' =~ (😀)(.) ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"", "<😀x><😀><x>")]
    [InlineData("[[ 'é😀漢' =~ (((é)(😀))(漢)) ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"",
        "<é😀漢><é😀漢><é😀><é><😀><漢>")]
    [InlineData("prefix='\\'; tail='([[:digit:]]+)'; [[ '\\42' =~ \"$prefix\"$tail ]]; printf '<%s>' \"${BASH_REMATCH[@]}\"", "<\\42><42>")]
    public async Task CapturesPreserveSupplementaryValuesAndQuotedPatternBoundaries(string script, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("prefix", "bb")]
    [InlineData("é😀", "é😀")]
    public async Task CapturesKeepCompactBytesAtNonzeroOffsets(string prefix, string captured)
    {
        var source = $"subject=$'{prefix}:{captured}\\rtail'; pattern=$':([^:]+)(z)?(\\r)()'; [[ $subject =~ $pattern ]]";
        var evaluation = new Eval(Parser.Parse(source));
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        await using var host = new TestHost { MemoryBudget = governor };
        using var owner = governor.CreateScope();

        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(host.StandardError.Content.IsEmpty);
        var values = evaluation.IndexedArrays["BASH_REMATCH"];
        Assert.Equal<ShellValue>(
            [ShellValue.FromText($":{captured}\r"), ShellValue.FromText(captured), ShellValue.Empty,
                ShellValue.FromText("\r"), ShellValue.Empty], values);
        foreach (var value in values.Where(value => !value.IsEmpty))
        {
            Assert.True(value.TryGetCompactArrayCapacity(out var capacity));
            Assert.Equal(value.Bytes.Length, capacity);
        }
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RepeatedPartialCapturesKeepIndependentCompactBacking()
    {
        var evaluation = new Eval(Parser.Parse("[[ 'xé😀漢y' =~ ((é😀漢))(z)?() ]]"));
        await using var host = new TestHost();
        using var owner = host.MemoryBudget.CreateScope();

        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(host.StandardError.Content.IsEmpty);
        var captures = evaluation.IndexedArrays["BASH_REMATCH"];
        var expected = ShellValue.FromText("é😀漢");
        Assert.Equal<ShellValue>([expected, expected, expected, ShellValue.Empty, ShellValue.Empty], captures);
        var backing = new HashSet<byte[]>();
        foreach (var capture in captures.Take(3))
        {
            Assert.True(MemoryMarshal.TryGetArray(capture.Bytes, out var slice));
            Assert.NotNull(slice.Array);
            Assert.Equal(expected.Bytes.Length, slice.Array.Length);
            Assert.True(backing.Add(slice.Array));
        }
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public async Task DistinctUnicodeSpansSurviveAcrossTheLengthCacheBoundary(int groups)
    {
        var values = Enumerable.Range(0, groups).Select(index => index % 2 == 0 ? "é" : "漢").ToArray();
        var subject = "x" + string.Concat(values) + "y";
        var expression = "^x" + string.Concat(values.Select(value => "(" + value + ")")) + "y$";
        var evaluation = new Eval(Parser.Parse($"pattern='{expression}'; [[ '{subject}' =~ $pattern ]]"));
        await using var host = new TestHost();
        using var owner = host.MemoryBudget.CreateScope();

        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal<ShellValue>(values.Prepend(subject).Select(ShellValue.FromText), evaluation.IndexedArrays["BASH_REMATCH"]);
    }

    [Theory]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[:unknown:]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[:ascii:]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[:word:]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ x =~ [[.x.]] ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ '😀' =~ . ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    [InlineData("[[ abc =~ (a) ]]; [[ 'é😀' =~ ^(é)(.)(.)$ ]]; printf '%s:<%s>' \"$?\" \"${BASH_REMATCH[*]}\"")]
    public async Task UnsupportedOrInvalidMatchingPreservesPriorCaptures(string script)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2:<a a>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("alnum", "a1", "!")]
    [InlineData("alpha", "aZ", "1")]
    [InlineData("blank", " \t", "\n")]
    [InlineData("cntrl", "\r\n", " ")]
    [InlineData("digit", "19", "a")]
    [InlineData("graph", "a!", " ")]
    [InlineData("lower", "az", "A")]
    [InlineData("print", " a!", "\n")]
    [InlineData("punct", "![]", "a")]
    [InlineData("space", " \t\n", "a")]
    [InlineData("upper", "AZ", "a")]
    [InlineData("xdigit", "09aF", "g")]
    public async Task PosixClassNamesUseDeclaredCCharacterMembership(string name, string yes, string no)
    {
        await using var host = new TestHost();
        var script = "yes='" + yes + "'; no='" + no + "'; pattern='^[[:" + name + ":]]+$'; "
            + "[[ $yes =~ $pattern ]]; printf '%s:' \"$?\"; [[ $no =~ $pattern ]]; printf '%s' \"$?\"";
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0:1", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }
}

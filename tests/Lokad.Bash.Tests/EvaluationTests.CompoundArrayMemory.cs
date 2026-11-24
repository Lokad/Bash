using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("direct", 8, false)]
    [InlineData("direct", 2048, true)]
    [InlineData("declared", 8, false)]
    [InlineData("declared", 2048, true)]
    [InlineData("pair", 8, false)]
    [InlineData("pair", 2048, false)]
    [InlineData("pair", 2048, true)]
    public async Task CompoundArrayMemory_AdmitsRhsItemsBeforeReplacement(string form, int count, bool refused)
    {
        var pair = form == "pair";
        var setup = pair ? "declare -A a=([old]=kept); a=(" : form == "declared" ? "a=(kept); declare -a a=(" : "a=(kept); a=(";
        var early = pair ? "'key' \"${early:=first}\"" : "\"${early:=first}\"";
        var operands = string.Join(' ', pair && refused
            ? Enumerable.Range(0, count).Select(index => $"'key{index}' 'value'")
            : Enumerable.Repeat(pair ? "'key' 'value'" : "'value'", count));
        var late = pair ? "'tail' \"${late:=last}\"" : "\"${late:=last}\"";
        var state = new Eval(Parser.Parse($"{setup}{early} {operands} {late})"));
        var budget = new MemoryGovernor(32 * 1024);
        using var owner = budget.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal("first", GetVariable(state, "early"));
        Assert.Equal(!refused, state.Variables.ContainsKey("late"));
        Assert.Equal(refused ? 1 : 0, state.LastExitCode);
        if (pair)
        {
            var array = state.AssociativeArrays["a"];
            if (refused)
            {
                var entry = Assert.Single(array);
                Assert.Equal(ShellValue.FromText("old"), entry.Key);
                Assert.Equal(ShellValue.FromText("kept"), entry.Value);
            }
            else
            {
                Assert.Equal(2, array.Count);
                Assert.Equal(ShellValue.FromText("value"), array[ShellValue.FromText("key")]);
                Assert.Equal(ShellValue.FromText("last"), array[ShellValue.FromText("tail")]);
            }
        }
        else
        {
            var array = state.IndexedArrays["a"];
            if (refused)
                Assert.Equal(ShellValue.FromText("kept"), Assert.Single(array));
            else
            {
                Assert.Equal(count + 2, array.Count);
                Assert.Equal(ShellValue.FromText("first"), array[0]);
                Assert.Equal(ShellValue.FromText("last"), array[^1]);
            }
        }

        if (refused)
        {
            var diagnostic = Assert.Single(host.Appends);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
            Assert.Equal("shell: shared memory budget exhausted\n", Text(diagnostic.Content));
        }
        else
            Assert.Empty(host.Appends);
    }

    [Fact]
    public async Task CompoundArrayMemory_ReusesPendingItemsWhenANamerefChangesTheArrayKind()
    {
        var pairs = string.Join(' ', Enumerable.Repeat("'key' 'value'", 255));
        var script = $"declare -A a=([old]=kept); declare -n r; declare r=(\"${{r:=a}}\" first {pairs})";
        var state = new Eval(Parser.Parse(script));
        var budget = new MemoryGovernor(96 * 1024);
        using var owner = budget.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(0, state.LastExitCode);
        Assert.Empty(host.Appends);
        var array = state.AssociativeArrays["a"];
        Assert.Equal(2, array.Count);
        Assert.Equal(ShellValue.FromText("first"), array[ShellValue.FromText("a")]);
        Assert.Equal(ShellValue.FromText("value"), array[ShellValue.FromText("key")]);
    }

    [Fact]
    public async Task CompoundArrayMemory_HoldsEarlierItemsThroughALaterLookupAndRefundsOnFailure()
    {
        var narrow = await ObserveAsync(8);
        var wide = await ObserveAsync(512);
        Assert.True(wide.Pending - narrow.Pending >= MemoryCapacity.ArrayBackingBytes<ShellValue>(512 - 8),
            $"Expected earlier items to be charged at the lookup. Narrow: {narrow}, wide: {wide}.");
        Assert.Equal(narrow.Settled, wide.Settled);

        static async Task<(long Pending, long Settled)> ObserveAsync(int count)
        {
            var operands = string.Join(' ', Enumerable.Repeat("'value'", count));
            var state = new Eval(Parser.Parse($"a=(kept); a=({operands} gate* \"${{missing:?stop}}\")"));
            var budget = new MemoryGovernor(512 * 1024);
            using var owner = budget.CreateScope();
            state.Memory.Budget = owner;

            var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
            var pending = budget.ReservedBytes;
            listing.SetResult(DirectoryListingResult.Success([]));
            var diagnostic = await NextWriteAsync(state);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Equal("line 1: missing: stop\n", Text(diagnostic.Content));
            diagnostic.SetResult(DescriptorWriteResult.Open);

            Assert.Null(await NextStepAsync(state));
            Assert.Equal(ShellValue.FromText("kept"), Assert.Single(state.IndexedArrays["a"]));
            return (pending, budget.ReservedBytes);
        }
    }
}

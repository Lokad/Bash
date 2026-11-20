using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
    [Theory]
    [InlineData("true", 0)]
    [InlineData("-x", 2)]
    public async Task WrapperDoesNotCopyUnexpandedOperandTail(string target, int status)
    {
        var narrow = await MeasureAsync(64);
        var wide = await MeasureAsync(4096);

        Assert.Equal(narrow, wide);

        async Task<long> MeasureAsync(int count)
        {
            var source = $"command '{target}' first/*"
                + string.Concat(Enumerable.Repeat(" 'unused'", count)) + " last/*";
            var state = new Evaluation.Evaluation(Parser.Parse(source));
            var governor = new MemoryGovernor(8 * 1024 * 1024);
            using var owner = governor.CreateScope();
            state.Memory.Budget = owner;

            var first = Assert.IsType<ListDirectoryOperation>(await state.GetNextStep());
            Assert.Equal("/first", first.Path.Path);
            var workspace = state.Memory.Workspace.Bytes;
            first.SetResult(DirectoryListingResult.Success([]));

            var last = Assert.IsType<ListDirectoryOperation>(await state.GetNextStep());
            Assert.Equal("/last", last.Path.Path);
            last.SetResult(DirectoryListingResult.Success([]));

            if (status == 2)
            {
                var diagnostic = Assert.IsType<WriteOperation>(await state.GetNextStep());
                Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
                Assert.True(diagnostic.Content.Span.StartsWith("command: -x: invalid option\n"u8));
                diagnostic.SetResult(DescriptorWriteResult.Open);
            }
            Assert.Null(await state.GetNextStep());
            Assert.Equal(status, state.LastExitCode);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return workspace;
        }
    }

    [Theory]
    [InlineData("command", "-p")]
    [InlineData("exec", "-c")]
    public void WrapperOptionAllocationScalesWithFieldCount(string command, string option)
    {
        _ = Measure(16);
        var smaller = Measure(1000);
        var larger = Measure(2000);

        Assert.True(larger <= smaller * 3 + 64 * 1024,
            $"Doubling option fields allocated {smaller:N0} then {larger:N0} bytes.");

        long Measure(int count)
        {
            var state = new Evaluation.Evaluation(Parser.Parse(
                command + " " + string.Concat(Enumerable.Repeat(option + " ", count)) + "true"));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var next = state.GetNextStep();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // No host operations or continuations: thread-local accounting excludes other tests' allocations.
            Assert.True(next.IsCompletedSuccessfully);
            Assert.Null(next.Result);
            Assert.Equal(0, state.LastExitCode);
            return allocated;
        }
    }
}

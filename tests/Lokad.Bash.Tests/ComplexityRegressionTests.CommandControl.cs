using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
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

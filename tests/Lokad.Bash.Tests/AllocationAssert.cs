namespace Lokad.Bash.Tests;

internal static class AllocationAssert
{
    internal static void SustainedReuse(Action action, int iterations, long maximumBytes)
    {
        var (minimum, median, maximum) = MeasureAllocatedBytes(action, iterations);
        Assert.True(median <= maximumBytes,
            $"Synchronous reuse allocated {minimum:N0}, {median:N0}, and {maximum:N0} bytes over "
            + $"{iterations:N0} iterations per sample. The median limit is {maximumBytes:N0} bytes.");
    }

    internal static (long Minimum, long Median, long Maximum) MeasureAllocatedBytes(Action action, int iterations)
    {
        // A single noisy sample should not determine the result of a synchronous reuse check.
        Span<long> samples = stackalloc long[3];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < iterations; iteration++)
                action();
            samples[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        samples.Sort();
        return (samples[0], samples[1], samples[2]);
    }
}

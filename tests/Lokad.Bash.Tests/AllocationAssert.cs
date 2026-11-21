namespace Lokad.Bash.Tests;

internal static class AllocationAssert
{
    internal static void SustainedReuse(Action action, int iterations, long maximumBytes)
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
        Assert.True(samples[1] <= maximumBytes,
            $"Synchronous reuse allocated {samples[0]:N0}, {samples[1]:N0}, and {samples[2]:N0} bytes over "
            + $"{iterations:N0} iterations per sample. The median limit is {maximumBytes:N0} bytes.");
    }
}

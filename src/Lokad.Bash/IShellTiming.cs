using System;

namespace Lokad.Bash;

/// <summary>Host-owned clocks and CPU accounting for shell evaluations.</summary>
public interface IShellTiming
{
    /// <summary>
    /// Enters an evaluation's async execution context. Nested evaluations contribute to
    /// their parents; concurrently executing siblings retain independent CPU counters.
    /// Dispose in the entering context after evaluation and descriptor cleanup complete.
    /// </summary>
    IDisposable BeginEvaluation();

    /// <summary>
    /// Reads a monotonic timestamp and cumulative user/system CPU time for the current
    /// evaluation and its descendants. Two reads delimit one timed command.
    /// Implementations may start CPU collection at the first read.
    /// </summary>
    ShellTimingSnapshot Read();
}

/// <summary>Monotonic wall time and cumulative CPU time, with arbitrary starting values.</summary>
public readonly record struct ShellTimingSnapshot(TimeSpan Real, TimeSpan User, TimeSpan System)
{
    /// <summary>Subtracts an earlier measurement taken in the same evaluation.</summary>
    public static ShellTimingSnapshot operator -(ShellTimingSnapshot end, ShellTimingSnapshot start) =>
        new(end.Real - start.Real, end.User - start.User, end.System - start.System);
}

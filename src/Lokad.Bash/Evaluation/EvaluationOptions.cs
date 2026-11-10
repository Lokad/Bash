using System;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Options controlling evaluation behavior.
/// </summary>
public sealed record EvaluationOptions(
    int Verbosity,
    ShellPath CurrentDirectory,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Creates deterministic evaluation options using the Unix epoch for operations that expose the current time.
    /// </summary>
    public EvaluationOptions(int Verbosity, ShellPath CurrentDirectory)
        : this(Verbosity, CurrentDirectory, DateTimeOffset.UnixEpoch)
    {
    }

    /// <summary> Default evaluation options. </summary>
    public static readonly EvaluationOptions Default = new(0, ShellPath.Root, DateTimeOffset.UnixEpoch);
}

using System.Diagnostics;
namespace Lokad.Bash.Tests;
internal sealed class TestTiming : IShellTiming
{
    public static TestTiming Instance { get; } = new();
    public IDisposable BeginEvaluation() => new Scope();
    public ShellTimingSnapshot Read() => new(Stopwatch.GetElapsedTime(0), TimeSpan.Zero, TimeSpan.Zero);
    private sealed class Scope : IDisposable { public void Dispose() { } }
}
public enum FileTreePermission { ReadOnly, ReadWrite }
